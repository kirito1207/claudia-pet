using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace Claudia
{
    sealed partial class Pet
    {
        static bool PowerSave => Store.Flag("powerSave");
        static string QuotaStyle => Store.Settings.TryGetValue("quotaStyle", out var v) && v as string == "bars" ? "bars" : "text";
        static void After(int ms, Action a)
        {
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
            t.Tick += (_, __) => { t.Stop(); a(); }; t.Start();
        }

        static readonly Dictionary<string, double[]> PhysicsRanges = new Dictionary<string, double[]>
        {
            ["throwPower"] = new[] { 0.2, 2.5, 1.0 },
            ["gravity"] = new[] { 0.0, 3000.0, 1400.0 },
            ["restitution"] = new[] { 0.0, 0.95, 0.78 },
            ["groundFriction"] = new[] { 0.5, 12.0, 2.5 },
            ["elasticity"] = new[] { 0.0, 0.25, 0.12 },
        };
        static bool PhysicsEnabled => !Store.Settings.TryGetValue("physicsEnabled", out var v) || !(v is bool b) || b;
        static bool CeilingBounce => !Store.Settings.TryGetValue("ceilingBounce", out var v) || !(v is bool b) || b;
        static double PhysicsValue(string key)
        {
            var r = PhysicsRanges[key];
            if (Store.Settings.TryGetValue(key, out var v) && (v is int || v is double || v is decimal))
            {
                var n = Convert.ToDouble(v);
                if (!double.IsNaN(n) && !double.IsInfinity(n)) return Math.Max(r[0], Math.Min(r[1], n));
            }
            return r[2];
        }
        void SetPhysics(string key, object value)
        {
            if (key == "physicsEnabled" || key == "ceilingBounce")
            {
                if (!(value is bool)) return;
                Store.Settings[key] = value;
                if (key == "physicsEnabled" && !(bool)value) StopMotion();
            }
            else
            {
                if (!PhysicsRanges.ContainsKey(key) || !(value is int || value is double || value is decimal)) return;
                var n = Convert.ToDouble(value); var r = PhysicsRanges[key];
                if (double.IsNaN(n) || double.IsInfinity(n) || n < r[0] || n > r[1]) return;
                Store.Settings[key] = n;
            }
            Store.SaveSettings();
            if (dragging || flying) Js($"petMotion.update({Store.Json.Serialize(MotionPacket())})");
        }
        // Browser rAF owns the whole motion loop, as in dsh-pet. WPF only forwards input and moves HWND.
        readonly Stopwatch motionClock = new Stopwatch();
        bool flying;
        int motionId;
        string lastMotionBounds;

        Dictionary<string, object> PhysicsConfig() => new Dictionary<string, object>
        {
            ["gravity"] = PhysicsValue("gravity"), ["restitution"] = PhysicsValue("restitution"),
            ["groundFriction"] = PhysicsValue("groundFriction"), ["throwPower"] = PhysicsValue("throwPower"),
            ["ceilingBounce"] = CeilingBounce, ["petCollision"] = false,
        };
        Dictionary<string, object> MotionGeometry()
        {
            if (!ClaudeRect(out var c) || !Native.GetWindowRect(hwnd, out var r)) return null;
            return new Dictionary<string, object> {
                ["minX"] = c.Left, ["minY"] = c.Top,
                ["maxX"] = Math.Max(c.Left, c.Right - (r.Right-r.Left)),
                ["maxY"] = Math.Max(c.Top, c.Bottom - (r.Bottom-r.Top)),
            };
        }
        Dictionary<string, object> MotionPacket() => new Dictionary<string, object> {
            ["id"] = motionId, ["bounds"] = MotionGeometry(), ["physics"] = PhysicsConfig(), ["enabled"] = PhysicsEnabled,
        };
        void BeginDrag(double downX, double downY)
        {
            StopMotion(false);
            if (!ready || hwnd == IntPtr.Zero) return;
            var packet = MotionPacket();
            if (packet["bounds"] == null) return;
            Native.GetWindowRect(hwnd, out var r);
            var pointer = Forms.Cursor.Position;
            dragging = true; motionClock.Restart();
            packet["x"] = r.Left; packet["y"] = r.Top;
            packet["downX"] = downX; packet["downY"] = downY;
            packet["px"] = pointer.X; packet["py"] = pointer.Y; packet["time"] = 0;
            lastMotionBounds = Store.Json.Serialize(packet["bounds"]);
            Js($"setMotionActive(true); petMotion.begin({Store.Json.Serialize(packet)})");
        }
        void SampleDragPointer()
        {
            if (!dragging) return;
            var pointer = Forms.Cursor.Position;
            Js($"petMotion.pointer({{id:{motionId},px:{pointer.X},py:{pointer.Y},time:{Num(motionClock.Elapsed.TotalMilliseconds)}}})");
        }
        void EndDrag()
        {
            if (!dragging) return;
            Native.GetWindowRect(hwnd, out var r);
            // Once released, reject any old drag-frame messages still queued by WebView2.
            dragging = false; flying = true;
            Js($"petMotion.end({{id:{motionId},x:{r.Left},y:{r.Top},time:{Num(motionClock.Elapsed.TotalMilliseconds)}}})");
        }
        void StopMotion(bool save = true)
        {
            var moved = dragging || flying;
            ++motionId; dragging = flying = false;
            if (moved) Js("petMotion.stop(); setMotionActive(false)");
            if (moved && save && hwnd != IntPtr.Zero) SavePosition();
        }
        void UpdateMotionGeometry()
        {
            if (!dragging && !flying) return;
            if (concealed || sessionLocked || !ClaudeRect(out var c)) { StopMotion(); return; }
            var packet = MotionPacket(); var json = Store.Json.Serialize(packet["bounds"]);
            if (json == lastMotionBounds) return;
            lastMotionBounds = json;
            Js($"petMotion.update({Store.Json.Serialize(packet)})");
        }
        void AcceptMotion(Dictionary<string, object> m)
        {
            if (!m.TryGetValue("id", out var id) || Convert.ToInt32(id) != motionId || (!dragging && !flying)) return;
            var phase = m.TryGetValue("phase", out var ph) ? ph as string : null;
            if (phase == "drag" && !dragging || phase == "flight" && !flying || phase == "end" && dragging) return;
            if (phase != "drag" && phase != "flight" && phase != "end") return;
            var x = Convert.ToDouble(m["x"]); var y = Convert.ToDouble(m["y"]);
            if (double.IsNaN(x) || double.IsInfinity(x) || double.IsNaN(y) || double.IsInfinity(y)) return;
            Native.MoveTo(hwnd, (int)Math.Round(x), (int)Math.Round(y));
            if (phase == "end") { StopMotion(); return; }
            if (m.TryGetValue("shake", out var shake) && shake is bool shaken && shaken) Js("shaken()");
            var ix = m.TryGetValue("impactX", out var ax) ? Convert.ToDouble(ax) : 0;
            var iy = m.TryGetValue("impactY", out var ay) ? Convert.ToDouble(ay) : 0;
            if (Math.Max(ix, iy) > 120) Js($"physicsImpact({Num(ix)}, {Num(iy)}, {Num(PhysicsValue("elasticity"))})");
        }
    }
}
