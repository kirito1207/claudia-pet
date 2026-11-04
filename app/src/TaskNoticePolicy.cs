using System;
using System.Collections.Generic;

namespace Claudia
{
    // Pure policy shared by state-file fallback and durable plugin events.
    public sealed class TaskNoticePolicy
    {
        readonly double started;
        readonly HashSet<string> seen = new HashSet<string>();
        readonly Dictionary<string, double> shown = new Dictionary<string, double>();
        public TaskNoticePolicy(double startedAt) { started = startedAt; }
        public bool Accept(string id, string kind, double timestamp, double now, bool enabled, bool foreground, bool backgroundOnly)
        {
            if (string.IsNullOrEmpty(id) || !seen.Add(id)) return false;
            if (seen.Count > 2000) { seen.Clear(); seen.Add(id); }
            if (kind != "complete" && kind != "error" && kind != "waiting") return false;
            if (double.IsNaN(timestamp) || double.IsInfinity(timestamp) || timestamp < started || timestamp > now + 5 || now - timestamp > 60) return false;
            if (!enabled || backgroundOnly && foreground) return false;
            // Coalesce bursts and duplicate fallback/queue signals; never postpone them for replay.
            if (shown.TryGetValue(kind, out var previous) && now - previous < 8) return false;
            shown[kind] = now; return true;
        }
    }
}
