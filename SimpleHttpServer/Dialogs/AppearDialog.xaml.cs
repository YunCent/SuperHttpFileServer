using System;
using System.IO;
using System.Text;
using System.Security.Cryptography.X509Certificates;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SimpleHttpServer
{
    public partial class AppearDialog : Window
    {
        public AppearDialog()
        {
            InitializeComponent();
            LoadConfig();

            WebTitleBox.TextChanged += (s, e) => UpdatePreview();
        }

        private void LoadConfig()
        {
            var cfg = ConfigManager.Get();
            WebTitleBox.Text = cfg.WebTitle ?? "File Server";
            IcoPathBox.Text = cfg.LogoText ?? "";
            FavIcoPathBox.Text = cfg.IcoPath ?? "";
            BeianBox.Text = cfg.Beian ?? "";
            BeianSizeBox.Text = (cfg.BeianSize > 0 ? cfg.BeianSize : 15).ToString();
            SslEnabledCheck.IsChecked = cfg.SslEnabled;
            DomainBox.Text = cfg.Domain ?? "";
            CertPathBox.Text = cfg.SslCertPath ?? "";
            KeyPathBox.Text = cfg.SslKeyPath ?? "";
            UpdatePreview();
        }

        private void UpdatePreview()
        {
            string title = string.IsNullOrEmpty(WebTitleBox.Text) ? "File Server" : WebTitleBox.Text;
            PreviewTitle.Text = title;

            string logoPath = IcoPathBox.Text;
            if (!string.IsNullOrEmpty(logoPath) && File.Exists(logoPath))
            {
                try
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.UriSource = new Uri(logoPath, UriKind.Absolute);
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.EndInit();
                    PreviewLogoImg.Source = bmp;
                }
                catch
                {
                    PreviewLogoImg.Source = null;
                }
            }
            else
            {
                PreviewLogoImg.Source = null;
            }
        }

        private void OnBrowseIco(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择 Logo 图标",
                Filter = "图标文件|*.ico;*.png;*.jpg;*.jpeg|ICO 文件|*.ico|PNG 文件|*.png|JPG 文件|*.jpg;*.jpeg"
            };

            if (!string.IsNullOrEmpty(IcoPathBox.Text) && File.Exists(IcoPathBox.Text))
                dialog.InitialDirectory = Path.GetDirectoryName(IcoPathBox.Text);

            if (dialog.ShowDialog() == true)
            {
                IcoPathBox.Text = dialog.FileName;
                UpdatePreview();
            }
        }

        private void OnBrowseFavIco(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择网站图标",
                Filter = "图标文件|*.ico;*.png;*.jpg;*.jpeg|ICO 文件|*.ico|PNG 文件|*.png|JPG 文件|*.jpg;*.jpeg"
            };

            if (!string.IsNullOrEmpty(FavIcoPathBox.Text) && File.Exists(FavIcoPathBox.Text))
                dialog.InitialDirectory = Path.GetDirectoryName(FavIcoPathBox.Text);

            if (dialog.ShowDialog() == true)
                FavIcoPathBox.Text = dialog.FileName;
        }

        private void OnBrowseCert(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择 SSL 证书文件",
                Filter = "证书文件|*.crt;*.pem;*.cer;*.pfx;*.p12|CRT/PEM|*.crt;*.pem;*.cer|PFX/P12|*.pfx;*.p12"
            };

            if (!string.IsNullOrEmpty(CertPathBox.Text) && File.Exists(CertPathBox.Text))
                dialog.InitialDirectory = Path.GetDirectoryName(CertPathBox.Text);

            if (dialog.ShowDialog() == true)
                CertPathBox.Text = dialog.FileName;
        }

        private void OnBrowseKey(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择 SSL 密钥文件",
                Filter = "密钥文件|*.key|所有文件|*.*"
            };

            if (!string.IsNullOrEmpty(KeyPathBox.Text) && File.Exists(KeyPathBox.Text))
                dialog.InitialDirectory = Path.GetDirectoryName(KeyPathBox.Text);

            if (dialog.ShowDialog() == true)
                KeyPathBox.Text = dialog.FileName;
        }

        private void OnSave(object sender, RoutedEventArgs e)
        {
            ConfigManager.WebTitleSave(WebTitleBox.Text.Trim());
            ConfigManager.LogoTextSave(IcoPathBox.Text.Trim());
            ConfigManager.IcoPathSave(FavIcoPathBox.Text.Trim());
            ConfigManager.BeianSave(BeianBox.Text.Trim());
            int.TryParse(BeianSizeBox.Text, out int bs);
            ConfigManager.BeianSizeSave(bs > 0 ? bs : 15);
            ConfigManager.SslEnabledSave(SslEnabledCheck.IsChecked == true);
            ConfigManager.DomainSave(DomainBox.Text.Trim());
            ConfigManager.SslCertPathSave(CertPathBox.Text.Trim());
            ConfigManager.SslKeyPathSave(KeyPathBox.Text.Trim());
            DialogResult = true;
        }

        private void OnNumberOnly(object sender, System.Windows.Input.TextCompositionEventArgs e)
        {
            foreach (char c in e.Text)
                if (!char.IsDigit(c)) { e.Handled = true; return; }
        }

        private void OnCancel(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }

        // 重置 Logo / 图标 / 证书路径
        private void OnReset(object sender, RoutedEventArgs e)
        {
            IcoPathBox.Text = "";
            FavIcoPathBox.Text = "";
            SslEnabledCheck.IsChecked = false;
            CertPathBox.Text = "";
            KeyPathBox.Text = "";
            CertStatusText.Text = "";
            UpdatePreview();
        }

        // 测试证书加载
        private void OnTestCert(object sender, RoutedEventArgs e)
        {
            string certPath = CertPathBox.Text.Trim();
            CertStatusText.Text = "检测中...";
            CertStatusText.Foreground = System.Windows.Media.Brushes.Gray;

            if (string.IsNullOrEmpty(certPath) || !File.Exists(certPath))
            {
                CertStatusText.Text = "❌ 证书文件不存在";
                CertStatusText.Foreground = System.Windows.Media.Brushes.Red;
                return;
            }

            try
            {
                using (var testCert = new X509Certificate2(File.ReadAllBytes(certPath)))
                {
                    string subj = testCert.Subject;
                    // 取 CN= 部分
                    int cnIdx = subj.IndexOf("CN=", StringComparison.OrdinalIgnoreCase);
                    if (cnIdx >= 0)
                    {
                        int end = subj.IndexOf(",", cnIdx);
                        subj = end > cnIdx ? subj.Substring(cnIdx + 3, end - cnIdx - 3) : subj.Substring(cnIdx + 3);
                    }
                    string expire = testCert.NotAfter.ToString("yyyy-MM-dd");
                    bool expired = testCert.NotAfter < DateTime.Now;
                    CertStatusText.Text = (expired ? "⛔ 已过期" : "✅ " + subj) + " · 至 " + expire;
                    CertStatusText.Foreground = expired
                        ? System.Windows.Media.Brushes.Red
                        : System.Windows.Media.Brushes.Green;
                }
            }
            catch (Exception ex)
            {
                CertStatusText.Text = "❌ " + ex.Message;
                CertStatusText.Foreground = System.Windows.Media.Brushes.Red;
            }
        }
    }
}
