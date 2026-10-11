using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace XiaoheiheFloat
{
    /// <summary>
    /// 无边框置顶悬浮窗：自绘标题栏 + WebView2 网页视图 + 底部状态条。
    /// </summary>
    internal sealed class FloatWindow : Form
    {
        private const int HotKeyToggle = 0x4A01;
        private const int HotKeyTopMost = 0x4A02;
        /// <summary>自动适配时允许的最小缩放：窗口很窄时页面需要缩得比较小才放得下。</summary>
        private const double MinFitZoom = 0.35;
        /// <summary>
        /// "适应"目标布局宽度（CSS px）：把页面缩放到这个虚拟视口宽度再等比放大到窗口。
        /// 实测该站点在 ≤900 CSS px 时是单栏布局（正文列占满、无右侧热门社区/下载栏），
        /// 取 880 留出安全余量——这样"适应"只展示左侧贴文，右边无关内容整个不出现在窗口里。
        /// </summary>
        private const double FitTargetCssWidth = 880.0;

        private static readonly Color ColBg = Color.FromArgb(255, 22, 25, 32);
        private static readonly Color ColTitleBg = Color.FromArgb(255, 28, 32, 41);
        private static readonly Color ColStatusBg = Color.FromArgb(255, 26, 30, 38);
        private static readonly Color ColText = Color.FromArgb(255, 226, 232, 240);
        private static readonly Color ColSubText = Color.FromArgb(255, 138, 148, 165);
        private static readonly Color ColHover = Color.FromArgb(255, 45, 51, 64);
        private static readonly Color ColAccent = Color.FromArgb(255, 74, 158, 255);
        private static readonly Color ColLine = Color.FromArgb(255, 40, 46, 58);
        private static readonly Color ColCloseHover = Color.FromArgb(255, 214, 69, 69);

        private readonly AppConfig _config;
        private readonly string _userDataFolder;

        private readonly Panel _titleBar;
        private readonly Panel _statusBar;
        private readonly Panel _progressBar;
        private readonly Panel[] _edgeStrips;
        private readonly WebView2 _web;
        private readonly NotifyIcon _tray;
        private readonly ContextMenuStrip _menu;
        private readonly Timer _initTimer;
        private readonly Timer _captionTimer;

        private Icon _windowIcon;
        private Size _logicalSize;

        private string _caption = "小黑盒社区";
        private string _statusText = "正在启动内置浏览器…";
        private double _progress;
        private double _baseFitZoom = 1.0;
        private bool _fitBusy;
        private bool _fitDirty;
        private int _fitPending;
        /// <summary>自适应自己更新滑块时置位，避免把"程序设置值"误判成"用户改了缩放"。</summary>
        private bool _syncingSlider;
        private bool _coreReady;
        private bool _initializing;
        private bool _closeToTray = true;
        private bool _allowExit;
        private bool _trayHintShown;

        // 底部状态条上的缩放控件
        private ZoomSlider _zoomSlider;
        private ToolStripMenuItem _zoomMenu;
        private ToolStripMenuItem _fitMenu;
        private Rectangle _zoomMinusRect;
        private Rectangle _zoomPlusRect;
        private Rectangle _zoomFitRect;
        private int _hoverStatus = -1;

        private int _hoverButton = -1;
        private int _menuButtonWidth;
        private int _homeButtonWidth;
        private int _buttonWidth;
        private int _titleBarHeight;
        private int _statusBarHeight;

        private Rectangle _menuRect;
        private Rectangle _homeRect;
        private Rectangle _minRect;
        private Rectangle _maxRect;
        private Rectangle _closeRect;

        // ---------- 子控件的 WM_NCHITTEST 转发（实现边框拖动缩放） ----------
        private readonly List<NativeWindow> _forwarders = new List<NativeWindow>();

        public FloatWindow(AppConfig config, string userDataFolder, string[] args)
        {
            _config = config;
            _userDataFolder = userDataFolder;

            // ---------- 窗体本体：无边框 + 悬浮 ----------
            Text = "小黑盒悬浮窗";
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            BackColor = ColBg;
            ForeColor = ColText;
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
            KeyPreview = true;
            AutoScaleMode = AutoScaleMode.None;

            if (_config.HasSavedLocation)
            {
                Location = new Point(_config.X, _config.Y);
                Size = new Size(_config.Width, _config.Height);
            }
            else
            {
                Size = new Size(_config.Width, _config.Height);
                Location = DefaultLocation(Size);
            }

            Opacity = _config.Opacity;
            TopMost = _config.TopMost;
            _logicalSize = new Size(_config.Width, _config.Height);

            try
            {
                _windowIcon = AppIcon.Get();
                Icon = _windowIcon;
            }
            catch
            {
                // 图标失败不影响主流程
            }

            // ---------- 顶部进度条 / 标题栏 / 底部状态条 ----------
            _progressBar = new Panel { Dock = DockStyle.Top, Height = 3, BackColor = ColBg, Visible = false };
            _progressBar.Paint += PaintProgressBar;

            _titleBar = new Panel { Dock = DockStyle.Top, BackColor = ColTitleBg };
            _titleBar.Paint += PaintTitleBar;
            _titleBar.MouseDown += TitleBarMouseDown;
            _titleBar.MouseMove += TitleBarMouseMove;
            _titleBar.MouseLeave += TitleBarMouseLeave;
            _titleBar.MouseUp += TitleBarMouseUp;
            _titleBar.MouseDoubleClick += TitleBarDoubleClick;

            _statusBar = new Panel { Dock = DockStyle.Bottom, BackColor = ColStatusBg };
            _statusBar.Paint += PaintStatusBar;
            _statusBar.MouseMove += StatusBarMouseMove;
            _statusBar.MouseLeave += StatusBarMouseLeave;
            _statusBar.MouseUp += StatusBarMouseUp;

            // 状态条右侧的缩放滑块：数值是"相对铺满窗口"的倍率，100% = 正好铺满
            _zoomSlider = new ZoomSlider
            {
                Value = (int)Math.Round(Math.Max(1.0, Math.Min(3.0, _config.ZoomFactor)) * 100)
            };
            _zoomSlider.ValueChanged += ZoomSliderValueChanged;
            _statusBar.Controls.Add(_zoomSlider);

            // 四条边缘抓取条：无边框窗口里最可靠的缩放入口（自己直接触发系统缩放）
            _edgeStrips = new Panel[4];
            for (int i = 0; i < _edgeStrips.Length; i++)
            {
                Panel strip = new Panel { BackColor = ColBg, TabStop = false };
                strip.MouseDown += EdgeStripMouseDown;
                strip.MouseMove += EdgeStripMouseMove;
                _edgeStrips[i] = strip;
                Controls.Add(strip);
            }

            // ---------- 网页视图 ----------
            _web = new WebView2
            {
                Dock = DockStyle.Fill,
                DefaultBackgroundColor = Color.White
            };
            _web.CoreWebView2InitializationCompleted += OnCoreInitialized;
            _web.NavigationStarting += OnNavigationStarting;
            _web.NavigationCompleted += OnNavigationCompleted;
            _web.MouseUp += WebMouseUp;
            _web.KeyDown += WebKeyDown;

            Controls.Add(_web);
            Controls.Add(_statusBar);
            Controls.Add(_progressBar);
            Controls.Add(_titleBar);

            _tray = BuildTrayIcon();
            _menu = BuildContextMenu();

            // 控件建好之后再设最小尺寸，避免构造期触发 OnResize
            MinimumSize = new Size(300, 360);

            // WebView2 初始化偶尔会慢启动或失败，失败时定时重试
            _initTimer = new Timer { Interval = 2500 };
            _initTimer.Tick += (s, e) => StartWebView();

            _captionTimer = new Timer { Interval = 4000 };
            _captionTimer.Tick += (s, e) => UpdateCaption();
        }

        // ================= 生命周期 =================

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);

            // 句柄建立之后才知道窗口所在显示器的缩放比例，这里把逻辑尺寸换算成物理像素
            try
            {
                Size physical = LogicalToDeviceUnits(_logicalSize);
                if (physical.Width > 0 && physical.Height > 0 && Size != physical)
                {
                    Size = physical;
                }
                Diag.Log("dpi=" + ScaleFactor() + " logical=" + _logicalSize + " physical=" + Size + " at " + Location);
            }
            catch
            {
                // 忽略：保持已有尺寸
            }

            Native.ApplyModernFrame(Handle);
            Native.RegisterHotKey(Handle, HotKeyToggle, Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT, (uint)Keys.H);
            Native.RegisterHotKey(Handle, HotKeyTopMost, Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT, (uint)Keys.T);
            EnsureOnScreen();
            LayoutChrome();
            AttachHitTestForwarders();
            StartWebView();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            EnsureOnScreen();
            LayoutChrome();
            Diag.Log("shown: rect=" + Bounds + " client=" + ClientSize + " handle=" + Handle.ToInt64());
            _captionTimer.Start();
        }

        protected override void OnDpiChanged(DpiChangedEventArgs e)
        {
            base.OnDpiChanged(e);
            LayoutChrome();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            LayoutChrome();
            if (_titleBar != null) _titleBar.Invalidate();
            if (_statusBar != null) _statusBar.Invalidate();
            if (Diag.Enabled && _web != null)
            {
                Diag.Log("resize: client=" + ClientSize + " web=" + _web.Bounds +
                         " state=" + WindowState + " title=" + _titleBar.Height + " status=" + _statusBar.Height);
            }
            if (_web != null && _coreReady) ScheduleFit();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_closeToTray && !_allowExit && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                ShowTrayHint();
                return;
            }

            SaveState();
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { _captionTimer.Stop(); _initTimer.Stop(); } catch { }
                if (IsHandleCreated)
                {
                    Native.UnregisterHotKey(Handle, HotKeyToggle);
                    Native.UnregisterHotKey(Handle, HotKeyTopMost);
                }
                if (_tray != null)
                {
                    _tray.Visible = false;
                    _tray.Dispose();
                }
                if (_menu != null) _menu.Dispose();
                if (_captionTimer != null) _captionTimer.Dispose();
                if (_initTimer != null) _initTimer.Dispose();
                if (_windowIcon != null) _windowIcon.Dispose();
            }
            base.Dispose(disposing);
        }

        // ================= WebView2 =================

        private async void StartWebView()
        {
            if (_coreReady || _initializing || IsDisposed || _web.IsDisposed) return;

            _initializing = true;
            try
            {
                CoreWebView2Environment env =
                    await CoreWebView2Environment.CreateAsync(null, _userDataFolder, null).ConfigureAwait(true);
                await _web.EnsureCoreWebView2Async(env).ConfigureAwait(true);
                // 初始化成功后由 CoreWebView2InitializationCompleted 事件继续
            }
            catch (Exception ex)
            {
                OnInitFailed(ex);
            }
            finally
            {
                _initializing = false;
            }
        }

        private void OnInitFailed(Exception ex)
        {
            if (IsDisposed) return;
            Diag.Log("init failed: " + Flatten(ex));
            _coreReady = false;
            _initTimer.Start();
            _statusText = "内置浏览器启动失败，正在重试…（" + Shorten(Flatten(ex), 52) + "）";
            _progressBar.Visible = false;
            _statusBar.Invalidate();
        }

        private async void OnCoreInitialized(object sender, CoreWebView2InitializationCompletedEventArgs e)
        {
            if (!e.IsSuccess)
            {
                OnInitFailed(e.InitializationException);
                return;
            }

            _initTimer.Stop();
            _coreReady = true;
            Diag.Log("CoreWebView2 ready, runtime=" + SafeBrowserVersion());

            try
            {
                CoreWebView2 core = _web.CoreWebView2;

                // 让网页使用白色底，避免暗色窗体透出
                _web.DefaultBackgroundColor = Color.White;

                core.Settings.AreDefaultContextMenusEnabled = false;
                core.Settings.AreDevToolsEnabled = false;
                core.Settings.IsStatusBarEnabled = false;
                core.Settings.IsZoomControlEnabled = true;
                core.Settings.IsScriptEnabled = true;
                core.Settings.IsWebMessageEnabled = true;
                core.Settings.IsBuiltInErrorPageEnabled = true;
                TrySet(core.Settings, "AreBrowserAcceleratorKeysEnabled", true);
                TrySet(core.Settings, "IsPasswordAutosaveEnabled", false);
                TrySet(core.Settings, "IsGeneralAutofillEnabled", false);
                TrySet(core.Settings, "UserAgent", string.IsNullOrWhiteSpace(_config.UserAgent) ? null : _config.UserAgent);

                core.NewWindowRequested += OnNewWindowRequested;
                core.WindowCloseRequested += (s2, e2) => Close();
                core.DocumentTitleChanged += (s2, e2) =>
                {
                    _caption = SafeTitle(core.DocumentTitle);
                    _titleBar.Invalidate();
                };
                core.ContainsFullScreenElementChanged += (s2, e2) => ApplyFullScreen(core.ContainsFullScreenElement);
                core.ProcessFailed += (s2, e2) =>
                {
                    Diag.Log("process failed: " + e2.ProcessFailedKind + " exit=" + e2.ExitCode + " desc=" + e2.ProcessDescription);
                    _statusText = "浏览器内核异常（" + e2.ProcessFailedKind + "），正在重试…";
                    _statusBar.Invalidate();
                };

                _baseFitZoom = 1.0;
                if (Program.ZoomOverride.HasValue)
                {
                    // 自检：固定"铺满基准"，便于观察某个缩放下的排版
                    _web.ZoomFactor = Program.ZoomOverride.Value;
                    _config.FitToWidth = false;
                }
                else
                {
                    ApplyZoom();
                }
                UpdateZoomUi();

                // 悬浮窗很窄，页面自带的滚动条会占掉一条宽度，这里让它细一点、不那么显眼
                await TryAddInitialScript(core).ConfigureAwait(true);

                Navigate(_config.HomeUrl);
            }
            catch (Exception ex)
            {
                OnInitFailed(ex);
            }
        }

        /// <summary>只注入无副作用的样式：细滚动条。
        /// 注意：绝不能在页面文档上注入 *{max-width:100%;min-width:0} 之类的全局规则，
        /// 它会破坏站点自身的固定宽布局/弹出层/图片容器（曾导致"整个页面崩坏"）。</summary>
        private static async Task TryAddInitialScript(CoreWebView2 core)
        {
            try
            {
                System.Reflection.MethodInfo mi = core.GetType().GetMethod(
                    "AddScriptToExecuteOnDocumentCreatedAsync", new[] { typeof(string) });
                if (mi == null) return;

                const string css =
                    "(function(){try{var styleId='xiaoheihe-scrollbar-style';" +
                    "if(document.getElementById(styleId))return;" +
                    "var s=document.createElement('style');s.id=styleId;" +
                    "s.textContent='::-webkit-scrollbar{width:8px;height:8px}'+" +
                    "'::-webkit-scrollbar-thumb{background:rgba(128,136,156,.45);border-radius:8px}'+" +
                    "'::-webkit-scrollbar-track{background:transparent}';" +
                    "(document.head||document.documentElement).appendChild(s);" +
                    "}catch(e){}})();";

                object task = mi.Invoke(core, new object[] { css });
                Task registration = task as Task;
                if (registration != null) await registration.ConfigureAwait(true);
            }
            catch
            {
                // 忽略
            }
        }

        private void OnNewWindowRequested(object sender, CoreWebView2NewWindowRequestedEventArgs e)
        {
            if (_config.OpenLinksInside)
            {
                return; // 交给 WebView2 自己弹新窗口
            }

            e.Handled = true;
            OpenExternal(e.Uri);
        }

        private void OnNavigationStarting(object sender, CoreWebView2NavigationStartingEventArgs e)
        {
            Diag.Log("navigation starting: " + e.Uri);
            _progress = 0.08;
            _progressBar.Visible = true;
            _statusText = "正在打开 " + Shorten(e.Uri, 56);
            _titleBar.Invalidate();
            _statusBar.Invalidate();
            _progressBar.Invalidate();
        }

        private void OnNavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            Diag.Log("navigation completed: success=" + e.IsSuccess + " error=" + e.WebErrorStatus +
                     " url=" + SafeSource() + " title=" + SafeTitle(SafeDocumentTitle()));
            _progress = 1.0;
            _progressBar.Invalidate();

            if (e.IsSuccess)
            {
                _statusText = "已加载 · " + Shorten(SafeSource(), 56);
                UpdateCaption();
            }
            else
            {
                _statusText = "加载失败：" + DescribeWebError(e.WebErrorStatus) + "（F5 重试，或用菜单里的系统浏览器打开）";
            }

            _titleBar.Invalidate();
            _statusBar.Invalidate();

            Timer hide = new Timer { Interval = 700 };
            hide.Tick += (s, args) =>
            {
                hide.Stop();
                hide.Dispose();
                _progress = 0;
                _progressBar.Visible = false;
                _progressBar.Invalidate();
            };
            hide.Start();

            // 页面加载完了，重新按当前窗口宽度做一次自适应
            _baseFitZoom = 1.0;
            ScheduleFit();

            MaybeAutoScreenshot();
        }

        /// <summary>带 --shot=路径 启动时，等页面画出来之后把窗口存成 PNG 并退出。</summary>
        private void MaybeAutoScreenshot()
        {
            if (string.IsNullOrEmpty(Program.ShotPath)) return;

            // 等页面懒加载完成再截图，否则可能拍到"看到更多内容"之类的占位
            Timer shot = new Timer { Interval = 6000 };
            shot.Tick += (s, e) =>
            {
                shot.Stop();
                shot.Dispose();
                bool ok = CaptureWindowTo(Program.ShotPath);
                Diag.Log("auto screenshot -> " + Program.ShotPath + " ok=" + ok);
                ExitApp();
            };
            shot.Start();
        }

        /// <summary>用 PrintWindow 把窗口（含 WebView2 内容）保存成 PNG。</summary>
        private bool CaptureWindowTo(string path)
        {
            if (!IsHandleCreated) return false;
            try
            {
                Native.RECT rc;
                if (Native.GetWindowRect(Handle, out rc) == 0) return false;
                Diag.Log("capture: client=" + ClientSize + " web=" + _web.Bounds + " webVisible=" + _web.Visible + " formBounds=" + Bounds);
                int w = rc.Width;
                int h = rc.Height;
                if (w <= 0 || h <= 0) return false;

                using (Bitmap bmp = new Bitmap(w, h))
                {
                    using (Graphics g = Graphics.FromImage(bmp))
                    {
                        IntPtr hdc = g.GetHdc();
                        try
                        {
                            Native.PrintWindow(Handle, hdc, Native.PW_RENDERFULLCONTENT);
                        }
                        finally
                        {
                            g.ReleaseHdc(hdc);
                        }
                    }

                    string dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                }
                return true;
            }
            catch (Exception ex)
            {
                Diag.Log("screenshot failed: " + ex.Message);
                return false;
            }
        }

        private void Navigate(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) url = AppConfig.DefaultHome;
            if (!_coreReady)
            {
                _statusText = "内置浏览器还没准备好…";
                _statusBar.Invalidate();
                return;
            }

            try
            {
                _web.CoreWebView2.Navigate(url);
            }
            catch (Exception ex)
            {
                _statusText = "导航失败：" + Shorten(Flatten(ex), 60);
                _statusBar.Invalidate();
            }
        }

        private void GoHome() { Navigate(_config.HomeUrl); }

        private void GoBack()
        {
            if (_coreReady && _web.CoreWebView2.CanGoBack) _web.CoreWebView2.GoBack();
        }

        private void GoForward()
        {
            if (_coreReady && _web.CoreWebView2.CanGoForward) _web.CoreWebView2.GoForward();
        }

        private void Reload()
        {
            if (_coreReady) _web.CoreWebView2.Reload();
        }

        /// <summary>
        /// 设置"相对铺满窗口"的缩放倍率：1.0 = 网页宽度正好等于窗口宽度。
        /// 实际生效的 WebView2 缩放 = 铺满基准 × 倍率。
        /// </summary>
        private void SetRelZoom(double rel)
        {
            if (!_coreReady) return;
            try
            {
                _config.ZoomFactor = Math.Min(3.0, Math.Max(1.0, rel));
                ApplyZoom();
                UpdateZoomUi();
            }
            catch
            {
                // 忽略
            }
        }

        /// <summary>把配置和滑块状态合成为实际缩放并应用。</summary>
        private void ApplyZoom()
        {
            if (!_coreReady) return;
            try
            {
                double z = EffectiveZoom();
                _web.ZoomFactor = z;
                Diag.Log("apply zoom: base=" + _baseFitZoom.ToString("F3") +
                         " rel=" + _config.ZoomFactor.ToString("F2") + " -> " + z.ToString("F3"));
            }
            catch
            {
                // 忽略
            }
        }

        /// <summary>实际缩放 = 铺满基准 × 用户倍率。</summary>
        private double EffectiveZoom()
        {
            double rel = Math.Max(1.0, Math.Min(3.0, _config.ZoomFactor));
            return Math.Max(0.2, Math.Min(3.0, _baseFitZoom * rel));
        }

        /// <summary>把当前状态同步到状态条滑块、百分比文字和右键菜单。</summary>
        private void UpdateZoomUi()
        {
            if (_zoomSlider != null)
            {
                // 关键：这是程序在同步显示，不是用户操作，不能让 ValueChanged 里的
                // "用户手动缩放"逻辑把自适应关掉（否则自适应会自己把自己停掉）
                _syncingSlider = true;
                try
                {
                    _zoomSlider.ValueFromZoom(_config.ZoomFactor);
                }
                finally
                {
                    _syncingSlider = false;
                }
            }
            SyncZoomMenuChecks();
            if (_statusBar != null) _statusBar.Invalidate();
        }

        private void SyncZoomMenuChecks()
        {
            if (_zoomMenu == null) return;
            int current = _zoomSlider != null
                ? _zoomSlider.Value
                : (int)Math.Round(_config.ZoomFactor * 100);
            foreach (ToolStripItem item in _zoomMenu.DropDownItems)
            {
                ToolStripMenuItem mi = item as ToolStripMenuItem;
                if (mi == null) continue;
                int value;
                string text = mi.Text.TrimEnd('%');
                bool parse = int.TryParse(text, out value);
                mi.Checked = parse && value == current;
            }
        }

        /// <summary>自检：把页面主要容器的结构打到诊断日志（--diag-probe）。</summary>

        // ================= 宽度自适应（小黑盒页面不随窗口变窄回流，这里用缩放补齐） =================

        private const string MeasureWidthScript =
            "(function(){try{" +
            "var d=document.documentElement,b=document.body;" +
            "var vw=window.innerWidth||d.clientWidth||1;" +
            "var f=document.querySelector('.bbs-home__content-list')" +
            "||document.querySelector('.hb-cpt__scroll-list.hb-bbs-home')" +
            "||document.querySelector('main.list')||document.querySelector('main');" +
            "var w=0,left=0,kind='page',sw=0,dbg='';" +
            "if(f){var r=f.getBoundingClientRect();" +
            "var fw=r.width;" +
            "dbg='rect='+Math.round(r.width)+' scroll='+Math.round(f.scrollWidth||0)+' client='+Math.round(f.clientWidth||0)+' used='+Math.round(fw);" +
            "if(fw>120){w=fw;left=r.left;kind='feed';}" +
            "var p=f.parentElement,up=0;" +
            "while(p&&up<4){var sib=p.children;" +
            "for(var i=0;i<sib.length;i++){var s=sib[i];if(s===f)continue;" +
            "var sr=s.getBoundingClientRect();" +
            "if(sr.width>60&&sr.height>150&&sr.left>r.left+r.width*0.55){sw=Math.max(sw,sr.left);break;}}" +
            "if(sw>0)break;p=p.parentElement;up++;}" +
            "if(sw>0&&sw-left>150&&sw-left<r.width*2)kind='feed+side';}" +
            "if(!(w>0)){w=Math.max(b?b.scrollWidth:0,d?d.scrollWidth:0,b?b.offsetWidth:0,d?d.offsetWidth:0);left=0;}" +
            "if(!(w>0))w=vw;" +
            "return JSON.stringify({vw:vw,w:w,left:left,kind:kind,side:sw,dbg:dbg});" +
            "}catch(e){return 'ERR:'+e.message;}})();";

        private const string DomHealthScript =
            "(function(){try{var imgs=document.images||[];var broken=0;" +
            "for(var i=0;i<imgs.length;i++){if(imgs[i].complete&&imgs[i].naturalWidth===0)broken++;}" +
            "var feed=document.querySelector('.bbs-home__content-list');" +
            "return JSON.stringify({imgs:imgs.length,broken:broken," +
            "feedItems:feed?feed.children.length:-1," +
            "scrollW:document.documentElement.scrollWidth||0,iw:window.innerWidth||0});" +
            "}catch(e){return 'ERR:'+e.message;}})();";

        /// <summary>窗口尺寸变化后（防抖 150ms）重新计算宽度适配。</summary>
        private void ScheduleFit()
        {
            if (!_config.FitToWidth || !_coreReady) return;

            // 正在测量/应用缩放时不要叠加：标脏，等这一次做完立刻补做
            if (_fitBusy)
            {
                _fitDirty = true;
                return;
            }

            _fitPending++;
            Timer t = new Timer { Interval = 150 };
            t.Tick += (s, e) =>
            {
                t.Stop();
                t.Dispose();
                _fitPending--;
                if (_fitPending <= 0) ApplyFitToWidth();
            };
            t.Start();
        }

        /// <summary>
        /// 宽度自适应（"适应"）：把页面放在 880 CSS px 的"单栏布局"下，再等比放大到填满窗口。
        ///
        /// 为什么用固定目标宽度而不是测量"正文列有多宽"：
        /// 宽窗口下站点渲染双栏（左贴文 + 右热门社区/下载栏）。若按当前正文列宽度适配，
        /// 右侧栏仍占着屏幕（视觉上就是"展示了整个页面"）；而把虚拟视口缩到 880 CSS px 后，
        /// 站点自身切换为单栏紧凑布局，右栏不再渲染，正文列直接占满整个窗口。
        /// 缩放倍率 = 窗口设备像素宽度 ÷ 880，与页面内容无关，所以是确定性动作（幂等）。
        /// </summary>
        private async void ApplyFitToWidth()
        {
            if (!_config.FitToWidth || !_coreReady || _fitBusy || IsDisposed) return;
            _fitBusy = true;
            try
            {
                double device = Math.Max(120, _web.ClientSize.Width);
                double baseZoom = Math.Max(MinFitZoom, Math.Min(3.0, device / FitTargetCssWidth));
                _baseFitZoom = baseZoom;
                ApplyZoom();
                UpdateZoomUi();

                Diag.Log("fit preset: device=" + Math.Round(device) + " targetCss=" + FitTargetCssWidth +
                         " -> base=" + baseZoom.ToString("F3") +
                         " webZoom=" + _web.ZoomFactor.ToString("F3") +
                         " rel=" + _config.ZoomFactor.ToString("F2"));

                // 验证（只记日志，不参与决策）：CSS 视口应 ≈880，且不应存在右侧栏
                try
                {
                    await Task.Delay(300).ConfigureAwait(true);
                    FitMeasure m = await MeasurePageAsync().ConfigureAwait(true);
                    if (m != null)
                    {
                        Diag.Log("fit check: cssVp=" + Math.Round(m.Viewport) +
                                 " kind=" + m.Kind + " content=" + Math.Round(m.Content) +
                                 " side=" + Math.Round(m.Side) + " left=" + Math.Round(m.Left));
                    }

                    if (Diag.Verbose)
                    {
                        string health = await _web.CoreWebView2.ExecuteScriptAsync(DomHealthScript).ConfigureAwait(true);
                        string hjson = health;
                        if (!string.IsNullOrEmpty(hjson) && hjson.Length > 1 && hjson[0] == '"')
                        {
                            hjson = System.Text.Json.JsonSerializer.Deserialize<string>(hjson);
                        }
                        Diag.Log("dom health: " + (hjson ?? "(null)"));
                    }
                }
                catch
                {
                    // 验证失败不影响主流程
                }
            }
            catch (Exception ex)
            {
                Diag.Log("fit failed: " + ex.Message);
            }
            finally
            {
                _fitBusy = false;
                // 缩放过程中窗口又变了：立刻补做一次
                if (_fitDirty && !IsDisposed)
                {
                    _fitDirty = false;
                    ScheduleFit();
                }
            }
        }

        private sealed class FitMeasure
        {
            public double Viewport;
            public double Content;
            /// <summary>正文列左边距（CSS px）。有右侧栏时，"可用宽度"只到右栏左边缘。</summary>
            public double Left;
            public double Side;
            public string Kind = "page";
        }

        private async Task<FitMeasure> MeasurePageAsync()
        {
            string raw = await _web.CoreWebView2.ExecuteScriptAsync(MeasureWidthScript).ConfigureAwait(true);
            if (string.IsNullOrEmpty(raw)) return null;

            // ExecuteScriptAsync 返回的是 JSON 字符串，脚本的返回值会被再包一层引号
            string json = raw;
            if (json.Length > 1 && json[0] == '"')
            {
                try { json = System.Text.Json.JsonSerializer.Deserialize<string>(json); }
                catch { json = raw; }
            }
            if (string.IsNullOrEmpty(json) || json.StartsWith("ERR:", StringComparison.Ordinal)) return null;

            double vw = 0, w = 0, left = 0, side = 0;
            string kind = "page";
            string dbg = "";
            try
            {
                using (System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(json))
                {
                    if (doc.RootElement.TryGetProperty("vw", out var a)) vw = a.GetDouble();
                    if (doc.RootElement.TryGetProperty("w", out var b)) w = b.GetDouble();
                    if (doc.RootElement.TryGetProperty("left", out var c)) left = c.GetDouble();
                    if (doc.RootElement.TryGetProperty("side", out var e)) side = e.GetDouble();
                    if (doc.RootElement.TryGetProperty("kind", out var f) &&
                        f.ValueKind == System.Text.Json.JsonValueKind.String) kind = f.GetString();
                    if (doc.RootElement.TryGetProperty("dbg", out var g) &&
                        g.ValueKind == System.Text.Json.JsonValueKind.String) dbg = g.GetString();
                }
            }
            catch
            {
                return null;
            }
            if (vw <= 0 || w <= 0) return null;
            if (Diag.Enabled && !string.IsNullOrEmpty(dbg) && Diag.Verbose)
            {
                Diag.Log("measure: " + dbg + " -> content=" + Math.Round(w) + " side=" + Math.Round(side));
            }
            return new FitMeasure { Viewport = vw, Content = w, Left = left, Side = side, Kind = kind };
        }

        /// <summary>
        /// 按名称设置 WebView2 设置项：不同版本的 SDK 属性集不完全一致，
        /// 不存在的属性直接跳过，保证在任何 WebView2 运行时上都能启动。
        /// </summary>
        private static void TrySet(object target, string propertyName, object value)
        {
            if (target == null || value == null) return;
            try
            {
                System.Reflection.PropertyInfo pi = target.GetType().GetProperty(propertyName);
                if (pi == null || !pi.CanWrite) return;
                if (pi.PropertyType != value.GetType()) return;
                pi.SetValue(target, value, null);
            }
            catch
            {
                // 忽略：属于可选的增强项
            }
        }

        private void OpenExternal(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return;
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                _statusText = "无法调用系统浏览器：" + Shorten(Flatten(ex), 48);
                _statusBar.Invalidate();
            }
        }

        // ================= 布局与自绘 =================

        private int ScaleFactor()
        {
            int dpi = 96;
            try
            {
                if (IsHandleCreated) dpi = (int)Native.GetDpiForWindow(Handle);
            }
            catch
            {
                dpi = 96;
            }
            if (dpi <= 0) dpi = DeviceDpi > 0 ? DeviceDpi : 96;
            return dpi;
        }

        private void LayoutChrome()
        {
            // 构造函数早期（例如设置 MinimumSize）也会触发 OnResize，此时控件字段还没赋值
            if (_titleBar == null || _statusBar == null || _progressBar == null) return;

            int dpi = ScaleFactor();
            Func<int, int> s = v => (int)Math.Round(v * dpi / 96.0);

            _titleBarHeight = s(34);
            _statusBarHeight = s(26);
            _menuButtonWidth = s(32);
            _homeButtonWidth = s(30);
            _buttonWidth = s(38);

            _titleBar.Height = _titleBarHeight;
            _statusBar.Height = _statusBarHeight;
            _progressBar.Height = s(3);

            int w = ClientSize.Width;
            _menuRect = new Rectangle(0, 0, _menuButtonWidth, _titleBarHeight);
            _homeRect = new Rectangle(_menuButtonWidth, 0, _homeButtonWidth, _titleBarHeight);
            _closeRect = new Rectangle(w - _buttonWidth, 0, _buttonWidth, _titleBarHeight);
            _maxRect = new Rectangle(_closeRect.X - _buttonWidth, 0, _buttonWidth, _titleBarHeight);
            _minRect = new Rectangle(_maxRect.X - _buttonWidth, 0, _buttonWidth, _titleBarHeight);

            LayoutStatusControls();
            LayoutEdgeStrips();
        }

        /// <summary>摆放状态条右侧的缩放控件（− [滑块] 适应 + 百分比）。</summary>
        private void LayoutStatusControls()
        {
            if (_statusBar == null || _zoomSlider == null) return;

            int dpi = ScaleFactor();
            Func<int, int> s = v => (int)Math.Round(v * dpi / 96.0);

            int w = _statusBar.ClientSize.Width;
            int btn = s(18);
            int cy = Math.Max(0, (_statusBarHeight - btn) / 2);

            // 从右往左排：百分比文字 | + | 适应 | 滑块 | −
            int pctWidth = s(40);
            int right = w - s(6) - pctWidth;
            _zoomPlusRect = new Rectangle(right - btn, cy, btn, btn);
            right = _zoomPlusRect.Left - s(4);
            _zoomFitRect = new Rectangle(right - s(34), cy, s(34), btn);
            right = _zoomFitRect.Left - s(4);

            int sliderW = s(86);
            int sliderLeft = Math.Max(s(4), right - sliderW);
            // 自绘滑块的轨道画在控件高度中央，这里给足高度并整体上移一点，
            // 否则视觉上会贴着窗口下边缘。上移量按 DPI 缩放，并夹在状态条内。
            int sliderH = Math.Max(s(20), _statusBarHeight - s(2));
            int sliderTop = (_statusBarHeight - sliderH) / 2 - s(3);
            sliderTop = Math.Max(0, Math.Min(sliderTop, Math.Max(0, _statusBarHeight - s(8))));
            _zoomSlider.Bounds = new Rectangle(
                sliderLeft, sliderTop,
                Math.Max(s(40), right - sliderLeft), sliderH);

            _zoomMinusRect = new Rectangle(
                Math.Max(s(2), sliderLeft - btn - s(2)), cy, btn, btn);
        }

        private void PaintProgressBar(object sender, PaintEventArgs e)
        {
            e.Graphics.Clear(ColBg);
            if (_progress <= 0) return;
            int w = (int)Math.Round(_progressBar.Width * Math.Min(1.0, _progress));
            if (w <= 0) return;
            using (SolidBrush b = new SolidBrush(ColAccent))
            {
                e.Graphics.FillRectangle(b, 0, 0, w, _progressBar.Height);
            }
        }

        private void PaintTitleBar(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.Clear(ColTitleBg);

            using (Pen p = new Pen(ColLine))
            {
                g.DrawLine(p, 0, _titleBar.Height - 1, _titleBar.Width, _titleBar.Height - 1);
            }

            // 左侧：菜单按钮 + 首页按钮
            DrawHoverBox(g, _menuRect, _hoverButton == 0);
            DrawHamburger(g, _menuRect);
            DrawHoverBox(g, _homeRect, _hoverButton == 1);
            DrawHome(g, _homeRect);

            // 右侧：最小化 / 最大化 / 关闭
            DrawHoverBox(g, _minRect, _hoverButton == 2);
            DrawGlyph(g, _minRect, GlyphKind.Minimize, ColSubText);
            DrawHoverBox(g, _maxRect, _hoverButton == 3);
            DrawGlyph(g, _maxRect,
                WindowState == FormWindowState.Maximized ? GlyphKind.Restore : GlyphKind.Maximize, ColSubText);

            if (_hoverButton == 4)
            {
                using (SolidBrush b = new SolidBrush(ColCloseHover))
                {
                    g.FillRectangle(b, _closeRect);
                }
            }
            DrawGlyph(g, _closeRect, GlyphKind.Close, _hoverButton == 4 ? Color.White : ColSubText);

            // 中间：应用名 + 当前页面标题
            int textLeft = _homeRect.Right + 8;
            int textRight = _minRect.Left - 8;
            if (textRight <= textLeft) return;

            using (Font nameFont = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold, GraphicsUnit.Point))
            using (Font subFont = new Font("Microsoft YaHei UI", 8F, FontStyle.Regular, GraphicsUnit.Point))
            using (SolidBrush nameBrush = new SolidBrush(ColText))
            using (SolidBrush subBrush = new SolidBrush(ColSubText))
            using (StringFormat sf = new StringFormat
            {
                LineAlignment = StringAlignment.Center,
                Trimming = StringTrimming.EllipsisCharacter,
                FormatFlags = StringFormatFlags.NoWrap
            })
            {
                const string name = "小黑盒社区";
                Size nameSize = TextRenderer.MeasureText(g, name, nameFont);
                int nameWidth = Math.Min(nameSize.Width, textRight - textLeft);
                g.DrawString(name, nameFont, nameBrush, new Rectangle(textLeft, 0, nameWidth, _titleBar.Height), sf);

                int subLeft = textLeft + nameWidth + 8;
                int subWidth = textRight - subLeft;
                if (subWidth > 24)
                {
                    g.DrawString(_caption, subFont, subBrush, new Rectangle(subLeft, 0, subWidth, _titleBar.Height), sf);
                }
            }
        }

        private void PaintStatusBar(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.Clear(ColStatusBg);
            using (Pen p = new Pen(ColLine))
            {
                g.DrawLine(p, 0, 0, _statusBar.Width, 0);
            }

            using (Font f = new Font("Microsoft YaHei UI", 8F, FontStyle.Regular, GraphicsUnit.Point))
            using (SolidBrush brush = new SolidBrush(ColSubText))
            using (StringFormat lf = new StringFormat
            {
                LineAlignment = StringAlignment.Center,
                Trimming = StringTrimming.EllipsisCharacter,
                FormatFlags = StringFormatFlags.NoWrap
            })
            {
                int left = 10;
                int rightEdge = _zoomMinusRect.Left - 6;
                if (rightEdge < left) rightEdge = left;
                string leftText = _statusText + (TopMost ? "  ·  置顶" : "");
                g.DrawString(leftText, f, brush, new Rectangle(left, 0, rightEdge - left, _statusBar.Height), lf);
            }

            // 缩放控件：− 滑块 + 百分比
            DrawStatusButton(g, _zoomMinusRect, _hoverStatus == 0);
            DrawStatusButton(g, _zoomPlusRect, _hoverStatus == 1);
            DrawStatusButton(g, _zoomFitRect, _hoverStatus == 2);
            DrawGlyphLine(g, _zoomMinusRect, true);
            DrawGlyphLine(g, _zoomPlusRect, false);

            using (Font f = new Font("Microsoft YaHei UI", 7.5F, FontStyle.Regular, GraphicsUnit.Point))
            using (SolidBrush brush = new SolidBrush(_config.FitToWidth ? ColSubText : ColAccent))
            using (StringFormat cf = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
                FormatFlags = StringFormatFlags.NoWrap
            })
            {
                g.DrawString("适应", f, brush, _zoomFitRect, cf);
            }

            using (Font f = new Font("Microsoft YaHei UI", 8F, FontStyle.Regular, GraphicsUnit.Point))
            using (SolidBrush brush = new SolidBrush(ColText))
            using (StringFormat cf = new StringFormat
            {
                Alignment = StringAlignment.Near,
                LineAlignment = StringAlignment.Center,
                FormatFlags = StringFormatFlags.NoWrap
            })
            {
                // 100% 时直接用文字说明"正好铺满"，比数字更直观
                int rel = (int)Math.Round(ZoomPercentNow());
                string pct = rel <= 100 ? "铺满" : (rel + "%");
                Rectangle rect = new Rectangle(_zoomPlusRect.Right + 4, 0, Math.Max(30, s4(44)), _statusBar.Height);
                g.DrawString(pct, f, brush, rect, cf);
            }
        }

        private int s4(int v)
        {
            return (int)Math.Round(v * ScaleFactor() / 96.0);
        }

        private double ZoomPercentNow()
        {
            if (_zoomSlider != null) return _zoomSlider.Value;
            return _config.ZoomFactor * 100;
        }

        private static void DrawStatusButton(Graphics g, Rectangle r, bool hover)
        {
            if (hover)
            {
                using (SolidBrush b = new SolidBrush(ColHover))
                {
                    g.FillRectangle(b, r);
                }
            }
        }

        private static void DrawGlyphLine(Graphics g, Rectangle r, bool minusOnly)
        {
            using (Pen p = new Pen(ColText, 1.4f))
            {
                int cx = r.X + r.Width / 2;
                int cy = r.Y + r.Height / 2;
                const int s = 4;
                g.DrawLine(p, cx - s, cy, cx + s, cy);
                if (!minusOnly)
                {
                    g.DrawLine(p, cx, cy - s, cx, cy + s);
                }
            }
        }

        // ================= 缩放控件的交互 =================

        private int HitTestStatusButton(Point p)
        {
            if (_zoomMinusRect.Contains(p)) return 0;
            if (_zoomPlusRect.Contains(p)) return 1;
            if (_zoomFitRect.Contains(p)) return 2;
            return -1;
        }

        private void StatusBarMouseMove(object sender, MouseEventArgs e)
        {
            int hit = HitTestStatusButton(e.Location);
            if (hit != _hoverStatus)
            {
                _hoverStatus = hit;
                _statusBar.Invalidate();
            }
        }

        private void StatusBarMouseLeave(object sender, EventArgs e)
        {
            if (_hoverStatus != -1)
            {
                _hoverStatus = -1;
                _statusBar.Invalidate();
            }
        }

        private void StatusBarMouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            switch (HitTestStatusButton(e.Location))
            {
                case 0:
                    NudgeZoom(-5);   // 百分点
                    break;
                case 1:
                    NudgeZoom(5);
                    break;
                case 2:
                    FitNow();
                    break;
            }
        }

        private void NudgeZoom(int deltaPercent)
        {
            if (_zoomSlider == null) return;
            int v = Math.Max(_zoomSlider.Minimum, Math.Min(_zoomSlider.Maximum, _zoomSlider.Value + deltaPercent));
            if (v == _zoomSlider.Value) return;
            _zoomSlider.Value = v;   // 触发 ValueChanged，走正常流程
        }

        private void ZoomSliderValueChanged(object sender, EventArgs e)
        {
            if (_zoomSlider == null || !_coreReady) return;

            // 程序自己在同步滑块位置（自适应期间），只记录倍率、不动其它状态
            if (_syncingSlider)
            {
                _config.ZoomFactor = Math.Max(1.0, _zoomSlider.ZoomFromValue());
                return;
            }

            // 用户把倍率调回 100%（正好铺满）时，顺手恢复"跟随窗口自适应"
            _config.ZoomFactor = Math.Max(1.0, _zoomSlider.ZoomFromValue());
            if (_config.ZoomFactor <= 1.001 && !_config.FitToWidth)
            {
                _config.FitToWidth = true;
                Diag.Log("倍率回到 100%，恢复跟随窗口自适应");
            }

            ApplyZoom();
            SyncZoomMenuChecks();
            _statusBar.Invalidate();
        }

        /// <summary>
        /// 点击"适应"：把页面切换到 880 CSS px 的单栏布局并放大到填满窗口，
        /// 右侧"热门社区/下载 App"等无关内容不会被渲染进窗口。
        /// 这是一个幂等动作（点多少次结果都一样），不是开关。
        /// </summary>
        private void FitNow()
        {
            Diag.Log("fit now: follow=" + _config.FitToWidth +
                     " rel=" + _config.ZoomFactor.ToString("F2") +
                     " base=" + _baseFitZoom.ToString("F3"));
            _config.FitToWidth = true;
            _config.ZoomFactor = 1.0;    // 倍率归 100%（正好铺满正文列）
            SyncFitMenuCheck();
            _statusBar.Invalidate();
            _config.Save();

            if (!_coreReady)
            {
                Diag.Log("fit now: core not ready");
                return;
            }

            ApplyFitToWidth();
        }

        /// <summary>跟随窗口宽度的开关（右键菜单里的勾选项）。</summary>
        private void SetFitToWidth(bool on)
        {
            _config.FitToWidth = on;
            SyncFitMenuCheck();
            Diag.Log("fit follow set: " + on + " rel=" + _config.ZoomFactor.ToString("F2") +
                     " base=" + _baseFitZoom.ToString("F3"));
            _statusBar.Invalidate();

            if (on && _coreReady)
            {
                // 打开跟随时，立刻按当前窗口重算一次铺满基准
                _baseFitZoom = 1.0;
                ApplyFitToWidth();
            }
            else if (_coreReady)
            {
                // 关掉跟随：固守当前基准（用户倍率不变）
                ApplyZoom();
                UpdateZoomUi();
            }
        }

        private void SyncFitMenuCheck()
        {
            if (_fitMenu != null) _fitMenu.Checked = _config.FitToWidth;
        }

        private enum GlyphKind { Minimize, Maximize, Restore, Close }

        private static void DrawHoverBox(Graphics g, Rectangle r, bool hover)
        {
            if (!hover) return;
            using (SolidBrush b = new SolidBrush(ColHover))
            {
                g.FillRectangle(b, r);
            }
        }

        private static void DrawHamburger(Graphics g, Rectangle r)
        {
            using (Pen p = new Pen(ColText, 1.6f))
            {
                int cx = r.X + r.Width / 2;
                int cy = r.Y + r.Height / 2;
                const int half = 7;
                const int gap = 4;
                g.DrawLine(p, cx - half, cy - gap, cx + half, cy - gap);
                g.DrawLine(p, cx - half, cy, cx + half, cy);
                g.DrawLine(p, cx - half, cy + gap, cx + half, cy + gap);
            }
        }

        private static void DrawGlyph(Graphics g, Rectangle r, GlyphKind kind, Color color)
        {
            using (Pen p = new Pen(color, 1.3f))
            {
                int cx = r.X + r.Width / 2;
                int cy = r.Y + r.Height / 2;
                const int s = 5;
                switch (kind)
                {
                    case GlyphKind.Minimize:
                        g.DrawLine(p, cx - s, cy, cx + s, cy);
                        break;
                    case GlyphKind.Maximize:
                        g.DrawRectangle(p, cx - s, cy - s, s * 2, s * 2);
                        break;
                    case GlyphKind.Restore:
                        g.DrawRectangle(p, cx - s + 2, cy - s - 1, s * 2 - 2, s * 2 - 2);
                        g.DrawRectangle(p, cx - s, cy - s + 2, s * 2 - 2, s * 2 - 2);
                        break;
                    case GlyphKind.Close:
                        g.DrawLine(p, cx - s, cy - s, cx + s, cy + s);
                        g.DrawLine(p, cx + s, cy - s, cx - s, cy + s);
                        break;
                }
            }
        }

        private static void DrawHome(Graphics g, Rectangle r)
        {
            using (Pen p = new Pen(ColText, 1.5f))
            {
                int cx = r.X + r.Width / 2;
                int cy = r.Y + r.Height / 2 + 1;
                const int half = 7;
                Point[] roof =
                {
                    new Point(cx - half - 1, cy - 1),
                    new Point(cx, cy - half - 2),
                    new Point(cx + half + 1, cy - 1)
                };
                g.DrawLines(p, roof);
                g.DrawLine(p, cx - half, cy - 1, cx - half, cy + half);
                g.DrawLine(p, cx + half, cy - 1, cx + half, cy + half);
                g.DrawLine(p, cx - half, cy + half, cx + half, cy + half);
                g.DrawRectangle(p, cx - 2, cy + 1, 4, half - 1);
            }
        }

        // ================= 标题栏交互 =================

        private int HitTestButton(Point p)
        {
            if (_menuRect.Contains(p)) return 0;
            if (_homeRect.Contains(p)) return 1;
            if (_minRect.Contains(p)) return 2;
            if (_maxRect.Contains(p)) return 3;
            if (_closeRect.Contains(p)) return 4;
            return -1;
        }

        private void TitleBarMouseMove(object sender, MouseEventArgs e)
        {
            int hit = HitTestButton(e.Location);
            if (hit != _hoverButton)
            {
                _hoverButton = hit;
                _titleBar.Invalidate();
            }
        }

        private void TitleBarMouseLeave(object sender, EventArgs e)
        {
            if (_hoverButton != -1)
            {
                _hoverButton = -1;
                _titleBar.Invalidate();
            }
        }

        private void TitleBarMouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            if (HitTestButton(e.Location) == -1)
            {
                Native.ReleaseCapture();
                Native.SendMessage(Handle, Native.WM_NCLBUTTONDOWN, (IntPtr)Native.HTCAPTION, IntPtr.Zero);
            }
        }

        private int EdgeBorderPx()
        {
            return Math.Max(5, (int)Math.Round(7 * ScaleFactor() / 96.0));
        }

        /// <summary>摆放四条边缘抓取条（放在最上层，保证边缘一定能抓到）。</summary>
        private void LayoutEdgeStrips()
        {
            if (_edgeStrips == null || _edgeStrips.Length < 4) return;

            int b = EdgeBorderPx();
            int w = ClientSize.Width;
            int h = ClientSize.Height;
            if (w <= 0 || h <= 0) return;

            bool show = WindowState != FormWindowState.Maximized && _titleBar.Visible;
            for (int i = 0; i < _edgeStrips.Length; i++)
            {
                _edgeStrips[i].Visible = show;
            }
            if (!show) return;

            _edgeStrips[0].Bounds = new Rectangle(0, 0, w, b);                                  // 上
            _edgeStrips[1].Bounds = new Rectangle(0, b, b, Math.Max(0, h - b * 2));             // 左
            _edgeStrips[2].Bounds = new Rectangle(w - b, b, b, Math.Max(0, h - b * 2));         // 右
            _edgeStrips[3].Bounds = new Rectangle(0, h - b, w, b);                              // 下

            for (int i = 0; i < _edgeStrips.Length; i++)
            {
                _edgeStrips[i].BringToFront();
            }
        }

        private void EdgeStripMouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;

            int b = EdgeBorderPx();
            Point p = PointToClient(((Control)sender).PointToScreen(e.Location));
            int w = ClientSize.Width;
            int h = ClientSize.Height;

            int hit = Native.HTBOTTOMRIGHT;
            bool left = p.X <= b;
            bool right = p.X >= w - b;
            bool top = p.Y <= b;
            bool bottom = p.Y >= h - b;

            if (top && left) hit = Native.HTTOPLEFT;
            else if (top && right) hit = Native.HTTOPRIGHT;
            else if (bottom && left) hit = Native.HTBOTTOMLEFT;
            else if (bottom && right) hit = Native.HTBOTTOMRIGHT;
            else if (left) hit = Native.HTLEFT;
            else if (right) hit = Native.HTRIGHT;
            else if (top) hit = Native.HTTOP;
            else if (bottom) hit = Native.HTBOTTOM;

            Diag.Log("edge strip mousedown: " + p + " → HT" + hit);
            Native.ReleaseCapture();
            Native.SendMessage(Handle, Native.WM_NCLBUTTONDOWN, (IntPtr)hit, IntPtr.Zero);
        }

        private void EdgeStripMouseMove(object sender, MouseEventArgs e)
        {
            int b = EdgeBorderPx();
            Point p = PointToClient(((Control)sender).PointToScreen(e.Location));
            int w = ClientSize.Width;
            int h = ClientSize.Height;

            bool lr = p.X <= b || p.X >= w - b;
            bool tb = p.Y <= b || p.Y >= h - b;
            int id;
            if (lr && tb)
            {
                bool nwse = (p.X <= b && p.Y <= b) || (p.X >= w - b && p.Y >= h - b);
                id = nwse ? Native.IDC_SIZENWSE : Native.IDC_SIZENESW;
            }
            else if (lr) id = Native.IDC_SIZEWE;
            else id = Native.IDC_SIZENS;

            Native.SetCursor(Native.LoadCursor(IntPtr.Zero, (IntPtr)id));
        }

        // ================= 子控件的 WM_NCHITTEST 转发 =================
        //
        // 无边框窗口的缩放边框是靠 WM_NCHITTEST 返回 HTLEFT/HTBOTTOM... 实现的，
        // 但这个消息是发给鼠标下的那个"子窗口"的（标题栏 Panel、状态栏、WebView2），
        // 子窗口一律回 HTCLIENT，消息永远到不了窗体，于是边框完全不能拖动缩放。
        // 这里给所有子控件挂一个原生窗口钩子，把命中测试交给窗体统一判定。

        private sealed class HitTestForwarder : NativeWindow
        {
            private readonly FloatWindow _owner;
            public HitTestForwarder(FloatWindow owner) { _owner = owner; }

            protected override void WndProc(ref Message m)
            {
                if (m.Msg == Native.WM_NCHITTEST && _owner != null && !_owner.IsDisposed)
                {
                    Point screen = Native.PointFromLParam(m.LParam);
                    Point client = _owner.PointToClient(screen);
                    int hit = _owner.HitTestChrome(client);
                    if (hit != Native.HTCLIENT)
                    {
                        m.Result = (IntPtr)hit;
                        return;
                    }
                }
                base.WndProc(ref m);
            }
        }

        private void AttachHitTestForwarders()
        {
            foreach (Control c in new Control[] { _titleBar, _statusBar, _progressBar, _web })
            {
                if (c == null || !c.IsHandleCreated) continue;
                try
                {
                    HitTestForwarder fwd = new HitTestForwarder(this);
                    fwd.AssignHandle(c.Handle);
                    _forwarders.Add(fwd);
                }
                catch
                {
                    // 忽略：某个控件挂不上不影响其它控件
                }
            }
            Diag.Log("hittest forwarders attached: " + _forwarders.Count);
        }

        /// <summary>
        /// 统一的边框/标题栏命中判定。clientPoint 是窗体客户区坐标，
        /// 命中缩放边框时返回 HTxxx，标题栏内部返回 HTCAPTION，其余返回 HTCLIENT。
        /// </summary>
        private int HitTestChrome(Point clientPoint)
        {
            if (WindowState == FormWindowState.Maximized) return Native.HTCLIENT;

            int b = EdgeBorderPx();
            int w = ClientSize.Width;
            int h = ClientSize.Height;
            int x = clientPoint.X;
            int y = clientPoint.Y;

            bool left = x <= b;
            bool right = x >= w - b;
            bool top = y <= b;
            bool bottom = y >= h - b;

            if (top && left) return Native.HTTOPLEFT;
            if (top && right) return Native.HTTOPRIGHT;
            if (bottom && left) return Native.HTBOTTOMLEFT;
            if (bottom && right) return Native.HTBOTTOMRIGHT;
            if (left) return Native.HTLEFT;
            if (right) return Native.HTRIGHT;
            if (top) return Native.HTTOP;
            if (bottom) return Native.HTBOTTOM;

            // 标题栏内部交给系统处理拖动 / 双击最大化
            if (_titleBar != null && _titleBar.Visible)
            {
                Point title = _titleBar.PointToClient(PointToScreen(clientPoint));
                if (_titleBar.ClientRectangle.Contains(title))
                {
                    if (HitTestButton(title) == -1) return Native.HTCAPTION;
                }
            }
            return Native.HTCLIENT;
        }

        /// <summary>按当前 DPI 把物理像素尺寸换算成逻辑尺寸保存。</summary>
        private void SetLogicalSize(int physicalWidth, int physicalHeight)
        {
            int dpi = Math.Max(96, ScaleFactor());
            _logicalSize = new Size(
                Math.Max(280, (int)Math.Round(physicalWidth * 96.0 / dpi)),
                Math.Max(320, (int)Math.Round(physicalHeight * 96.0 / dpi)));
            _config.Width = _logicalSize.Width;
            _config.Height = _logicalSize.Height;
        }

        private void TitleBarMouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right)
            {
                ShowMenu(e.Location);
                return;
            }

            if (e.Button != MouseButtons.Left) return;

            switch (HitTestButton(e.Location))
            {
                case 0: ShowMenu(new Point(_menuRect.Right, _menuRect.Bottom)); break;
                case 1: GoHome(); break;
                case 2: WindowState = FormWindowState.Minimized; break;
                case 3: ToggleMaximize(); break;
                case 4: Close(); break;
            }
        }

        private void TitleBarDoubleClick(object sender, MouseEventArgs e)
        {
            if (HitTestButton(e.Location) != -1) return;
            ToggleMaximize();
        }

        private void ToggleMaximize()
        {
            WindowState = WindowState == FormWindowState.Maximized
                ? FormWindowState.Normal
                : FormWindowState.Maximized;
            LayoutChrome();
        }

        private void ShowMenu(Point titleBarPoint)
        {
            _menu.Show(_titleBar, titleBarPoint);
        }

        // ================= 网页区域交互 =================

        private void WebMouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right)
            {
                ShowMenu(PointToClient(_web.PointToScreen(e.Location)));
            }
        }

        private void WebKeyDown(object sender, KeyEventArgs e)
        {
            HandleShortcut(e.KeyData);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (HandleShortcut(keyData)) return true;
            return base.ProcessCmdKey(ref msg, keyData);
        }

        private bool HandleShortcut(Keys keyData)
        {
            Keys key = keyData & Keys.KeyCode;
            bool ctrl = (keyData & Keys.Control) == Keys.Control;
            bool alt = (keyData & Keys.Alt) == Keys.Alt;
            bool shift = (keyData & Keys.Shift) == Keys.Shift;

            if (ctrl && alt && key == Keys.T) { ToggleTopMost(); return true; }
            if (ctrl && shift && key == Keys.H) { ToggleVisible(); return true; }
            if (ctrl && (key == Keys.D0 || key == Keys.NumPad0)) { SetRelZoom(1.0); return true; }
            if (ctrl && key == Keys.F) { FitNow(); return true; }
            if (ctrl && (key == Keys.Oemplus || key == Keys.Add)) { SetRelZoom(CurrentRel() + 0.1); return true; }
            if (ctrl && (key == Keys.OemMinus || key == Keys.Subtract)) { SetRelZoom(CurrentRel() - 0.1); return true; }
            if (alt && key == Keys.Left) { GoBack(); return true; }
            if (alt && key == Keys.Right) { GoForward(); return true; }
            if (key == Keys.F5) { Reload(); return true; }
            if (key == Keys.Escape && WindowState == FormWindowState.Maximized)
            {
                WindowState = FormWindowState.Normal;
                return true;
            }
            return false;
        }

        /// <summary>当前"相对铺满窗口"的倍率。</summary>
        private double CurrentRel()
        {
            return Math.Max(1.0, Math.Min(3.0, _config.ZoomFactor));
        }

        // ================= 托盘与菜单 =================

        private NotifyIcon BuildTrayIcon()
        {
            NotifyIcon tray = new NotifyIcon { Text = "小黑盒悬浮窗", Visible = true };
            try { tray.Icon = AppIcon.Get(); } catch { }

            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add("显示 / 隐藏悬浮窗 (Ctrl+Alt+H)", null, (s, e) => ToggleVisible());
            menu.Items.Add("回到小黑盒社区首页", null, (s, e) => { ShowWindow(); GoHome(); });
            menu.Items.Add("置顶显示（开/关）Ctrl+Alt+T", null, (s, e) => ToggleTopMost());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出", null, (s, e) => ExitApp());
            tray.ContextMenuStrip = menu;
            tray.DoubleClick += (s, e) => ToggleVisible();
            return tray;
        }

        private ContextMenuStrip BuildContextMenu()
        {
            ContextMenuStrip menu = new ContextMenuStrip
            {
                ShowImageMargin = false,
                Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point)
            };

            menu.Items.Add("← 后退 (Alt+←)", null, (s, e) => GoBack());
            menu.Items.Add("→ 前进 (Alt+→)", null, (s, e) => GoForward());
            menu.Items.Add("⟳ 刷新 (F5)", null, (s, e) => Reload());
            menu.Items.Add("⌂ 小黑盒社区首页", null, (s, e) => GoHome());
            menu.Items.Add("↗ 用系统默认浏览器打开当前页", null, (s, e) => OpenExternal(SafeSource()));
            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem pin = new ToolStripMenuItem("置顶显示 (Ctrl+Alt+T)");
            pin.Click += (s, e) => ToggleTopMost();
            menu.Items.Add(pin);

            ToolStripMenuItem size = new ToolStripMenuItem("窗口尺寸");
            size.DropDownItems.Add("手机窄栏 400 × 760", null, (s, e) => ApplySize(400, 760));
            size.DropDownItems.Add("默认 460 × 820", null, (s, e) => ApplySize(460, 820));
            size.DropDownItems.Add("宽屏 720 × 900", null, (s, e) => ApplySize(720, 900));
            size.DropDownItems.Add("大屏 1000 × 1000", null, (s, e) => ApplySize(1000, 1000));
            menu.Items.Add(size);

            ToolStripMenuItem opacity = new ToolStripMenuItem("不透明度");
            double[] opacityValues = { 1.0, 0.95, 0.9, 0.85, 0.75, 0.6 };
            foreach (double v in opacityValues)
            {
                double captured = v;
                opacity.DropDownItems.Add(((int)Math.Round(v * 100)) + "%", null, (s, e) => SetOpacity(captured));
            }
            menu.Items.Add(opacity);

            ToolStripMenuItem zoom = new ToolStripMenuItem("缩放倍率（100% = 正好铺满窗口）");
            double[] zoomValues = { 1.0, 1.15, 1.25, 1.5, 1.75, 2.0, 2.5 };
            foreach (double z in zoomValues)
            {
                double captured = z;
                ToolStripMenuItem item = new ToolStripMenuItem(((int)Math.Round(z * 100)) + "%");
                item.Click += (s, e) => SetRelZoom(captured);
                zoom.DropDownItems.Add(item);
            }
            _zoomMenu = zoom;
            menu.Items.Add(zoom);

            ToolStripMenuItem fit = new ToolStripMenuItem("跟随窗口宽度（窗口变化时自动重算铺满基准）")
            {
                CheckOnClick = true,
                Checked = _config.FitToWidth
            };
            _fitMenu = fit;
            fit.CheckedChanged += (s, e) =>
            {
                if (_config.FitToWidth != fit.Checked) SetFitToWidth(fit.Checked);
                _config.Save();
            };
            menu.Items.Add(fit);

            ToolStripMenuItem internalLinks = new ToolStripMenuItem("站内新窗口在悬浮窗内打开")
            {
                CheckOnClick = true,
                Checked = _config.OpenLinksInside
            };
            internalLinks.CheckedChanged += (s, e) => _config.OpenLinksInside = internalLinks.Checked;
            menu.Items.Add(internalLinks);

            ToolStripMenuItem chrome = new ToolStripMenuItem("在网页里显示浏览器默认右键菜单")
            {
                CheckOnClick = true,
                Checked = false
            };
            chrome.CheckedChanged += (s, e) =>
            {
                if (!_coreReady) return;
                try { _web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = chrome.Checked; } catch { }
            };
            menu.Items.Add(chrome);

            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("最小化", null, (s, e) => WindowState = FormWindowState.Minimized);
            menu.Items.Add("隐藏到托盘", null, (s, e) => { Hide(); ShowTrayHint(); });
            menu.Items.Add("退出", null, (s, e) => ExitApp());

            menu.Opening += (s, e) =>
            {
                pin.Checked = TopMost;
                internalLinks.Checked = _config.OpenLinksInside;
                fit.Checked = _config.FitToWidth;
                try
                {
                    if (_coreReady) chrome.Checked = _web.CoreWebView2.Settings.AreDefaultContextMenusEnabled;
                }
                catch
                {
                    // 忽略
                }
            };

            return menu;
        }

        private void ShowTrayHint()
        {
            if (_trayHintShown) return;
            _trayHintShown = true;
            try
            {
                _tray.BalloonTipTitle = "小黑盒悬浮窗还在运行";
                _tray.BalloonTipText = "双击托盘图标，或按 Ctrl+Alt+H 可以重新显示悬浮窗。";
                _tray.ShowBalloonTip(3000);
            }
            catch
            {
                // 忽略
            }
        }

        // ================= 行为 =================

        private void ToggleVisible()
        {
            if (Visible && WindowState != FormWindowState.Minimized)
            {
                Hide();
                ShowTrayHint();
            }
            else
            {
                ShowWindow();
            }
        }

        private void ShowWindow()
        {
            Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            try
            {
                Native.SetWindowPos(Handle, TopMost ? new IntPtr(-1) : IntPtr.Zero, 0, 0, 0, 0,
                    0x0001 /*SWP_NOSIZE*/ | 0x0002 /*SWP_NOMOVE*/ | 0x0040 /*SWP_SHOWWINDOW*/);
            }
            catch
            {
                // 忽略
            }
            Activate();
            BringToFront();
        }

        private void ToggleTopMost()
        {
            TopMost = !TopMost;
            _config.TopMost = TopMost;
            _statusBar.Invalidate();
        }

        private void SetOpacity(double value)
        {
            value = Math.Min(1.0, Math.Max(0.35, value));
            Opacity = value;
            _config.Opacity = value;
        }

        private void ApplySize(int width, int height)
        {
            if (WindowState == FormWindowState.Maximized) WindowState = FormWindowState.Normal;
            _logicalSize = new Size(width, height);
            Size = LogicalToDeviceUnits(_logicalSize);
            _config.Width = width;
            _config.Height = height;
            LayoutChrome();
        }

        private void ApplyFullScreen(bool fullScreen)
        {
            _titleBar.Visible = !fullScreen;
            _statusBar.Visible = !fullScreen;
            if (!fullScreen) LayoutChrome();
        }

        private void ExitApp()
        {
            _allowExit = true;
            _closeToTray = false;
            Close();
        }

        private void SaveState()
        {
            try
            {
                if (WindowState == FormWindowState.Normal)
                {
                    _config.X = Location.X;
                    _config.Y = Location.Y;
                    // 配置里保存逻辑尺寸，换到不同缩放的显示器上也能得到一致的观感
                    _config.Width = Math.Max(280, (int)Math.Round(Width * 96.0 / Math.Max(96, ScaleFactor())));
                    _config.Height = Math.Max(320, (int)Math.Round(Height * 96.0 / Math.Max(96, ScaleFactor())));
                }
                _config.TopMost = TopMost;
                _config.Opacity = Opacity;
                // 注意：这里刻意不保存 ZoomFactor —— 它可能是"自适应"算出来的值，
                // 只保存用户主动设定的缩放（_config.Zoom），否则下次启动基准会被带偏。
                _config.Save();
            }
            catch
            {
                // 忽略
            }
        }

        private void EnsureOnScreen()
        {
            Rectangle area = Screen.FromControl(this).WorkingArea;
            bool fits = Width <= area.Width && Height <= area.Height;
            if (!fits)
            {
                Size = new Size(Math.Min(Width, area.Width), Math.Min(Height, area.Height));
            }

            // 放不下、没有保存过位置、或者原位置已经跑到屏幕外：回到右下角
            Rectangle current = new Rectangle(Location, Size);
            if (!fits || !_config.HasSavedLocation || !area.Contains(current))
            {
                Location = new Point(
                    Math.Max(area.Left, area.Right - Width - 24),
                    Math.Max(area.Top, area.Bottom - Height - 24));
            }
        }

        private static Point DefaultLocation(Size size)
        {
            Rectangle area = Screen.PrimaryScreen.WorkingArea;
            return new Point(area.Right - size.Width - 24, area.Bottom - size.Height - 24);
        }

        private void UpdateCaption()
        {
            if (!_coreReady) return;
            try
            {
                _caption = SafeTitle(_web.CoreWebView2.DocumentTitle);
                _titleBar.Invalidate();
            }
            catch
            {
                // 忽略
            }
        }

        private string SafeSource()
        {
            try
            {
                if (_coreReady && !string.IsNullOrEmpty(_web.CoreWebView2.Source)) return _web.CoreWebView2.Source;
            }
            catch
            {
                // 忽略
            }
            return _config.HomeUrl;
        }

        private string SafeDocumentTitle()
        {
            try
            {
                if (_coreReady) return _web.CoreWebView2.DocumentTitle;
            }
            catch
            {
                // 忽略
            }
            return "";
        }

        private static string SafeBrowserVersion()
        {
            try
            {
                return CoreWebView2Environment.GetAvailableBrowserVersionString(null);
            }
            catch
            {
                return "(unknown)";
            }
        }

        private static string SafeTitle(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "小黑盒社区";
            string t = raw.Trim();
            return t.Length > 42 ? t.Substring(0, 42) + "…" : t;
        }

        private static string Shorten(string value, int max)
        {
            if (string.IsNullOrEmpty(value)) return "";
            value = value.Replace("\r", " ").Replace("\n", " ");
            return value.Length <= max ? value : value.Substring(0, max) + "…";
        }

        private static string Flatten(Exception ex)
        {
            StringBuilder sb = new StringBuilder();
            Exception cur = ex;
            int guard = 0;
            while (cur != null && guard++ < 4)
            {
                if (sb.Length > 0) sb.Append(" <- ");
                sb.Append(cur.Message);
                cur = cur.InnerException;
            }
            return sb.ToString();
        }

        private static string DescribeWebError(CoreWebView2WebErrorStatus status)
        {
            switch (status)
            {
                case CoreWebView2WebErrorStatus.HostNameNotResolved: return "域名解析失败（网络或 DNS 问题）";
                case CoreWebView2WebErrorStatus.ServerUnreachable: return "服务器无法连接";
                case CoreWebView2WebErrorStatus.ConnectionAborted: return "连接被中断";
                case CoreWebView2WebErrorStatus.ConnectionReset: return "连接被重置";
                case CoreWebView2WebErrorStatus.Timeout: return "请求超时";
                case CoreWebView2WebErrorStatus.Disconnected: return "网络已断开";
                case CoreWebView2WebErrorStatus.CannotConnect: return "无法建立连接";
                case CoreWebView2WebErrorStatus.OperationCanceled: return "操作被取消";
                case CoreWebView2WebErrorStatus.ClientCertificateContainsErrors: return "客户端证书错误";
                case CoreWebView2WebErrorStatus.RedirectFailed: return "重定向失败";
                case CoreWebView2WebErrorStatus.UnexpectedError: return "未知错误";
                default: return status.ToString();
            }
        }

        // ================= 无边框窗口的原生消息处理 =================

        protected override void WndProc(ref Message m)
        {
            switch (m.Msg)
            {
                case Native.WM_HOTKEY:
                    {
                        int id = m.WParam.ToInt32();
                        if (id == HotKeyToggle) ToggleVisible();
                        else if (id == HotKeyTopMost) ToggleTopMost();
                        return;
                    }

                case Native.WM_NCCALCSIZE:
                    {
                        if (m.WParam != IntPtr.Zero)
                        {
                            Native.RECT rect = (Native.RECT)Marshal.PtrToStructure(m.LParam, typeof(Native.RECT));
                            if (WindowState == FormWindowState.Maximized)
                            {
                                // 最大化时给任务栏/边缘留一点空隙
                                int border = Math.Max(0, (int)Math.Round(8 * ScaleFactor() / 96.0));
                                rect.Left += border;
                                rect.Right -= border;
                                rect.Bottom -= border;
                            }
                            // 非最大化：客户区 = 整个窗口（去掉系统边框）
                            Marshal.StructureToPtr(rect, m.LParam, false);
                        }
                        return;
                    }

                case Native.WM_NCHITTEST:
                    {
                        base.WndProc(ref m);
                        if (m.Result.ToInt32() != Native.HTCLIENT) return;

                        int hit = HitTestChrome(PointToClient(Native.PointFromLParam(m.LParam)));
                        if (hit != Native.HTCLIENT) m.Result = (IntPtr)hit;
                        if (Diag.Enabled)
                        {
                            Diag.Log("hittest (form): client=" + PointToClient(Native.PointFromLParam(m.LParam)) + " → " + hit);
                        }
                        return;
                    }

                case Native.WM_GETMINMAXINFO:
                    {
                        base.WndProc(ref m);
                        Native.MINMAXINFO info = (Native.MINMAXINFO)Marshal.PtrToStructure(m.LParam, typeof(Native.MINMAXINFO));
                        info.ptMinTrackSize = new Native.POINT
                        {
                            X = Math.Max(260, (int)Math.Round(300 * ScaleFactor() / 96.0)),
                            Y = Math.Max(320, (int)Math.Round(360 * ScaleFactor() / 96.0))
                        };
                        Marshal.StructureToPtr(info, m.LParam, false);
                        return;
                    }

                case Native.WM_EXITSIZEMOVE:
                    {
                        base.WndProc(ref m);
                        SaveState();
                        // 拖拽结束：按最终尺寸立刻做一次自适应，别留到最后一次防抖
                        if (_config.FitToWidth)
                        {
                            Diag.Log("exitsizemove: client=" + ClientSize + " web=" + (_web == null ? Size.Empty : _web.Bounds));
                            ScheduleFit();
                        }
                        return;
                    }

                case Native.WM_SETTINGCHANGE:
                case Native.WM_DWMCOMPOSITIONCHANGED:
                    {
                        base.WndProc(ref m);
                        Native.ApplyModernFrame(Handle);
                        return;
                    }
            }

            base.WndProc(ref m);
        }
    }
}
