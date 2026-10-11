using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace XiaoheiheFloat
{
    /// <summary>
    /// 运行时用 GDI+ 画一个图标（托盘 / 任务栏 / 窗口共用），不依赖外部 .ico 文件。
    /// </summary>
    internal static class AppIcon
    {
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        private static Icon _cached;

        public static Icon Get()
        {
            if (_cached != null) return _cached;

            using (Bitmap bmp = Render(64))
            {
                IntPtr h = bmp.GetHicon();
                try
                {
                    using (Icon tmp = Icon.FromHandle(h))
                    {
                        _cached = (Icon)tmp.Clone();
                    }
                }
                finally
                {
                    DestroyIcon(h);
                }
            }
            return _cached;
        }

        private static Bitmap Render(int size)
        {
            Bitmap bmp = new Bitmap(size, size);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                g.Clear(Color.Transparent);

                Rectangle rect = new Rectangle(1, 1, size - 2, size - 2);
                using (GraphicsPath path = Rounded(rect, (int)(size * 0.26f)))
                using (LinearGradientBrush brush = new LinearGradientBrush(rect,
                           Color.FromArgb(255, 42, 48, 62), Color.FromArgb(255, 18, 21, 28), 55f))
                {
                    g.FillPath(brush, path);
                    using (Pen pen = new Pen(Color.FromArgb(255, 96, 165, 250), Math.Max(1f, size * 0.045f)))
                    {
                        g.DrawPath(pen, path);
                    }
                }

                using (Font font = PickFont(size * 0.52f))
                using (Brush text = new SolidBrush(Color.FromArgb(255, 232, 240, 255)))
                using (StringFormat sf = new StringFormat
                {
                    Alignment = StringAlignment.Center,
                    LineAlignment = StringAlignment.Center
                })
                {
                    g.DrawString("盒", font, text, new RectangleF(0, 0, size, size), sf);
                }
            }
            return bmp;
        }

        private static Font PickFont(float emSize)
        {
            string[] preferred = { "Microsoft YaHei UI", "Microsoft YaHei", "SimHei", "Segoe UI" };
            foreach (string name in preferred)
            {
                try
                {
                    Font f = new Font(name, emSize, FontStyle.Bold, GraphicsUnit.Pixel);
                    if (string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)) return f;
                    f.Dispose();
                }
                catch
                {
                    // 忽略：换下一个候选字体
                }
            }
            return new Font(FontFamily.GenericSansSerif, emSize, FontStyle.Bold, GraphicsUnit.Pixel);
        }

        private static GraphicsPath Rounded(Rectangle r, int radius)
        {
            int d = Math.Max(1, radius * 2);
            GraphicsPath path = new GraphicsPath();
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}
