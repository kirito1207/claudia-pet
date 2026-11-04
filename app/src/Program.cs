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
                    try { msg = t == "plugin" ? Pet.SetPlugin(install) : t == "mcp" ? Pet.SetMcp(install) : "不认识：" + t; }
                    catch (Exception e) { msg = "没有完成：" + e.Message; }
                    Store.Log($"{flag} {t}：{msg.Replace("\n", " ")}");
                    Console.WriteLine($"[{t}] {msg}");
                }
                return;
            }
            if (!RenderRecovery.WaitForParent(args)) return;
            // 只开一个：再次打开时让已经在跑的那个回到 Claude 窗口里
            using (var mutex = new Mutex(true, Ensure.MutexName, out var first))
            using (var show = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\Claudia-Show-" + Environment.UserName))
            {
                if (!first) { show.Set(); return; }

                Directory.CreateDirectory(Store.Data);
                Store.MigrateOldData();
                RenderRecovery.Started();
                try { File.Delete(Pet.UserQuitFlag); } catch { }   // 自己打开了：恢复跟着 Claude 出现
                try { Directory.CreateDirectory(Store.State); File.WriteAllText(Path.Combine(Store.State, "pet.pid"), Process.GetCurrentProcess().Id.ToString()); } catch { }
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

                app.Startup += async (_, __) =>
                {
                    try
                    {
                        var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(Store.Data, "webview2"));
                        var pet = new Pet(env);
                        pet.Start();
                        new Thread(() =>
                        {
                            while (show.WaitOne()) app.Dispatcher.BeginInvoke(new Action(pet.Recenter));
                        }) { IsBackground = true }.Start();
                    }
                    catch (Exception e)
                    {
                        Store.Log("启动失败: " + e);
                        MessageBox.Show(Store.AppName + " 启动失败：\n" + e.Message, Store.AppName);
                        app.Shutdown();
                    }
                };
                app.Run();
                try { File.Delete(Path.Combine(Store.State, "pet.pid")); } catch { }
            }
        }
    }
}
