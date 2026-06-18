using System;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SuperHttpFileServer
{
    // TcpListener + SslStream HTTPS 反向代理到本地 HTTP 端口
    public class SslProxyServer : IDisposable
    {
        private TcpListener _listener;
        private Task _task;
        private CancellationTokenSource _cts;
        private readonly int _targetHttpPort;
        private readonly X509Certificate2 _certificate;
        private readonly string _listenAddr;
        private readonly int _listenPort;

        public SslProxyServer(X509Certificate2 certificate, string addr, int port, int targetHttpPort)
        {
            _certificate = certificate ?? throw new ArgumentNullException(nameof(certificate));
            _targetHttpPort = targetHttpPort;
            _listenAddr = addr;
            _listenPort = port;

            var ip = (addr == "0.0.0.0") ? IPAddress.Any : IPAddress.Parse(addr);
            _listener = new TcpListener(ip, port);
        }

        public bool IsRunning => _listener != null;

        public void Start()
        {
            if (_listener == null) return;

            _cts = new CancellationTokenSource();

            try
            {
                _listener.Start();
            }
            catch (SocketException ex)
            {
                Logger.Error("HTTPS 启动失败: 端口 " + _listenPort + " 被占用或无权限 (SocketErrorCode=" + ex.SocketErrorCode + ")");
                throw;
            }
            catch (Exception ex)
            {
                Logger.Error("HTTPS 启动失败: " + ex.Message);
                throw;
            }

            Logger.Info("HTTPS 监听已启动: " + _listenAddr + ":" + _listenPort + " → 内部HTTP 127.0.0.1:" + _targetHttpPort);
            Logger.Info("SSL 证书: " + _certificate.Subject + ", 有效期至 " + _certificate.NotAfter.ToString("yyyy-MM-dd"));

            _task = Task.Run(async () =>
            {
                while (!_cts.IsCancellationRequested)
                {
                    TcpClient client = null;
                    try
                    {
                        client = await _listener.AcceptTcpClientAsync();
                        var c = client;
                        _ = Task.Run(() => HandleClientAsync(c));
                    }
                    catch (ObjectDisposedException) { break; }
                    catch (Exception ex)
                    {
                        client?.Dispose();
                        if (!_cts.IsCancellationRequested)
                            Logger.Warn("HTTPS accept 异常: " + ex.Message);
                        break;
                    }
                }
                Logger.Info("HTTPS 监听循环已退出");
            });
        }

        public void Stop()
        {
            try { _cts?.Cancel(); } catch { }
            if (_listener != null)
            {
                try { _listener.Stop(); } catch { }
                _listener = null;
            }
            if (_task != null)
            {
                try { _task.Wait(1000); } catch { }
                _task = null;
            }
            Logger.Info("HTTPS 代理已停止");
        }

        public void Dispose()
        {
            Stop();
            try { _cts?.Dispose(); } catch { }
            try { _certificate?.Dispose(); } catch { }
        }

        private async Task HandleClientAsync(TcpClient tcpClient)
        {
            string clientInfo = "";
            try
            {
                clientInfo = ((IPEndPoint)tcpClient.Client.RemoteEndPoint).ToString();
                using (tcpClient)
                using (var sslStream = new SslStream(tcpClient.GetStream(), false))
                {
                    try
                    {
                        await sslStream.AuthenticateAsServerAsync(_certificate, false, SslProtocols.Tls12 | SslProtocols.Tls13, false);
                        Logger.Info("SSL 握手成功: " + clientInfo + ", 协议=" + sslStream.SslProtocol);
                    }
                    catch (AuthenticationException ex)
                    {
                        Logger.Warn("SSL 握手失败: " + clientInfo + " → " + ex.Message);
                        return;
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn("SSL 握手异常: " + clientInfo + " → " + ex.Message);
                        return;
                    }

                    byte[] requestBytes = await ReadHttpMessageAsync(sslStream);
                    if (requestBytes == null || requestBytes.Length == 0)
                    {
                        Logger.Warn("SSL 请求为空: " + clientInfo);
                        return;
                    }

                    // 提取请求行用于日志
                    string reqHead = Encoding.ASCII.GetString(requestBytes, 0, Math.Min(requestBytes.Length, 200));
                    int lineEnd = reqHead.IndexOf('\r');
                    if (lineEnd > 0) reqHead = reqHead.Substring(0, lineEnd);
                    Logger.Info("SSL 请求: " + clientInfo + " → " + reqHead);

                    // 重写 Host 头 + 添加 X-Forwarded-* 头
                    string clientIp = ((IPEndPoint)tcpClient.Client.RemoteEndPoint).Address.ToString();
                    requestBytes = RewriteRequestHeaders(requestBytes, clientIp);

                    // 转发到本地 HTTP 服务
                    try
                    {
                        using (var innerClient = new TcpClient("127.0.0.1", _targetHttpPort))
                        using (var innerStream = innerClient.GetStream())
                        {
                            innerClient.SendTimeout = 30000;
                            innerClient.ReceiveTimeout = 30000;
                            await innerStream.WriteAsync(requestBytes, 0, requestBytes.Length);
                            await innerStream.FlushAsync();

                            byte[] responseBytes = await ReadHttpMessageAsync(innerStream);
                            if (responseBytes != null && responseBytes.Length > 0)
                            {
                                await sslStream.WriteAsync(responseBytes, 0, responseBytes.Length);
                                Logger.Info("SSL 响应: " + clientInfo + " → " + responseBytes.Length + " bytes");
                            }
                            else
                            {
                                Logger.Warn("SSL 内部HTTP无响应: " + clientInfo);
                            }
                        }
                    }
                    catch (SocketException ex)
                    {
                        Logger.Error("SSL 转发失败: 无法连接内部HTTP 127.0.0.1:" + _targetHttpPort + " → " + ex.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn("HTTPS 连接错误: " + (clientInfo ?? "unknown") + " → " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // 重写请求头：Host 改为内部地址，添加 X-Forwarded-Proto/For
        private byte[] RewriteRequestHeaders(byte[] request, string clientIp)
        {
            string reqStr = Encoding.ASCII.GetString(request);

            // 找到 Host 头并替换
            int hostIdx = reqStr.IndexOf("\r\nHost:", StringComparison.OrdinalIgnoreCase);
            if (hostIdx >= 0)
            {
                int lineStart = hostIdx + 2; // skip \r\n
                int lineEnd = reqStr.IndexOf("\r\n", lineStart);
                if (lineEnd >= 0)
                {
                    string newHost = "Host: 127.0.0.1:" + _targetHttpPort;
                    reqStr = reqStr.Substring(0, lineStart) + newHost + reqStr.Substring(lineEnd);
                }
            }

            // 在 Host 行后插入 X-Forwarded 头
            hostIdx = reqStr.IndexOf("\r\nHost:", StringComparison.OrdinalIgnoreCase);
            if (hostIdx >= 0)
            {
                int lineEnd = reqStr.IndexOf("\r\n", hostIdx + 2);
                if (lineEnd >= 0)
                {
                    string extraHeaders = "\r\nX-Forwarded-Proto: https\r\nX-Forwarded-For: " + clientIp;
                    reqStr = reqStr.Substring(0, lineEnd) + extraHeaders + reqStr.Substring(lineEnd);
                }
            }

            return Encoding.ASCII.GetBytes(reqStr);
        }

        private static async Task<byte[]> ReadHttpMessageAsync(Stream stream)
        {
            using (var ms = new MemoryStream())
            {
                byte[] buf = new byte[8192];
                int headerEnd = -1, totalRead = 0;

                while (true)
                {
                    int n = await stream.ReadAsync(buf, 0, buf.Length);
                    if (n == 0) break;
                    ms.Write(buf, 0, n);
                    totalRead += n;

                    byte[] data = ms.ToArray();
                    for (int i = 0; i <= totalRead - 4; i++)
                    {
                        if (data[i] == 13 && data[i + 1] == 10 && data[i + 2] == 13 && data[i + 3] == 10)
                        { headerEnd = i + 4; break; }
                    }
                    if (headerEnd >= 0) break;
                    if (totalRead > 65536) break;
                }

                if (headerEnd < 0) return ms.ToArray();

                string headerStr = Encoding.ASCII.GetString(ms.GetBuffer(), 0, headerEnd);
                int contentLen = 0;
                bool chunked = false;
                foreach (string line in headerStr.Split('\n'))
                {
                    string l = line.Trim();
                    if (l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                        int.TryParse(l.Substring(15).Trim(), out contentLen);
                    if (l.StartsWith("Transfer-Encoding:", StringComparison.OrdinalIgnoreCase) &&
                        l.IndexOf("chunked", StringComparison.OrdinalIgnoreCase) >= 0)
                        chunked = true;
                }

                int bodyRead = totalRead - headerEnd;
                if (chunked)
                {
                    while (true)
                    {
                        int n = await stream.ReadAsync(buf, 0, buf.Length);
                        if (n == 0) break;
                        ms.Write(buf, 0, n);
                        byte[] all = ms.ToArray();
                        if (all.Length >= 7)
                        {
                            string tail = Encoding.ASCII.GetString(all, all.Length - 7, 7);
                            if (tail.Contains("0\r\n\r\n")) break;
                        }
                    }
                }
                else
                {
                    while (contentLen > 0 && bodyRead < contentLen)
                    {
                        int n = await stream.ReadAsync(buf, 0, buf.Length);
                        if (n == 0) break;
                        ms.Write(buf, 0, n);
                        bodyRead += n;
                    }
                }

                return ms.ToArray();
            }
        }
    }
}
