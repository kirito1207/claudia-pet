using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace Claudia
{
    sealed partial class Pet
    {
        // ---------------- 快捷键隐藏 ----------------
        // 全局快捷键（默认 Ctrl+Alt+P）：按一下藏起来，再按一下出来；双击托盘图标也能叫出来。藏着时 AI 干活也不出来，重开恢复显示
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        const int HotKeyId = 0x4350;
        static readonly (string id, string label, uint vk)[] HotkeyChoices = { ("p", "Ctrl+Alt+P", 0x50), ("h", "Ctrl+Alt+H", 0x48) };
        static string HideHotkey => Store.App.TryGetValue("hideHotkey", out var v) && v is string s ? s : "p";   // 所有桌宠共用
        static string HotkeyLabel => HotkeyChoices.FirstOrDefault(h => h.id == HideHotkey).label;
        bool userHidden, hotkeyFailed;
        void RegisterHideHotKey()
        {
            if (hwnd == IntPtr.Zero || !isLeader) return;   // 只有负责人注册：按了经 PetHub 叫大家一起藏 / 出来
            UnregisterHotKey(hwnd, HotKeyId);
            hotkeyFailed = false;
            var choice = HotkeyChoices.FirstOrDefault(h => h.id == HideHotkey);
            if (choice.id == null) return;
            hotkeyFailed = !RegisterHotKey(hwnd, HotKeyId, 0x0001 | 0x0002 | 0x4000, choice.vk);   // Alt | Ctrl | 不连发
            if (hotkeyFailed) Store.Log($"快捷键 {choice.label} 注册失败（被别的程序占用了）");
        }
        /// <summary>窗口消息先过这里（DirectHost.MessageHook）：返回 true = 处理掉了</summary>
        bool HotKeyHook(int msg, IntPtr wParam)
        {
            if (msg != 0x0312 || wParam.ToInt32() != HotKeyId) return false;   // WM_HOTKEY
            HubToggle();
            return true;
        }
        /// <summary>藏起来 / 叫出来是大家一起的：请负责人发给每一只（负责人自己也收到，见 HubSignal）</summary>
        void SetUserHidden(bool hidden)
        {
            if (hub != null) hub.Request(hidden ? "hide" : "show");
            else ApplyHidden(hidden);
        }
        void ApplyHidden(bool hidden)
        {
            userHidden = hidden;
            SetConcealed(hidden);
        }

        /// <summary>双击托盘：藏着就叫出来，位置不动</summary>
        void ShowAgain()
        {
            SetUserHidden(false);   // 多开的几只也一起叫出来
        }

        public void Recenter()
        {
            StopMotion(false);
            ResetFollowOffsets();   // 回到 Claude 窗口右下角的默认位置
            if (userHidden) SetUserHidden(false);   // 快捷键藏起来的：一起恢复（包括动画）
            SetConcealed(false);
        }
    }
}
