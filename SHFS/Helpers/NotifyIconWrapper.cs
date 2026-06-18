using System;
using System.IO;
using System.Windows;
using System.Windows.Forms;

namespace SuperHttpFileServer
{
    // 系统托盘图标
    public class NotifyIconWrapper : IDisposable
    {
        private readonly NotifyIcon _notifyIcon;
        private readonly MainWindow _owner;

        public NotifyIconWrapper(MainWindow owner)
        {
            _owner = owner;
            _notifyIcon = new NotifyIcon();

            // 加载图标：优先自定义，其次资源，最后系统默认
            try
            {
                var cfg = ConfigManager.Get();
                bool loaded = false;

                if (!string.IsNullOrEmpty(cfg.IcoPath) && File.Exists(cfg.IcoPath))
                {
                    try
                    {
                        _notifyIcon.Icon = new System.Drawing.Icon(cfg.IcoPath);
                        loaded = true;
                    }
                    catch { }
                }

                if (!loaded)
                {
                    var iconStream = System.Windows.Application.GetResourceStream(
                        new Uri("pack://application:,,,/Resources/main.ico"));
                    if (iconStream != null)
                        _notifyIcon.Icon = new System.Drawing.Icon(iconStream.Stream);
                    else
                        _notifyIcon.Icon = System.Drawing.SystemIcons.Information;
                }
            }
            catch
            {
                try { _notifyIcon.Icon = System.Drawing.SystemIcons.Information; } catch { }
            }

            _notifyIcon.Text = "文件服务器";
            _notifyIcon.Visible = true;

            var menu = new ContextMenuStrip();
            menu.Items.Add("显示窗口", null, (s, e) => ShowWindow());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出", null, (s, e) => QuitApp());
            _notifyIcon.ContextMenuStrip = menu;
            _notifyIcon.DoubleClick += (s, e) => ShowWindow();
        }

        private void ShowWindow()
        {
            try
            {
                _owner.Dispatcher.Invoke(() =>
                {
                    _owner.Show();
                    _owner.WindowState = WindowState.Normal;
                    _owner.Activate();
                });
            }
            catch { }
        }

        private void QuitApp()
        {
            _owner.Dispatcher.Invoke(() => _owner.ForceQuit());
        }

        public void Dispose()
        {
            try
            {
                if (_notifyIcon != null)
                {
                    _notifyIcon.Visible = false;
                    _notifyIcon.Dispose();
                }
            }
            catch { }
        }
    }
}
