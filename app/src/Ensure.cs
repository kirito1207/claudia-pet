using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace Claudia
{
    /// <summary>
    /// claudia-pet.exe --ensure：Claude Code 插件、桌面端 MCP 用它把桌宠叫出来。
    /// 已经在跑就什么都不做；你从菜单退出过、而 Claude 是在那之前就开着的，也不开（等 Claude 重开或重启电脑）。
    /// 用 CREATE_BREAKAWAY_FROM_JOB 起一个独立的进程：插件和 MCP 都是 Claude 的子进程，Claude 重启 / 更新时会连带清掉它们的整棵进程树，
    /// 桌宠不能跟着被清掉（她自己会在 Claude 关掉后退出，见 Pet.Follow.cs）。
    /// </summary>
    static class Ensure
    {
        public static string MutexName => @"Local\Claudia-" + Environment.UserName;

        public static bool PetRunning()
        {
            try { using (Mutex.OpenExisting(MutexName)) return true; }
            catch (WaitHandleCannotBeOpenedException) { return false; }
            catch (UnauthorizedAccessException) { return true; }
        }

        /// <summary>返回这次的结果（写进日志，排查「为什么没出来」用）</summary>
        public static string Run()
        {
            if (PetRunning()) return "已经在跑";
            var quit = Pet.UserQuitFlag;
            if (File.Exists(quit))
            {
                var quitAt = File.GetLastWriteTimeUtc(quit);
                var claudeAt = ClaudeStartedAt();
                if (claudeAt == null || claudeAt <= quitAt) return "不打开：你从菜单退出过，这个 Claude 是在那之前就开着的";
            }
            return Launch(Process.GetCurrentProcess().MainModule.FileName, "");
        }

        /// <summary>Claude 桌面端最早的那个进程是什么时候开的（UTC）；没开返回 null</summary>
        static DateTime? ClaudeStartedAt()
        {
            DateTime? first = null;
            foreach (var p in Process.GetProcessesByName("claude"))
            {
                try
                {
                    var path = p.MainModule?.FileName ?? "";
                    if (path.IndexOf(@"\WindowsApps\Claude_", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    var t = p.StartTime.ToUniversalTime();
                    if (first == null || t < first) first = t;
                }
                catch { }
                finally { p.Dispose(); }
            }
            return first;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct STARTUPINFO { public int cb; public string lpReserved, lpDesktop, lpTitle; public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags; public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError; }
        [StructLayout(LayoutKind.Sequential)]
        struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool CreateProcessW(string app, string cmd, IntPtr pa, IntPtr ta, bool inherit, uint flags, IntPtr env, string dir, ref STARTUPINFO si, out PROCESS_INFORMATION pi);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
        const uint CREATE_BREAKAWAY_FROM_JOB = 0x01000000, CREATE_NEW_PROCESS_GROUP = 0x00000200;

        static string Launch(string exe, string args)
        {
            var cmd = $"\"{exe}\"" + (args == "" ? "" : " " + args);
            var dir = Path.GetDirectoryName(exe);
            foreach (var flags in new[] { CREATE_BREAKAWAY_FROM_JOB | CREATE_NEW_PROCESS_GROUP, CREATE_NEW_PROCESS_GROUP })   // 进程组不许脱离时退回普通启动
            {
                var si = new STARTUPINFO { cb = Marshal.SizeOf(typeof(STARTUPINFO)) };
                if (CreateProcessW(null, cmd, IntPtr.Zero, IntPtr.Zero, false, flags, IntPtr.Zero, dir, ref si, out var pi))
                {
                    CloseHandle(pi.hThread); CloseHandle(pi.hProcess);
                    return (flags & CREATE_BREAKAWAY_FROM_JOB) != 0 ? $"已打开（独立进程 {pi.dwProcessId}）" : $"已打开（进程 {pi.dwProcessId}，没能脱离 Claude 的进程组）";
                }
            }
            return "打开失败：错误 " + Marshal.GetLastWin32Error();
        }
    }
}
