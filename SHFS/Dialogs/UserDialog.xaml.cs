using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;

namespace SuperHttpFileServer
{
    public partial class UserDialog : Window
    {
        public ObservableCollection<UserInfo> Users { get; } = new ObservableCollection<UserInfo>();
        public ObservableCollection<UserGroup> Groups { get; } = new ObservableCollection<UserGroup>();

        private bool IsUserTab => MainTabs.SelectedIndex == 0;
        private bool IsBlackTab => MainTabs.SelectedIndex == 2;

        public UserDialog()
        {
            InitializeComponent();
            UserListView.ItemsSource = Users;
            GroupListView.ItemsSource = Groups;

            foreach (var u in ConfigManager.Get().AuthUsers)
            {
                Users.Add(new UserInfo
                {
                    UserName = u.UserName,
                    Password = u.Password,
                    GroupName = u.GroupName,
                    AllowDelete = u.AllowDelete,
                    AllowUpload = u.AllowUpload,
                    AllowZip = u.AllowZip,
                    AllowMove = u.AllowMove,
                    AllowRename = u.AllowRename
                });
            }

            foreach (var g in ConfigManager.Get().UserGroups)
            {
                Groups.Add(new UserGroup
                {
                    GroupName = g.GroupName,
                    ShareDir = g.ShareDir,
                    AllowDelete = g.AllowDelete,
                    AllowUpload = g.AllowUpload,
                    AllowZip = g.AllowZip,
                    AllowMove = g.AllowMove,
                    AllowRename = g.AllowRename
                });
            }

            LoadBlacklist();
        }

        // 获取分组名列表
        public List<string> GetGroupNames()
        {
            var names = new List<string>();
            foreach (var g in Groups)
                names.Add(g.GroupName);
            return names;
        }

        // 页签切换
        private void OnTabChanged(object sender, SelectionChangedEventArgs e)
        {
            if (BtnAdd == null) return;
            bool isBlack = IsBlackTab;
            BtnAdd.Visibility = isBlack ? Visibility.Collapsed : Visibility.Visible;
            BtnEdit.Visibility = isBlack ? Visibility.Collapsed : Visibility.Visible;
            BtnDelete.Visibility = isBlack ? Visibility.Collapsed : Visibility.Visible;
            BtnUnlock.Visibility = isBlack ? Visibility.Visible : Visibility.Collapsed;
            BtnAdd.Content = IsUserTab ? "新建用户" : "新建";
            BtnEdit.Content = IsUserTab ? "编辑用户" : "编辑";
            BtnDelete.Content = IsUserTab ? "删除用户" : "删除";
            if (isBlack) LoadBlacklist();
        }

        // 统一操作按钮
        private void OnAdd(object sender, RoutedEventArgs e)
        {
            if (IsUserTab) OnAddUser();
            else OnAddGroup();
        }

        private void OnEdit(object sender, RoutedEventArgs e)
        {
            if (IsUserTab) OnEditUser();
            else OnEditGroup();
        }

        private void OnDelete(object sender, RoutedEventArgs e)
        {
            if (IsUserTab) OnDeleteUser();
            else OnDeleteGroup();
        }

        // 用户操作
        private void OnAddUser()
        {
            var dlg = new UserEditDialog(GetGroupNames()) { Owner = this };
            if (dlg.ShowDialog() == true)
            {
                foreach (var u in Users)
                {
                    if (u.UserName == dlg.UserName)
                    {
                        MessageBox.Show(this, "用户名已存在", "提示");
                        return;
                    }
                }
                Users.Add(new UserInfo
                {
                    UserName = dlg.UserName,
                    Password = dlg.Password,
                    GroupName = dlg.GroupName,
                    AllowDelete = dlg.AllowDelete,
                    AllowUpload = dlg.AllowUpload,
                    AllowZip = dlg.AllowZip,
                    AllowMove = dlg.AllowMove,
                    AllowRename = dlg.AllowRename
                });
            }
        }

        private void OnEditUser()
        {
            if (!(UserListView.SelectedItem is UserInfo selected))
            {
                MessageBox.Show(this, "请先选中一个用户", "提示");
                return;
            }
            var dlg = new UserEditDialog(selected, GetGroupNames()) { Owner = this };
            if (dlg.ShowDialog() == true)
            {
                if (dlg.Password != null)
                    selected.Password = dlg.Password;
                selected.GroupName = dlg.GroupName;
                selected.AllowDelete = dlg.AllowDelete;
                selected.AllowUpload = dlg.AllowUpload;
                selected.AllowZip = dlg.AllowZip;
                selected.AllowMove = dlg.AllowMove;
                selected.AllowRename = dlg.AllowRename;
                UserListView.Items.Refresh();
            }
        }

        private void OnUserDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (UserListView.SelectedItem is UserInfo)
                OnEditUser();
        }

        private void OnDeleteUser()
        {
            if (UserListView.SelectedItem is UserInfo selected)
                Users.Remove(selected);
        }

        // 分组操作
        private void OnAddGroup()
        {
            var dlg = new GroupEditDialog { Owner = this };
            if (dlg.ShowDialog() == true)
            {
                foreach (var g in Groups)
                {
                    if (g.GroupName == dlg.GroupName)
                    {
                        MessageBox.Show(this, "分组名已存在", "提示");
                        return;
                    }
                }
                Groups.Add(new UserGroup
                {
                    GroupName = dlg.GroupName,
                    ShareDir = dlg.ShareDir,
                    AllowDelete = dlg.AllowDelete,
                    AllowUpload = dlg.AllowUpload,
                    AllowZip = dlg.AllowZip,
                    AllowMove = dlg.AllowMove,
                    AllowRename = dlg.AllowRename
                });
            }
        }

        private void OnEditGroup()
        {
            if (!(GroupListView.SelectedItem is UserGroup selected))
            {
                MessageBox.Show(this, "请先选中一个分组", "提示");
                return;
            }
            var dlg = new GroupEditDialog(selected) { Owner = this };
            if (dlg.ShowDialog() == true)
            {
                string oldName = selected.GroupName;
                selected.GroupName = dlg.GroupName;
                selected.ShareDir = dlg.ShareDir;
                selected.AllowDelete = dlg.AllowDelete;
                selected.AllowUpload = dlg.AllowUpload;
                selected.AllowZip = dlg.AllowZip;
                selected.AllowMove = dlg.AllowMove;
                selected.AllowRename = dlg.AllowRename;

                // 同步更新用户引用的分组名
                if (oldName != dlg.GroupName)
                {
                    foreach (var u in Users)
                    {
                        if (u.GroupName == oldName)
                            u.GroupName = dlg.GroupName;
                    }
                    UserListView.Items.Refresh();
                }
                GroupListView.Items.Refresh();
            }
        }

        private void OnGroupDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (GroupListView.SelectedItem is UserGroup)
                OnEditGroup();
        }

        private void OnDeleteGroup()
        {
            if (!(GroupListView.SelectedItem is UserGroup selected)) return;
            foreach (var u in Users)
            {
                if (u.GroupName == selected.GroupName)
                {
                    MessageBox.Show(this, "该分组下还有用户，请先移除或更改用户分组", "提示");
                    return;
                }
            }
            Groups.Remove(selected);
        }

        // 加载黑名单
        private void LoadBlacklist()
        {
            BlackDataGrid.ItemsSource = FileHttpServer.GetLockedIps();
        }

        // 解封选中 IP
        private void OnUnlockIp(object sender, RoutedEventArgs e)
        {
            if (!(BlackDataGrid.SelectedItem is LockedIpInfo selected))
            {
                MessageBox.Show(this, "请先选中一个 IP", "提示");
                return;
            }
            FileHttpServer.UnlockIp(selected.Ip);
            LoadBlacklist();
        }

        private void OnOk(object sender, RoutedEventArgs e)
        {
            ConfigManager.UserListSave(new List<UserInfo>(Users));
            ConfigManager.UserGroupListSave(new List<UserGroup>(Groups));
            DialogResult = true;
        }

        private void OnCancel(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}
