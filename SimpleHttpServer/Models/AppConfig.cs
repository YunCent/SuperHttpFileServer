using System.Collections.Generic;
using System.Linq;

namespace SimpleHttpServer
{
    // 预览插件信息
    public class PreviewPlugin
    {
        public string Name { get; set; } = "";
        public string Category { get; set; } = "";
        public List<string> Extensions { get; set; } = new List<string>();
        public bool Enabled { get; set; } = true;
    }

    // 用户分组
    public class UserGroup
    {
        public string GroupName { get; set; } = "";
        // 该组的共享目录（为空则使用全局 ServerDir）
        public string ShareDir { get; set; } = "";
        // 组级权限（用户权限在此基础上叠加）
        public bool AllowDelete { get; set; }
        public bool AllowUpload { get; set; }
        public bool AllowZip { get; set; }
        public bool AllowMove { get; set; }
        public bool AllowRename { get; set; }
    }

    // 用户信息
    public class UserInfo
    {
        public string UserName { get; set; } = "";
        public string Password { get; set; } = "";
        public string GroupName { get; set; } = "";
        public bool AllowDelete { get; set; }
        public bool AllowUpload { get; set; }
        public bool AllowZip { get; set; }
        public bool AllowMove { get; set; }
        public bool AllowRename { get; set; }
    }

    // 应用配置
    public class AppConfig
    {
        public string ServerDir { get; set; } = "";
        public bool AuthEnable { get; set; } = true;
        public List<UserInfo> AuthUsers { get; set; } = new List<UserInfo>();
        public List<UserGroup> UserGroups { get; set; } = new List<UserGroup>();
        public string ListenAddr { get; set; } = "127.0.0.1";
        public int ListenPort { get; set; } = 9000;
        public int Timeout { get; set; }
        public bool AutoStartup { get; set; }

        // 界面管理
        public string WebTitle { get; set; } = "File Server";
        public string LogoText { get; set; } = "S";
        public string IcoPath { get; set; } = "";
        public string Beian { get; set; } = "";
        public int BeianSize { get; set; } = 15;
        public string Domain { get; set; } = "";
        public bool SslEnabled { get; set; }
        public string SslCertPath { get; set; } = "";
        public string SslKeyPath { get; set; } = "";

        // 上传限制（字节，0=不限）
        public long MaxUploadSize { get; set; }

        // 预览插件配置
        public List<PreviewPlugin> PreviewPlugins { get; set; } = new List<PreviewPlugin>();

        // 获取所有启用预览的后缀（小写，带点）
        public HashSet<string> GetPreviewExtensions()
        {
            var set = new HashSet<string>();
            foreach (var p in PreviewPlugins ?? new List<PreviewPlugin>())
            {
                if (p.Enabled)
                {
                    foreach (var ext in p.Extensions ?? new List<string>())
                    {
                        string e = ext.ToLowerInvariant();
                        if (!e.StartsWith(".")) e = "." + e;
                        set.Add(e);
                    }
                }
            }
            return set;
        }

        // 默认预览插件
        public static List<PreviewPlugin> DefaultPlugins => new List<PreviewPlugin>
        {
            new PreviewPlugin { Name = "图片预览", Category = "图片", Extensions = new List<string> { ".png", ".jpg", ".jpeg", ".gif", ".svg", ".bmp", ".webp" } },
            new PreviewPlugin { Name = "视频预览", Category = "视频", Extensions = new List<string> { ".mp4", ".webm" } },
            new PreviewPlugin { Name = "音频预览", Category = "音频", Extensions = new List<string> { ".mp3", ".wav", ".ogg", ".flac" } },
            new PreviewPlugin { Name = "PDF 预览", Category = "文档", Extensions = new List<string> { ".pdf" } },
            new PreviewPlugin { Name = "文本预览", Category = "文档", Extensions = new List<string> { ".txt", ".log", ".md", ".csv", ".ini", ".json", ".xml", ".yaml", ".yml" } },
            new PreviewPlugin { Name = "代码预览", Category = "文档", Extensions = new List<string> { ".css", ".js", ".html", ".htm", ".c", ".cpp", ".h", ".cs", ".py", ".java", ".go", ".rs", ".ts", ".sh", ".bat", ".ps1" } },
            new PreviewPlugin { Name = "Word 预览", Category = "Office", Extensions = new List<string> { ".docx", ".doc" } },
            new PreviewPlugin { Name = "Excel 预览", Category = "Office", Extensions = new List<string> { ".xlsx", ".xls" } },
            new PreviewPlugin { Name = "PPT 预览", Category = "Office", Extensions = new List<string> { ".pptx", ".ppt" } },
        };

        // 根据用户获取实际共享目录
        public string GetUserShareDir(UserInfo user)
        {
            if (user == null || string.IsNullOrEmpty(user.GroupName))
                return ServerDir;
            var group = UserGroups?.Find(g => g.GroupName == user.GroupName);
            if (group != null && !string.IsNullOrEmpty(group.ShareDir) && System.IO.Directory.Exists(group.ShareDir))
                return group.ShareDir;
            return ServerDir;
        }

        // 根据用户获取最终权限（用户权限 + 组权限，都为true才true）
        public UserInfo GetEffectiveUser(UserInfo user)
        {
            if (user == null) return null;
            var group = string.IsNullOrEmpty(user.GroupName) ? null : UserGroups?.Find(g => g.GroupName == user.GroupName);
            return new UserInfo
            {
                UserName = user.UserName,
                Password = user.Password,
                GroupName = user.GroupName,
                AllowDelete = group != null ? user.AllowDelete && group.AllowDelete : user.AllowDelete,
                AllowUpload = group != null ? user.AllowUpload && group.AllowUpload : user.AllowUpload,
                AllowZip = group != null ? user.AllowZip && group.AllowZip : user.AllowZip,
                AllowMove = group != null ? user.AllowMove && group.AllowMove : user.AllowMove,
                AllowRename = group != null ? user.AllowRename && group.AllowRename : user.AllowRename
            };
        }
    }
}
