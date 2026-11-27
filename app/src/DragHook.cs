using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace Claudia
{
    /// <summary>拖动时的鼠标「直通车」（2026-11-27 用户实测：扔出去的方向不对、有时停住不飞、跟手忽快忽慢）。
    /// 原来松手时间是界面线程下一次轮到检查按键的那一刻：16 毫秒的计时器实际约 30 毫秒一次，界面线程一忙（每秒找一次
    /// Claude 进程等）还会更晚，松手后鼠标往回带的那一小段也被当成松手前的轨迹，于是往反方向飞，或者算出来太慢就停住。
    /// 这里在单独的线程上挂一个低级鼠标钩子，只在拖动时挂：
    /// 1) 松开按键的准确时刻（系统给的时间戳，和 GetMouseMovePointsEx 的轨迹点同一个时钟），不看界面线程什么时候轮到；
    /// 2) 鼠标一动就通知一次（由使用方合并），不用干等计时器。
    /// 回调都在钩子线程上来，要回到界面线程再动窗口和网页</summary>
    sealed class DragHook
    {
        [StructLayout(LayoutKind.Sequential)]
        struct MSLLHOOKSTRUCT { public int x, y; public uint mouseData, flags, time; public IntPtr extraInfo; }
        [StructLayout(LayoutKind.Sequential)]
        struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }
        delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr module, uint thread);
        [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
        [DllImport("user32.dll")] static extern bool PostThreadMessage(uint thread, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);
        const int WH_MOUSE_LL = 14;
        const uint WM_QUIT = 0x12, WM_MOUSEMOVE = 0x200, WM_LBUTTONUP = 0x202, WM_RBUTTONUP = 0x205;

        readonly Action onMove;
        readonly Action<uint> onUp;
        readonly HookProc proc;   // 留着引用：交给系统的回调不能被回收
        volatile uint threadId;
        volatile bool stopped;

        /// <param name="onMove">鼠标动了（钩子线程上，来得很密，使用方自己合并）</param>
        /// <param name="onUp">松开了左键或右键，带系统时间戳（钩子线程上；按键交换过时哪个是主键由使用方判断）</param>
        public DragHook(Action onMove, Action<uint> onUp)
        {
            this.onMove = onMove; this.onUp = onUp;
            proc = Hook;
            var ready = new ManualResetEventSlim();
            var ok = false;
            new Thread(() =>
            {
                threadId = GetCurrentThreadId();
                var hook = SetWindowsHookEx(WH_MOUSE_LL, proc, GetModuleHandle(null), 0);
                ok = hook != IntPtr.Zero;
                ready.Set();
                if (!ok) { Store.Log("拖动用的鼠标钩子没挂上，退回按计时器判断松手"); return; }
                try { while (!stopped && GetMessage(out _, IntPtr.Zero, 0, 0) > 0) { } }
                finally { UnhookWindowsHookEx(hook); }
            }) { IsBackground = true, Name = "DragHook" }.Start();
            ready.Wait(500);   // 等钩子挂上（通常不到 1 毫秒）；挂不上也不耽误，照旧按计时器
            Active = ok;
        }

        /// <summary>钩子挂上了：松手时间可以信它</summary>
        public bool Active { get; }

        public void Stop()
        {
            if (stopped) return;
            stopped = true;
            if (threadId != 0) PostThreadMessage(threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        }

        IntPtr Hook(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code >= 0 && !stopped)
            {
                try
                {
                    var msg = (uint)wParam.ToInt64();
                    if (msg == WM_MOUSEMOVE) onMove();
                    else if (msg == WM_LBUTTONUP || msg == WM_RBUTTONUP) onUp(Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam).time);
                }
                catch { }   // 钩子里不能抛：这是系统回调
            }
            return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
        }
    }
}
