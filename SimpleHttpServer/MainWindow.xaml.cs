using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace SimpleHttpServer
{
    public partial class MainWindow : Window
    {
        private FileHttpServer _server;
        private bool _isQuitting;

        public MainWindow()
        {
            InitializeComponent();
            Title = Utility.IsAdmin()
                ? "文件服务器 [管理员]"
                : "文件服务器 [非管理员]";
            SourceInitialized += (s, e) => AddHelpButton();
        }

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hwnd, int index);
        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_CONTEXTHELP = 0x400;

        private void AddHelpButton()
        {
            IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_CONTEXTHELP);

            var source = System.Windows.Interop.HwndSource.FromHwnd(hwnd);
            source?.AddHook(WndProc);
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_SYSCOMMAND = 0x0112;
            const int SC_CONTEXTHELP = 0xF180;

            if (msg == WM_SYSCOMMAND && (int)wParam == SC_CONTEXTHELP)
            {
                handled = true;
                MessageBox.Show(this, "作者：YunCent", "关于", MessageBoxButton.OK, MessageBoxImage.Information);
                return IntPtr.Zero;
            }
            return IntPtr.Zero;
        }

        public void LoadConfig()
        {
            var cfg = ConfigManager.Get();
            ServerFolderBox.Text = cfg.ServerDir;
            AddrBox.Text = cfg.ListenAddr;
            PortBox.Text = cfg.ListenPort.ToString();
            TimeoutBox.Text = cfg.Timeout.ToString();
            AuthCheck.IsChecked = cfg.AuthEnable;
            AutoStartMenu.IsChecked = cfg.AutoStartup;

            foreach (var ip in Utility.GetInterfaceIPs())
            {
                if (!AddrBox.Items.Contains(ip))
                    AddrBox.Items.Add(ip);
            }
        }

        private void SaveUIConfig()
        {
            ConfigManager.ServerDirSave(ServerFolderBox.Text);
            ConfigManager.ListenAddrSave(AddrBox.Text);
            ConfigManager.AuthEnableSave(AuthCheck.IsChecked == true);
            ConfigManager.AutoStartupSave(AutoStartMenu.IsChecked);

            if (int.TryParse(PortBox.Text, out int port))
                ConfigManager.ListenPortSave(port);
            if (int.TryParse(TimeoutBox.Text, out int timeout))
                ConfigManager.TimeoutSave(timeout);
        }

        public void UpdateUrl()
        {
            string addr = AddrBox.Text;
            int port = int.TryParse(PortBox.Text, out int p) ? p : 9000;
            UrlBox.Text = Utility.GetServerUrl(addr, port);
        }

        public void UpdateStatusUI(bool running)
        {
            AddrBox.IsEnabled = !running;
            PortBox.IsEnabled = !running;
            TimeoutBox.IsEnabled = !running;
            ServerFolderBox.IsEnabled = !running;

            if (running)
            {
                ServerStatusText.Text = "运行中";
                ServerStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50));
                StatusDot.Fill = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50));
                StartButton.Content = "停止服务器";
                StartButton.Style = (Style)FindResource("PrimaryBtn");
                StartButton.Background = new SolidColorBrush(Color.FromRgb(0xEF, 0x53, 0x50));
            }
            else
            {
                ServerStatusText.Text = "已停止";
                ServerStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0x9E, 0x9E, 0x9E));
                StatusDot.Fill = new SolidColorBrush(Color.FromRgb(0x9E, 0x9E, 0x9E));
                StartButton.Content = "启动服务器";
                StartButton.Style = (Style)FindResource("PrimaryBtn");
                StartButton.Background = new SolidColorBrush(Color.FromRgb(0x42, 0x85, 0xF4));
            }
        }

        public void StartServer()
        {
            if (_server != null && _server.IsRunning) return;
            ToggleServer(null, null);
        }

        public void StopServer()
        {
            if (_server != null)
            {
                try { _server.Stop(); }
                catch (Exception ex) { Logger.Error("StopServer error: " + ex.Message); }
                _server = null;
                UpdateStatusUI(false);
            }
        }

        public void ForceQuit()
        {
            if (_isQuitting) return;
            _isQuitting = true;
            Logger.Info("ForceQuit called");

            // 停止服务器
            StopServer();

            // 释放托盘图标
            try { ((App)Application.Current).DisposeTrayIcon(); } catch { }

            // 释放 Mutex
            try { ((App)Application.Current).ReleaseMutex(); } catch { }

            Logger.Info("程序退出");

            // 强制终止进程——最可靠的方式
            Environment.Exit(0);
        }

        private void OnBrowseFolder(object sender, RoutedEventArgs e)
        {
            var dialog = new System.Windows.Forms.FolderBrowserDialog();
            dialog.Description = "选择共享目录";
            dialog.ShowNewFolderButton = true;
            if (!string.IsNullOrEmpty(ServerFolderBox.Text))
                dialog.SelectedPath = ServerFolderBox.Text;

            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                ServerFolderBox.Text = dialog.SelectedPath;
                ConfigManager.ServerDirSave(dialog.SelectedPath);
            }
        }

        private void OnOpenUrl(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(UrlBox.Text))
                Utility.OpenBrowser(UrlBox.Text);
        }

        private void OnNumberOnly(object sender, TextCompositionEventArgs e)
        {
            e.Handled = !e.Text.All(char.IsDigit);
        }

        private void OnAddrChanged(object sender, RoutedEventArgs e) => UpdateUrl();
        private void OnPortChanged(object sender, RoutedEventArgs e) => UpdateUrl();

        private void ToggleServer(object sender, RoutedEventArgs e)
        {
            if (_server != null && _server.IsRunning)
            {
                _server.Stop();
                _server = null;
                UpdateStatusUI(false);
                Logger.Info("User stopped server");
            }
            else
            {
                SaveUIConfig();

                string dir = ServerFolderBox.Text;
                if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
                {
                    MessageBox.Show(this, "请选择有效的共享目录", "启动失败",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                int port = int.TryParse(PortBox.Text, out int p) ? p : 9000;
                if (!Utility.IsPortAvailable(port))
                {
                    string hint = "端口 " + port + " 已被占用";
                    if (Utility.IsSystemPort(port))
                        hint += "\n\n系统端口(1-1023)可能被系统服务占用，请尝试更换为1024以上的端口";
                    MessageBox.Show(this, hint, "启动失败",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                if (Utility.IsSystemPort(port) && !Utility.IsAdmin())
                {
                    var result = MessageBox.Show(this,
                        "端口 " + port + " 为系统保留端口，非管理员模式可能无法绑定，将尝试以 localhost 方式启动。\n\n是否继续？",
                        "提示", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
                    if (result != MessageBoxResult.OK)
                        return;
                }

                try
                {
                    _server = new FileHttpServer();
                    _server.OnRequestCountChanged += count =>
                        Dispatcher.Invoke(() =>
                        {
                            StatusText.Text = "请求数: " + count;
                            BandwidthText.Text = "总流量: " + Utility.FormatBytes(_server.TotalBytes);
                        });

                    _server.Start(ConfigManager.Get());
                    UpdateStatusUI(true);
                    Logger.Info("User started server");
                }
                catch (Exception ex)
                {
                    Logger.Error("Start failed: " + ex.Message);
                    MessageBox.Show(this, ex.Message, "启动失败",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    _server = null;
                }
            }
        }

        private void OnExit(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show(this, "确定要退出文件服务器吗？", "退出确认",
                MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel);
            if (result == MessageBoxResult.OK)
                ForceQuit();
        }

        private void OnRunlog(object sender, RoutedEventArgs e)
        {
            var dlg = new LogDialog { Owner = this };
            dlg.ShowDialog();
        }
        private void OnAutoStartToggle(object sender, RoutedEventArgs e)
        {
            bool enable = AutoStartMenu.IsChecked == true;
            ConfigManager.AutoStartupSave(enable);
            Utility.SetAutoStartup(enable);
        }

        private void OnUsersEdit(object sender, RoutedEventArgs e)
        {
            var dialog = new UserDialog { Owner = this };
            dialog.ShowDialog();
        }

        private void OnAppearEdit(object sender, RoutedEventArgs e)
        {
            var dialog = new AppearDialog { Owner = this };
            dialog.ShowDialog();
        }

        private void OnPreviewEdit(object sender, RoutedEventArgs e)
        {
            var dialog = new PreviewDialog { Owner = this };
            dialog.ShowDialog();
        }

        private void OnOpenFolder(object sender, RoutedEventArgs e)
        {
            string dir = ServerFolderBox.Text;
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
            {
                MessageBox.Show(this, "共享目录不存在，请先选择有效目录", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = dir,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                Logger.Error("打开目录失败: " + ex.Message);
                MessageBox.Show(this, "打开目录失败: " + ex.Message, "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OnClosing(object sender, CancelEventArgs e)
        {
            if (_isQuitting) return;
            e.Cancel = true;
            Hide();
        }

        // Windows 关机/注销时允许关闭（由 App.SessionEnding 触发）
        public void AllowClose()
        {
            _isQuitting = true;
            Closing -= OnClosing;
        }
    }
}
