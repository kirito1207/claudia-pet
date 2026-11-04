using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Forms = System.Windows.Forms;

namespace Claudia
{
    sealed partial class Pet
    {
        static double NoticeNow => (DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds;
        readonly TaskNoticePolicy taskNotices = new TaskNoticePolicy(NoticeNow);
        void TaskNotice(string id, string kind, double timestamp)
        {
            var enabled = SettingOn(kind == "waiting" ? "notifyWaiting" : "notifyResults");
            if (!taskNotices.Accept(id, kind, timestamp, NoticeNow, enabled,
                claudePids.Contains(Native.ForegroundPid()), SettingOn("notifyBackgroundOnly"))) return;
            var title = kind == "waiting" ? "Claude 需要你确认" : kind == "error" ? "Claude 任务出错" : "Claude 任务已完成";
            var text = kind == "waiting" ? "有授权申请或问题等待你处理，回到 Claude 看一下吧。" :
                kind == "error" ? "这一轮未能正常完成，请回到 Claude 查看。" : "这一轮工作已结束，可以回到 Claude 查看结果。";
            tray?.ShowBalloonTip(6000, title, text, kind == "error" ? Forms.ToolTipIcon.Warning : Forms.ToolTipIcon.Info);
        }
        void NoticeFromState(Dictionary<string, object> state)
        {
            // New plugin versions use per-session event files; fallback supports already-open old sessions.
            if (state.ContainsKey("noticeV") || !state.TryGetValue("ts", out var ts)) return;
            var pose = state.TryGetValue("pose", out var p) ? p as string : null;
            var ev = state.TryGetValue("event", out var e) ? e as string : null;
            var kind = pose == "asking" ? "waiting" : ev == "Stop" && (pose == "happy" || pose == "proud") ? "complete" :
                ev == "Stop" && pose == "oops" ? "error" : null;
            if (kind != null) TaskNotice("state:" + ts + ":" + kind, kind, Convert.ToDouble(ts));
        }
        void PollTaskNotices()
        {
            try {
                foreach (var file in Directory.EnumerateFiles(Store.State, "claude-notify-*.json")) {
                    var fi = new FileInfo(file);
                    if (fi.Length > 100000 || (DateTime.UtcNow - fi.LastWriteTimeUtc).TotalMinutes > 2) continue;
                    var key = "notice:" + file; var stamp = fi.LastWriteTimeUtc.Ticks + ":" + fi.Length;
                    if (stamps.TryGetValue(key, out var previous) && previous == stamp) continue;
                    var data = Store.ReadJson(file);
                    if (data == null || !data.TryGetValue("events", out var raw) || !(raw is object[] events)) continue;
                    stamps[key] = stamp;
                    foreach (var value in events.OfType<Dictionary<string, object>>()) {
                        if (value.TryGetValue("id", out var id) && value.TryGetValue("kind", out var kind) && value.TryGetValue("ts", out var ts))
                            TaskNotice(id as string, kind as string, Convert.ToDouble(ts));
                    }
                }
            } catch (Exception e) { Store.Log("读取任务通知失败: " + e.Message); }
        }
    }
}
