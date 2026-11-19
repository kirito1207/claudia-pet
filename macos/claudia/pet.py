"""桌宠本体（对应 Windows 版 app/src/Pet*.cs，照 Linux 版 linux/claudia/pet.py 改）：透明、不抢焦点的 NSPanel 承载 web/index.html。

和另外两版的分工一样：网页负责画、动作、物理公式（pet-motion.js / dsh-physics.js）；外壳只管
窗口摆在哪、鼠标、读 Claude Code 插件写的状态文件、菜单、设置、通知、多开。

坐标一律是「主屏幕左上角为原点、y 向下」的点坐标（换算见 ui.py），网页里的物理也按这一套算。
「待在 Claude 窗口里」macOS 版还没做，和 Linux 版一样都待在桌面上。

macOS 上没有 X11 那种「输入区域」：窗口默认整个不收鼠标（ignoresMouseEvents），每 40 毫秒看一次鼠标
在不在人物、气泡、名牌、额度上，在就临时收鼠标。这样不用辅助功能权限，也不会挡住后面的窗口。
"""
import math
import os
import subprocess
import time

import AppKit
import objc  # noqa: F401

from . import actions, claude, store, system, ui, web
from .notices import TaskNoticePolicy

BASE_W, BASE_H = 260, 362
STAGGER_X = 190
HOME_OFF_RIGHT = 40
WALK_MIN_ROOM = 24
CLAUDE_WINDOW_SUPPORTED = False   # 跟着 Claude 窗口：下一步做
POINTER_MS = 40                   # 多久看一次鼠标在不在她身上

PHYSICS_RANGES = {
    "throwPower": (0.2, 2.5, 1.0),
    "gravity": (0.0, 3000.0, 1400.0),
    "restitution": (0.0, 0.95, 0.78),
    "groundFriction": (0.5, 12.0, 2.5),
    "elasticity": (0.0, 0.25, 0.12),
}

_CONTROL_KEY = 1 << 18   # NSEventModifierFlagControl：Control+单击 = 右键


def num(v):
    return repr(float(v)) if isinstance(v, float) else str(v)


def _clamp(v, lo, hi):
    return max(lo, min(v, hi))


def _ms():
    return time.monotonic() * 1000.0


# ================= 窗口和接鼠标的那一层 =================
class ClaudiaPetPanel(AppKit.NSPanel):
    """不抢焦点（点她不会把你正在用的程序切到后台），也不让系统把窗口推回屏幕里：
    头顶留给气泡的透明区要能伸到屏幕外，碰边判定按人物本体算"""

    def canBecomeKeyWindow(self):
        return False

    def canBecomeMainWindow(self):
        return False

    def constrainFrameRect_toScreen_(self, rect, screen):
        return rect


class ClaudiaHitView(AppKit.NSView):
    """盖在网页上面的透明层：拖动 / 单击 / 右键由外壳处理，网页只负责画"""

    def isFlipped(self):
        return True

    def acceptsFirstMouse_(self, event):
        return True   # 她不在前台时，第一下点击也算数

    def mouseDown_(self, event):
        self.pet.on_press(self, event)

    def mouseDragged_(self, event):
        self.pet.on_motion(self, event)

    def mouseUp_(self, event):
        self.pet.on_release(self, event)

    def rightMouseDown_(self, event):
        self.pet.on_right(self, event)


class Pet:
    def __init__(self):
        self.window = None
        self.web = None
        self.hit = None
        self.ready = False
        self.current = "claude"
        self.characters = []
        self.manifest_json = "{}"
        self.stamps = {}
        self.page_w, self.page_h, self.page_foot = BASE_W, BASE_H, 62.0
        self.shown_foot = 0.0
        self.shown_scale = 0.0
        self.dpr = 2.0
        self.x = self.y = 0
        self.w, self.h = BASE_W, BASE_H
        self.hit_rects = None
        self.applied_hit = None
        self.input_rects = []
        self.ignoring = True
        self.concealed = self.session_locked = self.user_hidden = False
        self.shown = False
        self.revealed = False
        self.reveal_polling = False
        self.desktop_mode = True
        self.was_standalone = False
        self.claude_pids = set()
        self.claude_seen_at = None
        self.follow_since = time.monotonic()
        self.top_at = 0.0
        # 拖动 / 飞行
        self.pressed = self.dragging = self.flying = False
        self.down = (0, 0)
        self.motion_id = 0
        self.motion_margins = None
        self.drag_t0 = 0.0
        self.drag_timer = None
        self.last_bounds = None
        self.last_ptr = None
        # 走路
        self.walking = False
        self.walk = None
        self.walk_timer = None
        # 其他
        self.menu = None
        self.settings_window = None
        self.tray = None
        self.notices = TaskNoticePolicy(time.time())
        self.hotkey_failed = False
        self.leaving = False
        self.quit_done = False
        self.main_missing = 0
        self.seen_signal = None
        self.crashes = []
        self.scale_timer = None
        self.js_errors = self.image_errors = 0
        self.logged_state = False

    # ================= 启动 =================
    def start(self):
        if store.is_main():
            store.sync_resources()
        self.was_standalone = store.standalone()
        self.desktop_mode = self.want_desktop()
        c = store.settings.get("character")
        self.current = c if isinstance(c, str) else "claude"
        store.log(f"运行方式：{'独立运行' if self.was_standalone else '跟着 Claude'}，待在桌面上")
        # 系统开着「减弱动态效果」时，网页按 prefers-reduced-motion 停掉精灵动画（干活、待机小动作都退成静态立绘，
        # 没有静态图的姿态显示待机图），看着像「干活时不动」。记一笔，排查时一眼能看到
        try:
            reduce = bool(AppKit.NSWorkspace.sharedWorkspace().accessibilityDisplayShouldReduceMotion())
            store.log("系统「减弱动态效果」：" + ("开着（精灵动画停掉，只显示静态立绘）" if reduce else "关着"))
        except Exception:
            pass

        s = self.scale()
        self.w, self.h = round(BASE_W * s), round(BASE_H * s)
        self.place_on_desktop(initial=True)
        style = AppKit.NSWindowStyleMaskBorderless | AppKit.NSWindowStyleMaskNonactivatingPanel
        w = self.window = ClaudiaPetPanel.alloc().initWithContentRect_styleMask_backing_defer_(
            ui.cocoa_rect(self.x, self.y, self.w, self.h), style, AppKit.NSBackingStoreBuffered, False)
        w.setReleasedWhenClosed_(False)
        w.setOpaque_(False)
        w.setBackgroundColor_(AppKit.NSColor.clearColor())
        w.setHasShadow_(False)
        w.setLevel_(AppKit.NSStatusWindowLevel)   # 比普通窗口、程序坞、菜单栏都高：和 Windows 版的「置顶」一样
        w.setHidesOnDeactivate_(False)            # NSPanel 默认程序不在前台就藏起来；她一直在后台
        w.setBecomesKeyOnlyIfNeeded_(True)
        w.setCollectionBehavior_(AppKit.NSWindowCollectionBehaviorCanJoinAllSpaces
                                 | AppKit.NSWindowCollectionBehaviorStationary
                                 | AppKit.NSWindowCollectionBehaviorIgnoresCycle
                                 | AppKit.NSWindowCollectionBehaviorFullScreenAuxiliary)
        try:
            w.setAnimationBehavior_(AppKit.NSWindowAnimationBehaviorNone)
        except AttributeError:
            pass
        w.setIgnoresMouseEvents_(True)
        # 先「隐形」打开：窗口就在该在的位置，但完全透明；人物画好了再显示（不然会先看到名牌、图片的占位框）
        w.setAlphaValue_(0.0)

        content = w.contentView()
        self.web = web.Web(self.on_web_message, on_event=self.on_web_event, transparent=True)
        self.web.set_zoom(s)
        web.fill(self.web.view, content)
        hit = self.hit = ClaudiaHitView.alloc().initWithFrame_(content.bounds())
        hit.pet = self
        web.fill(hit, content)   # 后加的在上面
        w.orderFrontRegardless()
        self.web.load("index.html")

        if store.is_main():
            self.tray = system.make_tray(self.build_menu, self.show_again)
            self.register_hotkey(startup=True)
        system.watch_lock(self.on_lock)
        ui.timeout_add(300, self._guard(self.poll))
        ui.timeout_add(1000, self._guard(self.tick))
        ui.timeout_add(POINTER_MS, self._guard(self.track_pointer))

    @staticmethod
    def _guard(fn):
        def run():
            try:
                fn()
            except Exception:
                import traceback
                store.log("定时任务出错: " + traceback.format_exc()[-1500:])
            return True
        return run

    # ================= 网页 =================
    def js(self, code):
        if self.web is not None:
            self.web.run_js(code)

    def on_web_event(self, kind):
        if os.environ.get("CLAUDIA_DEBUG"):
            store.log("网页事件 " + kind)
        if kind == "started":
            self.stop_motion()
            self.ready = False
            self.hit_rects = None
            self.apply_input_shape(force_empty=True)
        elif kind == "finished":
            self.on_loaded()
        elif kind == "crashed":
            self.on_web_crash()

    def on_loaded(self):
        self.load_characters()
        self.js(f"init({self.manifest_json}); setCharacter({store.dumps(self.current)}, true); "
                f"setAuraMode({store.dumps(self.aura_mode())}); setShowName({'true' if self.show_name() else 'false'}); "
                f"setPowerSave({'true' if store.flag('powerSave') else 'false'}); setQuotaStyle({store.dumps(self.quota_style())}); "
                f"setBehavior({store.dumps(self.behavior_state())})")
        last = store.settings.get("lastVersion")
        if not isinstance(store.settings.get("whatsNewSeen"), str):
            store.settings["whatsNewSeen"] = last or store.version()
            store.save_settings()
        if last != store.version():
            store.settings["lastVersion"] = store.version()
            store.save_settings()
        self.ready = True
        self.update_hit_rects()
        self.update_page_hidden()
        self.apply_sizes(anchor="corner")
        self.stamps.clear()
        self.poll()
        ui.timeout_add(3000, self.reveal)   # 万一一直没等到「画好了」，3 秒后也露出来

    def on_web_crash(self):
        store.log("网页进程退出了")
        now = time.monotonic()
        self.crashes = [t for t in self.crashes if now - t < 600]
        if len(self.crashes) >= 3:
            store.log("10 分钟内崩溃太多次，不再自动恢复")
            return
        self.crashes.append(now)
        self.ready = False
        self.web.reload()

    # 人物画出来了没有：当前立绘（或动画画布）有内容，换装的淡入也结束了。名牌、额度先画好，人物晚零点几秒才淡入，
    # 先露出来会看到一个没有人的名牌
    DRAWN_JS = ("(() => { const s = document.getElementById('stage'); if (!s || s.classList.contains('swap')) return false;"
                " const i = document.querySelector('#body img.on'); const c = document.querySelector('html.sheet-on canvas.sheet');"
                " return !!(c || i && i.complete && i.naturalWidth > 0); })()")

    def reveal_when_drawn(self):
        if self.revealed:
            return False

        def got(v):
            if v is True:
                ui.timeout_add(80, self.reveal)   # 再等几帧，让淡入开个头
            else:
                ui.timeout_add(100, self.reveal_when_drawn)
        self.web.eval_js(self.DRAWN_JS, got)
        return False

    def reveal(self):
        if self.revealed or self.window is None:
            return False
        self.revealed = True
        if not self.concealed:
            self.window.setAlphaValue_(1.0)
            self.top_at = 0
            self.keep_on_top()
        self.track_pointer()
        return False

    def on_web_message(self, m):
        if "hitRects" in m:
            if isinstance(m["hitRects"], list):
                self.hit_rects = m["hitRects"]
                self.update_hit_rects()
                if not self.revealed and not self.reveal_polling:
                    self.reveal_polling = True
                    ui.timeout_add(100, self.reveal_when_drawn)
            return
        if isinstance(m.get("motion"), dict):
            self.accept_motion(m["motion"])
            return
        if "walk" in m:
            self.handle_walk(m["walk"] if isinstance(m["walk"], dict) else None)
            return
        if "jsError" in m:
            if self.js_errors < 20:
                self.js_errors += 1
                store.log("网页脚本出错: " + str(m["jsError"]))
            return
        if "imageError" in m and self.image_errors < 5:
            self.image_errors += 1
            store.log("立绘加载失败: " + str(m["imageError"]))

    def load_characters(self):
        m = store.manifest()
        self.manifest_json = store.dumps(m)
        self.characters = [(k, (v or {}).get("name") or k) for k, v in m["characters"].items()]
        if not any(c[0] == self.current for c in self.characters):
            self.current = self.characters[0][0] if self.characters else "claude"

    def refresh_dpr(self):
        """网页的 devicePixelRatio：网页报走路速度时乘了它（Windows 版按物理像素挪窗口），这边要除回去"""
        def got(v):
            if isinstance(v, (int, float)) and not isinstance(v, bool) and 0.25 <= v <= 8:
                self.dpr = float(v)
        self.web.eval_js("window.devicePixelRatio", got)

    # ================= 读 Claude Code 插件写的状态 =================
    def poll(self):
        if not self.ready:
            return
        cid = "claude"
        for kind in ("state", "quota", "sessions"):
            f = store.STATE / f"{cid}-{kind}.json"
            try:
                st = f.stat()
            except OSError:
                continue
            if st.st_size > 1_000_000:
                continue
            stamp = f"{st.st_mtime_ns}:{st.st_size}"
            key = f"{cid}-{kind}"
            if self.stamps.get(key) == stamp:
                continue
            if kind == "state" and time.time() - st.st_mtime > 600:   # 只认 10 分钟内的
                self.stamps[key] = stamp
                continue
            value = store.read_json(f)
            if value is None:
                continue   # 可能正被另一边写一半，下次再读
            self.stamps[key] = stamp
            data = store.dumps(value)
            if kind == "sessions":
                self.js(f"setSessions({store.dumps(cid)}, {data})")
            elif kind == "state":
                if not self.logged_state and time.time() - st.st_mtime < 60:
                    # 每次启动记第一条：日志里能看出 Claude Code 插件确实在送状态过来
                    self.logged_state = True
                    store.log(f"收到 Claude Code 插件的状态：{value.get('pose')}（{value.get('event')}）")
                self.notice_from_state(value)
                self.js(f"applyCharState({store.dumps(cid)}, {data})")
            else:
                self.js(f"setQuota({store.dumps(cid)}, {data}, {int(st.st_mtime)})")
        self.poll_task_notices()

    # ---- 任务通知（只从第一只弹） ----
    def task_notice(self, nid, kind, ts):
        if not store.is_main():
            return
        enabled = store.setting_on("notifyWaiting" if kind == "waiting" else "notifyResults")
        try:
            foreground = ui.frontmost_pid() in self.claude_pids
        except Exception:
            foreground = False
        if not self.notices.accept(nid, kind, ts, time.time(), enabled, foreground, store.setting_on("notifyBackgroundOnly")):
            return
        title = {"waiting": "Claude 需要你确认", "error": "Claude 任务出错"}.get(kind, "Claude 任务已完成")
        text = {"waiting": "有授权申请或问题等待你处理，回到 Claude 看一下吧。",
                "error": "这一轮未能正常完成，请回到 Claude 查看。"}.get(kind, "这一轮工作已结束，可以回到 Claude 查看结果。")
        system.notify(title, text, warn=kind == "error")

    def notice_from_state(self, state):
        if "noticeV" in state or "ts" not in state:
            return
        pose, ev = state.get("pose"), state.get("event")
        kind = "waiting" if pose == "asking" else "complete" if ev == "Stop" and pose in ("happy", "proud") else \
            "error" if ev == "Stop" and pose == "oops" else None
        if kind:
            try:
                self.task_notice(f"state:{state['ts']}:{kind}", kind, float(state["ts"]))
            except (TypeError, ValueError):
                pass

    def poll_task_notices(self):
        try:
            files = list(store.STATE.glob("claude-notify-*.json"))
        except OSError:
            return
        for f in files:
            try:
                st = f.stat()
            except OSError:
                continue
            if st.st_size > 100000 or time.time() - st.st_mtime > 120:
                continue
            key, stamp = "notice:" + str(f), f"{st.st_mtime_ns}:{st.st_size}"
            if self.stamps.get(key) == stamp:
                continue
            data = store.read_json(f)
            if not data or not isinstance(data.get("events"), list):
                continue
            self.stamps[key] = stamp
            for e in data["events"]:
                if isinstance(e, dict) and {"id", "kind", "ts"} <= e.keys():
                    try:
                        self.task_notice(str(e["id"]), str(e["kind"]), float(e["ts"]))
                    except (TypeError, ValueError):
                        pass

    # ================= 每秒一次 =================
    def tick(self):
        self.pet_tick()
        if self.leaving:
            return
        self.check_run_mode()
        self.claude_pids = claude.claude_app_pids()
        now = time.monotonic()
        if self.claude_pids:
            self.claude_seen_at = now
        elif not store.standalone():
            # 开着时看不到 Claude 了：等 2 秒确认是真的关了，再退出；从一开始就没看到 Claude，等 30 秒
            never = self.claude_seen_at is None
            if now - (self.follow_since if never else self.claude_seen_at) > (30 if never else 2):
                store.log(("没找到 Claude 桌面端，" if never else "Claude 关了，") + store.APP_NAME + (" 退出" if never else " 跟着退出"))
                self.quit()
                return
        self.desktop_place()

    def want_desktop(self):
        if not CLAUDE_WINDOW_SUPPORTED:
            return True
        return self.on_desktop() or store.standalone() and not self.claude_pids

    def check_run_mode(self):
        s = store.standalone()
        if s != self.was_standalone:
            self.was_standalone = s
            if not s:
                self.follow_since = time.monotonic()
                self.claude_seen_at = None
            store.log("运行方式：" + ("独立运行" if s else "跟着 Claude"))
            self.push_settings()

    # ================= 窗口位置 =================
    def move_to(self, x, y):
        x, y = int(round(x)), int(round(y))
        if (x, y) != (self.x, self.y):
            self.x, self.y = x, y
            if self.window is not None:
                self.window.setFrameOrigin_(ui.cocoa_origin(x, y, self.h))
        return True

    def work_area(self, at_pointer=False):
        """桌宠能活动的范围：所在屏幕的可用区域（不含菜单栏、程序坞）；拖着她的时候按鼠标所在的屏幕算"""
        if at_pointer:
            return ui.work_area_at(*ui.pointer())
        return ui.work_area_at(self.x + self.w // 2, self.y + self.h // 2)

    def area_rect(self):
        return self.work_area(at_pointer=self.dragging)

    def body_margins(self):
        """窗口比人物大（头顶留着气泡的地方，左右也有空）：人物本体离窗口左、上、右边各多远（窗口像素）。
        本体 = 网页报来的能点到的区域里最大的那块。碰边、走到边都按本体算，不然头离屏幕顶还有一截就被弹回来。
        拖动、飞行时用开始那一刻的，免得边界跟着气泡变来变去"""
        if (self.dragging or self.flying) and self.motion_margins:
            return self.motion_margins
        best = None
        for r in self.hit_rects or []:
            if isinstance(r, list) and len(r) == 4 and all(isinstance(v, (int, float)) for v in r):
                if best is None or r[2] * r[3] > best[2] * best[3]:
                    best = r
        if best is None:
            return 0, 0, 0
        z = self.zoom()
        return max(0, round(best[0] * z)), max(0, round(best[1] * z)), max(0, round(self.w - (best[0] + best[2]) * z))

    def bounds(self):
        """窗口左上角能去的范围 (minX, minY, maxX, maxY)：人物本体不出可用区域；脚底站在可用区域底边上（程序坞上面）"""
        l, t, r, b = self.area_rect()
        ml, mt, mr = self.body_margins()
        min_x, max_x = l - ml, max(l - ml, r - self.w + mr)
        min_y, max_y = t - mt, max(t - mt, b - self.h)
        return min_x, min_y, max_x, max_y

    def place_on_desktop(self, initial=False):
        """启动时 / 回到默认位置：摆到上次的位置；没记过就放主屏幕右下角，站在程序坞上面"""
        d = store.settings.get("desk")
        if isinstance(d, dict) and isinstance(d.get("x"), (int, float)) and isinstance(d.get("y"), (int, float)):
            x, y = int(d["x"]), int(d["y"])
            l, t, r, b = ui.work_area_at(x + self.w // 2, y + self.h // 2)
        else:
            l, t, r, b = ui.primary_work_area()
            x, y = r - self.w - self.home_off_right(), b - self.h
        ml, mt, mr = self.body_margins()
        x = _clamp(x, l - ml, r - self.w + mr)
        y = _clamp(y, t - mt, b - self.h)
        if initial:
            self.x, self.y = x, y
        else:
            self.move_to(x, y)
        self.shown = True
        self.top_at = 0
        self.keep_on_top()

    def desktop_place(self):
        if self.dragging or self.flying:
            self.update_motion_geometry()
            return
        if not self.walking:
            # 分辨率、程序坞变了，或者那块屏幕拔掉了：夹回看得见的地方
            x0, y0, x1, y1 = self.bounds()
            x, y = _clamp(self.x, x0, x1), _clamp(self.y, y0, y1)
            if (x, y) != (self.x, self.y):
                self.move_to(x, y)
        self.keep_on_top()

    def keep_on_top(self):
        if self.concealed or self.window is None:
            return
        now = time.monotonic()
        if now - self.top_at < 5:
            return
        self.top_at = now
        self.window.orderFrontRegardless()

    def save_position(self):
        x0, y0, x1, y1 = self.bounds()
        x, y = _clamp(self.x, x0, x1), _clamp(self.y, y0, y1)
        self.move_to(x, y)
        store.settings["desk"] = {"x": x, "y": y}
        store.save_settings()

    def home_off_right(self):
        return HOME_OFF_RIGHT + self.pet_index() * STAGGER_X

    def recenter(self):
        self.stop_motion(False)
        store.settings.pop("desk", None)
        store.save_settings()
        self.place_on_desktop()
        if self.user_hidden:
            self.set_user_hidden(False)
        self.set_concealed(False)

    # ================= 能点到的区域 =================
    def zoom(self):
        return max(self.w / self.page_w, 0.01)

    def update_hit_rects(self):
        if not self.ready or self.hit_rects is None or self.dragging or self.pressed:
            return
        key = (id(self.hit_rects), self.w, self.page_w)
        if key == self.applied_hit:
            return
        self.applied_hit = key
        self.apply_input_shape()

    def apply_input_shape(self, force_empty=False):
        rects = []
        if not force_empty and not self.concealed and self.hit_rects:
            z = self.zoom()
            for r in self.hit_rects:
                if isinstance(r, list) and len(r) == 4:
                    try:
                        rects.append((math.floor(r[0] * z), math.floor(r[1] * z), math.ceil(r[2] * z) + 1, math.ceil(r[3] * z) + 1))
                    except (TypeError, ValueError):
                        pass
        self.input_rects = rects
        self.track_pointer()

    def track_pointer(self):
        """鼠标在人物、气泡、名牌、额度上时窗口收鼠标，别处点击穿透到后面"""
        if self.window is None:
            return
        if self.pressed or self.dragging:
            want = False
        elif self.concealed or not self.revealed or not self.input_rects:
            want = True
        else:
            px, py = ui.pointer()
            lx, ly = px - self.x, py - self.y
            want = not any(x <= lx < x + w and y <= ly < y + h for x, y, w, h in self.input_rects)
        if want != self.ignoring:
            self.ignoring = want
            self.window.setIgnoresMouseEvents_(want)

    # ================= 鼠标：单击摸头、拖动、右键菜单 =================
    def on_press(self, view, e):
        try:
            if e.modifierFlags() & _CONTROL_KEY:
                self.on_right(view, e)
                return
            self.stop_motion()
            self.down = ui.pointer()
            self.pressed = True
        except Exception as ex:
            store.log("按下出错: " + repr(ex))

    def on_motion(self, view, e):
        try:
            if not self.pressed or self.dragging:
                return
            px, py = ui.pointer()
            if abs(px - self.down[0]) + abs(py - self.down[1]) <= 4:
                return
            self.begin_drag(*self.down)
        except Exception as ex:
            store.log("拖动出错: " + repr(ex))

    def on_release(self, view, e):
        try:
            was_dragging = self.dragging
            if self.dragging:
                self.end_drag()
            click = self.pressed and not was_dragging
            self.pressed = False
            self.update_hit_rects()
            if click:
                p = view.convertPoint_fromView_(e.locationInWindow(), None)
                z = self.zoom()
                self.js(f"handleClick('pat', {round(p.x / z, 1)}, {round(p.y / z, 1)})")
        except Exception as ex:
            store.log("松开出错: " + repr(ex))

    def on_right(self, view, e):
        try:
            self.popup_menu(view, e)
        except Exception as ex:
            store.log("右键菜单出错: " + repr(ex))

    # ---- 拖动和飞行：网页的帧循环算位置（pet-motion.js），这里只交鼠标轨迹、按回来的位置挪窗口 ----
    def physics_value(self, key):
        lo, hi, default = PHYSICS_RANGES[key]
        v = store.settings.get(key)
        if isinstance(v, (int, float)) and not isinstance(v, bool) and math.isfinite(v):
            return _clamp(float(v), lo, hi)
        return default

    def physics_enabled(self):
        return store.setting_on("physicsEnabled")

    def physics_config(self):
        cfg = {k: self.physics_value(k) for k in ("gravity", "restitution", "groundFriction", "throwPower")}
        cfg.update(ceilingBounce=store.setting_on("ceilingBounce"), petCollision=False)
        return cfg

    def motion_packet(self):
        x0, y0, x1, y1 = self.bounds()
        return {"id": self.motion_id, "physics": self.physics_config(), "enabled": self.physics_enabled(),
                "bounds": {"minX": x0, "minY": y0, "maxX": x1, "maxY": y1}}

    def begin_drag(self, down_x, down_y):
        self.stop_walk(None)
        self.stop_motion(False)
        if not self.ready:
            return
        self.motion_margins = self.body_margins()
        px, py = ui.pointer()
        self.dragging = True
        p = self.motion_packet()
        self.drag_t0 = _ms()
        p.update(x=self.x, y=self.y, downX=down_x, downY=down_y, px=px, py=py, time=0)
        self.last_bounds = store.dumps(p["bounds"])
        self.js(f"setMotionActive(true); petMotion.begin({store.dumps(p)})")
        self.last_ptr = (px, py)
        # 一帧交一次鼠标位置就够了（网页按帧算）
        self.drag_timer = ui.timeout_add(16, self.pump_pointer)

    def pump_pointer(self):
        if not self.dragging:
            self.drag_timer = None
            return False
        # 松开的消息可能丢了（鼠标甩出窗口、窗口在鼠标底下挪动时）：每一帧都直接看左键还按没按着
        if not ui.button_held():
            self.drag_timer = None
            self.end_drag()
            self.pressed = False
            self.update_hit_rects()
            return False
        px, py = ui.pointer()
        if (px, py) != self.last_ptr:
            self.last_ptr = (px, py)
            self.js(f"petMotion.pointer({{id:{self.motion_id},px:{px},py:{py},time:{round(_ms() - self.drag_t0, 1)}}})")
        return True

    def end_drag(self):
        if not self.dragging:
            return
        px, py = ui.pointer()
        t = round(_ms() - self.drag_t0, 1)
        self.dragging = False
        self.flying = True
        self.js(f"petMotion.pointer({{id:{self.motion_id},px:{px},py:{py},time:{t}}}); "
                f"petMotion.end({{id:{self.motion_id},x:{self.x},y:{self.y},time:{t}}})")

    def stop_motion(self, save=True):
        moved = self.dragging or self.flying
        self.motion_id += 1
        self.dragging = self.flying = False
        if self.drag_timer:
            ui.source_remove(self.drag_timer)
            self.drag_timer = None
        if moved:
            self.js("petMotion.stop(); setMotionActive(false)")
            if save:
                self.save_position()

    def update_motion_geometry(self):
        if not self.dragging and not self.flying:
            return
        if self.concealed or self.session_locked:
            self.stop_motion()
            return
        p = self.motion_packet()
        j = store.dumps(p["bounds"])
        if j == self.last_bounds:
            return
        self.last_bounds = j
        self.js(f"petMotion.update({store.dumps(p)})")

    def accept_motion(self, m):
        try:
            if int(m.get("id", -1)) != self.motion_id or not (self.dragging or self.flying):
                return
            phase = m.get("phase")
            if phase not in ("drag", "flight", "end") or phase == "drag" and not self.dragging \
                    or phase == "flight" and not self.flying or phase == "end" and self.dragging:
                return
            x, y = float(m["x"]), float(m["y"])
        except (TypeError, ValueError, KeyError):
            return
        if not (math.isfinite(x) and math.isfinite(y)):
            return
        self.move_to(x, y)
        if phase == "end":
            self.stop_motion()
            return
        if m.get("shake") is True:
            self.js("shaken()")
        try:
            ix, iy = float(m.get("impactX") or 0), float(m.get("impactY") or 0)
        except (TypeError, ValueError):
            return
        if max(ix, iy) > 120:
            self.js(f"physicsImpact({ix}, {iy}, {self.physics_value('elasticity')})")

    def set_physics(self, key, value):
        if key in ("physicsEnabled", "ceilingBounce"):
            if not isinstance(value, bool):
                return
            store.settings[key] = value
            if key == "physicsEnabled" and not value:
                self.stop_motion()
        else:
            if key not in PHYSICS_RANGES or isinstance(value, bool) or not isinstance(value, (int, float)):
                return
            lo, hi, _ = PHYSICS_RANGES[key]
            if not math.isfinite(value) or not lo <= value <= hi:
                return
            store.settings[key] = float(value)
        store.save_settings()
        if self.dragging or self.flying:
            self.js(f"petMotion.update({store.dumps(self.motion_packet())})")

    # ================= 自己走路 =================
    # 网页定方向、速度、走多久，发来 {walk: {dir, speed, ms}}；这里逐帧平移窗口，只走水平方向。
    # 网页报的速度是「CSS 像素 × devicePixelRatio」每秒（Windows 版按物理像素挪窗口），这边换回点
    def handle_walk(self, w):
        if w is None:
            self.stop_walk(None)
            return
        try:
            d, speed, ms = float(w["dir"]), float(w["speed"]), float(w["ms"])
        except (KeyError, TypeError, ValueError):
            return
        if not (0 < speed <= 4000 and 0 < ms <= 60000 and d in (1, -1)):
            return
        speed = speed / max(self.dpr, 0.25) * self.zoom()
        self.refresh_dpr()   # 挪到另一块屏幕（Retina / 普通）后比例会变：下一次走路用新的
        self.stop_walk(None)
        if self.dragging or self.flying or self.concealed or self.session_locked:
            self.js("walkEnded('blocked')")
            return
        x0, _y0, x1, _y1 = self.bounds()
        room = self.x - x0 if d < 0 else x1 - self.x
        if room < WALK_MIN_ROOM:
            self.js("walkEnded('blocked')")
            return
        now = _ms()
        self.walking = True
        self.walk = {"dir": int(d), "speed": speed, "x": float(self.x), "last": now, "until": now + ms}
        self.walk_timer = ui.timeout_add(16, self.walk_frame)

    def walk_frame(self):
        if not self.walking:
            self.walk_timer = None
            return False
        if self.dragging or self.flying:
            self.stop_walk(None)
            return False
        if self.concealed or self.session_locked:
            self.stop_walk("done")
            return False
        w = self.walk
        now = _ms()
        dt = min(0.1, (now - w["last"]) / 1000)
        w["last"] = now
        x0, _y0, x1, _y1 = self.bounds()
        w["x"] += w["dir"] * w["speed"] * dt
        edge = False
        if w["x"] <= x0:
            w["x"], edge = x0, True
        if w["x"] >= x1:
            w["x"], edge = x1, True
        self.move_to(w["x"], self.y)
        if edge:
            self.stop_walk("edge")
            return False
        if now >= w["until"]:
            self.stop_walk("done")
            return False
        return True

    def stop_walk(self, reason):
        if not self.walking:
            return
        self.walking = False
        if self.walk_timer:
            ui.source_remove(self.walk_timer)
            self.walk_timer = None
        self.save_position()
        if reason:
            self.js(f"walkEnded('{reason}')")

    # ================= 大小 =================
    def scale(self):
        v = store.settings.get("size")
        return float(v) if isinstance(v, (int, float)) and not isinstance(v, bool) and 0.6 <= v <= 1.6 else 1.0

    def text_scale(self):
        v = store.settings.get("textSize")
        return float(v) if isinstance(v, (int, float)) and not isinstance(v, bool) and 0.6 <= v <= 1.6 else 1.0

    def text_linked(self):
        return store.settings.get("textLinked") is not False

    def apply_sizes(self, target=None, anchor="feet"):
        """人物 / 文字大小变了：交给网页算画布（规则在 web/text-size.js），按回来的尺寸设窗口"""
        self.stop_motion()

        def got(o):
            if isinstance(o, dict):
                try:
                    self.page_w, self.page_h, self.page_foot = float(o["w"]), float(o["h"]), float(o["foot"])
                except (KeyError, TypeError, ValueError):
                    pass
            if target is not None:
                self.animate_scale(target)
            else:
                self.apply_scale(self.shown_scale or self.scale(), anchor)
            self.refresh_dpr()

        if self.ready:
            self.web.eval_js(f"setSizes({num(float(target if target is not None else self.scale()))}, "
                             f"{num(self.text_scale())}, {'true' if self.text_linked() else 'false'})", got)
        else:
            got(None)

    def apply_scale(self, scale, anchor="bottom"):
        old = (self.x, self.y, self.x + self.w, self.y + self.h)
        w, h = max(1, round(self.page_w * scale)), max(1, round(self.page_h * scale))
        foot = self.page_foot * w / self.page_w
        if anchor == "corner":
            x, y = old[0], old[1]
        else:
            x = (old[0] + old[2]) // 2 - w // 2
            y = old[3] + round(foot - (self.shown_foot or foot)) - h if anchor == "feet" else old[3] - h
        self.w, self.h = w, h
        self.x, self.y = int(round(x)), int(round(y))
        self.web.set_zoom(scale)
        self.window.setFrame_display_(ui.cocoa_rect(self.x, self.y, w, h), True)
        self.shown_scale = scale
        self.shown_foot = foot
        self.applied_hit = None
        self.update_hit_rects()
        self.save_position()

    def animate_scale(self, target):
        if self.scale_timer:
            ui.source_remove(self.scale_timer)
            self.scale_timer = None
        start_scale = self.shown_scale or self.scale()
        if abs(target - start_scale) <= 0.06:
            self.apply_scale(target)
            return
        t0 = _ms()

        def step():
            k = min(1.0, (_ms() - t0) / 200)
            self.apply_scale(start_scale + (target - start_scale) * (1 - (1 - k) ** 3))
            if k >= 1:
                self.scale_timer = None
                return False
            return True

        self.scale_timer = ui.timeout_add(16, step)

    # ================= 藏起来 / 露出来 =================
    def set_concealed(self, c):
        if self.window is None or self.concealed == c:
            return
        self.concealed = c
        if c:
            self.close_menu()
            self.stop_motion()
            self.window.setAlphaValue_(0.0)
        else:
            if self.revealed:
                self.window.setAlphaValue_(1.0)
            self.top_at = 0
            self.keep_on_top()
        self.applied_hit = None
        self.apply_input_shape(force_empty=c)
        if not c:
            self.update_hit_rects()
        self.update_page_hidden()

    def update_page_hidden(self):
        hidden = self.concealed or self.session_locked or not self.shown
        self.js(f"setPageHidden({'true' if hidden else 'false'})")

    def on_lock(self, locked):
        if locked == self.session_locked:
            return
        self.session_locked = locked
        if locked:
            self.stop_motion()
            self.stop_walk(None)
        self.update_page_hidden()

    def set_user_hidden(self, hidden):
        self.user_hidden = hidden
        self.set_concealed(hidden)
        self.write_signal("hide" if hidden else "show")

    def toggle_hidden(self):
        self.set_user_hidden(not self.user_hidden)

    def show_again(self):
        if self.user_hidden:
            self.set_user_hidden(False)
        self.set_concealed(False)
        self.write_signal("show")

    def on_command(self, cmd):
        """控制 socket 收到的话（claudia-pet --toggle / --show / 再次打开）"""
        if cmd.startswith("eval ") and os.environ.get("CLAUDIA_DEBUG"):   # 调试用：在网页里求值，结果写进日志
            self.web.eval_js(cmd[5:], lambda v: store.log("eval = " + store.dumps(v)))
            return
        if cmd.startswith("py ") and os.environ.get("CLAUDIA_DEBUG"):     # 调试用：看外壳的状态
            try:
                ns = {"pet": self, "store": store, "ui": ui, "AppKit": AppKit}
                exec(cmd[3:], ns)
                store.log("py = " + repr(ns.get("_")))
            except Exception as e:
                store.log("py 出错 " + repr(e))
            return
        cmd = cmd.split()[0] if cmd.split() else ""
        store.log("收到指令：" + cmd)
        if cmd == "toggle":
            self.toggle_hidden()
        elif cmd == "show":
            self.recenter()
        elif cmd == "quit":
            self.user_quit()

    # ================= 快捷键 =================
    def hide_hotkey(self):
        v = store.settings.get("hideHotkey")
        return v if v in ("p", "h", "off") else "p"   # 和 Windows 版一样默认 ⌃⌥P：不用权限，也不改系统设置

    def hotkey_label(self):
        return next((label for i, label, _ in system.CHOICES if i == self.hide_hotkey()), None)

    def register_hotkey(self, startup=False):
        if not store.is_main():
            return
        choice = self.hide_hotkey()
        if startup and choice == "off":
            return
        self.hotkey_failed = not system.set_hotkey(choice, self.toggle_hidden)

    # ================= 菜单 =================
    def close_menu(self):
        if self.menu is not None:
            try:
                self.menu.cancelTracking()
            except Exception:
                pass

    def build_menu(self, menu=None):
        """右键菜单（菜单栏图标的菜单也用它往里加）"""
        menu = menu if menu is not None else ui.new_menu()

        def item(text, fn, parent=menu):
            i = ui.menu_item(text, fn)
            parent.addItem_(i)
            return i

        item("摸摸头", lambda: self.js("handleClick('pat')"))
        item("戳一下", lambda: self.js("handleClick('poke')"))
        sub = self.build_action_menu()
        if sub is not None:
            menu.addItem_(ui.submenu_item("动作", sub))
        menu.addItem_(AppKit.NSMenuItem.separatorItem())
        item("多开和位置…", lambda: self.open_settings("pets"))
        item("设置…", lambda: self.open_settings())
        item("回到默认位置", self.recenter)
        label = self.hotkey_label() if store.is_main() else None
        item(f"暂时隐藏（{label}）" if label else "暂时隐藏", lambda: self.set_user_hidden(True))
        menu.addItem_(AppKit.NSMenuItem.separatorItem())
        if store.is_main():
            item("退出 " + store.APP_NAME, self.user_quit)
        else:
            item("送走这一只", self.dismiss_pet)
            item("送走所有新加的", self.dismiss_extras)
        return menu

    def build_action_menu(self):
        d = store.CHARACTERS / self.current
        if not d.is_dir():
            return None
        have = set()
        for f in d.iterdir():
            if f.is_file() and f.suffix.lower() in (".webp", ".png") and store.is_sprite(f.stem) and f.stem != "idle-blink":
                have.add(f.stem.split("-")[0])
        info = store.read_json(d / "character.json") or {}
        anims = info.get("animations")
        if isinstance(anims, dict):
            for k, cfg in anims.items():
                if not isinstance(cfg, dict) or cfg.get("hidden") is True:
                    continue
                sheets = cfg.get("sheets")
                if isinstance(sheets, list) and any(isinstance(n, str) and (d / store.ANIM_DIR / (n + ".webp")).exists() for n in sheets):
                    have.add(k)
        if not have:
            return None
        root = ui.new_menu()
        used = set()

        def add(parent, pose, label):
            parent.addItem_(ui.menu_item(label, lambda: self.play_action(pose)))

        for group, poses in actions.GROUPS:
            sub = ui.new_menu(group)
            n = 0
            for p in poses:
                if p in have:
                    used.add(p)
                    add(sub, p, actions.NAMES.get(p, p))
                    n += 1
            if n:
                root.addItem_(ui.submenu_item(group, sub))
        others = sorted(p for p in have if p not in used)
        if others:
            sub = ui.new_menu("其他")
            for p in others:
                add(sub, p, p)
            root.addItem_(ui.submenu_item("其他", sub))
        return root

    def play_action(self, pose):
        if any(c in pose for c in "'\"\\\n\r"):
            return
        self.js("(function(p){ if (typeof show !== 'function') return; show(p, true);"
                " clearTimeout(window.__claudiaMenuTimer);"
                " if (typeof anim === 'function' && anim(p) && anim(p).walk && typeof startWalk === 'function' && startWalk(p)) return;"
                " if (typeof anim === 'function' && anim(p) && transientMs(p)) return;"
                " window.__claudiaMenuTimer = setTimeout(function(){ if (pose === p) show('idle', true); }, 6000); })('" + pose + "')")

    def popup_menu(self, view, event):
        self.close_menu()
        self.menu = self.build_menu()
        p = view.convertPoint_fromView_(event.locationInWindow(), None)
        # 菜单开着的时候这里会一直等到它关掉（定时器照走）
        self.menu.popUpMenuPositioningItem_atLocation_inView_(None, p, view)
        self.menu = None

    # ================= 退出 =================
    def user_quit(self):
        try:
            claude.USER_QUIT_FLAG.write_text("")
        except OSError:
            pass
        self.write_signal("quit")
        hint = not store.flag("quitHinted")
        if hint:
            store.settings["quitHinted"] = True
            store.save_settings()
        when = "下次打开我就回来" if store.standalone() else "下次打开 Claude 我再出来"
        self.js(f"farewell('我先走啦～{when}', 5000)" if hint else "farewell(null, 2000)")
        ui.timeout_add(5000 if hint else 2000, lambda: (self.quit(), False)[1])

    def quit(self):
        if self.quit_done:
            return
        self.quit_done = True
        self.leaving = True
        self.stop_motion()
        if self.tray:
            self.tray.remove()
        if store.is_main():
            system.set_hotkey("off")
        ui.stop()

    # ================= 设置 =================
    def show_name(self):
        return store.settings.get("showName") is not False

    def aura_mode(self):
        v = store.settings.get("auraMode")
        return v if v in ("on", "off") else "auto"

    def quota_style(self):
        return "bars" if store.settings.get("quotaStyle") == "bars" else "text"

    def idle_frequency(self):
        v = store.settings.get("idleFrequency")
        return v if v in ("quiet", "normal", "lively") else "normal"

    def behavior_state(self):
        return {"idleEnabled": store.setting_on("idleEnabled"), "idleFrequency": self.idle_frequency(),
                "clickBounce": store.setting_on("clickBounce"), "tumbleActions": store.setting_on("tumbleActions")}

    def set_behavior(self, key, value):
        if key in ("idleEnabled", "clickBounce", "tumbleActions"):
            if not isinstance(value, bool):
                return
            store.settings[key] = value
        elif key == "idleFrequency":
            if value not in ("quiet", "normal", "lively"):
                return
            store.settings[key] = value
        else:
            return
        store.save_settings()
        self.js(f"setBehavior({store.dumps(self.behavior_state())})")

    def settings_state(self):
        chars = []
        for cid, name in self.characters:
            info = store.read_json(store.CHARACTERS / cid / "character.json") or {}
            chars.append({"id": cid, "name": name, "defaultName": info.get("name") or cid, "accent": info.get("accent"),
                          "custom": store.names().get(cid) or ""})
        hotkeys = [{"id": "off", "label": "不用快捷键"}] + [{"id": i, "label": label} for i, label, _ in system.CHOICES]
        return {
            "version": store.version() + " macOS", "platform": "macos", "appVersion": store.version(),
            "claudeWindow": CLAUDE_WINDOW_SUPPORTED,
            "size": self.scale(), "textSize": self.text_scale(), "textLinked": self.text_linked(), "showName": self.show_name(),
            "aura": self.aura_mode(), "powerSave": store.flag("powerSave"), "quotaStyle": self.quota_style(),
            "clickBounce": store.setting_on("clickBounce"), "tumbleActions": store.setting_on("tumbleActions"),
            "idleEnabled": store.setting_on("idleEnabled"), "idleFrequency": self.idle_frequency(),
            "notifyResults": store.setting_on("notifyResults"), "notifyWaiting": store.setting_on("notifyWaiting"),
            "notifyBackgroundOnly": store.setting_on("notifyBackgroundOnly"),
            "physicsEnabled": self.physics_enabled(), "ceilingBounce": store.setting_on("ceilingBounce"),
            "physics": {k: self.physics_value(k) for k in PHYSICS_RANGES},
            "hideHotkey": self.hide_hotkey(), "hotkeyFailed": self.hotkey_failed, "hotkeys": hotkeys,
            "hotkeyHint": "按一下藏起来，再按一下出来（⌃ 是 Control，⌥ 是 Option）；也可以从菜单栏的图标叫她出来",
            "character": self.current, "characters": chars,
            "integrations": claude.integrations(),
            "runMode": "standalone" if store.standalone() else "claude",
            "pets": self.pet_list(), "maxPets": store.MAX_EXTRA_PETS + 1,
        }

    def handle_settings(self, msg):
        cmd = msg.get("cmd")
        if cmd == "set":
            key, value = msg.get("key"), msg.get("value")
            on = value is True
            if key in ("clickBounce", "tumbleActions", "idleEnabled", "idleFrequency"):
                self.set_behavior(key, value)
            elif key in ("notifyResults", "notifyWaiting", "notifyBackgroundOnly"):
                if isinstance(value, bool):
                    store.settings[key] = value
                    store.save_settings()
            elif key in ("physicsEnabled", "ceilingBounce") or key in PHYSICS_RANGES:
                self.set_physics(key, value)
            elif key == "size":
                v = _clamp(round(float(value) * 100) / 100, 0.6, 1.6)
                store.settings["size"] = v
                store.save_settings()
                self.apply_sizes(v)
            elif key == "textSize":
                store.settings["textSize"] = _clamp(round(float(value) * 100) / 100, 0.6, 1.6)
                store.settings["textLinked"] = False
                store.save_settings()
                self.apply_sizes()
            elif key == "textLinked":
                store.settings["textLinked"] = value is not False
                store.save_settings()
                self.apply_sizes()
            elif key == "showName":
                store.settings["showName"] = on
                store.save_settings()
                self.js(f"setShowName({'true' if on else 'false'})")
            elif key == "hideHotkey":
                if value in ("p", "h", "off"):
                    store.settings["hideHotkey"] = value
                    store.save_settings()
                    self.register_hotkey()
            elif key == "quotaStyle":
                if value in ("text", "bars"):
                    store.settings["quotaStyle"] = value
                    store.save_settings()
                    self.js(f"setQuotaStyle({store.dumps(value)})")
            elif key == "runMode":
                if value in ("claude", "standalone"):
                    store.set_standalone(value == "standalone")
                    self.check_run_mode()
            elif key == "powerSave":
                store.settings["powerSave"] = on
                store.save_settings()
                self.js(f"setPowerSave({'true' if on else 'false'})")
            elif key == "aura":
                if value in ("auto", "on", "off"):
                    store.settings["auraMode"] = value
                    store.save_settings()
                    self.js(f"setAuraMode({store.dumps(value)})")
        elif cmd == "resetPhysics":
            self.stop_motion()
            for k in list(PHYSICS_RANGES) + ["physicsEnabled", "ceilingBounce"]:
                store.settings.pop(k, None)
            store.save_settings()
        elif cmd == "rename":
            cid = msg.get("id")
            if any(c[0] == cid for c in self.characters):
                name = str(msg.get("name") or "").strip()
                if name:
                    store.names()[cid] = name
                else:
                    store.names().pop(cid, None)
                store.save_settings()
                self.load_characters()
                self.js(f"setName({store.dumps(cid)}, {store.dumps(dict(self.characters)[cid])})")
        elif cmd == "pet":
            self.handle_pet_command(msg)
        elif cmd == "integrate":
            target, action = msg.get("target"), msg.get("action")
            if target in ("plugin", "mcp") and action in ("install", "uninstall"):
                self.integrate(target, action == "install")
        elif cmd == "action":
            name = msg.get("name")
            target = {"openCharacters": str(store.CHARACTERS), "openData": str(store.DATA),
                      "openUpstream": "https://github.com/lokicorvus/crosspet",
                      "openDshPet": "https://github.com/PC2005-cloud/dsh-pet",
                      "openAboutmePets": "https://github.com/hunmer/aboutme-pets"}.get(name)
            if target:
                self.open(target)
        self.push_settings()

    @staticmethod
    def open(target):
        """用访达打开文件夹、用默认浏览器打开网址"""
        try:
            subprocess.Popen(["/usr/bin/open", target], stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL,
                             stderr=subprocess.DEVNULL)
        except OSError as e:
            store.log("打开失败: " + str(e))

    def integrate(self, target, install):
        try:
            message = claude.set_plugin(install) if target == "plugin" else claude.set_mcp(install)
        except Exception as e:
            message = "没有完成：" + str(e)
            store.log(f"接入 {target} 失败: {e!r}")
        store.log(f"接入 {target} {'install' if install else 'uninstall'}：{message}".replace("\n", " "))
        ui.alert(store.APP_NAME + " · 接入 Claude", message,
                 self.settings_window.window if self.settings_window else None)

    def open_settings(self, tab=None):
        from .settings_window import SettingsWindow
        if self.settings_window is None:
            self.settings_window = SettingsWindow(self)
        self.settings_window.show()
        if tab:
            self.settings_window.show_tab(tab)

    def push_settings(self):
        if self.settings_window:
            self.settings_window.push()

    # ================= 多开 =================
    def on_desktop(self):
        return store.settings.get("mode") == "desktop"

    @staticmethod
    def pet_index():
        if store.is_main():
            return 0
        try:
            return max(1, int(store.pet_id) - 1)
        except ValueError:
            return 1

    @staticmethod
    def launch_pet(pid, mode=None):
        r = claude.launch(["--pet", pid] + (["--mode", mode] if mode else []))
        store.log(f"叫出第 {pid} 只：{r}")

    def add_pet(self, desktop):
        ids = store.extra_pets()
        if len(ids) >= store.MAX_EXTRA_PETS:
            self.js(f"say({store.dumps('家里住不下啦')})")
            return
        pid = next(str(n) for n in range(2, store.MAX_EXTRA_PETS + 2) if str(n) not in ids)
        try:
            (store.DATA / "pets" / pid / "settings.json").unlink()
        except OSError:
            pass
        ids.append(pid)
        try:
            store.save_extra_pets(ids)
        except OSError as e:
            store.log("保存多开名单失败: " + str(e))
            return
        self.launch_pet(pid, "desktop" if desktop else "claude")

    def dismiss_pet(self):
        if store.is_main():
            return
        try:
            store.save_extra_pets([i for i in store.extra_pets() if i != store.pet_id])
        except OSError as e:
            store.log("保存多开名单失败: " + str(e))
        self.js("farewell(null, 2000)")
        ui.timeout_add(2000, lambda: (self.quit(), False)[1])

    def dismiss_extras(self):
        for pid in store.extra_pets():
            if pid != store.pet_id:
                try:
                    store.write_json(self.command_file(pid), {"action": "dismiss", "value": ""})
                except OSError as e:
                    store.log(f"没能把指令交给第 {pid} 只: {e}")
        self.dismiss_pet()

    SIGNAL = store.DATA / "pets-signal"

    def write_signal(self, what):
        if not store.is_main():
            return
        try:
            self.SIGNAL.write_text(f"{what} {time.time_ns()}")
        except OSError:
            pass

    @classmethod
    def clear_signal(cls):
        try:
            cls.SIGNAL.unlink()
        except OSError:
            pass

    @staticmethod
    def command_file(pid):
        return store.DATA / "pets-cmd" / f"{pid}.json"

    @staticmethod
    def settings_file_of(pid):
        return store.DATA / "settings.json" if pid == "main" else store.DATA / "pets" / pid / "settings.json"

    def pet_tick(self):
        """每秒一次：收设置页发来的指令；多开的那几只还要看第一只在不在、它让藏就藏、让走就走"""
        if self.leaving:
            return
        self.poll_pet_command()
        if store.is_main():
            return
        if claude.pet_running("main"):
            self.main_missing = 0
        else:
            self.main_missing += 1
            if self.main_missing >= 3:
                store.log("第一只不在了，跟着退出")
                self.quit()
                return
        s = store.read_text(self.SIGNAL) or ""
        if self.seen_signal is None:
            self.seen_signal = s
            return
        if s == self.seen_signal:
            return
        self.seen_signal = s
        if s.startswith("hide"):
            self.set_user_hidden(True)
        elif s.startswith("show"):
            self.set_user_hidden(False)
            self.set_concealed(False)
        elif s.startswith("quit"):
            self.leaving = True
            self.js("farewell(null, 2000)")
            ui.timeout_add(2000, lambda: (self.quit(), False)[1])

    def pet_list(self):
        out = []
        for n, pid in enumerate(["main"] + store.extra_pets(), 1):
            me = pid == store.pet_id
            s = store.settings if me else (store.read_json(self.settings_file_of(pid)) or {})
            nm = s.get("names") if isinstance(s.get("names"), dict) else {}
            out.append({"id": pid, "index": n, "self": me, "name": nm.get("claude") or "",
                        "mode": "desktop" if s.get("mode") == "desktop" else "claude"})
        return out

    def rename_self(self, name):
        name = (name or "").strip()[:20]
        if name:
            store.names()["claude"] = name
        else:
            store.names().pop("claude", None)
        store.save_settings()
        self.load_characters()
        shown = dict(self.characters).get("claude")
        if shown:
            self.js(f"setName('claude', {store.dumps(shown)})")

    def set_mode(self, desktop):
        if self.on_desktop() == desktop:
            return
        store.settings["mode"] = "desktop" if desktop else "claude"
        store.save_settings()
        store.log("改成待在桌面上" if desktop else "改成待在 Claude 窗口里（macOS 上还没做，先待在桌面上）")

    def handle_pet_command(self, msg):
        action, value = msg.get("action"), msg.get("value")
        if action == "add":
            self.add_pet(value == "desktop")
            ui.timeout_add(1500, lambda: (self.push_settings(), False)[1])
            ui.timeout_add(4000, lambda: (self.push_settings(), False)[1])
            return
        pid = msg.get("id")
        if pid != "main" and pid not in store.extra_pets():
            return
        if action not in ("mode", "rename", "dismiss"):
            return
        if pid == store.pet_id:
            self.apply_pet_command(action, value)
            return
        try:
            store.write_json(self.command_file(pid), {"action": action, "value": value or ""})
        except OSError as e:
            store.log(f"没能把指令交给第 {pid} 只: {e}")
        ui.timeout_add(1500, lambda: (self.push_settings(), False)[1])
        ui.timeout_add(3500, lambda: (self.push_settings(), False)[1])

    def apply_pet_command(self, action, value):
        if action == "mode":
            self.set_mode(value == "desktop")
        elif action == "rename":
            self.rename_self(value)
        elif action == "dismiss":
            self.dismiss_pet()

    def poll_pet_command(self):
        f = self.command_file(store.pet_id)
        if not f.exists():
            return
        cmd = store.read_json(f)
        try:
            f.unlink()
        except OSError:
            pass
        if cmd:
            self.apply_pet_command(cmd.get("action"), cmd.get("value"))
            self.push_settings()
