using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace SimpleHttpServer
{
    // 工具方法集合
    public static class Utility
    {
        // 字节大小格式化
        public static string FormatBytes(long size)
        {
            if (size < 1024) return size + "B";
            if (size < 1024L * 1024) return string.Format("{0:F1}KB", (double)size / 1024);
            if (size < 1024L * 1024 * 1024) return string.Format("{0:F1}MB", (double)size / (1024 * 1024));
            if (size < 1024L * 1024 * 1024 * 1024) return string.Format("{0:F1}GB", (double)size / (1024 * 1024 * 1024));
            return string.Format("{0:F1}TB", (double)size / (1024L * 1024 * 1024 * 1024));
        }

        // 检测端口是否可用（未被监听）
        public static bool IsPortAvailable(int port)
        {
            try
            {
                var listeners = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();
                foreach (var ep in listeners)
                {
                    if (ep.Port == port)
                        return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                Logger.Warn("端口检测失败: " + ex.Message);
                return true;
            }
        }

        // 检测端口是否需要管理员权限（<1024 为系统保留端口）
        public static bool IsSystemPort(int port) => port > 0 && port < 1024;

        // 注册/取消 Windows 开机自启
        public static void SetAutoStartup(bool enable)
        {
            try
            {
                var reg = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);
                if (reg == null) return;

                if (enable)
                {
                    string exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName;
                    reg.SetValue("SimpleHttpFileServer", "\"" + exePath + "\"");
                }
                else
                {
                    try { reg.DeleteValue("SimpleHttpFileServer", false); } catch { }
                }
                reg.Close();
            }
            catch (Exception ex)
            {
                Logger.Warn("注册开机自启失败: " + ex.Message);
            }
        }

        // 获取本机 IPv4 列表（含回环和通配地址）
        public static List<string> GetInterfaceIPs()
        {
            var ips = new List<string> { "127.0.0.1", "0.0.0.0" };
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    foreach (var addr in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (addr.Address.AddressFamily == AddressFamily.InterNetwork)
                            ips.Add(addr.Address.ToString());
                    }
                }
            }
            catch { }
            return ips.Distinct().ToList();
        }

        // 获取浏览器访问URL
        public static string GetServerUrl(string addr, int port)
        {
            var cfg = ConfigManager.Get();
            bool useSsl = cfg.SslEnabled && !string.IsNullOrEmpty(cfg.SslCertPath) && !string.IsNullOrEmpty(cfg.SslKeyPath);
            string scheme = useSsl ? "https" : "http";

            if (!string.IsNullOrEmpty(cfg.Domain))
                return string.Format(scheme + "://{0}" + (port == 443 && useSsl ? "" : ":{1}") + "/", cfg.Domain, port);
            if (addr == "0.0.0.0")
                return string.Format(scheme + "://localhost:{0}/", port);
            return string.Format(scheme + "://{0}:{1}/", addr, port);
        }

        // 打开系统浏览器
        public static void OpenBrowser(string url)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                Logger.Error("打开浏览器失败: " + ex.Message);
            }
        }

        // 判断当前进程是否以管理员身份运行
        public static bool IsAdmin()
        {
            try
            {
                using (var identity = System.Security.Principal.WindowsIdentity.GetCurrent())
                {
                    var principal = new System.Security.Principal.WindowsPrincipal(identity);
                    return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
                }
            }
            catch { return false; }
        }

        // SHA256 哈希
        public static string HashPassword(string password)
        {
            if (string.IsNullOrEmpty(password)) return "";
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                byte[] bytes = System.Text.Encoding.UTF8.GetBytes(password);
                byte[] hash = sha.ComputeHash(bytes);
                return Convert.ToBase64String(hash);
            }
        }

        // 验证密码
        public static bool VerifyPassword(string input, string stored)
        {
            if (string.IsNullOrEmpty(input) || string.IsNullOrEmpty(stored)) return false;
            if (input == stored) return true;
            return HashPassword(input) == stored;
        }
    }
}
