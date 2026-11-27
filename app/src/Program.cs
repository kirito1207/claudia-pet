using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace Claudia
{
    static class Program
    {
        const string WebView2Download = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";

        /// WPF 渲染线程挂了（0x88980406 UCEERR_RENDERTHREADFAILURE）：这之后窗口不会再重画，当作没事继续跑就是画面冻住
        static bool IsRenderThreadFailure(Exception e)
        {
            for (var x = e; x != null; x = x.InnerException)
                if ((uint)x.HResult == 0x88980406 || (x.Message ?? "").Contains("UCEERR_RENDERTHREADFAILURE")) return true;
            return false;
        }

        /// 还没选过运行方式（0.4.1 升上来的，或者第一次用）时选一次，之后在设置页「接入 Claude」里改：
        /// 第一次用的一律「独立运行」并待在桌面上（2026-11-27 用户定：有人只是喜欢这个形象、没装 Claude 桌面端，
        /// 或者只用 Claude Code——「跟着 Claude」找不到桌面端窗口 30 秒就退出了）；以前用过的（已有设置）照旧「跟着 Claude」
        static void ChooseRunMode(bool freshInstall, bool byClaude)
        {
            var connected = false;
            try { connected = Pet.ClaudeConnected(); } catch { }
            var standalone = freshInstall;
            Store.SetStandalone(standalone);
            if (standalone && !Store.Settings.ContainsKey("mode")) { Store.Settings["mode"] = "desktop"; Store.SaveSettings(); }
            Store.Log($"运行方式：{(standalone ? "独立运行" : "跟着 Claude")}（第一次用 = {freshInstall}，Claude 叫出来的 = {byClaude}，已接入 Claude = {connected}）");
        }

        /// <summary>直接打开（双击、Claude 叫出来）的这一只用哪个编号：已经有负责人在跑（或马上要有）就请它叫大家出来，返回 null；
        /// 不然抢负责人锁，用名单上第一个没在跑的编号（名单是空的就从 main 开始）。在界面线程（Main 的线程）上调：负责人锁归这个线程</summary>
        static (string id, Mutex leader)? ChooseIdentity()
        {
            for (var i = 0; i < 25; i++)   // 最多等约 5 秒：正在换负责人时锁会空一下
            {
                if (PetHubNames.SendCli("show", timeoutMs: 300)) return null;
                var lead = Pet.TakeLeaderLock();
                if (lead != null)
                {
                    PetHubNames.ClearStopping();   // 打开了：「全部退出」的记号作废
                    var ids = Store.Roster();
                    var free = ids.Where(x => !Pet.PetRunning(x)).ToList();
                    if (free.Count > 0 || ids.Count == 0)
                    {
                        var id = free.Count > 0 ? free[0] : Store.FreePetId(x => !ids.Contains(x));
                        if (!ids.Contains(id)) Store.SaveRoster(ids.Concat(new[] { id }));
                        return (id, lead);
                    }
                    try { lead.ReleaseMutex(); } catch { }   // 名单上的几只都在跑、只是在换负责人：等新负责人
                    lead.Dispose();
                }
                Thread.Sleep(200);
            }
            Store.Log("等不到负责人，这次不打开");
            return null;
        }

        // [STAThread] 必须紧挨着 Main：WPF 和 WebView2 都要求主线程是 STA（中间插了别的函数，Main 就成了 MTA，启动报 RPC_E_CHANGED_MODE）
        [STAThread]
        static void Main(string[] args)
        {
            // 两个给 Claude 用的入口：不开窗口，做完就走（--mcp 一直到 Claude 关掉连接）
            if (Array.IndexOf(args, "--ensure") >= 0) { Store.Log("--ensure：" + Ensure.Run()); return; }
            if (Array.IndexOf(args, "--mcp") >= 0) { Mcp.Run(); return; }
            // 接入 / 撤销（安装、卸载脚本用；设置页里点按钮走的是同一套）：--connect plugin|mcp|all、--disconnect plugin|mcp|all
            foreach (var (flag, install) in new[] { ("--connect", true), ("--disconnect", false) })
            {
                var i = Array.IndexOf(args, flag);
                if (i < 0) continue;
                var what = i + 1 < args.Length ? args[i + 1] : "all";
                Directory.CreateDirectory(Store.Data);
                foreach (var t in what == "all" ? new[] { "plugin", "mcp" } : new[] { what })
                {
                    string msg;
                    try
                    {
                        if (t == "plugin") msg = Pet.SetPlugin(install);
                        else if (t == "mcp") msg = Pet.SetMcp(install);
                        else throw new ArgumentException("不认识：" + t);
                    }
                    catch (Exception e)
                    {
                        Environment.ExitCode = 1;
                        msg = "没有完成：" + e.Message;
                    }
                    Store.Log($"{flag} {t}：{msg.Replace("\n", " ")}");
                    Console.WriteLine($"[{t}] {msg}");
                }
                return;
            }
            // 每只平级（2026-11-27）：--pet <编号> [--mode desktop|claude] 是负责人叫出来的那几只（见 PetHub.cs）；
            // 直接打开的那一只当负责人（见 ChooseIdentity）
            string firstMode = null;
            Mutex leader = null;
            var petArg = Array.IndexOf(args, "--pet");
            if (petArg >= 0)
            {
                var id = petArg + 1 < args.Length ? args[petArg + 1] : null;
                if (!Store.ValidPetId(id)) return;
                Store.PetId = id;
                var modeArg = Array.IndexOf(args, "--mode");
                if (modeArg >= 0 && modeArg + 1 < args.Length && (args[modeArg + 1] == "desktop" || args[modeArg + 1] == "claude")) firstMode = args[modeArg + 1];
                var joinArg = Array.IndexOf(args, "--join");
                if (joinArg >= 0 && joinArg + 1 < args.Length) Store.JoinToken = args[joinArg + 1];
            }
            if (!RenderRecovery.WaitForParent(args)) return;
            if (petArg < 0)
            {
                var picked = ChooseIdentity();
                if (picked == null) return;
                Store.PetId = picked.Value.id; leader = picked.Value.leader;
            }
            var isLeader = leader != null;
            using (var instance = Pet.TakeInstanceLock(Store.PetId))   // 每只一把锁：这个编号已经在跑就不开第二个
            {
                if (instance == null) { if (leader != null) { try { leader.ReleaseMutex(); } catch { } leader.Dispose(); } return; }

                Directory.CreateDirectory(Store.PetDir);
                if (isLeader) Store.MigrateOldData();
                RenderRecovery.Started();
                if (isLeader) try { File.Delete(Pet.UserQuitFlag); } catch { }   // 打开了：恢复跟着 Claude 出现
                AppDomain.CurrentDomain.UnhandledException += (_, e) => Store.Log("崩溃: " + e.ExceptionObject);
                Store.Log($"启动 {Store.Version}，{Environment.OSVersion}，64 位进程 = {Environment.Is64BitProcess}");
                System.Windows.Forms.Application.EnableVisualStyles();

                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                app.DispatcherUnhandledException += (_, e) =>
                {
                    Store.Log("界面异常: " + e.Exception);
                    e.Handled = true;
                    if (IsRenderThreadFailure(e.Exception)) RenderRecovery.Restart();
                };

                // WebView2 运行时：Windows 11 自带；老的 Windows 10 可能没有，指引去微软官网装
                string version;
                try { version = CoreWebView2Environment.GetAvailableBrowserVersionString(); }
                catch { version = null; }
                if (string.IsNullOrEmpty(version))
                {
                    if (MessageBox.Show(Store.AppName + " 需要系统组件「Microsoft Edge WebView2 运行时」（Windows 11 自带）。\n\n要现在打开微软官网下载吗？",
                        Store.AppName, MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                        Process.Start(new ProcessStartInfo(WebView2Download) { UseShellExecute = true });
                    return;
                }
                Store.Log("WebView2 运行时 " + version);
                var freshInstall = isLeader && !File.Exists(Path.Combine(Store.Data, "settings.json"));
                Store.LoadSettings();
                Store.LoadApp();   // 所有桌宠共用的设置；第一次从原来第一只的设置里搬过来
                if (firstMode != null && !Store.Settings.ContainsKey("mode")) { Store.Settings["mode"] = firstMode; Store.SaveSettings(); }
                if (isLeader && !Store.HasRunMode) ChooseRunMode(freshInstall, Array.IndexOf(args, Ensure.ByClaudeArg) >= 0);

                app.Startup += async (_, __) =>
                {
                    try
                    {
                        var env = await CoreWebView2Environment.CreateAsync(null, Store.WebViewDir);
                        var pet = new Pet(env) { LeaderLock = leader };
                        pet.Start();
                        if (isLeader)   // 把名单上别的几只也叫出来
                            foreach (var id in Store.Roster()) if (id != Store.PetId && !Pet.PetRunning(id)) Pet.LaunchPet(id);
                    }
                    catch (Exception e)
                    {
                        Store.Log("启动失败: " + e);
                        MessageBox.Show(Store.AppName + " 启动失败：\n" + e.Message, Store.AppName);
                        app.Shutdown();
                    }
                };
                app.Exit += (_, __) => Store.FlushSettings();   // 还没写进文件的大小、位置（SaveSettingsSoon）
                app.Run();
                // 负责人写的 pet.pid：还是自己的才删（交接后是下一任的）
                try { var pidFile = Path.Combine(Store.State, "pet.pid"); if (Store.ReadText(pidFile) == Process.GetCurrentProcess().Id.ToString()) File.Delete(pidFile); } catch { }
            }
        }
    }
}
