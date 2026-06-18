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

namespace SuperHttpFileServer
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
                try { WriteJson(response, 500, "{\"ok\":false,\"error\":\"" + WebPageBuilder.HtmlEncode(ex.Message) + "\"}"); } catch { }
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
            ServeEmbeddedImage(response, "SuperHttpFileServer.Resources.favicon.png", "image/png");
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
            ServeEmbeddedImage(response, "SuperHttpFileServer.Resources.logo.png", "image/png");
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
                        jsonParts.Add("{\"name\":\"" + WebPageBuilder.HtmlEncode(di.Name) + "\",\"path\":\"" + WebPageBuilder.HtmlEncode(relPath) + "\"}");
                    }
                    catch { }
                }

                string json = "{\"ok\":true,\"dirs\":[" + string.Join(",", jsonParts) + "]}";
                WriteJson(response, 200, json);
            }
            catch (Exception ex)
            {
                WriteJson(response, 500, "{\"ok\":false,\"error\":\"" + WebPageBuilder.HtmlEncode(ex.Message) + "\"}");
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
                            "{\"ok\":true,\"user\":\"" + WebPageBuilder.HtmlEncode(username) + "\"}");
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
                    "{\"ok\":false,\"error\":\"" + WebPageBuilder.HtmlEncode(ex.Message) + "\"}");
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
            string title = WebPageBuilder.HtmlEncode(cfg.WebTitle ?? "File Server");
            string logoSrc = "/logo";
            string beian = cfg.Beian ?? "";

            string html = WebPageBuilder.BuildLoginPage(title, logoSrc, beian, cfg.BeianSize);
            byte[] data = Encoding.UTF8.GetBytes(html);
            response.ContentType = "text/html; charset=utf-8";
            response.ContentLength64 = data.Length;
            response.OutputStream.Write(data, 0, data.Length);
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

            // 读取目录内容
            DirectoryInfo[] dirs;
            FileInfo[] files;
            try
            {
                var di = new DirectoryInfo(currentDir);
                dirs = di.GetDirectories();
                files = di.GetFiles();
            }
            catch (Exception ex)
            {
                WriteJson(response, 500, "{\"ok\":false,\"error\":\"" + WebPageBuilder.HtmlEncode(ex.Message) + "\"}");
                return;
            }

            var previewExts = config.GetPreviewExtensions();
            string html = WebPageBuilder.BuildDirectoryPage(
                cfg.WebTitle ?? "File Server", username,
                relativePath, urlPrefix,
                dirs, files, previewExts,
                showZip, showUpload, showDelete, showMove, showRename,
                timeoutMin, cfg.Beian ?? "", cfg.BeianSize);

            byte[] data = Encoding.UTF8.GetBytes(html);
            response.ContentType = "text/html; charset=utf-8";
            response.ContentLength64 = data.Length;
            response.OutputStream.Write(data, 0, data.Length);
            Interlocked.Add(ref _totalBytes, data.Length);
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
                WriteJson(response, 200, "{\"ok\":true,\"file\":\"" + WebPageBuilder.HtmlEncode(zipFileName) + "\"}");
            }
            catch (Exception ex)
            {
                Logger.Error("7z error: " + ex.Message);
                WriteJson(response, 500, "{\"ok\":false,\"error\":\"" + WebPageBuilder.HtmlEncode(ex.Message) + "\"}");
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
                string resourceName = "SuperHttpFileServer.7z.exe";
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

                        string html = WebPageBuilder.BuildUploadSuccessPage(fileName);
                        byte[] data = Encoding.UTF8.GetBytes(html);
                        response.ContentType = "text/html; charset=utf-8";
                        response.ContentLength64 = data.Length;
                        response.OutputStream.Write(data, 0, data.Length);
                        return;
                    }
                }

                WriteJson(response, 400, "{\"ok\":false,\"error\":\"未找到文件\"}");
            }
            catch (Exception ex)
            {
                Logger.Error("上传失败: " + ex.Message);
                WriteJson(response, 500, "{\"ok\":false,\"error\":\"上传失败: " + WebPageBuilder.HtmlEncode(ex.Message) + "\"}");
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
                WriteJson(response, 500, "{\"ok\":false,\"error\":\"删除失败: " + WebPageBuilder.HtmlEncode(ex.Message) + "\"}");
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
                WriteJson(response, 500, "{\"ok\":false,\"error\":\"移动失败: " + WebPageBuilder.HtmlEncode(ex.Message) + "\"}");
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
                WriteJson(response, 500, "{\"ok\":false,\"error\":\"重命名失败: " + WebPageBuilder.HtmlEncode(ex.Message) + "\"}");
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
                case ".tiff":
                case ".tif": return "image/tiff";
                case ".avif": return "image/avif";
                case ".pdf": return "application/pdf";
                case ".zip": return "application/zip";
                case ".gz": return "application/gzip";
                case ".mp4": return "video/mp4";
                case ".webm": return "video/webm";
                case ".avi": return "video/x-msvideo";
                case ".mkv": return "video/x-matroska";
                case ".mov": return "video/quicktime";
                case ".wmv": return "video/x-ms-wmv";
                case ".mp3": return "audio/mpeg";
                case ".wav": return "audio/wav";
                case ".ogg": return "audio/ogg";
                case ".flac": return "audio/flac";
                case ".m4a": return "audio/mp4";
                case ".aac": return "audio/aac";
                default: return "application/octet-stream";
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
