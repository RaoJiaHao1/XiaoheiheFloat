using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace XiaoheiheFloat
{
    /// <summary>
    /// 悬浮窗配置。保存在 %LOCALAPPDATA%\XiaoheiheFloat\config.json
    /// </summary>
    internal sealed class AppConfig
    {
        public const string DefaultHome = "https://www.xiaoheihe.cn/app/bbs/home";

        public int X { get; set; } = int.MinValue;
        public int Y { get; set; } = int.MinValue;
        public int Width { get; set; } = 440;
        public int Height { get; set; } = 780;
        public double Opacity { get; set; } = 1.0;
        public bool TopMost { get; set; } = true;
        public double Zoom { get; set; } = 1.0;
        /// <summary>
        /// 缩放倍率，相对"刚好铺满窗口宽度"这个基准：
        /// 1.0 = 网页宽度正好等于窗口宽度（不用横向滚动，也不留白）；
        /// &gt;1 = 在铺满的基础上继续放大；响应式页面会重新排版以适应更窄视口。
        /// </summary>
        public double ZoomFactor { get; set; } = 1.0;
        /// <summary>窗口尺寸变化时自动调整适配基准；内嵌页面同时使用响应式样式回流。</summary>
        public bool FitToWidth { get; set; } = true;
        public string HomeUrl { get; set; } = DefaultHome;
        /// <summary>站内新窗口是否仍在悬浮窗内打开；false 则交给系统默认浏览器。</summary>
        public bool OpenLinksInside { get; set; } = false;
        /// <summary>自定义 User-Agent；留空表示使用 WebView2 默认 UA。</summary>
        public string UserAgent { get; set; } = "";
        /// <summary>额外的浏览器启动参数（WebView2 additional browser arguments）。</summary>
        public string ExtraBrowserArguments { get; set; } = "--autoplay-policy=no-user-gesture-required";

        [JsonIgnore]
        public bool HasSavedLocation { get { return X != int.MinValue && Y != int.MinValue; } }

        private static string Dir
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "XiaoheiheFloat");
            }
        }

        private static string FilePath { get { return Path.Combine(Dir, "config.json"); } }

        private static string FallbackDir
        {
            get { return Path.Combine(AppContext.BaseDirectory, "Config"); }
        }

        private static string FallbackFilePath { get { return Path.Combine(FallbackDir, "config.json"); } }

        public static AppConfig Load()
        {
            foreach (string path in new[] { FilePath, FallbackFilePath })
            {
                try
                {
                    if (!File.Exists(path)) continue;
                    AppConfig cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path));
                    if (cfg != null)
                    {
                        cfg.Normalize();
                        return cfg;
                    }
                }
                catch
                {
                    // 配置损坏时继续尝试下一个位置
                }
            }

            AppConfig fresh = new AppConfig();
            fresh.Normalize();
            return fresh;
        }

        public void Save()
        {
            Normalize();
            JsonSerializerOptions options = new JsonSerializerOptions { WriteIndented = true };
            string json = JsonSerializer.Serialize(this, options);

            // 优先写入用户配置目录；不可写（例如被安全策略限制）时退回到程序目录
            foreach (string path in new[] { FilePath, FallbackFilePath })
            {
                try
                {
                    string dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    File.WriteAllText(path, json);
                    return;
                }
                catch
                {
                    // 换下一个位置
                }
            }
        }

        private void Normalize()
        {
            Width = Math.Max(280, Width);
            Height = Math.Max(320, Height);
            Opacity = Math.Min(1.0, Math.Max(0.35, Opacity));
            ZoomFactor = Math.Min(3.0, Math.Max(1.0, ZoomFactor));   // 至少 100%（正好铺满），不允许比铺满更小
            if (string.IsNullOrWhiteSpace(HomeUrl)) HomeUrl = DefaultHome;
            if (UserAgent == null) UserAgent = "";
            if (ExtraBrowserArguments == null) ExtraBrowserArguments = "";
        }
    }
}
