using System;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;

namespace XiaoheiheFloat
{
    internal static class Program
    {
        private const string MutexName = @"Local\XiaoheiheFloat.SingleInstance";
        private static Mutex _instanceMutex;
        /// <summary>--shot=&lt;路径&gt;：页面加载完成后把窗口另存为 PNG，用于自检/排障。</summary>
        internal static string ShotPath;

        /// <summary>--size=宽x高：用指定逻辑尺寸启动（自检用，覆盖已保存的尺寸）。</summary>
        internal static Size? OverrideSize;

        /// <summary>--zoom=0.6：固定网页缩放（自检用，同时关闭自动适配）。</summary>
        internal static double? ZoomOverride;

        /// <summary>--no-unblock-prompt：启动时不询问解除网络来源标记（自检用）。</summary>
        internal static bool SkipUnblockPrompt;

        /// <summary>--fit / --no-fit：强制开启/关闭“窄窗口自动缩放适配”（自检用，覆盖配置）。</summary>
        internal static bool? FitOverride;

        [STAThread]
        private static void Main(string[] args)
        {
            AppDomain.CurrentDomain.UnhandledException += (s, e) => LogCrash(e.ExceptionObject as Exception);
            Application.ThreadException += (s, e) => LogCrash(e.Exception);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

            foreach (string arg in args ?? new string[0])
            {
                if (string.Equals(arg, "--diag", StringComparison.OrdinalIgnoreCase))
                {
                    Diag.Enable(AppContext.BaseDirectory);
                }
                else if (arg.StartsWith("--diaglog=", StringComparison.OrdinalIgnoreCase))
                {
                    Diag.EnableAt(arg.Substring("--diaglog=".Length).Trim('"'));
                }
                else if (string.Equals(arg, "--diag-verbose", StringComparison.OrdinalIgnoreCase))
                {
                    Diag.SetVerbose(true);
                }
                else if (arg.StartsWith("--shot=", StringComparison.OrdinalIgnoreCase))
                {
                    ShotPath = arg.Substring("--shot=".Length).Trim('"');
                }
                else if (arg.StartsWith("--size=", StringComparison.OrdinalIgnoreCase))
                {
                    string raw = arg.Substring("--size=".Length).Trim('"');
                    string[] parts = raw.Split(new[] { 'x', 'X', '*' }, StringSplitOptions.RemoveEmptyEntries);
                    int w, h;
                    if (parts.Length == 2 &&
                        int.TryParse(parts[0], out w) && int.TryParse(parts[1], out h) &&
                        w >= 200 && h >= 200)
                    {
                        OverrideSize = new Size(w, h);
                    }
                }
                else if (arg.StartsWith("--zoom=", StringComparison.OrdinalIgnoreCase))
                {
                    double z;
                    if (double.TryParse(arg.Substring("--zoom=".Length).Trim('"'),
                            System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out z) && z >= 0.2 && z <= 3.0)
                    {
                        ZoomOverride = z;
                    }
                }
                else if (string.Equals(arg, "--hittest", StringComparison.OrdinalIgnoreCase))
                {
                    // 旧参数，保留兼容：无操作
                }
                else if (string.Equals(arg, "--no-unblock-prompt", StringComparison.OrdinalIgnoreCase))
                {
                    SkipUnblockPrompt = true;
                }
                else if (string.Equals(arg, "--fit", StringComparison.OrdinalIgnoreCase))
                {
                    FitOverride = true;
                }
                else if (string.Equals(arg, "--no-fit", StringComparison.OrdinalIgnoreCase))
                {
                    FitOverride = false;
                }
                else if (arg.StartsWith("--diag-unblock=", StringComparison.OrdinalIgnoreCase))
                {
                    string target = arg.Substring("--diag-unblock=".Length).Trim('"');
                    int left = Unblocker.Diagnose(target, true);
                    Console.WriteLine("unblock diag done, stillMarked=" + (left != 0));
                    return;
                }
            }
            Diag.Log("args: " + string.Join(" ", args ?? new string[0]));

            bool createdNew;
            _instanceMutex = new Mutex(true, MutexName, out createdNew);
            if (!createdNew)
            {
                MessageBox.Show(
                    "小黑盒悬浮窗已经在运行了。\n\n如果窗口被隐藏，请按 Ctrl+Alt+H 或点击托盘的“盒”图标把它叫出来。",
                    "小黑盒悬浮窗",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // SmartScreen 拦截的根源是文件上的"网络来源"标记，启动时问一句要不要解除
            if (!Program.SkipUnblockPrompt && !Unblocker.HandleStartupMark()) return;
            if (!Unblocker.EnsureWritableFolder()) return;

            AppConfig config = AppConfig.Load();
            if (OverrideSize.HasValue)
            {
                config.X = int.MinValue;
                config.Y = int.MinValue;
                config.Width = OverrideSize.Value.Width;
                config.Height = OverrideSize.Value.Height;
            }
            if (FitOverride.HasValue)
            {
                config.FitToWidth = FitOverride.Value;
            }
            Diag.Log("home=" + config.HomeUrl + " size=" + config.Width + "x" + config.Height +
                     " opacity=" + config.Opacity + " topmost=" + config.TopMost + " zoom=" + config.Zoom);

            // WebView2 运行时缺失时，给出一条人话提示，而不是抛异常崩溃。
            try
            {
                CoreWebView2Environment.GetAvailableBrowserVersionString();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "没有找到 Microsoft Edge WebView2 运行时，悬浮窗无法显示网页。\n\n" +
                    "解决办法：安装一次“Microsoft Edge WebView2 Runtime”（微软官网免费下载，Win10/11 通常已自带），" +
                    "或者安装/更新 Microsoft Edge 浏览器后重试。\n\n" +
                    "技术信息：" + ex.Message,
                    "缺少 WebView2 运行时",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return;
            }

            string userData = ResolveUserDataFolder();
            Diag.Log("userDataFolder=" + userData);

            // WebView2 运行时读取这两个环境变量来决定浏览器启动参数；
            // 在这里设置后会被 msedgewebview2.exe 子进程继承。
            if (!string.IsNullOrWhiteSpace(config.ExtraBrowserArguments))
            {
                Environment.SetEnvironmentVariable("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS", config.ExtraBrowserArguments);
            }
            if (!string.IsNullOrWhiteSpace(config.UserAgent))
            {
                Environment.SetEnvironmentVariable("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS",
                    (config.ExtraBrowserArguments + " --user-agent=\"" + config.UserAgent + "\"").Trim());
            }

            Application.Run(new FloatWindow(config, userData, args));
            Diag.Log("message loop exited");
        }

        /// <summary>
        /// 选择 WebView2 的用户数据目录。优先 %LOCALAPPDATA%，不可写时退回到 exe 同目录。
        /// </summary>
        private static string ResolveUserDataFolder()
        {
            string[] candidates =
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XiaoheiheFloat", "WebView2"),
                Path.Combine(AppContext.BaseDirectory, "WebView2Data")
            };

            foreach (string candidate in candidates)
            {
                try
                {
                    Directory.CreateDirectory(candidate);
                    string probe = Path.Combine(candidate, ".writable");
                    File.WriteAllText(probe, "ok");
                    File.Delete(probe);
                    return candidate;
                }
                catch
                {
                    // 换下一个候选目录
                }
            }

            return Path.Combine(Path.GetTempPath(), "XiaoheiheFloat", "WebView2");
        }

        /// <summary>把未处理异常写到 exe 同目录的 crash.log，方便用户回报问题。</summary>
        private static void LogCrash(Exception ex)
        {
            try
            {
                string path = Path.Combine(AppContext.BaseDirectory, "crash.log");
                string text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + Environment.NewLine +
                              (ex == null ? "(no exception object)" : ex.ToString()) + Environment.NewLine +
                              new string('-', 60) + Environment.NewLine;
                File.AppendAllText(path, text, Encoding.UTF8);
            }
            catch
            {
                // 记录失败时无能为力
            }

            try
            {
                MessageBox.Show(
                    "悬浮窗遇到了一个未处理的错误，已经写入 crash.log。\n\n" + (ex == null ? "" : ex.Message),
                    "小黑盒悬浮窗",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            catch
            {
                // 忽略
            }
        }
    }
}
