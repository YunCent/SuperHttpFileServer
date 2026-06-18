using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace SimpleHttpServer
{
    // HTTP 文件服务器
    public class FileHttpServer : IDisposable
    {
        private HttpListener _listener;
        private CancellationTokenSource _cts;
        private Task _listenTask;
        private readonly object _lock = new object();
        private volatile bool _running;

        private long _totalBytes;
        private long _totalRequests;
        private int _internalHttpPort;
        private SslProxyServer _sslProxy;

        // 会话存储
        private static readonly Dictionary<string, SessionInfo> _sessions = new Dictionary<string, SessionInfo>();
        private static readonly object _sessionLock = new object();

        // 登录防暴力破解
        private class LoginAttempt
        {
            public int Count;
            public DateTime FirstFail;
            public DateTime? LockUntil;
        }
        private static readonly ConcurrentDictionary<string, LoginAttempt> _loginAttempts = new ConcurrentDictionary<string, LoginAttempt>();
        private const int MaxLoginAttempts = 5;        // 最大失败次数
        private const int LockoutMinutes = 15;         // 封禁分钟
        private const int AttemptWindowMinutes = 5;    // 计数窗口分钟
        private static Timer _loginCleanupTimer;
        private static bool _timerStarted;
        private static readonly object _loginTimerLock = new object();

        // 登录防暴力破解
        private const int CleanupInterval = 100;

        public long TotalBytes => Interlocked.Read(ref _totalBytes);
        public long TotalRequests => Interlocked.Read(ref _totalRequests);
        public bool IsRunning => _running;

        public event Action<long> OnRequestCountChanged;

        // 启动 HTTP + HTTPS 服务器
        public void Start(AppConfig config)
        {
            lock (_lock)
            {
                if (_running) return;

                _cts = new CancellationTokenSource();

                string addr = config.ListenAddr;
                int port = config.ListenPort;
                bool wantSsl = config.SslEnabled
                    && !string.IsNullOrEmpty(config.SslCertPath) && !string.IsNullOrEmpty(config.SslKeyPath)
                    && File.Exists(config.SslCertPath) && File.Exists(config.SslKeyPath);

                // 内部 HTTP 端口（SSL 代理转发用）
                _internalHttpPort = new Random().Next(50000, 60000);
                string internalPrefix = "http://127.0.0.1:" + _internalHttpPort + "/";

                bool sslOk = false;

                // 先尝试启动 SSL 代理
                if (wantSsl)
                {
                    sslOk = TryStartSslProxy(config, addr, port);
                    if (!sslOk)
                        Logger.Warn("HTTPS 配置失败，将以纯 HTTP 模式运行");
                }

                // 构建 HttpListener 前缀
                _listener = new HttpListener();
                _listener.Prefixes.Add(internalPrefix); // 内部端口始终监听

                if (!sslOk)
                {
                    // 纯 HTTP 模式：同时监听外部端口
                    string externalPrefix = (addr == "0.0.0.0") ? "http://+:" + port + "/" : "http://" + addr + ":" + port + "/";
                    _listener.Prefixes.Add(externalPrefix);
                    Logger.Info("监听: " + externalPrefix + " + " + internalPrefix);
                }
                else
                {
                    Logger.Info("监听(内部): " + internalPrefix + " (SSL代理转发到此处)");
                }

                try
                {
                    _listener.Start();
                }
                catch (HttpListenerException)
                {
                    Logger.Warn("非管理员权限，回退到 localhost:" + port);
                    _listener = new HttpListener();
                    _listener.Prefixes.Add(internalPrefix);
                    if (!sslOk)
                        _listener.Prefixes.Add("http://localhost:" + port + "/");
                    _listener.Start();
                }
                catch (Exception ex)
                {
                    throw new Exception("HTTP 启动失败: " + ex.Message);
                }

                _running = true;
                string mode = sslOk ? "HTTPS" : "HTTP";
                Logger.Info("服务器已启动 (" + mode + "): " + addr + ":" + port);

                // 启动请求处理循环
                _listenTask = Task.Run(() => AcceptLoop(config, _cts.Token));
            }
        }

        // 尝试启动 SSL 代理
        private bool TryStartSslProxy(AppConfig config, string addr, int port)
        {
            X509Certificate2 cert = null;
            try
            {
                Logger.Info("[SSL] 正在加载证书: " + config.SslCertPath);

                try
                {
                    cert = CertificateHelper.LoadCertificate(config.SslCertPath, config.SslKeyPath);
                }
                catch (Exception ex)
                {
                    Logger.Error("[SSL] 证书加载失败: " + ex.Message);
                    return false;
                }

                Logger.Info("[SSL] 证书加载成功: Subject=" + cert.Subject + ", NotAfter=" + cert.NotAfter.ToString("yyyy-MM-dd") + ", HasPrivateKey=" + cert.HasPrivateKey);

                if (!cert.HasPrivateKey)
                {
                    Logger.Error("[SSL] 证书没有关联私钥，无法启动 HTTPS");
                    cert.Dispose();
                    return false;
                }

                // 启动 SSL 代理
                _sslProxy = new SslProxyServer(cert, addr, port, _internalHttpPort);
                cert = null; // 所有权已转移给 _sslProxy
                _sslProxy.Start();
                Logger.Info("[SSL] HTTPS 代理已启动: " + addr + ":" + port + " → 127.0.0.1:" + _internalHttpPort);
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error("[SSL] HTTPS 启动异常: " + ex.GetType().Name + ": " + ex.Message);
                if (ex.InnerException != null)
                    Logger.Error("[SSL] 内部异常: " + ex.InnerException.Message);
                // 清理：如果 _sslProxy 已创建则 Dispose（含证书），否则直接释放证书
                if (_sslProxy != null)
                {
                    try { _sslProxy.Dispose(); } catch { }
                    _sslProxy = null;
                }
                else
                {
                    cert?.Dispose();
                }
                return false;
            }
        }

        // 停止服务器并释放资源
        public void Stop()
        {
            Task taskToWait = null;

            lock (_lock)
            {
                if (!_running) return;
                _running = false;

                try { _cts?.Cancel(); } catch { }

                if (_listener != null)
                {
                    try
                    {
                        if (_listener.IsListening)
                            _listener.Stop();
                    }
                    catch { }

                    try { _listener.Close(); } catch { }
                    _listener = null;
                }

                // 停止 SSL 代理
                if (_sslProxy != null)
                {
                    try { _sslProxy.Dispose(); } catch { }
                    _sslProxy = null;
                }

                taskToWait = _listenTask;
                _listenTask = null;
            }

            // 等待 AcceptLoop 完全退出（确保端口释放）
            if (taskToWait != null)
            {
                try { taskToWait.Wait(1000); } catch { }
                try { taskToWait.Dispose(); } catch { }
            }

            Logger.Info("Server stopped");
        }

        public void Dispose()
        {
            Stop();
            try { _cts?.Dispose(); } catch { }
        }

        // 请求接收循环
        private void AcceptLoop(AppConfig config, CancellationToken token)
        {
            int requestCount = 0;
            while (!token.IsCancellationRequested && _running)
            {
                try
                {
                    var context = _listener.GetContext();
                    var ctx = context;
                    Task.Run(() =>
                    {
                        try
                        {
                            // 定期清理过期会话
                            if (Interlocked.Increment(ref requestCount) % CleanupInterval == 0)
                                CleanupExpiredSessions();

                            HandleRequest(ctx, config);
                        }
                        catch (Exception ex)
                        {
                            Logger.Error("Request handler error: " + ex.Message);
                            try { ctx.Response.Close(); } catch { }
                        }
                    });
                }
                catch (HttpListenerException) { break; }
                catch (OperationCanceledException) { break; }
                catch (ObjectDisposedException) { break; }
                catch (Exception ex)
                {
                    if (_running)
                        Logger.Error("AcceptLoop error: " + ex.Message);
                    break;
                }
            }
        }

        // 清理过期会话
        private void CleanupExpiredSessions()
        {
            lock (_sessionLock)
            {
                var now = DateTime.UtcNow;
                var expired = new List<string>();
                foreach (var kvp in _sessions)
                {
                    if (kvp.Value.ExpireTime <= now)
                        expired.Add(kvp.Key);
                }
                foreach (var key in expired)
                    _sessions.Remove(key);

                if (expired.Count > 0)
                    Logger.Info("Cleaned up " + expired.Count + " expired sessions");
            }
        }

        // 处理单个 HTTP 请求
        private void HandleRequest(HttpListenerContext context, AppConfig config)
        {
            Interlocked.Increment(ref _totalRequests);
            var ev = OnRequestCountChanged;
            if (ev != null) ev(TotalRequests);

            var request = context.Request;
            var response = context.Response;

            // 安全响应头（所有响应统一添加）
            response.AddHeader("X-Content-Type-Options", "nosniff");
            response.AddHeader("X-Frame-Options", "SAMEORIGIN");
            response.AddHeader("X-XSS-Protection", "1; mode=block");
            response.AddHeader("Referrer-Policy", "strict-origin-when-cross-origin");

            // HTTP 自动跳转 HTTPS（SSL 代理模式下，内部 HTTP 请求带 X-Forwarded-Proto: https 表示已走 SSL）
            if (!string.IsNullOrEmpty(config.Domain) && config.SslEnabled
                && request.Headers["X-Forwarded-Proto"] != "https" && !request.IsSecureConnection)
            {
                string host = config.Domain;
                int port = config.ListenPort;
                string httpsUrl = "https://" + host + (port == 443 ? "" : ":" + port) + request.Url.PathAndQuery;
                response.StatusCode = 302;
                response.AddHeader("Location", httpsUrl);
                response.Close();
                return;
            }

            try
            {
                string path = request.Url.AbsolutePath.TrimEnd('/');
                if (string.IsNullOrEmpty(path)) path = "/";

                // 公共资源：无需认证即可访问
                if (path == "/favicon.ico")
                {
                    ServeFavicon(response);
                    return;
                }
                if (path == "/logo")
                {
                    ServeLogo(response);
                    return;
                }
                // 登录接口：POST username/password
                if (path == "/_login" && request.HttpMethod == "POST")
                {
                    HandleLogin(request, response, config);
                    return;
                }

                // 退出接口：POST（无需认证）
                if (path == "/_logout" && request.HttpMethod == "POST")
                {
                    HandleLogout(request, response);
                    return;
                }

                // 认证检查
                UserInfo user = null;
                string clientIp = GetClientIp(request);
                string clientUa = (request.UserAgent ?? "").Length > 128
                    ? request.UserAgent.Substring(0, 128)
                    : (request.UserAgent ?? "");

                if (config.AuthEnable)
                {
                    user = Authenticate(request, response, config, clientIp, clientUa);
                    if (user == null) return;
                }

                // 目录列表接口：GET /_dirs?path=xxx（需认证后使用用户对应目录）
                if (path == "/_dirs" && request.HttpMethod == "GET")
                {
                    HandleListDirs(request, response, config, user);
                    return;
                }

                // Office 预览接口：GET /_preview?path=xxx（需认证，后端转 PDF）
                if (path == "/_preview" && request.HttpMethod == "GET")
                {
                    HandlePreview(request, response, config, user);
                    return;
                }

                // 路由分发
                string shareDir = config.GetUserShareDir(user);
                UserInfo effUser = config.GetEffectiveUser(user);

                if (path == "/")
                {
                    ServeDirectory(response, shareDir, shareDir, effUser, config);
                }
                else if (path.EndsWith("/_zip"))
                {
                    if (effUser != null && !effUser.AllowZip)
                    {
                        WriteJson(response, 403, "{\"ok\":false,\"error\":\"权限不足\"}");
                        return;
                    }
                    ServeZip(response, shareDir, path.Substring(0, path.Length - 5));
                }
                else if (request.HttpMethod == "POST")
                {
                    if (effUser != null && !effUser.AllowUpload)
                    {
                        WriteJson(response, 403, "{\"ok\":false,\"error\":\"权限不足\"}");
                        return;
                    }
                    HandleUpload(request, response, shareDir, path, effUser, clientIp);
                }
                else if (request.HttpMethod == "DELETE")
                {
                    if (effUser != null && !effUser.AllowDelete)
                    {
                        WriteJson(response, 403, "{\"ok\":false,\"error\":\"权限不足\"}");
                        return;
                    }
                    HandleDelete(response, shareDir, path, effUser, clientIp);
                }
                else if (request.HttpMethod == "MOVE")
                {
                    if (effUser != null && !effUser.AllowMove)
                    {
                        WriteJson(response, 403, "{\"ok\":false,\"error\":\"权限不足\"}");
                        return;
                    }
                    HandleMove(response, shareDir, path, request, effUser, clientIp);
                }
                else if (request.HttpMethod == "RENAME")
                {
                    if (effUser != null && !effUser.AllowRename)
                    {
                        WriteJson(response, 403, "{\"ok\":false,\"error\":\"权限不足\"}");
                        return;
                    }
                    HandleRename(response, shareDir, path, request, effUser, clientIp);
                }
                else
                {
                    // Range 请求或普通文件下载
                    ServeFile(request, response, shareDir, path, effUser, clientIp, config);
                }
            }
            catch (HttpListenerException)
            {
                // 客户端断开连接，非服务端错误
            }
            catch (Exception ex)
            {
                Logger.Error("Request error: " + ex.Message + "\n" + ex.StackTrace);
                try { WriteJson(response, 500, "{\"ok\":false,\"error\":\"" + HtmlEncode(ex.Message) + "\"}"); } catch { }
            }
            finally
            {
                try { response.Close(); } catch { }
            }
        }

        // 获取客户端真实 IP
        private string GetClientIp(HttpListenerRequest request)
        {
            // 优先从 X-Forwarded-For 取第一个 IP（反向代理场景）
            string fwd = request.Headers["X-Forwarded-For"];
            if (!string.IsNullOrEmpty(fwd))
            {
                string first = fwd.Split(',')[0].Trim();
                if (!string.IsNullOrEmpty(first)) return first;
            }
            return request.RemoteEndPoint?.Address?.ToString() ?? "unknown";
        }


        // 写入 JSON 响应
        private static void WriteJson(HttpListenerResponse response, int statusCode, string json)
        {
            response.StatusCode = statusCode;
            response.ContentType = "application/json; charset=utf-8";
            byte[] data = Encoding.UTF8.GetBytes(json);
            response.ContentLength64 = data.Length;
            response.OutputStream.Write(data, 0, data.Length);
        }


        // 提供 Favicon
        private static void ServeFavicon(HttpListenerResponse response)
        {
            var cfg = ConfigManager.Get();
            if (!string.IsNullOrEmpty(cfg.IcoPath) && File.Exists(cfg.IcoPath))
            {
                try
                {
                    byte[] icoData = File.ReadAllBytes(cfg.IcoPath);
                    string ext = Path.GetExtension(cfg.IcoPath).ToLower();
                    response.ContentType = ext == ".png" ? "image/png"
                        : ext == ".jpg" || ext == ".jpeg" ? "image/jpeg"
                        : "image/x-icon";
                    response.AddHeader("Cache-Control", "no-cache");
                    response.ContentLength64 = icoData.Length;
                    response.OutputStream.Write(icoData, 0, icoData.Length);
                    return;
                }
                catch { }
            }
            ServeEmbeddedImage(response, "SimpleHttpServer.Resources.favicon.png", "image/png");
        }

        // 提供 Logo 图片
        private static void ServeLogo(HttpListenerResponse response)
        {
            var cfg = ConfigManager.Get();
            string logoPath = cfg.LogoText;
            if (!string.IsNullOrEmpty(logoPath) && File.Exists(logoPath))
            {
                try
                {
                    byte[] imgData = File.ReadAllBytes(logoPath);
                    string ext = Path.GetExtension(logoPath).ToLower();
                    response.ContentType = ext == ".png" ? "image/png"
                        : ext == ".jpg" || ext == ".jpeg" ? "image/jpeg"
                        : ext == ".bmp" ? "image/bmp"
                        : "image/x-icon";
                    response.AddHeader("Cache-Control", "no-cache");
                    response.ContentLength64 = imgData.Length;
                    response.OutputStream.Write(imgData, 0, imgData.Length);
                    return;
                }
                catch { }
            }
            ServeEmbeddedImage(response, "SimpleHttpServer.Resources.logo.png", "image/png");
        }

        // 从嵌入资源加载图片
        private static void ServeEmbeddedImage(HttpListenerResponse response, string resourceName, string contentType)
        {
            var asm = Assembly.GetExecutingAssembly();
            using (var stream = asm.GetManifestResourceStream(resourceName))
            {
                if (stream != null)
                {
                    byte[] data = new byte[stream.Length];
                    stream.Read(data, 0, data.Length);
                    response.ContentType = contentType;
                    response.AddHeader("Cache-Control", "no-cache");
                    response.ContentLength64 = data.Length;
                    response.OutputStream.Write(data, 0, data.Length);
                    return;
                }
            }
            WriteBlankPng(response);
        }


        // 1x1 透明 PNG 占位图
        private static void WriteBlankPng(HttpListenerResponse response)
        {
            byte[] blankPng = Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAAC0lEQVQI12NgAAIABQABNjN9GQAAAABJRUEFTkSuQmCC");
            response.ContentType = "image/png";
            response.ContentLength64 = blankPng.Length;
            response.OutputStream.Write(blankPng, 0, blankPng.Length);
        }


        // 认证（Cookie 优先，兼容 Basic Auth）
        private UserInfo Authenticate(HttpListenerRequest request, HttpListenerResponse response,
            AppConfig config, string clientIp, string clientUa)
        {
            // 1. 检查会话 Cookie
            string cookie = request.Headers["Cookie"];
            if (!string.IsNullOrEmpty(cookie))
            {
                foreach (string part in cookie.Split(';'))
                {
                    string trimmed = part.Trim();
                    if (trimmed.StartsWith("_session="))
                    {
                        string token = trimmed.Substring(9);
                        lock (_sessionLock)
                        {
                            if (_sessions.TryGetValue(token, out SessionInfo si))
                            {
                                if (si.ExpireTime > DateTime.UtcNow)
                                {
                                    // 刷新会话有效期
                                    int timeoutMin = config.Timeout > 0 ? config.Timeout : 30;
                                    _sessions[token] = new SessionInfo(si.UserName, DateTime.UtcNow.AddMinutes(timeoutMin));

                                    // 查找用户对象
                                    foreach (var u in config.AuthUsers)
                                    {
                                        if (u.UserName == si.UserName)
                                            return u;
                                    }
                                }
                                else
                                {
                                    _sessions.Remove(token);
                                }
                            }
                        }
                    }
                }
            }

            // 2. 兼容 Basic Auth（供 API 客户端使用）
            string authHeader = request.Headers["Authorization"];
            if (!string.IsNullOrEmpty(authHeader) && authHeader.StartsWith("Basic ") && authHeader.Length >= 8)
            {
                try
                {
                    string decoded = Encoding.UTF8.GetString(Convert.FromBase64String(authHeader.Substring(6)));
                    int colon = decoded.IndexOf(':');
                    if (colon >= 0)
                    {
                        string username = decoded.Substring(0, colon);
                        string password = decoded.Substring(colon + 1);
                        foreach (var u in config.AuthUsers)
                        {
                            if (u.UserName == username && Utility.VerifyPassword(password, u.Password))
                                return u;
                        }
                    }
                }
                catch { }
            }

            // 未认证：显示自定义登录页
            WriteAuthRequired(response);
            return null;
        }


        // 退出登录
        private void HandleLogout(HttpListenerRequest request, HttpListenerResponse response)
        {
            string cookie = request.Headers["Cookie"];
            string clientIp = GetClientIp(request);

            if (!string.IsNullOrEmpty(cookie))
            {
                foreach (string part in cookie.Split(';'))
                {
                    string trimmed = part.Trim();
                    if (trimmed.StartsWith("_session="))
                    {
                        string token = trimmed.Substring(9);
                        lock (_sessionLock)
                        {
                            if (_sessions.TryGetValue(token, out SessionInfo si))
                            {
                                _sessions.Remove(token);
                            }
                        }
                    }
                }
            }

            // 清除客户端 Cookie
            response.AddHeader("Set-Cookie",
                "_session=; Path=/; HttpOnly; SameSite=Strict; Expires=Thu, 01 Jan 1970 00:00:00 GMT");
            WriteJson(response, 200, "{\"ok\":true}");
        }

    
        // 目录列表接口：GET /_dirs?path=xxx
        private void HandleListDirs(HttpListenerRequest request, HttpListenerResponse response, AppConfig config, UserInfo user)
        {
            string queryPath = request.QueryString["path"] ?? "/";
            string baseDir = config.GetUserShareDir(user);

            string targetDir = baseDir;
            if (queryPath.Length > 1)
            {
                if (!TryResolveSafePath(baseDir, queryPath, out string resolved))
                {
                    WriteJson(response, 403, "{\"ok\":false,\"error\":\"禁止访问\"}");
                    return;
                }
                targetDir = resolved;
            }

            try
            {
                if (!Directory.Exists(targetDir))
                {
                    WriteJson(response, 404, "{\"ok\":false,\"error\":\"目录不存在\"}");
                    return;
                }

                var jsonParts = new List<string>();
                foreach (var di in new DirectoryInfo(targetDir).GetDirectories())
                {
                    try
                    {
                        string relPath = di.FullName.Substring(baseDir.Length).Replace('\\', '/');
                        if (!relPath.StartsWith("/")) relPath = "/" + relPath;
                        jsonParts.Add("{\"name\":\"" + HtmlEncode(di.Name) + "\",\"path\":\"" + HtmlEncode(relPath) + "\"}");
                    }
                    catch { }
                }

                string json = "{\"ok\":true,\"dirs\":[" + string.Join(",", jsonParts) + "]}";
                WriteJson(response, 200, json);
            }
            catch (Exception ex)
            {
                WriteJson(response, 500, "{\"ok\":false,\"error\":\"" + HtmlEncode(ex.Message) + "\"}");
            }
        }

    
        // 登录处理：POST /_login
        private void HandleLogin(HttpListenerRequest request, HttpListenerResponse response, AppConfig config)
        {
            string clientIp = GetClientIp(request);

            // 防暴力破解：检查 IP 是否被封禁
            if (IsIpLocked(clientIp))
            {
                Logger.Warn("IP 被封禁: " + clientIp);
                response.StatusCode = 429;
                response.ContentType = "application/json";
                byte[] blockJson = Encoding.UTF8.GetBytes("{\"ok\":false,\"error\":\"尝试过于频繁，请稍后再试\"}");
                response.ContentLength64 = blockJson.Length;
                response.OutputStream.Write(blockJson, 0, blockJson.Length);
                return;
            }

            try
            {
                byte[] bodyBytes;
                using (var ms = new MemoryStream())
                {
                    request.InputStream.CopyTo(ms);
                    bodyBytes = ms.ToArray();
                }
                string body = Encoding.UTF8.GetString(bodyBytes);

                string username = "", password = "";
                foreach (string pair in body.Split('&'))
                {
                    string[] kv = pair.Split('=');
                    if (kv.Length == 2)
                    {
                        if (kv[0] == "username")
                            username = Uri.UnescapeDataString(kv[1].Replace('+', ' '));
                        if (kv[0] == "password")
                            password = Uri.UnescapeDataString(kv[1].Replace('+', ' '));
                    }
                }

                foreach (var u in config.AuthUsers)
                {
                    if (u.UserName == username && Utility.VerifyPassword(password, u.Password))
                    {
                        // 登录成功，清除该 IP 尝试记录
                        LoginAttempt removed;
                        _loginAttempts.TryRemove(clientIp, out removed);

                        // 生成会话令牌
                        string token = GenerateToken();
                        int timeoutMin = config.Timeout > 0 ? config.Timeout : 30;
                        lock (_sessionLock)
                        {
                            _sessions[token] = new SessionInfo(username, DateTime.UtcNow.AddMinutes(timeoutMin));
                        }

                        response.StatusCode = 200;
                        response.ContentType = "application/json";
                        response.AddHeader("Set-Cookie",
                            "_session=" + token + "; Path=/; HttpOnly; SameSite=Strict");
                        byte[] json = Encoding.UTF8.GetBytes(
                            "{\"ok\":true,\"user\":\"" + HtmlEncode(username) + "\"}");
                        response.ContentLength64 = json.Length;
                        response.OutputStream.Write(json, 0, json.Length);
                        return;
                    }
                }

                // 凭据错误：记录失败
                RecordFailedAttempt(clientIp);
                string failMsg = "用户名或密码错误";

                // 渐进延迟：每次失败增加延迟，防止暴力猜解
                if (_loginAttempts.TryGetValue(clientIp, out LoginAttempt att))
                {
                    int delayMs = Math.Min(att.Count * 1000, 5000);
                    if (delayMs > 0) Thread.Sleep(delayMs);

                    // 如果达到封禁阈值，提示封禁信息
                    if (att.LockUntil.HasValue)
                        failMsg = "尝试过于频繁，请稍后再试";
                }

                response.StatusCode = 401;
                response.ContentType = "application/json";
                byte[] errJson = Encoding.UTF8.GetBytes("{\"ok\":false,\"error\":\"" + failMsg + "\"}");
                response.ContentLength64 = errJson.Length;
                response.OutputStream.Write(errJson, 0, errJson.Length);
            }
            catch (Exception ex)
            {
                response.StatusCode = 500;
                response.ContentType = "application/json";
                byte[] errJson = Encoding.UTF8.GetBytes(
                    "{\"ok\":false,\"error\":\"" + HtmlEncode(ex.Message) + "\"}");
                response.ContentLength64 = errJson.Length;
                response.OutputStream.Write(errJson, 0, errJson.Length);
            }
        }

    
        // 生成会话令牌
        private static string GenerateToken()
        {
            byte[] bytes = new byte[32];
            using (var rng = RandomNumberGenerator.Create())
                rng.GetBytes(bytes);
            return Convert.ToBase64String(bytes)
                .Replace('+', '-').Replace('/', '_').TrimEnd('=');
        }

    
        // 渲染登录页
        private void WriteAuthRequired(HttpListenerResponse response)
        {
            response.StatusCode = 401;
            response.AddHeader("Cache-Control", "no-cache, no-store, must-revalidate");

            var cfg = ConfigManager.Get();
            string title = HtmlEncode(cfg.WebTitle ?? "File Server");
            string logoSrc = "/logo";

            var sb = new StringBuilder();
            sb.AppendLine("<!DOCTYPE html><html lang='zh-CN'><head><meta charset='utf-8'>");
            sb.AppendLine("<meta name='viewport' content='width=device-width,initial-scale=1'>");
            sb.AppendLine("<title>" + title + " - Login</title>");
            sb.AppendLine("<link rel='icon' href='/favicon.ico'>");
            sb.AppendLine("<style>");
            sb.AppendLine("*{margin:0;padding:0;box-sizing:border-box}");
            sb.AppendLine("body{font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,'PingFang SC','Microsoft YaHei',sans-serif;background:#f0f2f5;min-height:100vh;display:flex;flex-direction:column;align-items:center;padding-top:18vh}");
            sb.AppendLine(".card{background:#fff;border-radius:14px;padding:32px 32px 36px;width:370px;box-shadow:0 4px 20px rgba(0,0,0,.1);position:relative}");
            if (!string.IsNullOrEmpty(logoSrc))
            {
                sb.AppendLine(".logo{width:52px;height:52px;border-radius:10px;object-fit:contain;position:absolute;top:28px;left:32px}");
                sb.AppendLine(".title-row{text-align:center;margin-bottom:24px;padding-top:8px}");
            }
            sb.AppendLine(".title-row h2{font-size:22px;font-weight:600;color:#1a1a2e;margin:0}");
            sb.AppendLine(".input-group{position:relative;margin-bottom:18px}");
            sb.AppendLine(".input-group .icon{position:absolute;left:14px;top:50%;transform:translateY(-50%);font-size:15px;color:#aaa;pointer-events:none}");
            sb.AppendLine(".input-group input{width:100%;height:44px;border:1px solid #e0e0e0;border-radius:10px;padding:0 14px 0 40px;font-size:14px;outline:none;transition:border-color .2s,box-shadow .2s}");
            sb.AppendLine(".input-group input:focus{border-color:#4285F4;box-shadow:0 0 0 3px rgba(66,133,244,.12)}");
            sb.AppendLine(".input-group input::placeholder{color:#bbb}");
            sb.AppendLine(".eye-btn{position:absolute;right:12px;top:50%;transform:translateY(-50%);background:none;border:none;cursor:pointer;font-size:17px;color:#aaa;padding:4px;line-height:1}");
            sb.AppendLine(".remember-row{display:flex;align-items:center;gap:8px;margin:6px 0 18px;font-size:13px;color:#666;cursor:pointer}");
            sb.AppendLine(".remember-row input[type=checkbox]{width:16px;height:16px;accent-color:#4285F4;cursor:pointer}");
            sb.AppendLine(".login-btn{width:100%;height:44px;background:#1a1a2e;color:#fff;border:none;border-radius:10px;font-size:16px;font-weight:500;cursor:pointer;transition:all .2s}");
            sb.AppendLine(".login-btn:hover{opacity:.88;box-shadow:0 3px 10px rgba(26,26,46,.28)}");
            sb.AppendLine(".error{color:#EF5350;font-size:12px;margin-top:12px;display:none}");
            int beianSize = cfg.BeianSize > 0 ? cfg.BeianSize : 15;
            sb.AppendLine(".beian{text-align:center;font-size:" + beianSize + "px;color:#999;margin-top:24px;position:fixed;bottom:20px;left:0;width:100%}");
            sb.AppendLine(".beian a{color:#999;text-decoration:none}.beian a:hover{color:#666;text-decoration:underline}");
            sb.AppendLine("@media(max-width:500px){.card{width:92%;padding:20px 18px}.logo{width:44px;height:44px;margin-bottom:10px}.title-row h2{font-size:17px}.title-row{margin-bottom:16px}.input-group input{height:40px;font-size:13px}.login-btn{height:40px;font-size:14px}.input-group{margin-bottom:14px}.remember-row{margin:2px 0 14px}.beian{font-size:11px!important;bottom:12px}}");
            sb.AppendLine("</style></head><body>");

            sb.AppendLine("<div class='card'>");
            sb.AppendLine("<img class='logo' src='" + logoSrc + "' alt='Logo'>");
            sb.AppendLine("<div class='title-row'>");
            sb.AppendLine("<h2>" + title + "</h2>");
            sb.AppendLine("</div>");
            sb.AppendLine("<form id='loginForm' onsubmit='return doLogin(event)'>");
            sb.AppendLine("<div class='input-group'><span class='icon'>👤</span><input type='text' id='user' placeholder='请输入用户名' autocomplete='username' required></div>");
            sb.AppendLine("<div class='input-group'><span class='icon'>🔒</span><input type='password' id='pass' placeholder='请输入密码' autocomplete='current-password' required style='padding-right:36px'><button type='button' class='eye-btn' id='eyeBtn' onclick='toggleEye()' tabindex='-1'>👁</button></div>");
            sb.AppendLine("<label class='remember-row'><input type='checkbox' id='rememberUser'>记住用户名</label>");
            sb.AppendLine("<button type='submit' class='login-btn' id='loginBtn'>登 录</button>");
            sb.AppendLine("</form>");
            sb.AppendLine("<div class='error' id='errMsg'></div>");
            sb.AppendLine("</div>");
            // 备案信息
            string beian = HtmlEncode(cfg.Beian ?? "");
            if (!string.IsNullOrEmpty(beian))
                sb.AppendLine("<div class='beian'><a href='https://beian.miit.gov.cn/' target='_blank' rel='noopener'>" + beian + "</a></div>");

            sb.AppendLine("<script>");
            sb.AppendLine("(function(){var u=document.getElementById('user');var r=document.getElementById('rememberUser');var saved=localStorage.getItem('_saved_user');if(saved){u.value=saved;r.checked=true;u.focus()}})()");
            sb.AppendLine("function toggleEye(){var p=document.getElementById('pass');var e=document.getElementById('eyeBtn');if(p.type==='password'){p.type='text';e.textContent='👁‍🗨'}else{p.type='password';e.textContent='👁'}}");
            sb.AppendLine("function doLogin(e){e.preventDefault();var u=document.getElementById('user').value;var p=document.getElementById('pass').value;var r=document.getElementById('rememberUser');var btn=document.getElementById('loginBtn');if(r.checked){localStorage.setItem('_saved_user',u)}else{localStorage.removeItem('_saved_user')}btn.textContent='登录中...';btn.disabled=true;fetch('/_login',{method:'POST',headers:{'Content-Type':'application/x-www-form-urlencoded'},body:'username='+encodeURIComponent(u)+'&password='+encodeURIComponent(p)}).then(function(r){return r.json()}).then(function(d){if(d.ok){location.href='/'}else{var el=document.getElementById('errMsg');el.textContent=d.error||'用户名或密码错误';el.style.display='block';btn.textContent='登 录';btn.disabled=false;document.getElementById('pass').value='';document.getElementById('pass').focus()}}).catch(function(){var el=document.getElementById('errMsg');el.textContent='网络错误';el.style.display='block';btn.textContent='登 录';btn.disabled=false});return false}");
            sb.AppendLine("</script></body></html>");

            byte[] html = Encoding.UTF8.GetBytes(sb.ToString());
            response.ContentType = "text/html; charset=utf-8";
            response.ContentLength64 = html.Length;
            response.OutputStream.Write(html, 0, html.Length);
        }

    
        // 目录浏览页面
        private void ServeDirectory(HttpListenerResponse response, string baseDir,
            string currentDir, UserInfo user, AppConfig config)
        {
            bool showZip = user == null || user.AllowZip;
            bool showUpload = user == null || user.AllowUpload;
            bool showDelete = user == null || user.AllowDelete;
            bool showMove = user == null || user.AllowMove;
            bool showRename = user == null || user.AllowRename;
            string username = user != null ? user.UserName : "";
            var cfg = ConfigManager.Get();
            int timeoutMin = cfg.Timeout;

            if (!Directory.Exists(currentDir))
            {
                WriteJson(response, 404, "{\"ok\":false,\"error\":\"目录不存在\"}");
                return;
            }

            // 计算相对路径（用于面包屑导航）
            string relativePath = "";
            if (!currentDir.Equals(baseDir, StringComparison.OrdinalIgnoreCase))
            {
                relativePath = currentDir.Substring(baseDir.Length).TrimStart(Path.DirectorySeparatorChar).Replace('\\', '/');
            }

            // 计算当前目录的 URL 前缀（用于子目录内文件/文件夹链接）
            string urlPrefix = "/";
            if (!string.IsNullOrEmpty(relativePath))
            {
                string[] segs = relativePath.Split(new[]{'/'}, StringSplitOptions.RemoveEmptyEntries);
                urlPrefix = "/" + string.Join("/", segs.Select(s => Uri.EscapeDataString(s))) + "/";
            }

            var sb = new StringBuilder();
            sb.AppendLine("<!DOCTYPE html><html lang='zh-CN'><head><meta charset='utf-8'>");
            sb.AppendLine("<meta name='viewport' content='width=device-width,initial-scale=1'>");
            sb.AppendLine("<title>" + HtmlEncode(cfg.WebTitle ?? "File Server") + "</title>");
            sb.AppendLine("<link rel='icon' href='/favicon.ico'>");
            sb.AppendLine("<style>");
            sb.AppendLine("*{margin:0;padding:0;box-sizing:border-box}");
            sb.AppendLine("body{font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,'PingFang SC','Microsoft YaHei',sans-serif;background:#f0f2f5;color:#333;min-height:100vh}");
            // 页头
            sb.AppendLine(".header{background:#fff;border-bottom:1px solid #e8e8e8;height:56px;max-width:1024px;width:100%;margin:0 auto;padding:0 24px;display:flex;align-items:center;justify-content:space-between;box-shadow:0 1px 4px rgba(0,0,0,.06);border-radius:12px}");
            sb.AppendLine(".header-left{display:flex;align-items:center;gap:12px}");
            sb.AppendLine(".logo{width:32px;height:32px;border-radius:8px;display:flex;align-items:center;justify-content:center;overflow:hidden}");
            sb.AppendLine(".logo img{width:100%;height:100%;object-fit:contain}");
            sb.AppendLine(".brand{font-size:16px;font-weight:600;color:#333}");
            sb.AppendLine(".header-right{display:flex;align-items:center;gap:12px}");
            sb.AppendLine(".user-badge{display:flex;align-items:center;gap:8px;padding:6px 14px;background:#f5f5f5;border-radius:20px;font-size:13px;color:#666;box-shadow:inset 0 1px 2px rgba(0,0,0,.06)}");
            sb.AppendLine(".user-avatar{width:24px;height:24px;background:#4285F4;border-radius:50%;color:#fff;display:flex;align-items:center;justify-content:center;font-size:12px;font-weight:600;box-shadow:0 1px 3px rgba(66,133,244,.3)}");
            // 面包屑
            sb.AppendLine(".breadcrumb{padding:0 24px;margin-top:12px;font-size:15px;color:#666;max-width:1024px;width:100%;margin-left:auto;margin-right:auto}");
            sb.AppendLine(".breadcrumb a{color:#4285F4;text-decoration:none}.breadcrumb a:hover{color:#5A95F5}");
            sb.AppendLine(".breadcrumb .sep{color:#ccc;margin:0 6px}");
            // 按钮
            sb.AppendLine(".btn{display:inline-flex;align-items:center;justify-content:center;gap:6px;height:34px;padding:0 16px;border-radius:6px;font-size:13px;font-weight:500;cursor:pointer;border:none;text-decoration:none;transition:all .2s;box-shadow:0 1px 3px rgba(0,0,0,.08)}");
            sb.AppendLine(".btn-primary{background:#4285F4;color:#fff}.btn-primary:hover{background:#5A95F5;box-shadow:0 2px 6px rgba(66,133,244,.3)}");
            sb.AppendLine(".btn-outline{background:#fff;color:#4285F4;border:1px solid #A4C2F4}.btn-outline:hover{background:#E3F0FF;box-shadow:0 2px 6px rgba(66,133,244,.15)}");
            sb.AppendLine(".btn-danger{background:#EF5350;color:#fff}.btn-danger:hover{background:#F44336;box-shadow:0 2px 6px rgba(244,67,54,.3)}");
            sb.AppendLine(".btn-danger-outline{background:#fff;color:#EF5350;border:1px solid #FFCDD2}.btn-danger-outline:hover{background:#FFEBEE;box-shadow:0 2px 6px rgba(244,67,54,.15)}");
            sb.AppendLine(".btn-icon{background:#fff;color:#666;border:1px solid #e0e0e0;height:34px;padding:0 12px;font-size:13px}.btn-icon:hover{background:#f5f5f5;box-shadow:0 2px 6px rgba(0,0,0,.1)}");
            // 容器
            sb.AppendLine(".container{max-width:1024px;width:100%;margin:12px auto 40px;padding:0 24px}");
            // 搜索栏
            sb.AppendLine(".search-bar{display:flex;gap:8px;margin:0 -24px 12px}");
            sb.AppendLine(".search-bar input{flex:1;height:34px;border:1px solid #e0e0e0;border-radius:6px;padding:0 12px;font-size:13px;outline:none;box-shadow:inset 0 1px 2px rgba(0,0,0,.06);transition:border-color .2s,box-shadow .2s}");
            sb.AppendLine(".search-bar input:focus{border-color:#4285F4;box-shadow:inset 0 1px 2px rgba(0,0,0,.06),0 0 0 3px rgba(66,133,244,.12)}");
            sb.AppendLine(".search-bar .clear-btn{height:34px;padding:0 12px;background:#fff;border:1px solid #e0e0e0;border-radius:6px;color:#666;cursor:pointer;font-size:13px;box-shadow:0 1px 2px rgba(0,0,0,.04);transition:all .2s}");
            sb.AppendLine(".search-bar .clear-btn:hover{background:#f5f5f5;box-shadow:0 2px 4px rgba(0,0,0,.08)}");
            // 工具栏
            sb.AppendLine(".toolbar{display:flex;justify-content:space-between;align-items:center;margin:0 -24px 8px}");
            sb.AppendLine(".toolbar-left{display:flex;gap:8px;align-items:center}");
            sb.AppendLine(".toolbar-right{display:flex;gap:8px}");
            sb.AppendLine(".count-label{font-size:12px;color:#666}");
            // 文件列表
            sb.AppendLine(".file-list{background:#fff;border-radius:12px;box-shadow:0 2px 12px rgba(0,0,0,.07);overflow:hidden;margin:0 -24px}");
            sb.AppendLine(".file-row{display:flex;align-items:center;padding:10px 20px;border-bottom:1px solid #f0f0f0;transition:all .15s;position:relative}");
            sb.AppendLine(".file-row:last-child{border-bottom:none}");
            sb.AppendLine(".file-row:hover{background:#f8f9ff;box-shadow:inset 0 0 0 1px rgba(66,133,244,.06)}");
            sb.AppendLine(".file-row.hidden{display:none}");
            sb.AppendLine(".file-icon{width:36px;height:36px;border-radius:8px;display:flex;align-items:center;justify-content:center;margin-right:14px;font-size:18px;flex-shrink:0;box-shadow:0 1px 3px rgba(0,0,0,.1)}");
            sb.AppendLine(".icon-folder{background:#FFF3E0;color:#F57C00}");
            sb.AppendLine(".icon-file{background:#E3F2FD;color:#1976D2}");
            sb.AppendLine(".icon-zip{background:#FCE4EC;color:#C62828}");
            sb.AppendLine(".icon-img{background:#E3F0FF;color:#3367D6}");
            sb.AppendLine(".icon-audio{background:#F3E5F5;color:#7B1FA2}");
            sb.AppendLine(".icon-video{background:#E0F7FA;color:#00838F}");
            sb.AppendLine(".icon-code{background:#ECEFF1;color:#455A64}");
            sb.AppendLine(".icon-text{background:#FFF8E1;color:#F9A825}");
            sb.AppendLine(".icon-doc{background:#E8EAF6;color:#3949AB}");
            sb.AppendLine(".icon-word{background:#E3F2FD;color:#2B579A}");
            sb.AppendLine(".icon-excel{background:#E8F5E9;color:#217346}");
            sb.AppendLine(".icon-ppt{background:#FFF3E0;color:#D24726}");
            sb.AppendLine(".icon-pdf{background:#FFEBEE;color:#C62828}");
            sb.AppendLine(".icon-exe{background:#ECEFF1;color:#546E7A}");
            sb.AppendLine(".icon-dll{background:#E0F2F1;color:#00796B}");
            sb.AppendLine(".icon-script{background:#FFF3E0;color:#E65100}");
            sb.AppendLine(".file-info{flex:1;min-width:0}");
            sb.AppendLine(".file-name{font-size:14px;font-weight:500;color:#333;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}");
            sb.AppendLine(".file-name a{color:#333;text-decoration:none}.file-name a:hover{color:#4285F4;text-decoration:none}");
            sb.AppendLine(".file-meta{font-size:12px;color:#666;margin-top:2px}");
            // 右键菜单
            sb.AppendLine(".ctx-menu{position:fixed;background:#fff;border-radius:10px;box-shadow:0 8px 32px rgba(0,0,0,.16);padding:6px 0;min-width:160px;z-index:200;display:none;overflow:hidden}");
            sb.AppendLine(".ctx-menu.show{display:block}");
            sb.AppendLine(".ctx-item{display:flex;align-items:center;gap:10px;padding:9px 16px;font-size:13px;color:#333;cursor:pointer;transition:background .1s}");
            sb.AppendLine(".ctx-item:hover{background:#f0f2f5}");
            sb.AppendLine(".ctx-item.danger{color:#EF5350}.ctx-item.danger:hover{background:#FFEBEE}");
            sb.AppendLine(".ctx-sep{height:1px;background:#eee;margin:4px 0}");
            // 上传
            sb.AppendLine(".upload-area{margin-top:24px;background:#fff;border-radius:12px;padding:24px;box-shadow:0 2px 12px rgba(0,0,0,.07)}");
            sb.AppendLine(".upload-area h3{font-size:15px;font-weight:600;margin-bottom:14px;color:#333}");
            sb.AppendLine(".drop-zone{border:2px dashed #A4C2F4;border-radius:10px;padding:28px;text-align:center;color:#5A95F5;transition:all .2s;cursor:pointer}");
            sb.AppendLine(".drop-zone:hover,.drop-zone.dragover{border-color:#4285F4;background:#E3F0FF;color:#3367D6;box-shadow:inset 0 2px 8px rgba(66,133,244,.1)}");
            sb.AppendLine(".drop-zone p{margin:8px 0;font-size:14px}");
            sb.AppendLine(".drop-zone input[type=file]{display:none}");
            sb.AppendLine(".progress-bar-wrap{display:none;margin-top:12px;background:#f0f0f0;border-radius:4px;height:8px;overflow:hidden}");
            sb.AppendLine(".progress-bar-wrap.show{display:block}");
            sb.AppendLine(".progress-bar{height:100%;background:#4285F4;border-radius:4px;width:0%;transition:width .2s}");
            sb.AppendLine(".progress-text{font-size:12px;color:#666;margin-top:4px}");
            // 空状态
            sb.AppendLine(".empty-state{text-align:center;padding:48px 20px;color:#bbb}");
            sb.AppendLine(".empty-state .icon{font-size:48px;margin-bottom:12px}");
            // 提示组件
            sb.AppendLine("#toast{position:fixed;top:72px;right:24px;background:#fff;border-radius:10px;padding:12px 20px;box-shadow:0 6px 24px rgba(0,0,0,.14);font-size:13px;color:#333;z-index:300;display:none;align-items:center;gap:8px;max-width:320px;transition:all .2s}");
            sb.AppendLine("#toast.show{display:flex}");
            sb.AppendLine("#toast.success{border-left:3px solid #4285F4}");
            sb.AppendLine("#toast.error{border-left:3px solid #EF5350}");
            sb.AppendLine("#toast.info{border-left:3px solid #2196F3}");
            // 弹窗
            sb.AppendLine(".modal-overlay{position:fixed;top:0;left:0;width:100%;height:100%;background:rgba(0,0,0,.4);z-index:250;display:none;align-items:center;justify-content:center;backdrop-filter:blur(2px)}");
            sb.AppendLine(".modal-overlay.show{display:flex}");
            sb.AppendLine(".modal{background:#fff;border-radius:14px;padding:32px;width:440px;max-width:90vw;box-shadow:0 16px 48px rgba(0,0,0,.2);text-align:center}");
            sb.AppendLine(".modal h3{font-size:17px;font-weight:600;color:#333;margin-bottom:18px}");
            sb.AppendLine(".modal input[type=text]{width:100%;height:42px;border:1px solid #d9d9d9;border-radius:8px;padding:0 12px;font-size:14px;outline:none;margin-bottom:14px;box-shadow:inset 0 1px 2px rgba(0,0,0,.06);transition:border-color .2s,box-shadow .2s}");
            sb.AppendLine(".modal input[type=text]:focus{border-color:#4285F4;box-shadow:inset 0 1px 2px rgba(0,0,0,.06),0 0 0 3px rgba(66,133,244,.12)}");
            sb.AppendLine(".modal-actions{display:flex;gap:10px;justify-content:center;margin-top:8px}");
            sb.AppendLine(".modal-actions .btn{min-width:100px;justify-content:center}");
            // 目录树项
            sb.AppendLine(".dir-item{padding:8px 14px;font-size:13px;color:#333;cursor:pointer;display:flex;align-items:center;gap:8px;transition:background .1s;user-select:none}");
            sb.AppendLine(".dir-item:hover{background:#f0f2f5}");
            sb.AppendLine(".dir-item.selected{background:#E3F0FF;color:#3367D6;font-weight:500}");
            sb.AppendLine(".dir-item .arrow{font-size:10px;transition:transform .15s;flex-shrink:0;width:16px;text-align:center}");
            sb.AppendLine(".dir-item .arrow.open{transform:rotate(90deg)}");
            sb.AppendLine(".dir-children{display:none;padding-left:20px}");
            sb.AppendLine(".dir-children.open{display:block}");
            sb.AppendLine("@media(max-width:768px){.header{padding:0 20px;width:auto;max-width:none;margin:0 12px;border-radius:10px}.breadcrumb{padding:0 20px;margin:12px 12px 0;width:auto;max-width:none}.container{width:auto;max-width:none}.search-bar{margin:0 12px 12px}.toolbar{margin:0 12px 8px}.file-list{margin:0 12px;border-radius:10px}.file-row{padding:10px 16px}.file-icon{width:32px;height:32px;margin-right:12px;font-size:16px}.upload-area{padding:16px;margin:20px 12px 0;border-radius:10px}.drop-zone{padding:20px 16px}}");
            sb.AppendLine("@media(max-width:500px){.header{height:52px;padding:0 16px;width:auto;max-width:none;margin:0 8px;border-radius:8px}.brand{font-size:15px}.logo{width:32px;height:32px}.user-badge{padding:5px 12px;font-size:13px}.breadcrumb{padding:0 16px;margin:10px 8px 0;width:auto;max-width:none;font-size:13px}.container{width:auto;max-width:none;padding:0 8px;margin:10px auto 32px}.search-bar{margin:0 8px 10px;gap:6px}.search-bar input{height:36px;font-size:14px;padding:0 12px}.search-bar .clear-btn{height:36px;padding:0 12px;font-size:13px}.toolbar{margin:0 8px 6px}.toolbar .btn{height:32px;padding:0 12px;font-size:13px}.count-label{font-size:12px}.file-list{margin:0 8px;border-radius:8px}.file-row{padding:10px 12px}.file-icon{width:28px;height:28px;margin-right:10px;font-size:14px}.file-name{font-size:14px}.file-meta{font-size:12px}.upload-area{padding:14px;margin:16px 8px 0;border-radius:8px}.drop-zone{padding:16px}.modal{width:92%;padding:24px 16px;border-radius:12px}.ctx-menu{min-width:140px;font-size:13px}.btn{height:32px;padding:0 14px;font-size:13px}}");
            sb.AppendLine("</style></head><body>");

            // 页头
            sb.AppendLine("<div class='header'>");
            string logoHtml = "<div class='logo'><img src='/logo' alt='logo'></div>";
            sb.AppendLine("<div class='header-left'>" + logoHtml + "<div class='brand'>" + HtmlEncode(cfg.WebTitle ?? "File Server") + "</div></div>");
            sb.AppendLine("<div class='header-right'>");
            if (!string.IsNullOrEmpty(username))
            {
                sb.AppendLine("<div class='user-badge'><div class='user-avatar'>" +
                    HtmlEncode(username.Substring(0, 1).ToUpper()) + "</div>" +
                    HtmlEncode(username) + "</div>");
                sb.AppendLine("<button class='btn btn-outline' id='logoutBtn' style='font-size:12px;padding:5px 12px'>退出登录</button>");
            }
            sb.AppendLine("</div></div>");

            // 面包屑
            sb.AppendLine("<div class='breadcrumb'>");
            sb.AppendLine("<a href='/'><span style='font-size:18px'>🏠</span> 首页</a>");
            if (!string.IsNullOrEmpty(relativePath))
            {
                string[] parts = relativePath.Split(new[]{'/'}, StringSplitOptions.RemoveEmptyEntries);
                string accum = "";
                foreach (string part in parts)
                {
                    if (string.IsNullOrEmpty(part)) continue;
                    accum += "/" + Uri.EscapeDataString(part);
                    sb.AppendLine("<span class='sep'>/</span><a href='" + accum + "/'>" + HtmlEncode(part) + "</a>");
                }
            }
            sb.AppendLine("</div>");

            // 容器
            sb.AppendLine("<div class='container'>");

            // 搜索栏
            sb.AppendLine("<div class='search-bar'>");
            sb.AppendLine("<input type='text' id='searchInput' placeholder='搜索文件...' autocomplete='off'>");
            sb.AppendLine("<button class='clear-btn' onclick='doClearSearch()'>清除</button>");
            sb.AppendLine("</div>");

            // 工具栏
            sb.AppendLine("<div class='toolbar'>");
            sb.AppendLine("<div class='toolbar-left'>");
            sb.AppendLine("<span class='count-label' id='itemCount'></span>");
            sb.AppendLine("</div>");
            sb.AppendLine("<div class='toolbar-right'>");
            if (showUpload)
                sb.AppendLine("<button class='btn btn-primary' id='uploadBtn' onclick='openUploadModal()'>📤 上传文件</button>");
            sb.AppendLine("</div>");
            sb.AppendLine("</div>");

            // 右键菜单
            sb.AppendLine("<div class='ctx-menu' id='ctxMenu'>");
            sb.AppendLine("<div class='ctx-item' id='ctxOpen'>📂 打开</div>");
            sb.AppendLine("<div class='ctx-item' id='ctxPreview'>🔍 预览</div>");
            sb.AppendLine("<div class='ctx-item' id='ctxDownload'>📥 下载</div>");
            if (showZip)
                sb.AppendLine("<div class='ctx-item' id='ctxZip'>📦 打包ZIP</div>");
            if (showRename)
            {
                sb.AppendLine("<div class='ctx-sep'></div>");
                sb.AppendLine("<div class='ctx-item' id='ctxRename'>✏️ 重命名</div>");
            }
            if (showMove)
            {
                sb.AppendLine("<div class='ctx-item' id='ctxMove'>📁 移动到...</div>");
            }
            if (showDelete)
            {
                sb.AppendLine("<div class='ctx-sep'></div>");
                sb.AppendLine("<div class='ctx-item danger' id='ctxDelete'>❌ 删除</div>");
            }
            sb.AppendLine("</div>");

            // 文件列表
            sb.AppendLine("<div class='file-list' id='fileList'>");

            try
            {
                var di = new DirectoryInfo(currentDir);
                var dirs = di.GetDirectories();
                var files = di.GetFiles();

                if (dirs.Length == 0 && files.Length == 0)
                    sb.AppendLine("<div class='empty-state'><div class='icon'>📭</div><p>此文件夹为空</p></div>");

                foreach (var dir in dirs)
                {
                    string encodedName = Uri.EscapeDataString(dir.Name);
                    sb.AppendLine("<div class='file-row' data-name='" + HtmlEncode(dir.Name) +
                        "' data-href='" + urlPrefix + encodedName + "/' data-isdir='1'>");
                    sb.AppendLine("<div class='file-icon icon-folder'>📁</div>");
                    sb.AppendLine("<div class='file-info'><div class='file-name'><a href='" + urlPrefix + encodedName + "/'>" +
                        HtmlEncode(dir.Name) + "</a></div><div class='file-meta'>" +
                        dir.LastWriteTime.ToString("yyyy-MM-dd HH:mm") + "</div></div>");
                    sb.AppendLine("</div>");
                }

                foreach (var file in files)
                {
                    string iconClass = GetFileIconClass(file.Extension);
                    string iconEmoji = GetFileEmoji(file.Extension);
                    string iconHtml = iconEmoji;
                    string encodedName = Uri.EscapeDataString(file.Name);
                    var previewExts = config.GetPreviewExtensions();
                    bool canPreview = previewExts.Contains(file.Extension.ToLowerInvariant());
                    string previewAttr = canPreview ? " data-preview='1'" : "";
                    sb.AppendLine("<div class='file-row' data-name='" + HtmlEncode(file.Name) +
                        "' data-href='" + urlPrefix + encodedName + "' data-isdir='0' data-size='" + file.Length + "'" + previewAttr + ">");
                    sb.AppendLine("<div class='file-icon " + iconClass + "'>" + iconHtml + "</div>");
                    sb.AppendLine("<div class='file-info'><div class='file-name'><a href='" + urlPrefix + encodedName + "'>" +
                        HtmlEncode(file.Name) + "</a></div><div class='file-meta'>" +
                        Utility.FormatBytes(file.Length) + " &middot; " +
                        file.LastWriteTime.ToString("yyyy-MM-dd HH:mm") + "</div></div>");
                    sb.AppendLine("</div>");
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine("<div class='empty-state'><p>读取目录失败: " + HtmlEncode(ex.Message) + "</p></div>");
            }

            sb.AppendLine("</div>"); // file-list

            sb.AppendLine("</div>"); // container

            // 上传 modal
            if (showUpload)
            {
                sb.AppendLine("<div class='modal-overlay' id='uploadModal'>");
                sb.AppendLine("<div class='modal'>");
                sb.AppendLine("<h3>📤 上传文件</h3>");
                sb.AppendLine("<form method='post' enctype='multipart/form-data' id='uploadForm'>");
                sb.AppendLine("<div class='drop-zone' id='dropZone' onclick='document.getElementById(\"fileInput\").click()'>");
                sb.AppendLine("<p>📂 点击或拖拽文件到此区域上传</p>");
                sb.AppendLine("<input type='file' name='file' id='fileInput' multiple>");
                sb.AppendLine("</div>");
                sb.AppendLine("<div class='progress-bar-wrap' id='progressWrap'><div class='progress-bar' id='progressBar'></div></div>");
                sb.AppendLine("<div class='progress-text' id='progressText'></div>");
                sb.AppendLine("</form>");
                sb.AppendLine("<div class='modal-actions'>");
                sb.AppendLine("<button class='btn btn-outline' onclick='closeUploadModal()'>关闭</button>");
                sb.AppendLine("</div>");
                sb.AppendLine("</div>");
                sb.AppendLine("</div>");
            }

            // 重命名弹窗
            if (showRename)
            {
                sb.AppendLine("<div class='modal-overlay' id='renameModal'>");
                sb.AppendLine("<div class='modal'>");
                sb.AppendLine("<h3>✏️ 重命名</h3>");
                sb.AppendLine("<input type='text' id='renameInput' placeholder='请输入新名称'>");
                sb.AppendLine("<div class='modal-actions'>");
                sb.AppendLine("<button class='btn btn-outline' onclick='closeRenameModal()'>取消</button>");
                sb.AppendLine("<button class='btn btn-primary' onclick='doRename()'>确定</button>");
                sb.AppendLine("</div>");
                sb.AppendLine("</div>");
                sb.AppendLine("</div>");
            }

            // 移动弹窗
            if (showMove)
            {
                sb.AppendLine("<div class='modal-overlay' id='moveModal'>");
                sb.AppendLine("<div class='modal' style='width:480px;text-align:left'>");
                sb.AppendLine("<h3 style='text-align:center'>📁 移动到</h3>");
                sb.AppendLine("<div id='moveSelectedPath' style='font-size:13px;color:#4285F4;margin-bottom:10px;padding:8px 12px;background:#E3F0FF;border-radius:6px;word-break:break-all'>当前选择: /</div>");
                sb.AppendLine("<div id='dirTree' style='max-height:300px;overflow-y:auto;border:1px solid #e0e0e0;border-radius:8px;padding:8px 0'>");
                sb.AppendLine("<div class='dir-item selected' data-path='/' onclick='selectDir(this)'><span class='arrow' onclick='event.stopPropagation();toggleRootDir()'>▶</span> 📁 根目录</div>");
                sb.AppendLine("<div class='dir-children open' id='rootChildren'></div>");
                sb.AppendLine("</div>");
                sb.AppendLine("<div class='modal-actions' style='margin-top:14px'>");
                sb.AppendLine("<button class='btn btn-outline' onclick='closeMoveModal()'>取消</button>");
                sb.AppendLine("<button class='btn btn-primary' onclick='doMove()'>确定</button>");
                sb.AppendLine("</div>");
                sb.AppendLine("</div>");
                sb.AppendLine("</div>");
            }

            // 预览弹窗
            sb.AppendLine("<div class='modal-overlay' id='previewModal' onclick='if(event.target===this)closePreview()'>");
            sb.AppendLine("<div style='background:#fff;border-radius:12px;padding:0;width:90vw;max-width:1024px;max-height:90vh;display:flex;flex-direction:column;box-shadow:0 8px 32px rgba(0,0,0,.2)'>");
            sb.AppendLine("<div style='display:flex;align-items:center;justify-content:space-between;padding:14px 20px;border-bottom:1px solid #f0f0f0;flex-shrink:0'>");
            sb.AppendLine("<div style='font-size:15px;font-weight:600;color:#333;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;flex:1;margin-right:12px' id='previewTitle'>预览</div>");
            sb.AppendLine("<div style='display:flex;gap:8px;flex-shrink:0'>");
            sb.AppendLine("<a id='previewDownload' href='#' download style='display:inline-flex;align-items:center;gap:6px;height:34px;padding:0 16px;border-radius:6px;font-size:13px;font-weight:500;cursor:pointer;border:none;text-decoration:none;background:#4285F4;color:#fff;transition:background .15s'>📥 下载</a>");
            sb.AppendLine("<button onclick='closePreview()' style='width:34px;height:34px;border-radius:6px;border:1px solid #e0e0e0;background:#fff;cursor:pointer;font-size:18px;display:flex;align-items:center;justify-content:center;color:#666'>✕</button>");
            sb.AppendLine("</div></div>");
            sb.AppendLine("<div id='previewContent' style='flex:1;overflow:auto;padding:20px;display:flex;align-items:center;justify-content:center;min-height:200px'></div>");
            sb.AppendLine("</div></div>");

            // 提示组件
            sb.AppendLine("<div id='toast'></div>");

            // 脚本
            sb.AppendLine("<script>");
            // 更新条目计数
            sb.AppendLine("function updateCount(){var rows=document.querySelectorAll('.file-row');var visible=document.querySelectorAll('.file-row:not(.hidden)');var el=document.getElementById('itemCount');if(el)el.textContent=visible.length+' / '+rows.length+' items'}updateCount();");

            // 会话超时
            if (timeoutMin > 0)
            {
                sb.AppendLine("var SESSION_MS=" + (timeoutMin * 60 * 1000) + ";var sessionStart=Date.now();");
                sb.AppendLine("function checkTimeout(){if(Date.now()-sessionStart>=SESSION_MS){fetch('/_logout',{method:'POST'}).then(function(){location.reload()}).catch(function(){location.reload()});return}var rem=SESSION_MS-(Date.now()-sessionStart);var m=Math.floor(rem/60000);var s=Math.floor((rem%60000)/1000);var el=document.getElementById('timeoutInfo');if(el)el.textContent=' Expires in '+m+':'+(s<10?'0':'')+s}function resetTimeout(){sessionStart=Date.now();checkTimeout()}document.addEventListener('click',resetTimeout);setInterval(checkTimeout,10000);checkTimeout();");
            }

            // 提示组件
            sb.AppendLine("var toastTimer=null;function showToast(msg,type){var t=document.getElementById('toast');t.textContent=msg;t.className='show '+type;if(toastTimer)clearTimeout(toastTimer);toastTimer=setTimeout(function(){t.classList.remove('show')},3000)}");

            // 预览
            sb.AppendLine("function getPreviewType(name){var ext=name.split('.').pop().toLowerCase();var imgs=['png','jpg','jpeg','gif','svg','bmp','webp'];var texts=['txt','log','md','csv','css','js','json','xml','ini','c','cpp','h','cs','py','java','go','rs','ts','yaml','yml','sh','bat','ps1','html','htm'];var videos=['mp4','webm'];var audios=['mp3','wav','ogg','flac'];var offices=['docx','doc','xlsx','xls','pptx','ppt'];if(imgs.indexOf(ext)>=0)return'img';if(texts.indexOf(ext)>=0)return'text';if(ext==='pdf')return'pdf';if(videos.indexOf(ext)>=0)return'video';if(audios.indexOf(ext)>=0)return'audio';if(offices.indexOf(ext)>=0)return'office';return''}");
            sb.AppendLine("function openPreview(href,name){var t=getPreviewType(name);if(!t){window.location.href=href;return}var c=document.getElementById('previewContent');var dl=document.getElementById('previewDownload');document.getElementById('previewTitle').textContent=name;dl.href=href;c.innerHTML='<div style=\"color:#999\">加载中...</div>';if(t==='img'){c.innerHTML='<img src=\"'+href+'\" style=\"max-width:100%;max-height:70vh;object-fit:contain;border-radius:6px\"/>'}else if(t==='text'){fetch(href).then(function(r){return r.arrayBuffer()}).then(function(buf){var enc=new TextDecoder('utf-8',{fatal:false});var txt=enc.decode(buf);if(txt.indexOf('\\ufffd')>=0){try{txt=new TextDecoder('gbk',{fatal:false}).decode(buf)}catch(e){}}c.innerHTML='<pre style=\"margin:0;white-space:pre-wrap;word-break:break-all;font-size:13px;line-height:1.6;color:#333;background:#fafafa;padding:16px;border-radius:6px;width:100%;max-height:65vh;overflow:auto\">'+txt.replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;')+'</pre>'}).catch(function(){c.innerHTML='<div style=\"color:#EF5350\">加载失败</div>'})}else if(t==='pdf'){c.innerHTML='<iframe src=\"'+href+'\" style=\"width:100%;height:65vh;border:none;border-radius:6px\"></iframe>'}else if(t==='video'){c.innerHTML='<video src=\"'+href+'\" controls style=\"max-width:100%;max-height:65vh;border-radius:6px\"></video>'}else if(t==='audio'){c.innerHTML='<audio src=\"'+href+'\" controls style=\"width:100%\"></audio>'}else if(t==='office'){c.innerHTML='<iframe src=\"/_preview?path='+encodeURIComponent(href)+'\" style=\"width:100%;height:65vh;border:none;border-radius:6px\"></iframe>'}document.getElementById('previewModal').classList.add('show')}");
            sb.AppendLine("function closePreview(){document.getElementById('previewModal').classList.remove('show');document.getElementById('previewContent').innerHTML=''}");

            // 双击：预览或下载
            sb.AppendLine("document.querySelectorAll('.file-row').forEach(function(r){if(r.dataset.isdir==='0'){r.addEventListener('dblclick',function(e){e.preventDefault();var href=r.dataset.href;var name=r.dataset.name;if(r.dataset.preview==='1'){openPreview(href,name)}else{window.location.href=href}})}});");

            // 退出登录
            sb.AppendLine("document.getElementById('logoutBtn').addEventListener('click',function(){fetch('/_logout',{method:'POST'}).then(function(){location.reload()}).catch(function(){location.reload()})});");

            // 上传弹窗
            if (showUpload)
            {
                sb.AppendLine("function openUploadModal(){document.getElementById('uploadModal').classList.add('show')}");
                sb.AppendLine("function closeUploadModal(){document.getElementById('uploadModal').classList.remove('show');var pw=document.getElementById('progressWrap');if(pw)pw.classList.remove('show');var pb=document.getElementById('progressBar');if(pb)pb.style.width='0%';var pt=document.getElementById('progressText');if(pt)pt.textContent='';var fi=document.getElementById('fileInput');if(fi)fi.value=''}");
            }

            // 重命名弹窗
            if (showRename)
            {
                sb.AppendLine("var renameTarget=null;");
                sb.AppendLine("function openRenameModal(name){renameTarget=name;document.getElementById('renameInput').value=name;document.getElementById('renameModal').classList.add('show');setTimeout(function(){document.getElementById('renameInput').select()},100)}");
                sb.AppendLine("function closeRenameModal(){document.getElementById('renameModal').classList.remove('show');renameTarget=null}");
                sb.AppendLine("function doRename(){if(!renameTarget)return;var newName=document.getElementById('renameInput').value.trim();if(!newName){showToast('请输入新名称','error');return}if(newName===renameTarget){closeRenameModal();return}fetch(ctxRow.dataset.href,{method:'RENAME',headers:{'Content-Type':'application/x-www-form-urlencoded'},body:'name='+encodeURIComponent(newName)}).then(function(r){return r.json()}).then(function(d){if(d.ok){showToast('重命名成功','success');closeRenameModal();setTimeout(function(){location.reload()},500)}else{showToast(d.error||'重命名失败','error')}}).catch(function(){showToast('重命名失败','error')})}");
            }

            // 移动弹窗
            if (showMove)
            {
                sb.AppendLine("var moveTarget=null,moveDest='/';");
                sb.AppendLine("function openMoveModal(name){moveTarget=name;moveDest='/';document.getElementById('moveModal').classList.add('show');document.getElementById('moveSelectedPath').textContent='当前选择: /';var rc=document.getElementById('rootChildren');rc.innerHTML='';rc.classList.add('open');loadDirTree('/',rc)}");
                sb.AppendLine("function toggleRootDir(){var rc=document.getElementById('rootChildren');var arrow=document.querySelector('#dirTree>.dir-item>.arrow');if(arrow)arrow.classList.toggle('open');if(rc.classList.contains('open')){rc.classList.remove('open');return}rc.classList.add('open');if(rc.children.length===0)loadDirTree('/',rc)}");
                sb.AppendLine("function closeMoveModal(){document.getElementById('moveModal').classList.remove('show');moveTarget=null;moveDest='/'}");
                sb.AppendLine("function selectDir(el){document.querySelectorAll('.dir-item.selected').forEach(function(i){i.classList.remove('selected')});el.classList.add('selected');moveDest=el.dataset.path;document.getElementById('moveSelectedPath').textContent='当前选择: '+moveDest}");
                sb.AppendLine("function toggleDir(el,path,children){var arrow=el.querySelector('.arrow');if(arrow)arrow.classList.toggle('open');if(children.classList.contains('open')){children.classList.remove('open');return}children.classList.add('open');if(children.children.length===0)loadDirTree(path,children)}");
                sb.AppendLine("function loadDirTree(path,container){fetch('/_dirs?path='+encodeURIComponent(path)).then(function(r){return r.json()}).then(function(d){if(!d.ok||!d.dirs)return;container.innerHTML='';d.dirs.forEach(function(dir){var item=document.createElement('div');item.className='dir-item';item.dataset.path=dir.path;item.innerHTML='<span class=\"arrow\">▶</span> 📁 '+dir.name.replace(/</g,'&lt;');item.addEventListener('click',function(e){e.stopPropagation();selectDir(item)});var arrow=item.querySelector('.arrow');arrow.addEventListener('click',function(e){e.stopPropagation();var ch=container.querySelector('[data-parent=\"'+dir.path+'\"]');if(!ch){ch=document.createElement('div');ch.className='dir-children';ch.dataset.parent=dir.path;container.appendChild(ch)}toggleDir(item,dir.path,ch)});container.appendChild(item)})}).catch(function(){})}");
                sb.AppendLine("function doMove(){if(!moveTarget)return;fetch(ctxRow.dataset.href,{method:'MOVE',headers:{'Content-Type':'application/x-www-form-urlencoded'},body:'destination='+encodeURIComponent(moveDest)}).then(function(r){return r.json()}).then(function(d){if(d.ok){showToast('移动成功','success');closeMoveModal();setTimeout(function(){location.reload()},500)}else{showToast(d.error||'移动失败','error')}}).catch(function(){showToast('移动失败','error')})}");
            }

            // 右键菜单
            sb.AppendLine("var ctx=document.getElementById('ctxMenu'),ctxRow=null;var ctxOpen=document.getElementById('ctxOpen');var ctxPreview=document.getElementById('ctxPreview');");
            sb.AppendLine("document.querySelectorAll('.file-row').forEach(function(r){r.addEventListener('contextmenu',function(e){e.preventDefault();ctxRow=r;ctx.style.left=e.clientX+'px';ctx.style.top=e.clientY+'px';ctx.classList.add('show');var zi=document.getElementById('ctxZip');if(zi)zi.style.display=r.dataset.isdir==='1'?'flex':'none';if(ctxOpen)ctxOpen.style.display=r.dataset.isdir==='1'?'flex':'none';if(ctxPreview)ctxPreview.style.display=r.dataset.preview==='1'?'flex':'none'})});");
            sb.AppendLine("document.addEventListener('click',function(){ctx.classList.remove('show')});");
            sb.AppendLine("if(ctxOpen)ctxOpen.addEventListener('click',function(){if(ctxRow){if(ctxRow.dataset.isdir==='1'){window.location.href=ctxRow.dataset.href}else if(ctxRow.dataset.preview==='1'){openPreview(ctxRow.dataset.href,ctxRow.dataset.name)}else{window.location.href=ctxRow.dataset.href}}ctx.classList.remove('show')});");
            sb.AppendLine("if(ctxPreview)ctxPreview.addEventListener('click',function(){if(ctxRow&&ctxRow.dataset.preview==='1'){openPreview(ctxRow.dataset.href,ctxRow.dataset.name)}ctx.classList.remove('show')});");
            sb.AppendLine("document.getElementById('ctxDownload').addEventListener('click',function(){if(ctxRow)window.location.href=ctxRow.dataset.href;ctx.classList.remove('show')});");
            if (showZip)
                sb.AppendLine("document.getElementById('ctxZip').addEventListener('click',function(){if(ctxRow&&ctxRow.dataset.isdir==='1'){if(confirm('确认打包此目录为ZIP？')){fetch(ctxRow.dataset.href.replace(/\\/$/,'')+'/_zip').then(function(r){return r.json()}).then(function(d){if(d.ok){showToast('打包成功: '+d.file,'success');setTimeout(function(){location.reload()},1000)}else{showToast(d.error||'打包失败','error')}}).catch(function(){showToast('打包失败','error')})}}ctx.classList.remove('show')});");
            if (showRename)
            {
                sb.AppendLine("document.getElementById('ctxRename').addEventListener('click',function(){if(ctxRow){openRenameModal(ctxRow.dataset.name)}ctx.classList.remove('show')});");
            }
            if (showMove)
            {
                sb.AppendLine("document.getElementById('ctxMove').addEventListener('click',function(){if(ctxRow){openMoveModal(ctxRow.dataset.name)}ctx.classList.remove('show')});");
            }
            if (showDelete)
            {
                sb.AppendLine("document.getElementById('ctxDelete').addEventListener('click',function(){if(ctxRow){var n=ctxRow.dataset.name;if(confirm('确认删除: '+n+' ? 此操作不可撤销。')){fetch(ctxRow.dataset.href,{method:'DELETE'}).then(function(r){if(r.ok){showToast('已删除: '+n,'success');setTimeout(function(){location.reload()},500)}else{showToast('删除失败','error')}}).catch(function(){showToast('删除失败','error')})}}ctx.classList.remove('show')});");
            }

            // 搜索过滤
            sb.AppendLine("var searchTimer=null;document.getElementById('searchInput').addEventListener('input',function(){var q=this.value.toLowerCase();clearTimeout(searchTimer);searchTimer=setTimeout(function(){var rows=document.querySelectorAll('.file-row');rows.forEach(function(r){var name=(r.dataset.name||'').toLowerCase();r.classList.toggle('hidden',q.length>0&&name.indexOf(q)===-1)});updateCount()},150)});");
            sb.AppendLine("function doClearSearch(){document.getElementById('searchInput').value='';document.querySelectorAll('.file-row').forEach(function(r){r.classList.remove('hidden')});updateCount()}");

            // 上传进度条
            sb.AppendLine("var dz=document.getElementById('dropZone'),fi=document.getElementById('fileInput'),fm=document.getElementById('uploadForm');");
            sb.AppendLine("if(fi)fi.addEventListener('change',function(){if(fi.files.length>0)uploadFiles(fi.files)});");
            sb.AppendLine("if(dz){dz.addEventListener('dragover',function(e){e.preventDefault();dz.classList.add('dragover')});dz.addEventListener('dragleave',function(){dz.classList.remove('dragover')});dz.addEventListener('drop',function(e){e.preventDefault();dz.classList.remove('dragover');uploadFiles(e.dataTransfer.files)})};");
            sb.AppendLine("function uploadFiles(files){if(files.length===0)return;var total=files.length;var done=0;var pw=document.getElementById('progressWrap');var pb=document.getElementById('progressBar');var pt=document.getElementById('progressText');pw.classList.add('show');var uploadUrl='" + urlPrefix + "';Array.from(files).forEach(function(file,idx){var xhr=new XMLHttpRequest();var form=new FormData();form.append('file',file);xhr.upload.onprogress=function(e){if(e.lengthComputable){var pct=Math.round((done/e.total+e.loaded/e.total*idx)*100);pb.style.width=pct+'%';pt.textContent='上传中: '+file.name+' ('+pct+'%)'}};xhr.onload=function(){done++;pb.style.width=Math.round(done/total*100)+'%';pt.textContent=done+'/'+total+' 已上传';if(done===total){setTimeout(function(){pw.classList.remove('show');pb.style.width='0%';pt.textContent='';showToast('全部上传完成','success');setTimeout(function(){location.reload()},500)},500)}};xhr.onerror=function(){done++;showToast('上传失败: '+file.name,'error')};xhr.open('POST',uploadUrl);xhr.send(form)})}");

            sb.AppendLine("</script>");
            // 超时倒计时（页面底部，低调显示）
            if (timeoutMin > 0)
            {
                sb.AppendLine("<div id='timeoutInfo' style='position:fixed;bottom:8px;right:12px;font-size:11px;color:#bbb;z-index:50;pointer-events:none'></div>");
            }
            sb.AppendLine("</body></html>");

            byte[] html = Encoding.UTF8.GetBytes(sb.ToString());
            response.ContentType = "text/html; charset=utf-8";
            response.ContentLength64 = html.Length;
            response.OutputStream.Write(html, 0, html.Length);
            Interlocked.Add(ref _totalBytes, html.Length);
        }

    
        // 提供文件（支持 Range 断点续传）
        private void ServeFile(HttpListenerRequest request, HttpListenerResponse response,
            string baseDir, string urlPath, UserInfo user, string clientIp, AppConfig config)
        {
            if (!TryResolveSafePath(baseDir, urlPath, out string fullPath))
            {
                Logger.Warn("403 ServeFile: " + urlPath);
                WriteJson(response, 403, "{\"ok\":false,\"error\":\"禁止访问\"}");
                return;
            }

            if (File.Exists(fullPath))
            {
                var fi = new FileInfo(fullPath);
                long fileSize = fi.Length;
                string ext = (fi.Extension ?? "").ToLowerInvariant();

                // 判断是内联预览还是附件下载
                bool inlinePreview = IsInlinePreviewType(ext);
                string encodedName = Uri.EscapeDataString(fi.Name);
                string disposition = inlinePreview
                    ? "inline; filename=\"" + encodedName + "\"; filename*=UTF-8''" + encodedName
                    : "attachment; filename=\"" + encodedName + "\"; filename*=UTF-8''" + encodedName;

                response.AppendHeader("Content-Disposition", disposition);
                response.ContentType = GetMimeType(ext);

                // Range 请求支持（断点续传）
                string rangeHeader = request.Headers["Range"];
                if (!string.IsNullOrEmpty(rangeHeader) && rangeHeader.StartsWith("bytes="))
                {
                    string rangeValue = rangeHeader.Substring(6);
                    long rangeStart = 0;
                    long rangeEnd = fileSize - 1;

                    string[] parts = rangeValue.Split('-');
                    if (parts.Length >= 1 && !string.IsNullOrEmpty(parts[0]))
                        rangeStart = long.Parse(parts[0]);
                    if (parts.Length >= 2 && !string.IsNullOrEmpty(parts[1]))
                        rangeEnd = long.Parse(parts[1]);

                    if (rangeStart >= fileSize) rangeStart = fileSize - 1;
                    if (rangeEnd >= fileSize) rangeEnd = fileSize - 1;
                    if (rangeStart > rangeEnd) rangeStart = rangeEnd;

                    long contentLength = rangeEnd - rangeStart + 1;
                    response.StatusCode = 206;
                    response.AddHeader("Content-Range", "bytes " + rangeStart + "-" + rangeEnd + "/" + fileSize);
                    response.ContentLength64 = contentLength;

                    using (var fs = fi.OpenRead())
                    {
                        fs.Position = rangeStart;
                        var buffer = new byte[81920];
                        long remaining = contentLength;
                        while (remaining > 0)
                        {
                            int toRead = (int)Math.Min(buffer.Length, remaining);
                            int read = fs.Read(buffer, 0, toRead);
                            if (read == 0) break;
                            response.OutputStream.Write(buffer, 0, read);
                            remaining -= read;
                        }
                    }
                }
                else
                {
                    // 整文件下载
                    response.ContentLength64 = fileSize;
                    using (var fs = fi.OpenRead())
                        fs.CopyTo(response.OutputStream);
                }

                Interlocked.Add(ref _totalBytes, response.ContentLength64);
            }
            else if (Directory.Exists(fullPath))
            {
                ServeDirectory(response, baseDir, fullPath, user, config);
            }
            else
            {
                Logger.Warn("404 ServeFile: " + urlPath + " => " + fullPath);
                WriteJson(response, 404, "{\"ok\":false,\"error\":\"未找到\"}");
            }
        }

    
        // 判断文件类型是否支持内联预览
        private static bool IsInlinePreviewType(string ext)
        {
            // 图片类型：浏览器可直接显示
            if (ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".gif"
                || ext == ".svg" || ext == ".bmp" || ext == ".webp")
                return true;

            // PDF：浏览器可直接显示
            if (ext == ".pdf")
                return true;

            // 文本类型：浏览器可直接显示
            if (ext == ".txt" || ext == ".log" || ext == ".md" || ext == ".csv"
                || ext == ".css" || ext == ".js" || ext == ".json" || ext == ".xml"
                || ext == ".ini" || ext == ".html" || ext == ".htm" || ext == ".c" || ext == ".cpp"
                || ext == ".h" || ext == ".cs" || ext == ".py" || ext == ".java"
                || ext == ".go" || ext == ".rs" || ext == ".ts" || ext == ".yaml"
                || ext == ".yml" || ext == ".sh" || ext == ".bat" || ext == ".ps1")
                return true;

            return false;
        }

    
        // 打包目录为 ZIP（使用 7z）
        private void ServeZip(HttpListenerResponse response, string baseDir, string urlPath)
        {
            string zipDir = baseDir;

            if (urlPath.Length > 1)
            {
                if (!TryResolveSafePath(baseDir, urlPath, out string resolved))
                {
                    WriteJson(response, 403, "{\"ok\":false,\"error\":\"禁止访问\"}");
                    return;
                }
                zipDir = resolved;
            }

            if (!Directory.Exists(zipDir))
            {
                WriteJson(response, 404, "{\"ok\":false,\"error\":\"目录不存在\"}");
                return;
            }

            // 查找 7z 可执行文件：先检查程序目录，再检查安装目录
            string sevenZ = Find7z();
            if (sevenZ == null)
            {
                WriteJson(response, 500, "{\"ok\":false,\"error\":\"未找到7z.exe，请将其放在程序同目录下\"}");
                return;
            }

            string zipFileName = Path.GetFileName(zipDir.TrimEnd(Path.DirectorySeparatorChar)) + ".zip";
            string zipOutputPath = Path.Combine(
                Path.GetDirectoryName(zipDir.TrimEnd(Path.DirectorySeparatorChar)),
                zipFileName);

            // 如果目标 zip 已存在则先删除
            if (File.Exists(zipOutputPath))
            {
                try { File.Delete(zipOutputPath); }
                catch
                {
                    WriteJson(response, 500, "{\"ok\":false,\"error\":\"ZIP文件已存在，无法覆盖\"}");
                    return;
                }
            }

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = sevenZ,
                    Arguments = "a -tzip -mx=5 \"" + zipOutputPath + "\" \"" + zipDir.TrimEnd(Path.DirectorySeparatorChar) + "\"",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using (var proc = Process.Start(psi))
                {
                    proc.WaitForExit(300000); // 5分钟超时
                    if (!proc.HasExited)
                    {
                        proc.Kill();
                        WriteJson(response, 500, "{\"ok\":false,\"error\":\"7z打包超时\"}");
                        return;
                    }

                    if (proc.ExitCode != 0)
                    {
                        Logger.Error("7z exit code: " + proc.ExitCode);
                        WriteJson(response, 500, "{\"ok\":false,\"error\":\"7z打包失败 (退出码 " + proc.ExitCode + ")\"}");
                        return;
                    }
                }

                if (!File.Exists(zipOutputPath))
                {
                    WriteJson(response, 500, "{\"ok\":false,\"error\":\"7z完成但ZIP文件未找到\"}");
                    return;
                }

                Logger.Info("7z created: " + zipOutputPath);
                WriteJson(response, 200, "{\"ok\":true,\"file\":\"" + HtmlEncode(zipFileName) + "\"}");
            }
            catch (Exception ex)
            {
                Logger.Error("7z error: " + ex.Message);
                WriteJson(response, 500, "{\"ok\":false,\"error\":\"" + HtmlEncode(ex.Message) + "\"}");
            }
        }

        // Office 文件预览：后端转 PDF 后返回
        private void HandlePreview(HttpListenerRequest request, HttpListenerResponse response, AppConfig config, UserInfo user)
        {
            string queryPath = request.QueryString["path"] ?? "";
            string baseDir = config.GetUserShareDir(user);

            if (string.IsNullOrEmpty(queryPath) || queryPath == "/")
            {
                WriteJson(response, 400, "{\"ok\":false,\"error\":\"缺少文件路径\"}");
                return;
            }

            if (!TryResolveSafePath(baseDir, queryPath, out string fullPath))
            {
                WriteJson(response, 403, "{\"ok\":false,\"error\":\"禁止访问\"}");
                return;
            }

            if (!File.Exists(fullPath))
            {
                WriteJson(response, 404, "{\"ok\":false,\"error\":\"文件不存在\"}");
                return;
            }

            string ext = Path.GetExtension(fullPath).ToLowerInvariant();
            var previewExts = config.GetPreviewExtensions();

            // 检查该后缀是否启用预览
            if (!previewExts.Contains(ext))
            {
                response.StatusCode = 302;
                response.RedirectLocation = queryPath;
                return;
            }

            // 非 Office 文件：直接重定向到原文件（前端 iframe 加载）
            if (!OfficePreview.CanPreview(ext))
            {
                response.StatusCode = 302;
                response.RedirectLocation = queryPath;
                return;
            }

            // Office 文件：转 PDF
            string pdfPath = OfficePreview.ConvertToPdf(fullPath);
            if (pdfPath == null || !File.Exists(pdfPath))
            {
                WriteJson(response, 500, "{\"ok\":false,\"error\":\"文件预览转换失败\"}");
                return;
            }

            try
            {
                var fi = new FileInfo(pdfPath);
                response.StatusCode = 200;
                response.ContentType = "application/pdf";
                string pdfName = Path.GetFileNameWithoutExtension(fullPath) + ".pdf";
                byte[] nameBytes = System.Text.Encoding.UTF8.GetBytes(pdfName);
                string encodedName = Uri.EscapeDataString(pdfName);
                response.AddHeader("Content-Disposition", "inline; filename=\"" + encodedName + "\"; filename*=UTF-8''" + encodedName);
                response.ContentLength64 = fi.Length;
                using (var fs = fi.OpenRead())
                    fs.CopyTo(response.OutputStream);
            }
            catch (Exception ex)
            {
                Logger.Error("预览文件发送失败: " + ex.Message);
                WriteJson(response, 500, "{\"ok\":false,\"error\":\"预览失败\"}");
            }
        }
        // 查找 7z.exe：嵌入资源释放 > 程序目录 > 安装目录
        private static string Find7z()
        {
            // 1. 从嵌入资源释放到临时目录
            string temp7zDir = Path.Combine(Path.GetTempPath(), "SimpleHttpFileServer");
            string temp7z = Path.Combine(temp7zDir, "7z.exe");

            try
            {
                if (!Directory.Exists(temp7zDir))
                    Directory.CreateDirectory(temp7zDir);

                // 每次检查嵌入资源的版本，如果 exe 变了就重新释放
                var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                string resourceName = "SimpleHttpServer.7z.exe";
                using (var stream = assembly.GetManifestResourceStream(resourceName))
                {
                    if (stream != null)
                    {
                        // 用文件长度判断是否需要更新
                        bool needExtract = !File.Exists(temp7z) || new FileInfo(temp7z).Length != stream.Length;
                        if (needExtract)
                        {
                            using (var fs = File.Create(temp7z))
                                stream.CopyTo(fs);
                        }
                        if (File.Exists(temp7z))
                            return temp7z;
                    }
                }
            }
            catch { }

            // 2. 程序所在目录
            string appDir = AppDomain.CurrentDomain.BaseDirectory;
            string local7z = Path.Combine(appDir, "7z.exe");
            if (File.Exists(local7z)) return local7z;

            // 3. 安装目录
            string[] installPaths = new[]
            {
                @"C:\Program Files\7-Zip\7z.exe",
                @"C:\Program Files (x86)\7-Zip\7z.exe"
            };
            foreach (string p in installPaths)
                if (File.Exists(p)) return p;

            return null;
        }

    
        // 处理文件上传
        private void HandleUpload(HttpListenerRequest request, HttpListenerResponse response,
            string baseDir, string urlPath, UserInfo user, string clientIp)
        {
            if (!request.HasEntityBody)
            {
                WriteJson(response, 400, "{\"ok\":false,\"error\":\"请求体为空\"}");
                return;
            }

            // 上传大小限制（AppConfig.MaxUploadSize，单位字节，0=不限）
            var cfg = ConfigManager.Get();
            if (cfg.MaxUploadSize > 0 && request.ContentLength64 > cfg.MaxUploadSize)
            {
                WriteJson(response, 413, "{\"ok\":false,\"error\":\"文件过大\"}");
                return;
            }

            string uploadDir = baseDir;
            if (urlPath.Length > 1)
            {
                if (TryResolveSafePath(baseDir, urlPath, out string resolved))
                    uploadDir = resolved;
            }

            string boundary = GetBoundary(request.ContentType);
            if (boundary == null)
            {
                WriteJson(response, 400, "{\"ok\":false,\"error\":\"无效的multipart请求\"}");
                return;
            }

            try
            {
                byte[] bodyBytes;
                using (var ms = new MemoryStream())
                {
                    request.InputStream.CopyTo(ms);
                    bodyBytes = ms.ToArray();
                }

                var boundaryPrefix = Encoding.UTF8.GetBytes("--" + boundary);
                int nameStart = IndexOfBytes(bodyBytes, Encoding.UTF8.GetBytes("filename=\""), 0);
                if (nameStart >= 0)
                {
                    nameStart += 10;
                    int nameEnd = IndexOfBytes(bodyBytes, Encoding.UTF8.GetBytes("\""), nameStart);
                    string fileName = Path.GetFileName(
                        Encoding.UTF8.GetString(bodyBytes, nameStart, nameEnd - nameStart));

                    int dataStart = IndexOfBytes(bodyBytes, Encoding.UTF8.GetBytes("\r\n\r\n"), nameEnd);
                    if (dataStart >= 0)
                    {
                        dataStart += 4;
                        int dataEnd = IndexOfBytes(bodyBytes, boundaryPrefix, dataStart);
                        if (dataEnd < 0) dataEnd = bodyBytes.Length;
                        if (dataEnd >= 2 && bodyBytes[dataEnd - 2] == '\r' && bodyBytes[dataEnd - 1] == '\n')
                            dataEnd -= 2;

                        int fileLen = dataEnd - dataStart;
                        if (cfg.MaxUploadSize > 0 && fileLen > cfg.MaxUploadSize)
                        {
                            WriteJson(response, 413, "{\"ok\":false,\"error\":\"文件过大\"}");
                            return;
                        }

                        byte[] fileData = new byte[fileLen];
                        Array.Copy(bodyBytes, dataStart, fileData, 0, fileLen);

                        if (!Directory.Exists(uploadDir))
                            Directory.CreateDirectory(uploadDir);

                        string savedPath = Path.Combine(uploadDir, fileName);
                        File.WriteAllBytes(savedPath, fileData);
                        Logger.Info("Uploaded: " + fileName + " (" + fileLen + "B)");

                        byte[] html = Encoding.UTF8.GetBytes(
                            "<!DOCTYPE html><html lang='zh-CN'><head><meta charset='utf-8'>" +
                            "<meta name='viewport' content='width=device-width,initial-scale=1'>" +
                            "<style>*{margin:0;padding:0;box-sizing:border-box}body{font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,sans-serif;background:#f0f2f5;display:flex;justify-content:center;align-items:center;min-height:100vh}" +
                            ".card{background:#fff;border-radius:12px;padding:48px;text-align:center;box-shadow:0 4px 16px rgba(0,0,0,.08)}" +
                            ".card .icon{font-size:48px;margin-bottom:16px}.card h2{color:#333;margin-bottom:8px}.card p{color:#666;font-size:14px}" +
                            ".btn{display:inline-block;margin-top:20px;padding:10px 24px;background:#4285F4;color:#fff;border-radius:6px;text-decoration:none;font-size:14px;transition:background .2s}.btn:hover{background:#5A95F5}" +
                            "</style></head><body><div class='card'><div class='icon'>✅</div>" +
                            "<h2>上传成功</h2><p>文件已保存: " + HtmlEncode(fileName) + "</p>" +
                            "<a href='javascript:history.back()' class='btn'>← 返回</a></div></body></html>");
                        response.ContentType = "text/html; charset=utf-8";
                        response.ContentLength64 = html.Length;
                        response.OutputStream.Write(html, 0, html.Length);
                        return;
                    }
                }

                WriteJson(response, 400, "{\"ok\":false,\"error\":\"未找到文件\"}");
            }
            catch (Exception ex)
            {
                Logger.Error("上传失败: " + ex.Message);
                WriteJson(response, 500, "{\"ok\":false,\"error\":\"上传失败: " + HtmlEncode(ex.Message) + "\"}");
            }
        }

    
        // 处理文件/目录删除
        private void HandleDelete(HttpListenerResponse response, string baseDir,
            string urlPath, UserInfo user, string clientIp)
        {
            if (!TryResolveSafePath(baseDir, urlPath, out string fullPath))
            {
                WriteJson(response, 403, "{\"ok\":false,\"error\":\"禁止访问\"}");
                return;
            }

            try
            {
                if (File.Exists(fullPath))
                {
                    string fileName = Path.GetFileName(fullPath);
                    File.Delete(fullPath);
                    Logger.Info("Deleted file: " + fullPath);
                    WriteJson(response, 200, "{\"ok\":true}");
                }
                else if (Directory.Exists(fullPath))
                {
                    string dirName = Path.GetFileName(fullPath.TrimEnd(Path.DirectorySeparatorChar));
                    Directory.Delete(fullPath, true);
                    Logger.Info("Deleted directory: " + fullPath);
                    WriteJson(response, 200, "{\"ok\":true}");
                }
                else
                {
                    WriteJson(response, 404, "{\"ok\":false,\"error\":\"未找到\"}");
                }
            }
            catch (Exception ex)
            {
                Logger.Error("删除失败: " + ex.Message);
                WriteJson(response, 500, "{\"ok\":false,\"error\":\"删除失败: " + HtmlEncode(ex.Message) + "\"}");
            }
        }

    
        // 处理文件/目录移动（MOVE 方法）
        private void HandleMove(HttpListenerResponse response, string baseDir,
            string urlPath, HttpListenerRequest request, UserInfo user, string clientIp)
        {
            if (!TryResolveSafePath(baseDir, urlPath, out string fullPath))
            {
                WriteJson(response, 403, "{\"ok\":false,\"error\":\"禁止访问\"}");
                return;
            }

            if (!File.Exists(fullPath) && !Directory.Exists(fullPath))
            {
                WriteJson(response, 404, "{\"ok\":false,\"error\":\"未找到\"}");
                return;
            }

            try
            {
                byte[] bodyBytes;
                using (var ms = new MemoryStream())
                {
                    request.InputStream.CopyTo(ms);
                    bodyBytes = ms.ToArray();
                }
                string body = Encoding.UTF8.GetString(bodyBytes);

                string destPath = "";
                foreach (string pair in body.Split('&'))
                {
                    string[] kv = pair.Split('=');
                    if (kv.Length == 2 && kv[0] == "destination")
                        destPath = Uri.UnescapeDataString(kv[1].Replace('+', ' '));
                }

                if (string.IsNullOrWhiteSpace(destPath))
                {
                    WriteJson(response, 400, "{\"ok\":false,\"error\":\"缺少目标路径\"}");
                    return;
                }

                // destPath 是目标目录的 URL 路径，解析为完整文件系统路径
                string destOs = destPath.Replace('/', Path.DirectorySeparatorChar)
                    .TrimStart(Path.DirectorySeparatorChar);
                string destDir = Path.GetFullPath(Path.Combine(baseDir, destOs));

                // 安全检查
                string safeBase = Path.GetFullPath(baseDir)
                    .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!destDir.StartsWith(safeBase, StringComparison.OrdinalIgnoreCase) &&
                    !destDir.Equals(Path.GetFullPath(baseDir), StringComparison.OrdinalIgnoreCase))
                {
                    WriteJson(response, 403, "{\"ok\":false,\"error\":\"目标路径超出根目录\"}");
                    return;
                }

                // 目标目录必须存在
                if (!Directory.Exists(destDir))
                {
                    WriteJson(response, 400, "{\"ok\":false,\"error\":\"目标目录不存在\"}");
                    return;
                }

                // 拼接源文件/目录名到目标路径
                string srcName = File.Exists(fullPath)
                    ? Path.GetFileName(fullPath)
                    : Path.GetFileName(fullPath.TrimEnd(Path.DirectorySeparatorChar));
                string fullDest = Path.Combine(destDir, srcName);

                if (File.Exists(fullPath))
                    File.Move(fullPath, fullDest);
                else
                    Directory.Move(fullPath, fullDest);

                Logger.Info("Moved: " + fullPath + " -> " + fullDest);
                WriteJson(response, 200, "{\"ok\":true}");
            }
            catch (Exception ex)
            {
                Logger.Error("移动失败: " + ex.Message);
                WriteJson(response, 500, "{\"ok\":false,\"error\":\"移动失败: " + HtmlEncode(ex.Message) + "\"}");
            }
        }

    
        // 处理文件/目录重命名（RENAME 方法）
        private void HandleRename(HttpListenerResponse response, string baseDir,
            string urlPath, HttpListenerRequest request, UserInfo user, string clientIp)
        {
            if (!TryResolveSafePath(baseDir, urlPath, out string fullPath))
            {
                WriteJson(response, 403, "{\"ok\":false,\"error\":\"禁止访问\"}");
                return;
            }

            if (!File.Exists(fullPath) && !Directory.Exists(fullPath))
            {
                WriteJson(response, 404, "{\"ok\":false,\"error\":\"未找到\"}");
                return;
            }

            try
            {
                byte[] bodyBytes;
                using (var ms = new MemoryStream())
                {
                    request.InputStream.CopyTo(ms);
                    bodyBytes = ms.ToArray();
                }
                string body = Encoding.UTF8.GetString(bodyBytes);

                string newName = "";
                foreach (string pair in body.Split('&'))
                {
                    string[] kv = pair.Split('=');
                    if (kv.Length == 2 && kv[0] == "name")
                        newName = Uri.UnescapeDataString(kv[1].Replace('+', ' '));
                }

                if (string.IsNullOrWhiteSpace(newName))
                {
                    WriteJson(response, 400, "{\"ok\":false,\"error\":\"缺少新名称\"}");
                    return;
                }

                // 安全检查：不允许包含路径分隔符
                if (newName.Contains('/') || newName.Contains('\\') || newName.Contains(".."))
                {
                    WriteJson(response, 400, "{\"ok\":false,\"error\":\"名称无效\"}");
                    return;
                }

                string parentDir = Path.GetDirectoryName(fullPath);
                string newPath = Path.Combine(parentDir, newName);

                if (File.Exists(fullPath))
                    File.Move(fullPath, newPath);
                else
                    Directory.Move(fullPath, newPath);

                Logger.Info("Renamed: " + fullPath + " -> " + newPath);
                WriteJson(response, 200, "{\"ok\":true}");
            }
            catch (Exception ex)
            {
                Logger.Error("重命名失败: " + ex.Message);
                WriteJson(response, 500, "{\"ok\":false,\"error\":\"重命名失败: " + HtmlEncode(ex.Message) + "\"}");
            }
        }

    
        // 路径安全检查：防止 ../ 遍历攻击
        private static bool TryResolveSafePath(string baseDir, string urlPath, out string fullPath)
        {
            fullPath = null;
            try
            {
                string decoded = Uri.UnescapeDataString(urlPath);
                string osPath = decoded.Replace('/', Path.DirectorySeparatorChar)
                    .TrimStart(Path.DirectorySeparatorChar);
                string resolved = Path.GetFullPath(Path.Combine(baseDir, osPath));
                string safeBase = Path.GetFullPath(baseDir).TrimEnd(Path.DirectorySeparatorChar);

                // 允许 baseDir 本身及其子路径（兼容 C:\ 根目录）
                if (resolved.StartsWith(safeBase, StringComparison.OrdinalIgnoreCase))
                {
                    fullPath = resolved;
                    return true;
                }

                Logger.Warn("Path blocked (traversal): " + urlPath + " => " + resolved);
                return false;
            }
            catch (Exception ex)
            {
                Logger.Warn("Path resolve failed: " + urlPath + " - " + ex.Message);
                return false;
            }
        }

    
        // 解析 multipart boundary
        private static string GetBoundary(string contentType)
        {
            if (string.IsNullOrEmpty(contentType)) return null;
            foreach (var part in contentType.Split(';'))
            {
                string trimmed = part.Trim();
                if (trimmed.StartsWith("boundary=", StringComparison.OrdinalIgnoreCase))
                    return trimmed.Substring(9).Trim('"');
            }
            return null;
        }

    
        // 字节数组模式搜索
        private static int IndexOfBytes(byte[] source, byte[] pattern, int startIndex)
        {
            for (int i = startIndex; i <= source.Length - pattern.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < pattern.Length; j++)
                {
                    if (source[i + j] != pattern[j]) { match = false; break; }
                }
                if (match) return i;
            }
            return -1;
        }

    
        // MIME 类型映射
        private static string GetMimeType(string ext)
        {
            switch ((ext ?? "").ToLowerInvariant())
            {
                case ".txt": return "text/plain; charset=utf-8";
                case ".ini": return "text/plain; charset=utf-8";
                case ".html":
                case ".htm": return "text/html; charset=utf-8";
                case ".css": return "text/css; charset=utf-8";
                case ".js": return "application/javascript; charset=utf-8";
                case ".json": return "application/json; charset=utf-8";
                case ".xml": return "application/xml; charset=utf-8";
                case ".csv": return "text/csv; charset=utf-8";
                case ".md": return "text/markdown; charset=utf-8";
                case ".png": return "image/png";
                case ".jpg":
                case ".jpeg": return "image/jpeg";
                case ".gif": return "image/gif";
                case ".svg": return "image/svg+xml";
                case ".ico": return "image/x-icon";
                case ".webp": return "image/webp";
                case ".bmp": return "image/bmp";
                case ".pdf": return "application/pdf";
                case ".zip": return "application/zip";
                case ".gz": return "application/gzip";
                case ".mp4": return "video/mp4";
                case ".mp3": return "audio/mpeg";
                default: return "application/octet-stream";
            }
        }

    
        // HTML 实体编码
        private static string HtmlEncode(string value)
        {
            if (string.IsNullOrEmpty(value)) return value;
            return value.Replace("&", "&amp;").Replace("<", "&lt;")
                        .Replace(">", "&gt;").Replace("\"", "&quot;").Replace("'", "&#39;");
        }

    
        // 文件图标样式
        private static string GetFileIconClass(string ext)
        {
            switch ((ext ?? "").ToLowerInvariant())
            {
                case ".zip": case ".gz": case ".rar": case ".7z": return "icon-zip";
                case ".iso": return "icon-zip";
                case ".png": case ".jpg": case ".jpeg": case ".gif": case ".svg":
                case ".bmp": case ".webp": return "icon-img";
                case ".mp3": case ".wav": case ".flac": case ".ogg": case ".aac": return "icon-audio";
                case ".mp4": case ".avi": case ".mkv": case ".mov": case ".wmv": return "icon-video";
                case ".cs": case ".js": case ".py": case ".java": case ".html":
                case ".css": case ".json": case ".xml": case ".ts": case ".go":
                case ".rs": case ".cpp": case ".c": case ".h": return "icon-code";
                case ".txt": case ".log": case ".md": case ".csv": case ".ini":
                case ".yaml": case ".yml": case ".sh": case ".ps1": return "icon-text";
                case ".pdf": return "icon-pdf";
                case ".doc": case ".docx": return "icon-word";
                case ".xls": case ".xlsx": return "icon-excel";
                case ".ppt": case ".pptx": return "icon-ppt";
                case ".exe": case ".msi": return "icon-exe";
                case ".dll": case ".sys": return "icon-dll";
                case ".bat": case ".cmd": return "icon-script";
                default: return "icon-file";
            }
        }

    
        // 文件 Emoji 图标
        private static string GetFileEmoji(string ext)
        {
            switch ((ext ?? "").ToLowerInvariant())
            {
                case ".zip": case ".gz": case ".rar": case ".7z": return "📦";
                case ".iso": return "💿";
                case ".png": case ".jpg": case ".jpeg": case ".gif": case ".svg":
                case ".bmp": case ".webp": return "🖼️";
                case ".mp3": case ".wav": case ".flac": case ".ogg": case ".aac": return "🎵";
                case ".mp4": case ".avi": case ".mkv": case ".mov": case ".wmv": return "🎬";
                case ".cs": return "🔷";
                case ".js": return "🟨";
                case ".py": return "🐍";
                case ".java": return "☕";
                case ".html": case ".htm": return "🌐";
                case ".css": return "🎨";
                case ".json": return "📋";
                case ".xml": return "📰";
                case ".ts": return "🔹";
                case ".go": return "🔵";
                case ".rs": return "🦀";
                case ".pdf": return "📄";
                case ".txt": case ".log": case ".md": case ".ini": return "📝";
                case ".csv": return "📊";
                case ".doc": case ".docx": return "📋";
                case ".xls": case ".xlsx": return "📊";
                case ".ppt": case ".pptx": return "🖥";
                case ".exe": case ".msi": return "💻";
                case ".bat": case ".cmd": return "📟";
                case ".dll": case ".sys": return "🧩";
                default: return "📄";
            }
        }

        // 检查 IP 是否被封禁
        private static bool IsIpLocked(string ip)
        {
            if (!_loginAttempts.TryGetValue(ip, out LoginAttempt att)) return false;
            if (!att.LockUntil.HasValue) return false;
            if (DateTime.UtcNow < att.LockUntil.Value) return true;
            // 封禁过期，移除记录
            LoginAttempt expired;
            _loginAttempts.TryRemove(ip, out expired);
            return false;
        }

        // 记录失败尝试
        private static void RecordFailedAttempt(string ip)
        {
            var now = DateTime.UtcNow;
            var att = _loginAttempts.AddOrUpdate(ip,
                _ => new LoginAttempt { Count = 1, FirstFail = now },
                (_, existing) =>
                {
                    // 如果超出计数窗口则重置
                    if ((now - existing.FirstFail).TotalMinutes > AttemptWindowMinutes)
                    {
                        existing.Count = 1;
                        existing.FirstFail = now;
                        existing.LockUntil = null;
                    }
                    else
                    {
                        existing.Count++;
                    }
                    // 达到阈值则封禁
                    if (existing.Count >= MaxLoginAttempts)
                    {
                        existing.LockUntil = now.AddMinutes(LockoutMinutes);
                        Logger.Warn("Brute force blocked IP: " + ip);
                    }
                    return existing;
                });
            // 确保清理定时器已启动
            StartLoginCleanupTimer();
        }

        // 定期清理过期的封禁和失效记录
        private static void StartLoginCleanupTimer()
        {
            if (_timerStarted) return;
            lock (_loginTimerLock)
            {
                if (_timerStarted) return;
                _loginCleanupTimer = new Timer(_ =>
                {
                    var now = DateTime.UtcNow;
                    foreach (var kv in _loginAttempts)
                    {
                        if (kv.Value.LockUntil.HasValue && now >= kv.Value.LockUntil.Value)
                        {
                            LoginAttempt removed;
                            _loginAttempts.TryRemove(kv.Key, out removed);
                        }
                        else if (!kv.Value.LockUntil.HasValue &&
                                 (now - kv.Value.FirstFail).TotalMinutes > AttemptWindowMinutes + 5)
                        {
                            LoginAttempt removed;
                            _loginAttempts.TryRemove(kv.Key, out removed);
                        }
                    }
                }, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
                _timerStarted = true;
            }
        }

        // 获取当前被封禁的 IP 列表
        public static List<LockedIpInfo> GetLockedIps()
        {
            var now = DateTime.UtcNow;
            var result = new List<LockedIpInfo>();
            foreach (var kv in _loginAttempts)
            {
                if (kv.Value.LockUntil.HasValue && now < kv.Value.LockUntil.Value)
                {
                    result.Add(new LockedIpInfo
                    {
                        Ip = kv.Key,
                        FailCount = kv.Value.Count,
                        LockUntil = kv.Value.LockUntil.Value
                    });
                }
            }
            return result;
        }

        // 解封 IP
        public static void UnlockIp(string ip)
        {
            LoginAttempt removed;
            _loginAttempts.TryRemove(ip, out removed);
        }
    }

    // 被封禁 IP 信息
    public class LockedIpInfo
    {
        public string Ip { get; set; }
        public int FailCount { get; set; }
        public DateTime LockUntil { get; set; }
        public string LockTimeStr => LockUntil.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    }

    // 会话信息
    public class SessionInfo
    {
        public string UserName { get; }
        public DateTime ExpireTime { get; }

        public SessionInfo(string userName, DateTime expireTime)
        {
            UserName = userName;
            ExpireTime = expireTime;
        }
    }
}
