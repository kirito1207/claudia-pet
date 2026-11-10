using System;
using System.Diagnostics;
using System.IO;
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
            // 多开的那几只：claudia-pet.exe --pet 2 [--mode desktop|claude]，由第一只带起来（见 Pet.Pets.cs）
            string firstMode = null;
            var petArg = Array.IndexOf(args, "--pet");
            if (petArg >= 0)
            {
                var id = petArg + 1 < args.Length ? args[petArg + 1] : null;
                if (!Store.ValidPetId(id)) return;
                Store.PetId = id;
                var modeArg = Array.IndexOf(args, "--mode");
                if (modeArg >= 0 && modeArg + 1 < args.Length && (args[modeArg + 1] == "desktop" || args[modeArg + 1] == "claude")) firstMode = args[modeArg + 1];
            }
            if (!RenderRecovery.WaitForParent(args)) return;
            // 每只只开一个：第一只再次打开时，让已经在跑的那个回到默认位置
            var main = Store.IsMain;
            using (var mutex = new Mutex(true, main ? Ensure.MutexName : Ensure.MutexName + "-pet-" + Store.PetId, out var first))
            using (var show = main ? new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\Claudia-Show-" + Environment.UserName) : new EventWaitHandle(false, EventResetMode.AutoReset))
            using (var quitAll = main ? new EventWaitHandle(false, EventResetMode.AutoReset, Pet.QuitAllEvent) : new EventWaitHandle(false, EventResetMode.AutoReset))
            {
                if (!first) { if (main) show.Set(); return; }
                if (!main && !Ensure.PetRunning()) { Store.Log("第一只没在跑，不单独打开"); return; }

                Directory.CreateDirectory(Store.PetDir);
                if (main) Store.MigrateOldData();
                RenderRecovery.Started();
                if (main)
                {
                    try { File.Delete(Pet.UserQuitFlag); } catch { }   // 自己打开了：恢复跟着 Claude 出现
                    try { Directory.CreateDirectory(Store.State); File.WriteAllText(Path.Combine(Store.State, "pet.pid"), Process.GetCurrentProcess().Id.ToString()); } catch { }
                    Pet.ClearPetSignal();
                }
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
                Store.LoadSettings();
                if (firstMode != null && !Store.Settings.ContainsKey("mode")) { Store.Settings["mode"] = firstMode; Store.SaveSettings(); }

                app.Startup += async (_, __) =>
                {
                    try
                    {
                        var env = await CoreWebView2Environment.CreateAsync(null, Store.WebViewDir);
                        var pet = new Pet(env);
                        pet.Start();
                        if (main)
                        {
                            new Thread(() =>
                            {
                                while (show.WaitOne()) app.Dispatcher.BeginInvoke(new Action(pet.Recenter));
                            }) { IsBackground = true }.Start();
                            new Thread(() =>
                            {
                                while (quitAll.WaitOne()) app.Dispatcher.BeginInvoke(new Action(pet.RequestQuit));
                            }) { IsBackground = true }.Start();
                            foreach (var id in Store.ExtraPets()) Pet.LaunchPet(id);   // 把上次多开的几只也叫出来
                        }
                    }
                    catch (Exception e)
                    {
                        Store.Log("启动失败: " + e);
                        MessageBox.Show(Store.AppName + " 启动失败：\n" + e.Message, Store.AppName);
                        app.Shutdown();
                    }
                };
                app.Run();
                if (main) try { File.Delete(Path.Combine(Store.State, "pet.pid")); } catch { }
            }
        }
    }
}
