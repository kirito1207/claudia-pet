namespace Claudia
{
    static class ZOrderRule
    {
        /// <summary>桌面和任务栏窗口类</summary>
        public static bool IsShell(string cls) =>
            cls == "Progman" || cls == "WorkerW" || cls == "Shell_TrayWnd" || cls == "Shell_SecondaryTrayWnd";

        /// <summary>隐藏时只插到外部普通窗口下方，避开桌面、任务栏和自己的菜单。</summary>
        public static bool TuckBelow(string frontClass, bool frontIsOurs) =>
            !frontIsOurs && !string.IsNullOrEmpty(frontClass) && !IsShell(frontClass);

    }
}
