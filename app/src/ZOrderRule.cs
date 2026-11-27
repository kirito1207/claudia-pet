using System;
using System.Collections.Generic;

namespace Claudia
{
    static class ZOrderRule
    {
        /// <summary>跟同一个 Claude 窗口的桌宠排成一组（2026-11-27，照 Codex 的方案）：从 Claude 往上看，看不见的窗口跳过，
        /// 连着遇到的同组桌宠（窗口上贴着同一个 Claude 的标记）都算排好了；这一段里有自己就不用动。
        /// 先碰到别的看得见的窗口，说明自己掉出了队伍，要插回 Claude 正上方。
        /// 原来每只都要求「紧挨 Claude 的就是我」：两只跟同一个窗口时互相插队，每 0.1 秒（切窗口后每 1 毫秒）来回换一次</summary>
        /// <param name="above">从紧挨 Claude 的那个往上，依次是 (窗口, 看不看得见, 它贴的标记：跟着哪个 Claude，没有是 0)</param>
        public static bool InClaudeStack(IEnumerable<(IntPtr h, bool visible, IntPtr mark)> above, IntPtr pet, IntPtr claude)
        {
            foreach (var (h, visible, mark) in above)
            {
                if (h == pet) return true;
                if (!visible) continue;
                if (mark == claude && claude != IntPtr.Zero) continue;   // 同组的另一只
                return false;   // 别的看得见的窗口
            }
            return false;
        }

        /// <summary>桌面和任务栏窗口类</summary>
        public static bool IsShell(string cls) =>
            cls == "Progman" || cls == "WorkerW" || cls == "Shell_TrayWnd" || cls == "Shell_SecondaryTrayWnd";

        /// <summary>隐藏时只插到外部普通窗口下方，避开桌面、任务栏和自己的菜单。</summary>
        public static bool TuckBelow(string frontClass, bool frontIsOurs) =>
            !frontIsOurs && !string.IsNullOrEmpty(frontClass) && !IsShell(frontClass);

    }
}
