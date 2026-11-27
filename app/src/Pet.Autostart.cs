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

        /// <summary>菜单里的「退出」：只走这一只（请负责人把我从名单去掉，它再叫我挥手走）</summary>
        void QuitSelf()
        {
            if (hub != null) hub.Request("leave");
            else Leave();
        }

        public void Quit()
        {
            StopMotion();
            if (hwnd != IntPtr.Zero) FollowNative.Mark(hwnd, IntPtr.Zero);   // 窗口销毁前撕掉标记
            GiveUpLeading();
            hub?.Close();
            UnregisterAppBar();
            Application.Current.Shutdown();
        }
    }
}
