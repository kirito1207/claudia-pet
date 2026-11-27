using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace Claudia
{
    sealed partial class Pet
    {
        static string QuotaStyle => Store.Settings.TryGetValue("quotaStyle", out var v) && v as string == "bars" ? "bars" : "text";
        static void After(int ms, Action a)
        {
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
            t.Tick += (_, __) => { t.Stop(); a(); }; t.Start();
        }

        // 物理参数的范围、默认值、步长：三端共用 web/physics.json（2026-11-27 Codex 复审：原来 Windows、Linux、设置页各写一份）。
        // 每项 [最小, 最大, 默认, 步长]；设置页的滑块范围也从这里发过去（physicsRanges）
        static Dictionary<string, double[]> physicsRanges;
        static Dictionary<string, double[]> PhysicsRanges => physicsRanges ?? (physicsRanges = LoadPhysicsRanges());
        static Dictionary<string, double[]> LoadPhysicsRanges()
        {
            var table = new Dictionary<string, double[]>();
            foreach (var kv in Store.ReadJson(System.IO.Path.Combine(Store.Install, "web", "physics.json")) ?? new Dictionary<string, object>())
            {
                try
                {
                    var r = (Dictionary<string, object>)kv.Value;
                    table[kv.Key] = new[] { Convert.ToDouble(r["min"]), Convert.ToDouble(r["max"]), Convert.ToDouble(r["default"]), Convert.ToDouble(r["step"]) };
                }
                catch { Store.Log($"web/physics.json 里 {kv.Key} 这一项不对，跳过"); }
            }
            if (table.Count == 0) Store.Log("读不到 web/physics.json，物理参数都按 0 算（安装不完整？）");
            return table;
        }
        static Dictionary<string, object> PhysicsRangesForPage()
        {
            var page = new Dictionary<string, object>();
            foreach (var kv in PhysicsRanges)
                page[kv.Key] = new Dictionary<string, object> { ["min"] = kv.Value[0], ["max"] = kv.Value[1], ["default"] = kv.Value[2], ["step"] = kv.Value[3] };
            return page;
        }
        static bool PhysicsEnabled => !Store.Settings.TryGetValue("physicsEnabled", out var v) || !(v is bool b) || b;
        static bool CeilingBounce => !Store.Settings.TryGetValue("ceilingBounce", out var v) || !(v is bool b) || b;
        static double PhysicsValue(string key)
        {
            if (!PhysicsRanges.TryGetValue(key, out var r)) return 0;   // 只有参数表读不到时
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
            if (key == "elasticity") Js($"setBehavior({Store.Json.Serialize(BehaviorState())})");   // Q 弹幅度点击也要用
        }
        // Browser rAF owns the whole motion loop, as in dsh-pet. WPF only forwards input and moves HWND.
        // 拖动轨迹的时间用「开机以来的毫秒」：和鼠标消息的 Timestamp、系统鼠标历史是同一个时钟，从开始拖动算起
        int dragTick0;
        double DragMs(uint tick) => unchecked((int)(tick - (uint)dragTick0));
        bool flying;
        int motionId;
        string lastMotionBounds;
        (int x, int y)? lastMotionPosition;

        Dictionary<string, object> PhysicsConfig() => new Dictionary<string, object>
        {
            ["gravity"] = PhysicsValue("gravity"), ["restitution"] = PhysicsValue("restitution"),
            ["groundFriction"] = PhysicsValue("groundFriction"), ["throwPower"] = PhysicsValue("throwPower"),
            ["ceilingBounce"] = CeilingBounce, ["petCollision"] = false,
        };
        Dictionary<string, object> MotionGeometry()
        {
            if (!AreaRect(out var c) || !Native.GetWindowRect(hwnd, out var r)) return null;
            var (ml, mt, mr) = BodyMargins();   // 按人物本体碰边（见 Pet.Follow.cs）
            return new Dictionary<string, object> {
                ["minX"] = c.Left - ml, ["minY"] = c.Top - mt,
                ["maxX"] = Math.Max(c.Left - ml, c.Right - (r.Right-r.Left) + mr),
                ["maxY"] = Math.Max(c.Top - mt, c.Bottom - (r.Bottom-r.Top)),
            };
        }
        Dictionary<string, object> MotionPacket() => new Dictionary<string, object> {
            ["id"] = motionId, ["bounds"] = MotionGeometry(), ["physics"] = PhysicsConfig(), ["enabled"] = PhysicsEnabled,
        };
        void BeginDrag(double downX, double downY)
        {
            StopWalk(null);   // 走着的时候被你拎起来
            StopMotion(false);
            if (!ready || hwnd == IntPtr.Zero) return;
            motionMargins = null; motionMargins = BodyMargins();   // 这一次拖动、甩出去都按这一刻的人物本体碰边
            var packet = MotionPacket();
            if (packet["bounds"] == null) return;
            Native.GetWindowRect(hwnd, out var r);
            var pointer = Forms.Cursor.Position;
            dragging = true; dragTick0 = Environment.TickCount; sentAny = false;
            StartDragPump();
            packet["x"] = r.Left; packet["y"] = r.Top;
            packet["downX"] = downX; packet["downY"] = downY;
            packet["px"] = pointer.X; packet["py"] = pointer.Y; packet["time"] = 0;
            lastMotionBounds = Store.Json.Serialize(packet["bounds"]);
            Js($"setMotionActive(true); petMotion.begin({Store.Json.Serialize(packet)})");
        }
        // ---- 拖动轨迹：用系统记下的鼠标历史（和 dsh-pet 用事件自带的坐标、时间同理） ----
        // 拖动时同一个窗口线程还在逐帧挪窗口，鼠标移动消息会被合并、晚到；处理时再读「当前」鼠标位置，
        // 松手前那 150ms 的点就少而挤，算出的速度偏低，常被当成轻放。改成每次向系统要「上次以来」鼠标真实经过的每个点
        [StructLayout(LayoutKind.Sequential)]
        struct MOUSEMOVEPOINT { public int x, y; public uint time; public IntPtr dwExtraInfo; }
        [DllImport("user32.dll", SetLastError = true)]
        static extern int GetMouseMovePointsEx(uint cbSize, ref MOUSEMOVEPOINT lppt, [In, Out] MOUSEMOVEPOINT[] lpptBuf, int nBufPoints, uint resolution);
        const uint GMMP_USE_DISPLAY_POINTS = 1;   // 屏幕物理像素（程序按显示器各自缩放，和窗口位置同一套坐标）
        readonly MOUSEMOVEPOINT[] moveBuf = new MOUSEMOVEPOINT[64];
        MOUSEMOVEPOINT lastSent; bool sentAny;

        /// <summary>上次交出去之后、到 untilTick 为止鼠标真实经过的点（旧 → 新）；系统给不出时返回 null</summary>
        List<MOUSEMOVEPOINT> FreshMovePoints(System.Drawing.Point cursor, uint untilTick)
        {
            var probe = new MOUSEMOVEPOINT { x = cursor.X & 0xFFFF, y = cursor.Y & 0xFFFF };
            int n;
            try { n = GetMouseMovePointsEx((uint)Marshal.SizeOf(typeof(MOUSEMOVEPOINT)), ref probe, moveBuf, moveBuf.Length, GMMP_USE_DISPLAY_POINTS); }
            catch { return null; }
            if (n <= 0) return null;
            var fresh = new List<MOUSEMOVEPOINT>();
            for (var i = 0; i < n; i++)   // 系统给的顺序是最新的在前
            {
                var p = moveBuf[i];
                if (p.x > 32767) p.x -= 65536;   // 显示坐标只有 16 位：主屏左边、上边的显示器是负数
                if (p.y > 32767) p.y -= 65536;
                if (sentAny && p.time == lastSent.time && p.x == lastSent.x && p.y == lastSent.y) break;   // 接上了上次交出去的点
                if (DragMs(p.time) < 0 || sentAny && unchecked((int)(p.time - lastSent.time)) < 0) break;   // 开始拖动之前的不要
                if (unchecked((int)(p.time - untilTick)) > 0) continue;   // 松手之后的移动不算
                fresh.Add(p);
            }
            fresh.Reverse();
            return fresh;
        }

        /// <summary>把新的轨迹点交给网页：优先系统鼠标历史，取不到时退回当前位置</summary>
        void PumpDragPointer(uint untilTick)
        {
            var cursor = Forms.Cursor.Position;
            var points = FreshMovePoints(cursor, untilTick);
            var js = new StringBuilder();
            if (points == null)
                js.Append($"petMotion.pointer({{id:{motionId},px:{cursor.X},py:{cursor.Y},time:{Num(DragMs((uint)Environment.TickCount))}}});");
            else foreach (var p in points)
            {
                js.Append($"petMotion.pointer({{id:{motionId},px:{p.x},py:{p.y},time:{Num(DragMs(p.time))}}});");
                lastSent = p; sentAny = true;
            }
            if (js.Length > 0) Js(js.ToString());
        }

        // 鼠标消息一秒能来几百条，每条都往网页发一次会把窗口线程堵住（松手消息曾晚到 200ms）。
        // 系统鼠标历史里什么点都不会丢，所以只在每画一帧时统一交一次（见 PumpOnFrame），鼠标消息本身不再转发
        // 松手不等鼠标消息：WPF 里鼠标输入的优先级低于网页消息和脚本回调，拖动、飞行时网页每帧都在发消息，
        // 「松开」消息会排队 0.15～0.2 秒才轮到（日志实测）。每帧刷新的优先级比它们高，这里直接问系统按键还在不在按着
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vKey);
        [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
        static bool PrimaryButtonDown()
        {
            var vk = GetSystemMetrics(23 /* SM_SWAPBUTTON */) != 0 ? 0x02 /* VK_RBUTTON */ : 0x01 /* VK_LBUTTON */;   // 查的是物理按键
            return (GetAsyncKeyState(vk) & 0x8000) != 0;
        }
        bool releasedEarly;   // 已按上面的办法松手了，迟到的鼠标消息只收尾，不算单击、不再开始拖动
        // 拖动中每帧交一次轨迹、看一次按键。以前挂在 WPF 的 CompositionTarget.Rendering（每画一帧来一次）上；
        // 换成 DirectHost 后屏幕上没有 WPF 窗口在画，它不一定还来，改用 16 毫秒（约 60 次 / 秒）的计时器，优先级和画面刷新一样
        DispatcherTimer dragPump;
        void StartDragPump()
        {
            if (dragPump == null)
            {
                dragPump = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
                dragPump.Tick += PumpOnFrame;
            }
            dragPump.Start();
        }
        void StopDragPump() => dragPump?.Stop();
        void PumpOnFrame(object sender, EventArgs e)
        {
            if (!dragging) { StopDragPump(); return; }
            if (!PrimaryButtonDown()) { releasedEarly = true; EndDrag(Environment.TickCount); return; }
            PumpDragPointer((uint)Environment.TickCount);
        }
        void EndDrag(int upTimestamp = 0)
        {
            if (upTimestamp == 0) upTimestamp = Environment.TickCount;
            if (!dragging) return;
            // 松手前最后一段移动先补齐，再用松手消息自带的时间算速度（不用「处理到它」的时刻）
            PumpDragPointer((uint)upTimestamp);
            Native.GetWindowRect(hwnd, out var r);
            // Once released, reject any old drag-frame messages still queued by WebView2.
            dragging = false; flying = true;
            StopDragPump();
            var upMs = DragMs((uint)upTimestamp);
            Js($"petMotion.end({{id:{motionId},x:{r.Left},y:{r.Top},time:{Num(upMs)}}})");
        }
        void StopMotion(bool save = true)
        {
            var moved = dragging || flying;
            ++motionId; dragging = flying = false;
            lastMotionPosition = null;
            StopDragPump();
            if (moved) Js("petMotion.stop(); setMotionActive(false)");
            if (moved && save && hwnd != IntPtr.Zero) SavePosition();
        }
        void UpdateMotionGeometry()
        {
            if (!dragging && !flying) return;
            if (concealed || sessionLocked || !AreaRect(out var c)) { StopMotion(); return; }
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
            var position = (x: (int)Math.Round(x), y: (int)Math.Round(y));
            if (lastMotionPosition != position && Native.MoveTo(hwnd, position.x, position.y))
                lastMotionPosition = position;
            if (phase == "end") { StopMotion(); return; }
            if (m.TryGetValue("shake", out var shake) && shake is bool shaken && shaken) Js("shaken()");
            // 落地 Q 弹网页自己压（2026-11-27 起只在落地时、按落下的速度），这里不再转交撞击
        }
    }
}
