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
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace Claudia
{
    /// <summary>
    /// 桌宠本体：透明、不抢焦点的窗口承载 web/index.html，跟随 Claude 主窗口。
    /// 读取 Claude 插件的状态与额度，管理互动、托盘、通知及设置。
    /// 窗口是 DirectHost（2026-11-22 照 CrossPet 换的）：网页由系统直接合成，逐像素半透明（柔和边缘、光晕），
    /// 换屏换缩放跟着走，画面也不经界面线程。以前的 WPF 透明窗口 + WebView2CompositionControl 为什么换掉，见 DirectHost.cs
    /// </summary>
    sealed partial class Pet
    {
        CoreWebView2Environment env;   // 浏览器进程崩溃后要换一个新的（见 RecoverWeb）
        public DirectHost Host { get; private set; }
        WebView2 web;
        readonly Dispatcher ui = Dispatcher.CurrentDispatcher;
        IntPtr hwnd;
        bool ready;
        string current;
        bool paused;
        public bool Paused                     // 开发者控制台测试时，先不把真实 AI 的状态交给桌宠
        {
            get => paused;
            set => paused = value;
        }
        public string ManifestJson { get; private set; } = "{}";
        readonly Dictionary<string, string> stamps = new Dictionary<string, string>();
        Forms.NotifyIcon tray;
        SettingsWindow settings;
        const double BaseWidth = 380, BaseHeight = 362;   // 380：派活里左边的小人跑到最远时 330 还会被切掉（2026-11-26）；之前 260 → 330 是为了派活的小布偶（2026-11-24）
        // 文字大小：网页按「人物大小、文字大小」算出的画布（网页 CSS 尺寸），窗口 = 画布 × 人物大小（app/web/text-size.js）
        double pageW = BaseWidth, pageH = BaseHeight;
        double pageFoot = 62;      // 画布底栏高（网页像素）
        double shownFootPx;        // 窗口现在的底栏在屏幕上多高（物理像素）；0 = 还没按画布设过

        public Pet(CoreWebView2Environment env) { this.env = env; }

        // ---------------- 启动 ----------------
        public void Start()
        {
            if (LeaderLock != null) Store.SyncResources();   // 启动时就是负责人：素材由它同步
            // 先看一眼 Claude 在不在：独立运行时 Claude 没开就从桌面上开始，开着就直接进窗口，不先在桌面上闪一下
            claudePids = FollowNative.ClaudeAppPids();
            claudeHwnd = claudePids.Count > 0 ? FollowNative.FindClaudeMain(claudePids) : IntPtr.Zero;
            wasStandalone = Store.Standalone;
            desktopMode = WantDesktop();
            Store.Log($"运行方式：{(wasStandalone ? "独立运行" : "跟着 Claude")}，{(desktopMode ? "待在桌面上" : "待在 Claude 窗口里")}");
            current = Store.Settings.TryGetValue("character", out var c) && c is string s ? s : "claude";

            // 先放屏幕外，拿到窗口句柄后按像素摆到保存的位置（大小在 HandleCreated 里按当时所在屏幕的缩放重设）
            Host = new DirectHost(Store.AppName, (int)Math.Round(BaseWidth * Scale), (int)Math.Round(BaseHeight * Scale));
            web = Host.Web;
            Host.MessageHook = HotKeyHook;   // 快捷键只有负责人注册（RegisterHideHotKey），别的几只收不到 WM_HOTKEY
            Host.RawMessage += AppBarMessage;   // 全屏打开 / 关闭的通知（Pet.Fullscreen）
            Host.HandleCreated += (_, __) =>
            {
                hwnd = Host.Handle;
                ResizeHost(Scale);
                RestorePosition();
                RegisterHideHotKey();
                RegisterAppBar();   // 全屏打开 / 关闭时系统来通知（Pet.Fullscreen）
                Host.ForcePassThrough = ClickThrough;
            };
            // 拖动 / 单击 / 右键：网页收到鼠标后交给外壳（pointer 消息，见 Pet.Mouse.cs 的 HandlePointer）；
            // 鼠标只在桌宠本体、气泡、名牌、额度上才接（hitRects，见 UpdateHitRects），窗口其余地方穿过去，点击直接落到后面的窗口
            // 换了系统缩放（改了缩放、拖到缩放不同的另一块屏幕）：窗口按新缩放重设物理像素大小，脚底不动。网页控件自己跟着缩放走，不用再逼它对齐
            Host.DpiScaleChanged += (_, __) => ui.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                Store.Log($"系统缩放变了：现在 {Math.Round(Host.DpiScale * 100)}%");
                lastMotionPosition = null;   // 换屏时系统可能已重新摆放窗口
                ApplyScale(shownScale > 0 ? shownScale : Scale, Anchor.Feet);
            }));
            Host.Load += async (_, __) => await InitWeb();
            Host.Show();

            // 负责人（托盘、快捷键、通知、多开名单都归它，见 PetHub.cs）：启动时抢到了锁就当，不然连上它
            if (LeaderLock != null) BecomeLeader();
            else { hub = new PetHubClient(this); hub.Start(); }

            Every(TimeSpan.FromMilliseconds(300), Poll);
            StartFollow();
            // 锁屏时动画全停（看不见还在画，白白耗电）
            SystemEvents.SessionSwitch += (_, e) =>
            {
                if (e.Reason == SessionSwitchReason.SessionLock || e.Reason == SessionSwitchReason.SessionUnlock)
                    ui.BeginInvoke(new Action(() =>
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

        int imageErrors, jsErrors, videoErrors;
        async Task InitWeb()
        {
            try
            {
                await web.EnsureCoreWebView2Async(env);
                var core = web.CoreWebView2;
                Map(core);
                core.NavigationStarting += (_, a) => { if (!a.Cancel) { StopMotion(); ready = false; hitRects = null; } };
                core.Settings.AreDefaultContextMenusEnabled = false;
                core.Settings.AreDevToolsEnabled = false;
                core.Settings.IsZoomControlEnabled = false;
                core.Settings.IsStatusBarEnabled = false;
                web.ZoomFactor = Scale * zoomFix;
                // 网页进程 / 浏览器进程崩溃（显卡驱动、系统睡眠醒来、内存不够时偶尔会）：不处理的话她就一直停住，
                // 之后每次调网页都报「控件已失效」
                core.ProcessFailed += (_, a) => ui.BeginInvoke(new Action(() => _ = RecoverWeb(a.ProcessFailedKind)));
                core.NavigationCompleted += async (_, a) =>
                {
                    ready = false;
                    if (!a.IsSuccess) { Store.Log("桌宠页面加载失败: " + a.WebErrorStatus); return; }
                    LoadCharacters();
                    await core.ExecuteScriptAsync($"init({ManifestJson}); setCharacter({Q(current)}, true); setAuraMode({Q(AuraMode)}); setShowName({(ShowName ? "true" : "false")}); setQuotaStyle({Q(QuotaStyle)}); setBehavior({Store.Json.Serialize(BehaviorState())}); setPointerBridge(true)");   // 鼠标由网页交给外壳（见 HandlePointer）
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
                    stamps.Clear();
                    Poll();
                    // 网页重新加载了：会话状态重放一遍（负责人自己扫的、别的几只收到的都在 sessionBook 里）
                    if (sessionBook.Count > 0) Js($"setSessionStates('claude', {Store.Json.Serialize(sessionBook.Values)})");
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
                        if (core == web?.CoreWebView2 && HandlePointer(m)) return;   // 按下 / 拖动 / 单击 / 右键（见 Pet.Mouse.cs）
                        if (m.TryGetValue("motion", out var mm) && mm is Dictionary<string, object> movement) { AcceptMotion(movement); return; }
                        if (m.ContainsKey("walk")) { HandleWalk(m["walk"] as Dictionary<string, object>); return; }
                        // 网页脚本出错：记进日志（每次启动最多 20 条）
                        if (m.TryGetValue("jsError", out var je)) { if (jsErrors++ < 20) Store.Log("网页脚本出错: " + je); return; }
                        if (m.TryGetValue("videoError", out var ve)) { if (videoErrors++ < 30) Store.Log("视频出错: " + ve); return; }   // 网页重读 / 改播待机，见 sheet-player.js 的 videoTrouble
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
            ready = false;
            try
            {
                if (kind != CoreWebView2ProcessFailedKind.BrowserProcessExited && web?.CoreWebView2 != null)
                {
                    web.CoreWebView2.Reload();
                    return;
                }
                var old = Host.ReplaceWeb();   // 浏览器进程挂了控件就废了：窗口里换一个新的网页控件
                web = Host.Web;
                try { old.Dispose(); } catch { }
                env = await CoreWebView2Environment.CreateAsync(null, Store.WebViewDir);
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
        Dictionary<string, object> manifest;
        /// <summary>扫一遍角色目录（素材、character.json），整份交给网页的清单也跟着换</summary>
        public void LoadCharacters()
        {
            manifest = Store.Manifest();
            ApplyManifest();
        }
        void ApplyManifest()
        {
            ManifestJson = Store.Json.Serialize(manifest);
            var chars = (Dictionary<string, object>)manifest["characters"];
            characters = chars
                              .Select(kv => (kv.Key, (kv.Value as Dictionary<string, object>)?["name"] as string ?? kv.Key)).ToList();
            if (!characters.Any(c => c.id == current)) current = characters.FirstOrDefault().id ?? "claude";
        }
        /// <summary>改了名字：只换清单里这一个名字，不重扫素材（2026-11-27）</summary>
        void RefreshCharacterName(string cid)
        {
            if (!(manifest?["characters"] is Dictionary<string, object> chars) || !chars.TryGetValue(cid, out var v) || !(v is Dictionary<string, object> info))
            { LoadCharacters(); return; }
            info["name"] = Store.Names.TryGetValue(cid, out var n) && n is string s && s.Trim() != "" ? s.Trim()
                : info.TryGetValue("defaultName", out var d) && d is string ds ? ds : cid;
            ApplyManifest();
        }
        List<(string id, string name)> Characters() => characters;

    }
}
