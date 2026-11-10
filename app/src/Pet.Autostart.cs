using System;
using System.IO;
using System.Windows;

namespace Claudia
{
    sealed partial class Pet
    {
        // ---------------- 退出 ----------------
        // 她由 Claude 拉起（Claude Code 插件、桌面端 MCP 都调 claudia-pet.exe --ensure，见 Ensure.cs），Claude 关了她自己走（Pet.Follow.cs）。
        // 从菜单手动退出 = 这会儿不想看到她：记下 user-quit（看文件修改时间），在那之前就开着的 Claude 不再把她叫出来；
        // Claude 关掉重开、重启电脑后作废，自己双击打开也会清掉（见 Program）。
        public static string UserQuitFlag => Path.Combine(Store.Data, "user-quit");

        /// 菜单里的「退出」：挥手道别再走；第一次退出时说明什么时候会再出来，多停一会儿让人看清
        void UserQuit()
        {
            try { File.WriteAllText(UserQuitFlag, ""); } catch { }
            WriteSignal("quit");   // 多开的几只一起挥手、一起走
            var hint = !Store.Flag("quitHinted");
            if (hint) { Store.Settings["quitHinted"] = true; Store.SaveSettings(); }
            Js(hint ? "farewell('我先走啦～下次打开 Claude 我再出来', 5000)" : "farewell(null, 2000)");
            var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(hint ? 5 : 2) };
            t.Tick += (_, __) => { t.Stop(); Quit(); };
            t.Start();
        }

        public void Quit()
        {
            StopMotion();
            if (tray != null) { tray.Visible = false; tray.Dispose(); }   // 多开的几只没有托盘图标
            Application.Current.Shutdown();
        }
    }
}
