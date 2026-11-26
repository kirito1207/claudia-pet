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
            // claude-sessions.json 是仍在运行的旧插件写的多会话状态。
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
                else Js($"setQuota({Q(id)}, {json}, {new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeSeconds()})");   // 带上修改时间：网页据此标「多久前的数据」
            }
            if (!Paused) PollSessionStates(id);
            CleanStaleState();
            PollTaskNotices();
            ApplyLayer();
        }

        // 插件每个会话写一个 claude-session-<sid>.json、一个 claude-notify-<sid>.json，自己从不删：会话多了 state 目录一直攒小文件。
        // 这两种只认最近 10 分钟 / 2 分钟的，超过一天的由第一只删掉（启动时一次、之后每小时一次；几只一起删会抢）
        DateTime stateCleanedAt = DateTime.MinValue;
        void CleanStaleState()
        {
            if (!Store.IsMain || (DateTime.UtcNow - stateCleanedAt).TotalHours < 1) return;
            stateCleanedAt = DateTime.UtcNow;
            int n = 0;
            try
            {
                var cutoff = DateTime.UtcNow.AddDays(-1);
                foreach (var pattern in new[] { "claude-session-*.json", "claude-notify-*.json" })
                    foreach (var file in new DirectoryInfo(Store.State).EnumerateFiles(pattern))
                        try { if (file.LastWriteTimeUtc < cutoff) { file.Delete(); n++; } } catch { }
            }
            catch (Exception e) { Store.Log("清理旧会话状态出错: " + e.Message); }
            if (n > 0) Store.Log($"清掉 {n} 个一天前的会话状态文件");
        }

        void PollSessionStates(string id)
        {
            var cutoff = DateTime.UtcNow.AddMinutes(-10);
            var changed = new List<Dictionary<string, object>>();
            try
            {
                foreach (var file in new DirectoryInfo(Store.State).EnumerateFiles("claude-session-*.json"))
                {
                    var path = file.FullName;
                    if (!file.Exists || file.LastWriteTimeUtc < cutoff || file.Length > 65536)
                    {
                        stamps.Remove(path);
                        continue;
                    }
                    var stamp = file.LastWriteTimeUtc.Ticks + ":" + file.Length;
                    if (stamps.TryGetValue(path, out var old) && old == stamp) continue;
                    var state = Store.ReadJson(path);
                    if (state == null) continue;
                    stamps[path] = stamp;
                    changed.Add(state);
                }
            }
            catch (IOException) { }   // 下次轮询重试。
            catch (UnauthorizedAccessException) { }
            if (changed.Count != 0) Js($"setSessionStates({Q(id)}, {Store.Json.Serialize(changed)})");
        }
    }
}
