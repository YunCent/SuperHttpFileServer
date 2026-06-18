using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace SuperHttpFileServer
{
    // 配置管理器（JSON 存储，线程安全）
    public static class ConfigManager
    {
        private static readonly object _lock = new object();
        private static AppConfig _config = new AppConfig();
        private static readonly string _configDir;
        private static readonly string _configFile;

        static ConfigManager()
        {
            _configDir = AppDomain.CurrentDomain.BaseDirectory;
            _configFile = Path.Combine(_configDir, "config.json");
        }

        public static string ConfigDir => _configDir;

        // 从文件加载配置
        public static AppConfig Load()
        {
            lock (_lock)
            {
                try
                {
                    if (File.Exists(_configFile))
                    {
                        string json = File.ReadAllText(_configFile);
                        _config = JsonConvert.DeserializeObject<AppConfig>(json) ?? new AppConfig();
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error("加载配置失败: " + ex.Message);
                }

                EnsureDefaults();
                return _config;
            }
        }

        // 获取当前配置
        public static AppConfig Get()
        {
            lock (_lock) { return _config; }
        }

        // 更新字段并持久化
        public static void SaveField(Action<AppConfig> update)
        {
            lock (_lock)
            {
                update(_config);
                SyncToFile();
            }
        }

        // 写入配置文件
        private static void SyncToFile()
        {
            try
            {
                string json = JsonConvert.SerializeObject(_config, Formatting.Indented);
                File.WriteAllText(_configFile, json);
            }
            catch (Exception ex)
            {
                Logger.Error("保存配置失败: " + ex.Message);
            }
        }

        // 确保配置默认值完整
        private static void EnsureDefaults()
        {
            if (_config.AuthUsers == null)
                _config.AuthUsers = new List<UserInfo>();
            if (_config.UserGroups == null)
                _config.UserGroups = new List<UserGroup>();
            if (_config.PreviewPlugins == null || _config.PreviewPlugins.Count == 0)
            {
                _config.PreviewPlugins = AppConfig.DefaultPlugins;
            }
            else
            {
                // 合并默认插件的新后缀到已保存配置
                foreach (var def in AppConfig.DefaultPlugins)
                {
                    var saved = _config.PreviewPlugins.Find(p => p.Name == def.Name);
                    if (saved == null)
                    {
                        _config.PreviewPlugins.Add(def);
                    }
                    else
                    {
                        foreach (var ext in def.Extensions)
                        {
                            if (!saved.Extensions.Contains(ext))
                                saved.Extensions.Add(ext);
                        }
                    }
                }
            }
            if (string.IsNullOrEmpty(_config.ListenAddr))
                _config.ListenAddr = "127.0.0.1";
            if (_config.ListenPort <= 0)
                _config.ListenPort = 9000;
        }

        // 快捷保存方法
        public static void ServerDirSave(string dir) => SaveField(c => c.ServerDir = dir);
        public static void ListenPortSave(int port) => SaveField(c => c.ListenPort = port);
        public static void ListenAddrSave(string addr) => SaveField(c => c.ListenAddr = addr);
        public static void TimeoutSave(int timeout) => SaveField(c => c.Timeout = timeout);
        public static void AuthEnableSave(bool enable) => SaveField(c => c.AuthEnable = enable);
        public static void AutoStartupSave(bool? enable) => SaveField(c => c.AutoStartup = enable == true);
        public static void UserListSave(List<UserInfo> users)
        {
            SaveField(c => { c.AuthUsers = users; });
        }
        public static void UserGroupListSave(List<UserGroup> groups)
        {
            SaveField(c => { c.UserGroups = groups; });
        }
        public static void WebTitleSave(string title) => SaveField(c => c.WebTitle = title);
        public static void LogoTextSave(string text) => SaveField(c => c.LogoText = text);
        public static void IcoPathSave(string path) => SaveField(c => c.IcoPath = path);
        public static void BeianSave(string beian) => SaveField(c => c.Beian = beian);
        public static void BeianSizeSave(int size) => SaveField(c => c.BeianSize = size);
        public static void DomainSave(string domain) => SaveField(c => c.Domain = domain);
        public static void SslEnabledSave(bool enable) => SaveField(c => c.SslEnabled = enable);
        public static void SslCertPathSave(string path) => SaveField(c => c.SslCertPath = path);
        public static void SslKeyPathSave(string path) => SaveField(c => c.SslKeyPath = path);
        public static void PreviewPluginsSave(List<PreviewPlugin> plugins)
        {
            SaveField(c => { c.PreviewPlugins = plugins; });
        }

    }
}
