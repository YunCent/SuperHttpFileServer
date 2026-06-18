using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;

namespace SimpleHttpServer
{
    // 显示用模型
    public class PreviewPluginView : INotifyPropertyChanged
    {
        public string Name { get; set; } = "";
        public string Category { get; set; } = "";
        public List<string> Extensions { get; set; } = new List<string>();
        public string ExtText => string.Join("  ", Extensions);

        private bool _enabled = true;
        public bool Enabled
        {
            get => _enabled;
            set { _enabled = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Enabled))); }
        }

        public event PropertyChangedEventHandler PropertyChanged;
    }

    public partial class PreviewDialog : Window
    {
        public ObservableCollection<PreviewPluginView> Plugins { get; } = new ObservableCollection<PreviewPluginView>();

        public PreviewDialog()
        {
            InitializeComponent();

            var cfg = ConfigManager.Get();
            var saved = cfg.PreviewPlugins ?? new List<PreviewPlugin>();
            var defaults = AppConfig.DefaultPlugins;

            foreach (var def in defaults)
            {
                var s = saved.FirstOrDefault(p => p.Name == def.Name);
                var view = new PreviewPluginView
                {
                    Name = def.Name,
                    Category = def.Category,
                    Extensions = def.Extensions,
                    Enabled = s != null ? s.Enabled : def.Enabled
                };
                Plugins.Add(view);
            }

            PluginListView.ItemsSource = Plugins;
        }

        private void OnSelectAll(object sender, RoutedEventArgs e)
        {
            foreach (var p in Plugins) p.Enabled = true;
        }

        private void OnSelectNone(object sender, RoutedEventArgs e)
        {
            foreach (var p in Plugins) p.Enabled = false;
        }

        private void OnOk(object sender, RoutedEventArgs e)
        {
            var list = Plugins.Select(p => new PreviewPlugin
            {
                Name = p.Name,
                Category = p.Category,
                Extensions = p.Extensions,
                Enabled = p.Enabled
            }).ToList();

            ConfigManager.PreviewPluginsSave(list);
            DialogResult = true;
        }

        private void OnCancel(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}
