using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Interop;

namespace SuperHttpFileServer
{
    public partial class App : Application
    {
        private MainWindow _mainWindow;
        private NotifyIconWrapper _trayIcon;
        private static Mutex _mutex;
        private static EventWaitHandle _activateEvent;
        private Thread _activateListener;

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        private const int SW_RESTORE = 9;

        protected override void OnStartup(StartupEventArgs e)
        {
            bool createdNew;
            _mutex = new Mutex(true, "SimpleHttpFileServer_SingleInstance", out createdNew);
            if (!createdNew)
            {
                try
                {
                    using (var evt = EventWaitHandle.OpenExisting("SimpleHttpFileServer_Activate"))
                        evt.Set();
                }
                catch { }
                Shutdown();
                return;
            }

            base.OnStartup(e);

            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
            DispatcherUnhandledException += OnDispatcherUnhandledException;
            SessionEnding += OnSessionEnding;

            // 优先显示主界面
            _mainWindow = new MainWindow();
            _mainWindow.Show();

            // 异步加载组件
            _mainWindow.Dispatcher.BeginInvoke(new Action(() =>
            {
                Logger.Info("程序启动");
                ConfigManager.Load();
                _mainWindow.LoadConfig();
                _mainWindow.UpdateUrl();
                _mainWindow.UpdateStatusUI(false);

                _trayIcon = new NotifyIconWrapper(_mainWindow);

                _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "SimpleHttpFileServer_Activate");
                _activateListener = new Thread(() =>
                {
                    while (true)
                    {
                        try
                        {
                            _activateEvent.WaitOne();
                            _mainWindow.Dispatcher.BeginInvoke(new Action(() =>
                            {
                                _mainWindow.Show();
                                _mainWindow.WindowState = WindowState.Normal;
                                _mainWindow.Activate();
                                var handle = new WindowInteropHelper(_mainWindow).Handle;
                                if (handle != IntPtr.Zero)
                                {
                                    ShowWindow(handle, SW_RESTORE);
                                    SetForegroundWindow(handle);
                                }
                            }));
                        }
                        catch { break; }
                    }
                })
                { IsBackground = true };
                _activateListener.Start();

                // 开机自启：同步注册表状态
                Utility.SetAutoStartup(ConfigManager.Get().AutoStartup);
            }));
        }

        public void DisposeTrayIcon()
        {
            try { _trayIcon?.Dispose(); } catch { }
            _trayIcon = null;
        }

        public void ReleaseMutex()
        {
            try { _activateEvent?.Set(); } catch { }
            try { _mutex?.ReleaseMutex(); } catch { }
            try { _mutex?.Dispose(); } catch { }
            _mutex = null;
        }

        private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            Logger.Error("未捕获异常: " + (e.ExceptionObject ?? "null"));
        }

        private void OnUnobservedTaskException(object sender, System.Threading.Tasks.UnobservedTaskExceptionEventArgs e)
        {
            Logger.Error("Task未捕获异常: " + e.Exception);
            e.SetObserved();
        }

        private void OnDispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
        {
            Logger.Error("UI线程未捕获异常: " + e.Exception);
            e.Handled = true;
        }

        // Windows 关机/注销时确保进程退出
        private void OnSessionEnding(object sender, SessionEndingCancelEventArgs e)
        {
            Logger.Info("Session ending: " + e.ReasonSessionEnding);
            try { _mainWindow?.AllowClose(); } catch { }
            try { _mainWindow?.StopServer(); } catch { }
            try { _trayIcon?.Dispose(); } catch { }
            try { _mutex?.ReleaseMutex(); } catch { }
            try { _mutex?.Dispose(); } catch { }
            _mutex = null;
            Environment.Exit(0);
        }
    }
}
