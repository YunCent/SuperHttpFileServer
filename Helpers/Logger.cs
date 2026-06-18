using System;
using System.IO;

namespace SuperHttpFileServer
{
    // 日志记录器，线程安全，自动归档和清理
    public static class Logger
    {
        private static readonly string _logDir;
        private static readonly object _lock = new object();
        private static DateTime _currentDate;
        private static string _currentFile;

        // 实时日志事件
        public static event Action<string> OnLog;

        static Logger()
        {
            _logDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "runlog");
            _currentDate = DateTime.Now.Date;
            _currentFile = GetLogFilePath(_currentDate);
            EnsureLogDir();
        }

        public static string RunlogDir => _logDir;

        private static string GetLogFilePath(DateTime date) => Path.Combine(_logDir, "runlog.log");

        private static void EnsureLogDir()
        {
            try
            {
                if (!Directory.Exists(_logDir))
                    Directory.CreateDirectory(_logDir);
            }
            catch { }
        }

        private static void Write(string level, string message)
        {
            lock (_lock)
            {
                try
                {
                    EnsureLogDir();

                    // 日期变更时归档旧日志
                    if (DateTime.Now.Date != _currentDate)
                    {
                        string oldFile = _currentFile;
                        _currentDate = DateTime.Now.Date;
                        _currentFile = GetLogFilePath(_currentDate);

                        if (File.Exists(oldFile))
                        {
                            string archiveName = Path.Combine(_logDir,
                                string.Format("runlog_{0:yyyyMMdd}.log", _currentDate.AddDays(-1)));
                            try { File.Move(oldFile, archiveName); } catch { }
                        }

                        CleanOldLogs();
                    }

                    string line = string.Format("[{0:yyyy-MM-dd HH:mm:ss}] [{1}] {2}",
                        DateTime.Now, level, message);
                    File.AppendAllText(_currentFile, line + Environment.NewLine);

                    // 触发实时事件
                    OnLog?.Invoke(line);
                }
                catch { }
            }
        }

        // 清理7天前的日志
        private static void CleanOldLogs()
        {
            try
            {
                if (!Directory.Exists(_logDir)) return;
                var cutoff = DateTime.Now.AddDays(-7);
                foreach (var file in Directory.GetFiles(_logDir, "runlog_*.log"))
                {
                    var fi = new FileInfo(file);
                    if (fi.LastWriteTime < cutoff)
                    {
                        try { fi.Delete(); } catch { }
                    }
                }
            }
            catch { }
        }

        public static void Info(string msg) => Write("INFO", msg);
        public static void Warn(string msg) => Write("WARN", msg);
        public static void Error(string msg) => Write("ERROR", msg);

        // 读取当前日志文件全部内容（最新在前面）
        public static string ReadCurrentLog()
        {
            lock (_lock)
            {
                try
                {
                    if (File.Exists(_currentFile))
                    {
                        var lines = File.ReadAllLines(_currentFile);
                        var list = new System.Collections.Generic.List<string>(lines);
                        list.Reverse();
                        return string.Join(Environment.NewLine, list);
                    }
                }
                catch { }
                return "";
            }
        }

        // 清空当前日志文件
        public static void ClearCurrentLog()
        {
            lock (_lock)
            {
                try
                {
                    if (File.Exists(_currentFile))
                        File.WriteAllText(_currentFile, "");
                }
                catch { }
            }
        }
    }
}
