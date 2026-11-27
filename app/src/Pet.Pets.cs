using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace Claudia
{
    sealed partial class Pet : IPetHubHost
    {
        // ---------------- 待在哪：Claude 窗口里 / 桌面上 ----------------
        // 照 dsh-pet 的桌面模式：桌面上的那只始终置顶，活动范围是所在显示器的工作区（不进任务栏）；「跟着 Claude」时 Claude 关了一样跟着退出。
        // 每只各选各的，记在自己的设置里（mode = "desktop" / 没有）。具体怎么摆见 Pet.Follow.cs 的 DesktopPlace
        static bool OnDesktop => Store.Settings.TryGetValue("mode", out var v) && v as string == "desktop";

        void SetMode(bool desktop)
        {
            if (hwnd == IntPtr.Zero || OnDesktop == desktop) return;
            Store.Settings["mode"] = desktop ? "desktop" : "claude"; Store.SaveSettings();
            Store.Log(desktop ? "改成待在桌面上" : "改成待在 Claude 窗口里");
            SwitchArea(WantDesktop());   // 独立运行、Claude 又没开时，选了 Claude 窗口也先待在桌面上
            hub?.Report();
        }

        /// <summary>真正换地方（不改设置）：SetMode 和 CheckRunMode 都走这里</summary>
        void SwitchArea(bool desktop)
        {
            if (desktopMode == desktop || hwnd == IntPtr.Zero) return;
            StopWalk(null); StopMotion(false);
            desktopMode = desktop;
            shownInClaude = false; lastClaude = null;
            if (!desktop)
            {
                Native.SetWindowPos(hwnd, Native.HWND_NOTOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            }
            PlaceHome();
            UpdateFollowMark();   // 切到桌面上就撕掉「跟着 Claude」的标记
            UpdatePageHidden();
        }

        // ---------------- 多开 ----------------
        // 每只平级，都经过 PetHub（谁是负责人、怎么交接见 PetHub.cs 开头）
        /// <summary>默认位置按编号错开，免得几只叠在一起（只是位置，和谁是负责人无关）</summary>
        static int PetIndex => Store.IsMain ? 0 : int.TryParse(Store.PetId, out var n) ? Math.Max(1, n - 1) : 1;
        const int StaggerX = 190;

        public static bool LaunchPet(string id, string mode = null, string join = null)
        {
            try
            {
                var exe = Process.GetCurrentProcess().MainModule.FileName;
                var p = Process.Start(new ProcessStartInfo(exe, "--pet " + id + (mode == null ? "" : " --mode " + mode) + (join == null ? "" : " --join " + join))
                    { UseShellExecute = false, WorkingDirectory = Store.Install });
                if (p == null) return false;
                p.Dispose();
                return true;
            }
            catch (Exception e) { Store.Log($"没能叫出 {id} 号: " + e.Message); return false; }
        }

        IPetHub hub;
        bool leaving, isLeader;
        /// <summary>负责人锁：Program 启动时抢到了就交进来；接手时自己抢。要在界面线程上拿、上面放（互斥量归线程所有）</summary>
        public Mutex LeaderLock;

        static string InstanceMutex(string id) => Ensure.MutexName + "-pet-" + id;
        /// <summary>每只一把锁：这个编号是不是已经有一只在跑</summary>
        public static bool PetRunning(string id)
        {
            try { using (Mutex.OpenExisting(InstanceMutex(id))) return true; }
            catch (WaitHandleCannotBeOpenedException) { return false; }
            catch (UnauthorizedAccessException) { return true; }
        }
        public static Mutex TakeInstanceLock(string id)
        {
            var m = new Mutex(true, InstanceMutex(id), out var mine);
            if (mine) return m;
            m.Dispose(); return null;
        }
        /// <summary>挥手再走（退出、被送走、大家一起走）</summary>
        void Leave()
        {
            if (leaving) return;
            leaving = true;
            Js("farewell(null, 2000)");
            After(2000, Quit);
        }

        /// <summary>claudia-pet --quit 之类请大家一起走（以前的「全部退出」事件），交给负责人</summary>
        public void RequestQuit() => hub?.Request("quitAll");

        /// <summary>设置页「位置与多开」用：现在有哪几只、各叫什么、待在哪</summary>
        List<Dictionary<string, object>> PetList() => hub?.Roster(Store.PetId) ?? new List<Dictionary<string, object>>();

        void RenameSelf(string name)
        {
            const string cid = "claude";
            name = (name ?? "").Trim();
            if (name.Length > 20) name = name.Substring(0, 20);
            if (name == "") Store.Names.Remove(cid); else Store.Names[cid] = name;
            Store.SaveSettings();
            RefreshCharacterName(cid);
            var shown = Characters().FirstOrDefault(x => x.id == cid).name;
            if (shown != null) Js($"setName({Q(cid)}, {Q(shown)})");
            hub?.Report();
        }

        /// <summary>设置页发来的 {cmd:"pet", id, action, value}：改自己的名字 / 待在哪直接改，别的都交给负责人</summary>
        void HandlePetCommand(Dictionary<string, object> msg)
        {
            var action = msg.TryGetValue("action", out var a) ? a as string : null;
            var value = msg.TryGetValue("value", out var v) ? v as string : null;
            var id = msg.TryGetValue("id", out var i) ? i as string : null;
            if (action == "add") hub?.Request("add", value: value == "desktop" ? "desktop" : "claude");
            else if ((action == "mode" || action == "rename") && id == Store.PetId) HubApply(action, value);
            else if (action == "mode" || action == "rename" || action == "dismiss") hub?.Request("pet", id, action, value);
        }

        // ---- PetHub 回调 ----
        public Dictionary<string, object> HubInfo() => new Dictionary<string, object>
        {
            ["name"] = Store.Names.TryGetValue("claude", out var n) ? n as string ?? "" : "", ["mode"] = OnDesktop ? "desktop" : "claude",
        };
        public void HubApply(string action, string value)
        {
            if (action == "mode") SetMode(value == "desktop");
            else if (action == "rename") RenameSelf(value);
            else if (action == "dismiss") Leave();
        }
        public void HubRosterChanged() => PushSettings();
        /// <summary>请求没成：设置页底部说一声，再整页刷新一次（「再来一只」之类按了以后变灰的按钮恢复可点）</summary>
        public void HubNotice(string text) { settings?.Notice(text); PushSettings(); }
        public void HubSignal(string what)
        {
            if (what == "hide") ApplyHidden(true);
            else if (what == "show") { ApplyHidden(false); SetConcealed(false); }
            else if (what == "quit") Leave();
        }
        /// <summary>共用设置（快捷键、通知、鼠标穿透）重读一遍、照着来</summary>
        public void HubAppChanged()
        {
            Store.LoadApp();
            if (Host != null) Host.ForcePassThrough = concealed || ClickThrough;
            UpdateDeskLayer();   // 待在桌面上时的层级
            if (isLeader) RegisterHideHotKey();
            PushSettings();
        }
        /// <summary>不是负责人时：负责人发来的会话状态（变化，或者刚连上时的完整一份）</summary>
        public void HubSessions(List<Dictionary<string, object>> states) => ReceiveSessions(states);
        public bool HubLaunch(string id, string mode, string join) => LaunchPet(id, mode, join);
        public bool HubRunning(string id) => PetRunning(id);
        public bool HubLeaving() => leaving;
        public void HubToggle() => SetUserHidden(!userHidden);
        /// <summary>从菜单退出过（最后一只也走了、全部退出）：之后 Claude 先不把她叫出来，见 Ensure.Run</summary>
        public void HubUserQuit() { try { File.WriteAllText(UserQuitFlag, ""); } catch { } }
        /// <summary>负责人自己要走：先交出去，再挥手</summary>
        public void HubLeaveSelf() { GiveUpLeading(); Leave(); }
        public void HubMainLost() { leaving = true; Quit(); }
        public void Post(Action a) => System.Windows.Application.Current?.Dispatcher.BeginInvoke(a);
        public void Later(int ms, Action a) => After(ms, a);
    }
}
