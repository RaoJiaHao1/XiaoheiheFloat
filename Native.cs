using System;
using System.Runtime.InteropServices;

namespace XiaoheiheFloat
{
    /// <summary>
    /// 原生 Win32 / DWM 互操作声明。全部使用原生 Windows 组件，不依赖任何第三方库。
    /// </summary>
    internal static class Native
    {
        // ---------- 窗口消息 ----------
        public const int WM_NCHITTEST = 0x0084;
        public const int WM_NCCALCSIZE = 0x0083;
        public const int WM_GETMINMAXINFO = 0x0024;
        public const int WM_NCLBUTTONDOWN = 0x00A1;
        public const int WM_LBUTTONDOWN = 0x0201;
        public const int WM_EXITSIZEMOVE = 0x0232;
        public const int WM_DPICHANGED = 0x02E0;
        public const int WM_SETTINGCHANGE = 0x001A;
        public const int WM_DWMCOMPOSITIONCHANGED = 0x031E;

        // ---------- 命中测试返回值 ----------
        public const int HTCLIENT = 1;
        public const int HTCAPTION = 2;
        public const int HTLEFT = 10;
        public const int HTRIGHT = 11;
        public const int HTTOP = 12;
        public const int HTTOPLEFT = 13;
        public const int HTTOPRIGHT = 14;
        public const int HTBOTTOM = 15;
        public const int HTBOTTOMLEFT = 16;
        public const int HTBOTTOMRIGHT = 17;

        // ---------- DWM ----------
        public const int DWMWA_NCRENDERING_POLICY = 2;
        public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        public const int DWMNCRP_ENABLED = 2;
        public const int DWMWCP_ROUND = 2;

        // ---------- 热键 ----------
        public const int WM_HOTKEY = 0x0312;
        public const uint MOD_ALT = 0x0001;
        public const uint MOD_CONTROL = 0x0002;
        public const uint MOD_SHIFT = 0x0004;
        public const uint MOD_NOREPEAT = 0x4000;

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left, Top, Right, Bottom;

            public int Width { get { return Right - Left; } }
            public int Height { get { return Bottom - Top; } }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X, Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MINMAXINFO
        {
            public POINT ptReserved;
            public POINT ptMaxSize;
            public POINT ptMaxPosition;
            public POINT ptMinTrackSize;
            public POINT ptMaxTrackSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MARGINS
        {
            public int cxLeftWidth, cxRightWidth, cyTopHeight, cyBottomHeight;
        }

        [DllImport("user32.dll")]
        public static extern bool ReleaseCapture();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

        [DllImport("user32.dll")]
        public static extern int GetWindowRect(IntPtr hWnd, out RECT rect);

        [DllImport("user32.dll")]
        public static extern uint GetDpiForWindow(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("dwmapi.dll")]
        public static extern int DwmSetWindowAttribute(IntPtr hWnd, int attribute, ref int value, int size);

        [DllImport("dwmapi.dll")]
        public static extern int DwmExtendFrameIntoClientArea(IntPtr hWnd, ref MARGINS margins);

        // ---------- 截图 ----------
        public const uint PW_RENDERFULLCONTENT = 0x00000002;

        [DllImport("user32.dll")]
        public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint flags);

        // ---------- 模拟鼠标（仅用于 --hittest 自检） ----------
        public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        public const uint MOUSEEVENTF_LEFTUP = 0x0004;

        [DllImport("user32.dll")]
        public static extern bool SetCursorPos(int x, int y);

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern void mouse_event(uint flags, int dx, int dy, uint data, UIntPtr extraInfo);

        // ---------- 备用数据流（判断文件的“网络来源”标记） ----------
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr FindFirstStreamW(string fileName, int infoLevel, IntPtr data, int flags);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool FindNextStreamW(IntPtr handle, IntPtr data);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FindClose(IntPtr handle);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WIN32_FIND_STREAM_DATA
        {
            public long StreamSize;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 296)]
            public string StreamName;
        }

        /// <summary>文件是否存在指定的备用数据流（如 Zone.Identifier）。</summary>
        public static bool StreamExists(string path, string streamName)
        {
            IntPtr data = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(WIN32_FIND_STREAM_DATA)));
            IntPtr handle = IntPtr.Zero;
            try
            {
                handle = FindFirstStreamW(path, 0, data, 0);
                if (handle == (IntPtr)(-1)) return false;

                do
                {
                    WIN32_FIND_STREAM_DATA info =
                        (WIN32_FIND_STREAM_DATA)Marshal.PtrToStructure(data, typeof(WIN32_FIND_STREAM_DATA));
                    string name = info.StreamName ?? "";

                    // 返回的名字形如 ":Zone.Identifier:$DATA"，要先把首尾的冒号和类型后缀去掉
                    name = name.TrimStart(':');
                    int lastColon = name.IndexOf(':');
                    if (lastColon >= 0) name = name.Substring(0, lastColon);

                    if (string.Equals(name, streamName, StringComparison.OrdinalIgnoreCase)) return true;
                }
                while (FindNextStreamW(handle, data));
                return false;
            }
            catch
            {
                return false;
            }
            finally
            {
                Marshal.FreeHGlobal(data);
                if (handle != IntPtr.Zero && handle != (IntPtr)(-1)) FindClose(handle);
            }
        }

        // ---------- 自绘窗口的手动缩放 ----------
        [DllImport("user32.dll")]
        public static extern bool GetCursorPos(out POINT p);

        [DllImport("user32.dll")]
        public static extern short GetAsyncKeyState(int vKey);

        public const int VK_LBUTTON = 0x01;

        // 鼠标指针形状（LoadCursor 的 lpCursorName）
        public const int IDC_SIZEWE = 32644;
        public const int IDC_SIZENS = 32645;
        public const int IDC_SIZENWSE = 32642;
        public const int IDC_SIZENESW = 32643;

        [DllImport("user32.dll")]
        public static extern IntPtr LoadCursor(IntPtr hInstance, IntPtr cursorName);

        [DllImport("user32.dll")]
        public static extern IntPtr SetCursor(IntPtr hCursor);

        /// <summary>把 lParam 里的屏幕坐标（两个 16 位有符号数）解出来。</summary>
        public static System.Drawing.Point PointFromLParam(IntPtr lParam)
        {
            long v = lParam.ToInt64();
            return new System.Drawing.Point(
                unchecked((short)(v & 0xFFFF)),
                unchecked((short)((v >> 16) & 0xFFFF)));
        }

        /// <summary>开启 DWM 阴影 + Win11 圆角，边框不可见但视觉上有立体感。</summary>
        public static void ApplyModernFrame(IntPtr hWnd)
        {
            int policy = DWMNCRP_ENABLED;
            DwmSetWindowAttribute(hWnd, DWMWA_NCRENDERING_POLICY, ref policy, sizeof(int));

            int round = DWMWCP_ROUND;
            DwmSetWindowAttribute(hWnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));

            MARGINS margins = new MARGINS { cxLeftWidth = 1, cxRightWidth = 1, cyTopHeight = 1, cyBottomHeight = 1 };
            DwmExtendFrameIntoClientArea(hWnd, ref margins);
        }
    }
}
