using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;

namespace SimpleHttpServer
{
    public partial class UserEditDialog : Window
    {
        private bool _isEditMode = false;
        private bool _pwdVisible = false;
        private string _originalPassword = "";
        private string _currentPlainText = "";

        public string UserName { get; private set; }
        public string Password { get; private set; }
        public string GroupName { get; private set; }
        public bool AllowDelete { get; private set; }
        public bool AllowUpload { get; private set; }
        public bool AllowZip { get; private set; }
        public bool AllowMove { get; private set; }
        public bool AllowRename { get; private set; }

        private List<string> _groupNames;

        // 新建用户
        public UserEditDialog(List<string> groupNames) : this(null, groupNames) { }

        // 编辑已有用户
        public UserEditDialog(UserInfo existing, List<string> groupNames)
        {
            InitializeComponent();
            _groupNames = groupNames ?? new List<string>();

            // 填充分组下拉
            GroupComboBox.Items.Add("(无分组)");
            foreach (var name in _groupNames)
                GroupComboBox.Items.Add(name);
            GroupComboBox.SelectedIndex = 0;

            if (existing != null)
            {
                _isEditMode = true;
                Title = "编辑用户";
                UsernameBox.Text = existing.UserName;
                _originalPassword = existing.Password;
                _currentPlainText = existing.Password;

                _pwdVisible = false;
                PasswordTextBox.Text = new string('●', 6);
                PasswordTextBox.IsReadOnly = false;

                AllowDeleteCheck.IsChecked = existing.AllowDelete;
                AllowUploadCheck.IsChecked = existing.AllowUpload;
                AllowZipCheck.IsChecked = existing.AllowZip;
                AllowMoveCheck.IsChecked = existing.AllowMove;
                AllowRenameCheck.IsChecked = existing.AllowRename;
                UsernameBox.IsReadOnly = true;

                // 选中用户所在分组
                if (!string.IsNullOrEmpty(existing.GroupName))
                {
                    int idx = _groupNames.IndexOf(existing.GroupName);
                    GroupComboBox.SelectedIndex = idx >= 0 ? idx + 1 : 0;
                }
            }
            else
            {
                _isEditMode = false;
                Title = "新建用户";
                PasswordTextBox.Text = "";
                PasswordTextBox.IsReadOnly = false;
                AllowDeleteCheck.IsChecked = true;
                AllowUploadCheck.IsChecked = true;
                AllowZipCheck.IsChecked = true;
                AllowMoveCheck.IsChecked = true;
                AllowRenameCheck.IsChecked = true;
            }
        }

        private void OnTogglePwd(object sender, RoutedEventArgs e)
        {
            _pwdVisible = !_pwdVisible;
            if (_pwdVisible)
            {
                PasswordTextBox.Text = _currentPlainText;
                PasswordTextBox.SelectAll();
            }
            else
            {
                _currentPlainText = PasswordTextBox.Text;
                PasswordTextBox.Text = new string('●', 6);
            }
            PasswordTextBox.Focus();
        }

        private void OnOk(object sender, RoutedEventArgs e)
        {
            string username = UsernameBox.Text.Trim();
            if (string.IsNullOrEmpty(username))
            {
                MessageBox.Show(this, "用户名不能为空", "提示");
                return;
            }

            if (_isEditMode)
            {
                string pwd = _pwdVisible ? PasswordTextBox.Text.Trim() : _currentPlainText.Trim();
                if (pwd == _originalPassword)
                    Password = null;
                else
                    Password = pwd;
            }
            else
            {
                string password = PasswordTextBox.Text.Trim();
                if (string.IsNullOrEmpty(password))
                {
                    MessageBox.Show(this, "密码不能为空", "提示");
                    return;
                }
                Password = password;
            }

            UserName = username;
            GroupName = GroupComboBox.SelectedIndex > 0
                ? _groupNames[GroupComboBox.SelectedIndex - 1]
                : "";
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
