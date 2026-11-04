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
        // ---------------- 和网页打交道 ----------------
        public void Js(string code)
        {
            try { web?.CoreWebView2?.ExecuteScriptAsync(code); } catch (Exception e) { Store.Log("JS 出错: " + e.Message); }
        }
        public async Task<string> Eval(string code)
        {
            try { return web?.CoreWebView2 == null ? "null" : await web.CoreWebView2.ExecuteScriptAsync(code); }
            catch { return "null"; }
        }
        public void Reload() { ++followEventGeneration; ready = false; web?.CoreWebView2?.Reload(); }
        public static string Q(string s) => Store.Json.Serialize(s);

        /// <summary>读取 Claude 插件写入 state 目录的状态和额度，有变化就交给网页</summary>
        void Poll()
        {
            if (!ready) return;
            // Claudia 只有 Claude 一个角色：插件写 claude-state.json（当前动作）、claude-quota.json（额度）、
            // claude-sessions.json（好几个 Code 会话同时干活时各自的状态）
            const string id = "claude";
            foreach (var kind in new[] { "state", "quota", "sessions" })
            {
                var file = Path.Combine(Store.State, $"{id}-{kind}.json");
                FileInfo fi;
                try { fi = new FileInfo(file); if (!fi.Exists || fi.Length > 1_000_000) continue; } catch { continue; }
                var stamp = fi.LastWriteTimeUtc.Ticks + ":" + fi.Length;
                var key = id + "-" + kind;
                if (stamps.TryGetValue(key, out var old) && old == stamp) continue;
                if (kind == "state" && (DateTime.UtcNow - fi.LastWriteTimeUtc).TotalMinutes > 10) { stamps[key] = stamp; continue; }  // 只认 10 分钟内的
                var value = Store.ReadJson(file);
                if (value == null) continue;   // 可能正被另一边写一半，下次再读
                stamps[key] = stamp;
                var json = Store.Json.Serialize(value);
                if (Paused) continue;
                if (kind == "sessions") { Js($"setSessions({Q(id)}, {json})"); continue; }
                if (kind == "state")
                {
                    NoticeFromState(value);
                    Js($"applyCharState({Q(id)}, {json})");
                }
                else Js($"setQuota({Q(id)}, {json})");
            }
            PollTaskNotices();
            ApplyLayer();
        }
    }
}
