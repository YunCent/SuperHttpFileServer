using System.Windows;

namespace SimpleHttpServer
{
    public partial class GroupEditDialog : Window
    {
        public string GroupName { get; private set; }
        public string ShareDir { get; private set; }
        public bool AllowDelete { get; private set; }
        public bool AllowUpload { get; private set; }
        public bool AllowZip { get; private set; }
        public bool AllowMove { get; private set; }
        public bool AllowRename { get; private set; }

        public GroupEditDialog() : this(null) { }

        public GroupEditDialog(UserGroup existing)
        {
            InitializeComponent();

            if (existing != null)
            {
                Title = "编辑分组";
                GroupNameBox.Text = existing.GroupName;
                ShareDirBox.Text = existing.ShareDir;
                AllowDeleteCheck.IsChecked = existing.AllowDelete;
                AllowUploadCheck.IsChecked = existing.AllowUpload;
                AllowZipCheck.IsChecked = existing.AllowZip;
                AllowMoveCheck.IsChecked = existing.AllowMove;
                AllowRenameCheck.IsChecked = existing.AllowRename;
                GroupNameBox.IsReadOnly = true;
            }
            else
            {
                Title = "新建分组";
                AllowDeleteCheck.IsChecked = true;
                AllowUploadCheck.IsChecked = true;
                AllowZipCheck.IsChecked = true;
                AllowMoveCheck.IsChecked = true;
                AllowRenameCheck.IsChecked = true;
            }
        }

        private void OnBrowseDir(object sender, RoutedEventArgs e)
        {
            var dialog = new System.Windows.Forms.FolderBrowserDialog();
            dialog.Description = "选择共享目录";
            dialog.ShowNewFolderButton = true;
            if (!string.IsNullOrEmpty(ShareDirBox.Text))
                dialog.SelectedPath = ShareDirBox.Text;

            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                ShareDirBox.Text = dialog.SelectedPath;
        }

        private void OnOk(object sender, RoutedEventArgs e)
        {
            string name = GroupNameBox.Text.Trim();
            if (string.IsNullOrEmpty(name))
            {
                MessageBox.Show(this, "分组名不能为空", "提示");
                return;
            }

            GroupName = name;
            ShareDir = ShareDirBox.Text.Trim();
            AllowDelete = AllowDeleteCheck.IsChecked == true;
            AllowUpload = AllowUploadCheck.IsChecked == true;
            AllowZip = AllowZipCheck.IsChecked == true;
            AllowMove = AllowMoveCheck.IsChecked == true;
            AllowRename = AllowRenameCheck.IsChecked == true;

            DialogResult = true;
        }

        private void OnCancel(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}
