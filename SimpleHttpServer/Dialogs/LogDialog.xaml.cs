using System.Windows;

namespace SimpleHttpServer
{
    public partial class LogDialog : Window
    {
        private const int MaxLines = 500;

        public LogDialog()
        {
            InitializeComponent();

            // 加载已有日志（最新在前面）
            LogBox.Text = Logger.ReadCurrentLog();
            TrimLines();
            LogBox.ScrollToHome();

            // 订阅实时日志
            Logger.OnLog += OnLogLine;
        }

        private void OnLogLine(string line)
        {
            Dispatcher.Invoke(() =>
            {
                // 新日志插入到最前面
                if (string.IsNullOrEmpty(LogBox.Text))
                    LogBox.Text = line;
                else
                    LogBox.Text = line + "\n" + LogBox.Text;

                TrimLines();
            });
        }

        // 限制显示行数，防止内存溢出
        private void TrimLines()
        {
            if (string.IsNullOrEmpty(LogBox.Text)) return;
            var lines = LogBox.Text.Split('\n');
            if (lines.Length > MaxLines)
            {
                // 保留最新的 MaxLines 行
                var trimmed = new string[MaxLines];
                System.Array.Copy(lines, trimmed, MaxLines);
                LogBox.Text = string.Join("\n", trimmed);
            }
        }

        private void OnClear(object sender, RoutedEventArgs e)
        {
            Logger.ClearCurrentLog();
            LogBox.Clear();
        }

        private void OnClose(object sender, RoutedEventArgs e)
        {
            Logger.OnLog -= OnLogLine;
            DialogResult = false;
        }

        protected override void OnClosed(System.EventArgs e)
        {
            Logger.OnLog -= OnLogLine;
            base.OnClosed(e);
        }
    }
}
