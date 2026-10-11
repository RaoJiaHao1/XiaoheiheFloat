using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace XiaoheiheFloat
{
    /// <summary>
    /// 处理从网络/压缩包拿到 exe 后 Windows 打上的"网络来源"标记（Mark of the Web）。
    /// SmartScreen 就是靠这个标记弹"Windows 已保护你的电脑"的，去掉它弹窗就会消失。
    /// </summary>
    internal static class Unblocker
    {
        private const int ZoneIdentifier = 0;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool DeleteFileW(string fileName);
        /// <summary>程序目录是否可写。不可写往往说明 exe 被放在了受保护位置。</summary>
        public static bool IsFolderWritable()
        {
            try
            {
                string probe = Path.Combine(AppContext.BaseDirectory, ".write-probe");
                File.WriteAllText(probe, "ok");
                File.Delete(probe);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>目录里是否还有带 Zone.Identifier 的文件。</summary>
        public static bool HasZoneMark()
        {
            try
            {
                foreach (string file in Directory.GetFiles(AppContext.BaseDirectory, "*", SearchOption.AllDirectories))
                {
                    if (Native.StreamExists(file, "Zone.Identifier")) return true;
                }
            }
            catch
            {
                // 扫描失败就当没有
            }
            return false;
        }

        /// <summary>去掉整个程序目录里所有文件的网络来源标记，返回处理数量。</summary>
        public static int UnblockFolder()
        {
            int count = 0;
            try
            {
                foreach (string file in Directory.GetFiles(AppContext.BaseDirectory, "*", SearchOption.AllDirectories))
                {
                    // 删除 Zone.Identifier 数据流即可解除锁定
                    if (Native.StreamExists(file, "Zone.Identifier") && DeleteFileW(file + ":Zone.Identifier"))
                    {
                        count++;
                    }
                }
            }
            catch
            {
                // 忽略
            }
            return count;
        }

        /// <summary>
        /// 启动时检查：有网络来源标记就问用户要不要解除。
        /// 返回 false 表示用户选择退出程序。
        /// </summary>
        public static bool HandleStartupMark()
        {
            if (!HasZoneMark()) return true;

            DialogResult answer = MessageBox.Show(
                "检测到这个程序的文件带有“来自网络”的标记，这正是 Windows SmartScreen 提示“已阻止无法识别的应用”的原因。\n\n" +
                "是否现在解除该标记？（只是去掉文件上的 Zone.Identifier 附加信息，不改动程序本身，也不需要管理员权限）\n\n" +
                "选择“否”将退出程序，你可以右键 exe → 属性 → 勾选“解除锁定”后再运行。",
                "解除 Windows SmartScreen 阻止",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);
            if (answer != DialogResult.Yes) return false;

            int count = UnblockFolder();
            MessageBox.Show(
                count > 0
                    ? "已解除 " + count + " 个文件的网络来源标记。\n\n下次启动就不会再出现 SmartScreen 提示了。"
                    : "没有找到可解除的标记，可能已经被系统处理过。\n\n如果仍然被拦截，请右键 exe → 属性 → 勾选“解除锁定”。",
                "完成",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return true;
        }

        /// <summary>自检用：报告某个文件的标记状态，可选地解除它。</summary>
        public static int Diagnose(string path, bool remove)
        {
            bool before = Native.StreamExists(path, "Zone.Identifier");
            bool removed = false;
            if (remove && before)
            {
                removed = DeleteFileW(path + ":Zone.Identifier");
            }
            bool after = Native.StreamExists(path, "Zone.Identifier");
            try
            {
                File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "unblock-test.log"),
                    DateTime.Now.ToString("HH:mm:ss") + "  path=" + path +
                    " before=" + before + " removed=" + removed + " after=" + after + Environment.NewLine);
            }
            catch
            {
                // 忽略
            }
            return after ? 1 : 0;
        }

        /// <summary>需要写文件时必须有一个可写目录，不然程序运行一半就会失败。</summary>
        public static bool EnsureWritableFolder()
        {
            if (IsFolderWritable()) return true;

            MessageBox.Show(
                "程序所在目录不可写：\n" + AppContext.BaseDirectory + "\n\n" +
                "请把整个程序文件夹移动到可写的位置再运行，例如：\n" +
                "  · 桌面上的一个新文件夹\n" +
                "  · D:\\Tools\\XiaoheiheFloat\\\n" +
                "  · %LOCALAPPDATA%\\XiaoheiheFloat\\\n\n" +
                "（放在 C:\\Program Files 或系统保护目录里会因为权限不足而无法工作）",
                "放置位置不合适",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return false;
        }
    }
}
