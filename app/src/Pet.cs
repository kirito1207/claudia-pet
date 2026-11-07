using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace Claudia
{
    /// <summary>
    /// 桌宠本体：透明、不抢焦点的 WPF 窗口承载 web/index.html，跟随 Claude 主窗口。
    /// 读取 Claude 插件的状态与额度，管理互动、托盘、通知及设置。
    /// 网页用 WebView2CompositionControl：它把网页画面当图片放进 WPF 透明窗口，才能做到逐像素半透明（柔和边缘、光晕）。
    /// </summary>
    sealed partial class Pet
    {
        CoreWebView2Environment env;   // 浏览器进程崩溃后要换一个新的（见 RecoverWeb）
        Grid grid;
        public Window Window { get; private set; }
        WebView2CompositionControl web;
        IntPtr hwnd;
        bool ready;
        string current;
        bool paused;
        public bool Paused                     // 开发者控制台测试时，先不把真实 AI 的状态交给桌宠
        {
            get => paused;
            set { if (paused != value) ++followEventGeneration; paused = value; }
        }
        public string ManifestJson { get; private set; } = "{}";
        readonly Dictionary<string, string> stamps = new Dictionary<string, string>();
        Forms.NotifyIcon tray;
        SettingsWindow settings;
        const double BaseWidth = 260, BaseHeight = 362;
        // 文字大小：网页按「人物大小、文字大小」算出的画布（网页 CSS 尺寸），窗口 = 画布 × 人物大小（app/web/text-size.js）
        double pageW = BaseWidth, pageH = BaseHeight;
        double pageFoot = 62;      // 画布底栏高（网页像素）
        double shownFootPx;        // 窗口现在的底栏在屏幕上多高（物理像素）；0 = 还没按画布设过

        public Pet(CoreWebView2Environment env) { this.env = env; }

        // ---------------- 启动 ----------------
        public void Start()
        {
            Store.SyncResources();
            current = Store.Settings.TryGetValue("character", out var c) && c is string s ? s : "claude";

            Window = new Window
            {
                Title = Store.AppName, WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent,
                Width = BaseWidth * Scale, Height = BaseHeight * Scale, Topmost = false, ShowInTaskbar = false, ShowActivated = false, ResizeMode = ResizeMode.NoResize,
                Left = -10000, Top = -10000,   // 先放屏幕外，拿到窗口句柄后按像素摆到保存的位置
            };
            Window.SourceInitialized += (_, __) =>
            {
                hwnd = new WindowInteropHelper(Window).Handle;
                Native.MakeToolWindow(hwnd);
                RestorePosition();
                HwndSource.FromHwnd(hwnd)?.AddHook(HotKeyHook);
                RegisterHideHotKey();
            };

            grid = new Grid();
            web = new WebView2CompositionControl { DefaultBackgroundColor = System.Drawing.Color.Transparent, IsHitTestVisible = false };
            grid.Children.Add(web);
            // 透明的接鼠标层：拖动 / 单击 / 右键由窗口自己处理，网页只负责画
            // 只在桌宠本体、气泡、名牌、额度上铺：窗口其余地方完全透明，点击直接落到后面的窗口
            // （窗口比桌宠大，是给动作、气泡留的空间；透明窗口里全透明的像素本来就不接鼠标）
            hit = new Canvas();
            grid.Children.Add(hit);
            Window.Content = grid;
            WireMouse(hit);

            // 换 DPI 时 WPF 先按新缩放排好窗口再发这个事件；等这一轮排版走完再让网页控件对齐（原因见 ResyncWebForDpi）
            Window.DpiChanged += (_, e) => Window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded,
                new Action(() => ResyncWebForDpi(e.OldDpi.DpiScaleX, e.NewDpi.DpiScaleX)));
            Window.Loaded += async (_, __) => await InitWeb();
            Window.Show();

            tray = new Forms.NotifyIcon { Text = Store.AppName, Visible = true };
            try { tray.Icon = new System.Drawing.Icon(Path.Combine(Store.Install, "AppIcon.ico")); } catch { tray.Icon = System.Drawing.SystemIcons.Application; }
            var trayMenu = new Forms.ContextMenuStrip();
            trayMenu.Opening += (_, e) => { FillMenu(trayMenu); e.Cancel = false; };
            tray.ContextMenuStrip = trayMenu;
            tray.DoubleClick += (_, __) => ShowAgain();   // 只叫出来、不挪位置（2026-11-07 用户定）；要回默认位置用菜单

            Every(TimeSpan.FromMilliseconds(300), Poll);
            StartFollow();
            // 锁屏时动画全停（看不见还在画，白白耗电）
            SystemEvents.SessionSwitch += (_, e) =>
            {
                if (e.Reason == SessionSwitchReason.SessionLock || e.Reason == SessionSwitchReason.SessionUnlock)
                    Window.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        var locked = e.Reason == SessionSwitchReason.SessionLock;
                        sessionLocked = locked;
                        UpdatePageHidden();
                    }));
            };
        }

        static void Every(TimeSpan t, Action a)
        {
            var timer = new DispatcherTimer { Interval = t };
            timer.Tick += (_, __) => { try { a(); } catch (Exception e) { Store.Log("定时任务出错: " + e); } };
            timer.Start();
        }

        int imageErrors, jsErrors;
        async Task InitWeb()
        {
            try
            {
                await web.EnsureCoreWebView2Async(env);
                var core = web.CoreWebView2;
                Map(core);
                core.NavigationStarting += (_, a) => { if (!a.Cancel) { StopMotion(); ++followEventGeneration; ready = false; hitRects = null; } };
                core.Settings.AreDefaultContextMenusEnabled = false;
                core.Settings.AreDevToolsEnabled = false;
                core.Settings.IsZoomControlEnabled = false;
                core.Settings.IsStatusBarEnabled = false;
                web.ZoomFactor = Scale * zoomFix;
                // 网页进程 / 浏览器进程崩溃（显卡驱动、系统睡眠醒来、内存不够时偶尔会）：不处理的话她就一直停住，
                // 之后每次调网页都报「控件已失效」
                core.ProcessFailed += (_, a) => Window.Dispatcher.BeginInvoke(new Action(() => _ = RecoverWeb(a.ProcessFailedKind)));
                core.NavigationCompleted += async (_, a) =>
                {
                    ready = false;
                    if (!a.IsSuccess) { Store.Log("桌宠页面加载失败: " + a.WebErrorStatus); return; }
                    LoadCharacters();
                    await core.ExecuteScriptAsync($"init({ManifestJson}); setCharacter({Q(current)}, true); setAuraMode({Q(AuraMode)}); setShowName({(ShowName ? "true" : "false")}); setPowerSave({(PowerSave ? "true" : "false")}); setQuotaStyle({Q(QuotaStyle)}); setBehavior({Store.Json.Serialize(BehaviorState())})");
                    // 刚更新完（上次打开的是别的版本）：让她说一声
                    var last = Store.Settings.TryGetValue("lastVersion", out var lv) ? lv as string : null;
                    // 「新功能」页看到哪一版了（设置里的小红点用）：没记过时，老用户按上次的版本算（有没看过的就亮），新安装按当前版本（不亮）。要在下面改掉 lastVersion 之前记
                    if (!(Store.Settings.TryGetValue("whatsNewSeen", out var wn) && wn is string)) { Store.Settings["whatsNewSeen"] = last ?? Store.Version; Store.SaveSettings(); }
                    if (last != Store.Version) { Store.Settings["lastVersion"] = Store.Version; Store.SaveSettings(); }
                    ready = true;
                    UpdateHitRects();
                    UpdatePageHidden();
                    if (RenderRecovery.Ready()) tray?.ShowBalloonTip(6000, Store.AppName, "上次画面出了问题，现已重新打开。", Forms.ToolTipIcon.Info);
                    ApplySizes(anchor: Anchor.Corner);   // 解锁过文字大小的：按画布重设窗口，左上角不动（锁着的尺寸不变）
                    After(1000, PassThroughBrowserWindows); After(5000, PassThroughBrowserWindows);   // 背后的浏览器窗口不接鼠标（见 PassThroughBrowserWindows）
                    stamps.Clear();
                    Poll();
                };
                // 立绘加载失败时网页发消息过来：记进日志（每次启动最多 5 条），便于定位加载问题
                core.WebMessageReceived += (_, a) =>
                {
                    try
                    {
                        if (!(Store.Json.DeserializeObject(a.WebMessageAsJson) is Dictionary<string, object> m)) return;
                        if (m.TryGetValue("hitRects", out var rects) && rects is object[] regions)
                        {
                            if (core == web?.CoreWebView2) { hitRects = regions; UpdateHitRects(); }
                            return;
                        }
                        if (m.TryGetValue("motion", out var mm) && mm is Dictionary<string, object> movement) { AcceptMotion(movement); return; }
                        if (m.ContainsKey("walk")) { HandleWalk(m["walk"] as Dictionary<string, object>); return; }
                        // 网页脚本出错：记进日志（每次启动最多 20 条）
                        if (m.TryGetValue("jsError", out var je)) { if (jsErrors++ < 20) Store.Log("网页脚本出错: " + je); return; }
                        if (imageErrors >= 5 || !m.TryGetValue("imageError", out var src)) return;
                        imageErrors++;
                        Store.Log($"立绘加载失败: {src}");
                    }
                    catch { }
                };
                core.Navigate(Store.PageUrl("index.html"));
            }
            catch (Exception e)
            {
                Store.Log("WebView2 初始化失败: " + e);
                MessageBox.Show("桌宠画面启动失败：\n" + e.Message + "\n\n日志：" + Path.Combine(Store.Data, "windows.log"), Store.AppName);
            }
        }

        readonly List<DateTime> crashes = new List<DateTime>();
        /// <summary>WebView2 崩溃后恢复：网页进程挂了就重新加载；浏览器进程挂了控件就废了，换新的环境和控件重来。
        /// 显卡进程挂了 WebView2 会自己重启，不用管。10 分钟内最多恢复 3 次，免得一直崩一直重来</summary>
        async Task RecoverWeb(CoreWebView2ProcessFailedKind kind)
        {
            Store.Log("WebView2 进程崩溃: " + kind);
            if (kind == CoreWebView2ProcessFailedKind.GpuProcessExited) return;
            crashes.RemoveAll(t => (DateTime.UtcNow - t).TotalMinutes > 10);
            if (crashes.Count >= 3) { Store.Log("10 分钟内崩溃太多次，不再自动恢复"); return; }
            crashes.Add(DateTime.UtcNow);
            ++followEventGeneration;
            ready = false;
            try
            {
                if (kind != CoreWebView2ProcessFailedKind.BrowserProcessExited && web?.CoreWebView2 != null)
                {
                    web.CoreWebView2.Reload();
                    return;
                }
                var old = web;
                web = new WebView2CompositionControl { DefaultBackgroundColor = System.Drawing.Color.Transparent, IsHitTestVisible = false };
                grid.Children.Remove(old);
                grid.Children.Insert(0, web);
                try { old.Dispose(); } catch { }
                env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(Store.Data, "webview2"));
                await InitWeb();
                Store.Log("WebView2 已恢复");
            }
            catch (Exception e) { Store.Log("WebView2 恢复失败: " + e); }
        }

        /// <summary>两个虚拟域名：程序自带的网页（安装目录\web，文件夹映射）、角色立绘（数据目录\characters，程序自己读文件，
        /// 不能用文件夹映射，原因见 SpriteResources）。只在网页的域名里导航，外部网页一律不开</summary>
        public static void Map(CoreWebView2 core)
        {
            core.SetVirtualHostNameToFolderMapping(Store.AppHost, Path.Combine(Store.Install, "web"), CoreWebView2HostResourceAccessKind.Allow);
            SpriteResources.Attach(core);
            core.NavigationStarting += (_, a) => { if (!a.Uri.StartsWith($"https://{Store.AppHost}/")) a.Cancel = true; };
            core.NewWindowRequested += (_, a) => a.Handled = true;
        }

        List<(string id, string name)> characters = new List<(string id, string name)>();   // 当前素材清单中的角色
        public void LoadCharacters()
        {
            ++followEventGeneration;
            var m = Store.Manifest();
            ManifestJson = Store.Json.Serialize(m);
            var chars = (Dictionary<string, object>)m["characters"];
            characters = chars
                              .Select(kv => (kv.Key, (kv.Value as Dictionary<string, object>)?["name"] as string ?? kv.Key)).ToList();
            if (!characters.Any(c => c.id == current)) current = characters.FirstOrDefault().id ?? "claude";
        }
        List<(string id, string name)> Characters() => characters;

    }
}
