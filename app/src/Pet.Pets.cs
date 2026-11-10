using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace Claudia
{
    sealed partial class Pet
    {
        // ---------------- 待在哪：Claude 窗口里 / 桌面上 ----------------
        // 照 dsh-pet 的桌面模式：桌面上的那只始终置顶，活动范围是所在显示器的工作区（不进任务栏），Claude 关了一样跟着退出。
        // 每只各选各的，记在自己的设置里（mode = "desktop" / 没有）。具体怎么摆见 Pet.Follow.cs 的 DesktopPlace
        static bool OnDesktop => Store.Settings.TryGetValue("mode", out var v) && v as string == "desktop";

        void SetMode(bool desktop)
        {
            if (desktopMode == desktop || hwnd == IntPtr.Zero) return;
            StopWalk(null); StopMotion(false);
            Store.Settings["mode"] = desktop ? "desktop" : "claude"; Store.SaveSettings();
            desktopMode = desktop;
            shownInClaude = false; lastClaude = null; topmostAt = DateTime.MinValue;
            if (!desktop)
            {
                if (Window != null && Window.Topmost) Window.Topmost = false;
                Native.SetWindowPos(hwnd, Native.HWND_NOTOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            }
            PlaceHome();
            UpdatePageHidden();
            Store.Log(desktop ? "改成待在桌面上" : "改成待在 Claude 窗口里");
        }

        // ---------------- 多开 ----------------
        /// <summary>第几只（第一只是 0）：默认位置按这个错开，免得几只叠在一起</summary>
        static int PetIndex => Store.IsMain ? 0 : int.TryParse(Store.PetId, out var n) ? Math.Max(1, n - 1) : 1;
        const int StaggerX = 190;

        public static void LaunchPet(string id, string mode = null)
        {
            try
            {
                var exe = Process.GetCurrentProcess().MainModule.FileName;
                Process.Start(new ProcessStartInfo(exe, "--pet " + id + (mode == null ? "" : " --mode " + mode))
                    { UseShellExecute = false, WorkingDirectory = Store.Install })?.Dispose();
            }
            catch (Exception e) { Store.Log($"没能叫出第 {id} 只: " + e.Message); }
        }

        /// <summary>再来一只：挑一个没用过的编号，记进名单，开一个新进程</summary>
        void AddPet(bool desktop)
        {
            var ids = Store.ExtraPets();
            if (ids.Count >= Store.MaxExtraPets) { Js($"say({Q("家里住不下啦")})"); return; }
            var id = Enumerable.Range(2, Store.MaxExtraPets).Select(n => n.ToString()).First(n => !ids.Contains(n));
            // 这个编号以前用过、又送走了：旧设置不留给新来的
            try { var old = Path.Combine(Store.Data, "pets", id, "settings.json"); if (File.Exists(old)) File.Delete(old); } catch { }
            ids.Add(id);
            try { Store.SaveExtraPets(ids); } catch (Exception e) { Store.Log("保存多开名单失败: " + e.Message); return; }
            LaunchPet(id, desktop ? "desktop" : "claude");
        }

        /// <summary>送走这一只（多开的才有）：从名单里去掉，挥手再走</summary>
        void DismissPet()
        {
            if (Store.IsMain) return;
            try { Store.SaveExtraPets(Store.ExtraPets().Where(i => i != Store.PetId)); } catch (Exception e) { Store.Log("保存多开名单失败: " + e.Message); }
            Js("farewell(null, 2000)");
            After(2000, Quit);
        }

        // 全部退出：多开的那几只里点了，是请第一只来退；第一只退出时把「quit」写进信号文件，大家一起挥手、一起走
        // （2026-11-10 用户实测：原来是第一只先走，别的过两三秒才发现它不在了）
        public static string QuitAllEvent => @"Local\Claudia-Quit-" + Environment.UserName;
        /// <summary>多开的那几只菜单里的「送走所有新加的」：新加的全部送走，第一只不受影响（2026-11-10 用户定）</summary>
        void DismissExtras()
        {
            foreach (var id in Store.ExtraPets().Where(i => i != Store.PetId))
                try { Store.WriteJson(CommandFile(id), new Dictionary<string, object> { ["action"] = "dismiss", ["value"] = "" }); }
                catch (Exception e) { Store.Log("没能把指令交给第 " + id + " 只: " + e.Message); }
            DismissPet();
        }
        public void RequestQuit() => UserQuit();

        // 藏起来 / 叫出来 / 一起退出由第一只说了算（快捷键、托盘都在它那儿）：它把最新一次写进这个文件，别的每秒看一眼，变了就照做
        static string PetSignal => Path.Combine(Store.Data, "pets-signal");
        string seenSignal;
        static void WriteSignal(string what)
        {
            if (!Store.IsMain) return;
            try { File.WriteAllText(PetSignal, what + " " + DateTime.UtcNow.Ticks); } catch { }
        }
        static void WriteHiddenSignal(bool hidden) => WriteSignal(hidden ? "hide" : "show");
        public static void ClearPetSignal() { try { File.Delete(PetSignal); } catch { } }

        int mainMissing;
        bool leaving;
        /// <summary>每秒一次：收设置页发来的指令；多开的那几只还要看第一只在不在、它让藏就藏、让走就走</summary>
        void PetTick()
        {
            if (leaving) return;
            PollPetCommand();
            if (Store.IsMain) return;
            if (Ensure.PetRunning()) mainMissing = 0;
            else if (++mainMissing >= 3) { Store.Log("第一只不在了，跟着退出"); leaving = true; Quit(); return; }
            var s = Store.ReadText(PetSignal);
            if (seenSignal == null) { seenSignal = s ?? ""; return; }   // 刚来：以前的不算
            if ((s ?? "") == seenSignal) return;
            seenSignal = s ?? "";
            if (seenSignal.StartsWith("hide")) SetUserHidden(true);
            else if (seenSignal.StartsWith("show")) { SetUserHidden(false); SetConcealed(false); }
            else if (seenSignal.StartsWith("quit")) { leaving = true; Js("farewell(null, 2000)"); After(2000, Quit); }
        }

        // ---------------- 设置页里一次管理所有的（「多开」页） ----------------
        // 每只是独立的进程，设置页可能开在任何一只那里：管自己的直接改；管别的就往 pets-cmd\<编号>.json 放一条指令，那一只下一秒读到照做
        static string CommandFile(string id) => Path.Combine(Store.Data, "pets-cmd", id + ".json");
        static string SettingsFileOf(string id) => id == "main" ? Path.Combine(Store.Data, "settings.json") : Path.Combine(Store.Data, "pets", id, "settings.json");

        /// <summary>设置页用：现在有哪几只、各叫什么、待在哪</summary>
        List<Dictionary<string, object>> PetList()
        {
            var list = new List<Dictionary<string, object>>();
            var n = 0;
            foreach (var id in new[] { "main" }.Concat(Store.ExtraPets()))
            {
                var self = id == Store.PetId;
                var s = self ? Store.Settings : Store.ReadJson(SettingsFileOf(id)) ?? new Dictionary<string, object>();
                var name = s.TryGetValue("names", out var nv) && nv is Dictionary<string, object> names && names.TryGetValue("claude", out var cn) ? cn as string ?? "" : "";
                list.Add(new Dictionary<string, object>
                {
                    ["id"] = id, ["index"] = ++n, ["self"] = self, ["name"] = name,
                    ["mode"] = s.TryGetValue("mode", out var m) && m as string == "desktop" ? "desktop" : "claude",
                });
            }
            return list;
        }

        void RenameSelf(string name)
        {
            const string cid = "claude";
            name = (name ?? "").Trim();
            if (name.Length > 20) name = name.Substring(0, 20);
            if (name == "") Store.Names.Remove(cid); else Store.Names[cid] = name;
            Store.SaveSettings();
            LoadCharacters();
            var shown = Characters().FirstOrDefault(x => x.id == cid).name;
            if (shown != null) Js($"setName({Q(cid)}, {Q(shown)})");
        }

        /// <summary>设置页发来的 {cmd:"pet", id, action, value}</summary>
        void HandlePetCommand(Dictionary<string, object> msg)
        {
            var action = msg.TryGetValue("action", out var a) ? a as string : null;
            var value = msg.TryGetValue("value", out var v) ? v as string : null;
            if (action == "add") { AddPet(value == "desktop"); After(1500, PushSettings); After(4000, PushSettings); return; }
            var id = msg.TryGetValue("id", out var i) ? i as string : null;
            if (id != "main" && !Store.ExtraPets().Contains(id)) return;
            if (action != "mode" && action != "rename" && action != "dismiss") return;
            if (id == Store.PetId) { ApplyPetCommand(action, value); return; }
            try { Store.WriteJson(CommandFile(id), new Dictionary<string, object> { ["action"] = action, ["value"] = value ?? "" }); }
            catch (Exception e) { Store.Log("没能把指令交给第 " + id + " 只: " + e.Message); }
            After(1500, PushSettings); After(3500, PushSettings);   // 等那一只照做了再刷新这一页
        }

        void ApplyPetCommand(string action, string value)
        {
            if (action == "mode") SetMode(value == "desktop");
            else if (action == "rename") RenameSelf(value);
            else if (action == "dismiss") DismissPet();
        }

        void PollPetCommand()
        {
            var file = CommandFile(Store.PetId);
            if (!File.Exists(file)) return;
            var cmd = Store.ReadJson(file);
            try { File.Delete(file); } catch { }
            if (cmd == null) return;
            ApplyPetCommand(cmd.TryGetValue("action", out var a) ? a as string : null, cmd.TryGetValue("value", out var v) ? v as string : null);
            PushSettings();
        }
    }
}
