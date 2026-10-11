using System;
using System.IO;
using System.Text;

namespace XiaoheiheFloat
{
    /// <summary>
    /// 可选的诊断日志：只有带 --diag 启动时才会写文件。
    /// 用于排查“页面打不开”这类问题，正常使用不会产生任何文件。
    /// </summary>
    internal static class Diag
    {
        private static readonly object Gate = new object();
        private static string _path;
        private static bool _enabled;

        public static bool Enabled { get { return _enabled; } }

        /// <summary>--diag-verbose：额外记录高频细节（例如每次测量的三个宽度值）。</summary>
        public static bool Verbose { get; private set; }

        public static void SetVerbose(bool on)
        {
            Verbose = on;
        }

        public static void Enable(string baseDirectory)
        {
            EnableAt(Path.Combine(baseDirectory, "diag.log"));
        }

        /// <summary>指定诊断日志路径（程序目录不可写时可以放到别处）。</summary>
        public static void EnableAt(string path)
        {
            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(path, "=== 小黑盒悬浮窗诊断日志 " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " ===" + Environment.NewLine, Encoding.UTF8);
                _path = path;
                _enabled = true;
            }
            catch
            {
                _enabled = false;
            }
        }

        public static void Log(string message)
        {
            if (!_enabled) return;
            try
            {
                lock (Gate)
                {
                    File.AppendAllText(_path,
                        DateTime.Now.ToString("HH:mm:ss.fff") + "  " + message + Environment.NewLine,
                        Encoding.UTF8);
                }
            }
            catch
            {
                // 忽略
            }
        }
    }
}
