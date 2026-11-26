"""桌宠本体（对应 Windows 版 app/src/Pet*.cs）：透明、不抢焦点的 GTK 窗口承载 web/index.html。

和 Windows 版的分工一样：网页负责画、动作、物理公式（pet-motion.js / dsh-physics.js）；外壳只管
窗口摆在哪、鼠标、读 Claude Code 插件写的状态文件、菜单、设置、通知、多开。

窗口坐标一律按 GDK 的逻辑像素（XWayland 下就是屏幕像素），网页里的物理也按这一套算。
「待在 Claude 窗口里」：Wayland 下桌宠看不到 Claude 的窗口，靠 GNOME Shell 扩展报来位置（claude_window.py）。
扩展没装 / 没启用时一律待在桌面上。
"""
import math
import os
import time
from pathlib import Path

import gi

gi.require_version("Gtk", "3.0")
gi.require_version("Gdk", "3.0")
from gi.repository import Gdk, GLib, Gtk  # noqa: E402

from . import actions, claude, store, system, web, xshape, xwin  # noqa: E402
from .claude_window import ClaudeWindowWatch  # noqa: E402
from .notices import TaskNoticePolicy, legacy_notice, pending_notices  # noqa: E402

BASE_W, BASE_H = 330, 362   # 330：派活的小布偶会跑到人物两边，260 时两边各被窗口切掉一半（2026-11-24）
STAGGER_X = 190
HOME_OFF_RIGHT = 40
DEFAULT_OFF_BOTTOM = 100   # 待在 Claude 窗口里：默认离 Claude 窗口右下角多远（同 Windows 版）
WALK_MIN_ROOM = 24

PHYSICS_RANGES = {
    "throwPower": (0.2, 2.5, 1.0),
    "gravity": (0.0, 3000.0, 1400.0),
    "restitution": (0.0, 0.95, 0.78),
    "groundFriction": (0.5, 12.0, 2.5),
    "elasticity": (0.0, 0.25, 0.12),
}


def num(v):
    return repr(float(v)) if isinstance(v, float) else str(v)


def _clamp(v, lo, hi):
    return max(lo, min(v, hi))


def _ms():
    return GLib.get_monotonic_time() / 1000.0


class Pet:
    def __init__(self):
        self.window = None
        self.view = None
        self.ready = False
        self.current = "claude"
        self.characters = []
        self.manifest_json = "{}"
        self.stamps = {}
        self.page_w, self.page_h, self.page_foot = BASE_W, BASE_H, 62.0
        self.shown_foot = 0.0
        self.shown_scale = 0.0
        self.x = self.y = 0
        self.w, self.h = BASE_W, BASE_H
        self.hit_rects = None
        self.applied_hit = None
        self.concealed = self.session_locked = self.user_hidden = False
        self.shown = False
        self.desktop_mode = True
        self.was_standalone = False
        self.claude_pids = set()
        self.claude_watch = None
        self.last_claude = None      # 上次按哪个 Claude 窗口范围摆的：变了就按离右下角多远重新摆
        self.off_right, self.off_bottom = 0, DEFAULT_OFF_BOTTOM
        self.vis_applied = False
        self.claude_seen_at = None
        self.claude_window_seen = False   # 这次运行里 GNOME 扩展找到过 Claude 窗口没有（找到过又没了 = 收进托盘）
        self.follow_since = time.monotonic()
        self.top_at = 0.0
        self.state_cleaned_at = 0.0
        self.yielding = False   # 桌面模式「全屏时让开」：她这块屏上前台窗口全屏，先藏起来（见 update_yield）
        # 拖动 / 飞行
        self.pressed = self.dragging = self.flying = False
        self.down = (0, 0)
        self.motion_id = 0
        self.drag_t0 = 0.0
        self.drag_timer = None
        self.last_bounds = None
        self.last_pos = None
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
        self.main_missing = 0
        self.seen_signal = None
        self.crashes = []
        self.scale_timer = None
        self.js_errors = self.image_errors = 0

    # ================= 启动 =================
    def start(self):
        if store.is_main():
            store.sync_resources()
        self.was_standalone = store.standalone()
        self.load_follow_offsets()
        self.claude_watch = ClaudeWindowWatch(self.on_claude_window)
        self.desktop_mode = self.want_desktop()
        c = store.settings.get("character")
        self.current = c if isinstance(c, str) else "claude"
        store.log(f"运行方式：{'独立运行' if self.was_standalone else '跟着 Claude'}，"
                  f"{'待在桌面上' if self.desktop_mode else '待在 Claude 窗口里'}"
                  f"（GNOME 扩展{'在' if self.claude_watch.available else '不在'}）")

        # 不归窗口管理器管的窗口（X11 override-redirect）：GNOME 会把普通窗口推回屏幕里（头顶的透明区伸不出屏幕顶，
        # 碰边判定就不对），挪窗口也要先经过它、拖起来慢半拍。这种窗口自己定位置、挪了马上生效，天然在最上层、不进任务栏
        w = self.window = Gtk.Window(type=Gtk.WindowType.POPUP)
        w.set_title(store.APP_NAME)
        w.set_wmclass(store.APP_NAME, store.APP_NAME)
        w.set_decorated(False)
        w.set_app_paintable(True)
        visual = w.get_screen().get_rgba_visual()
        if visual is not None:
            w.set_visual(visual)
        else:
            store.log("没有透明窗口（合成器没开？），背景会是黑的")
        w.set_accept_focus(False)
        w.set_resizable(False)
        w.get_style_context().add_class("claudia-pet")
        w.connect("delete-event", lambda *_: True)
        self.w, self.h = round(BASE_W * self.scale()), round(BASE_H * self.scale())
        w.set_size_request(self.w, self.h)
        w.resize(self.w, self.h)

        overlay = Gtk.Overlay()
        self.view = web.create(self.on_web_message, transparent=True)
        self.view.set_zoom_level(self.scale())
        overlay.add(self.view)
        # 透明的接鼠标层：拖动 / 单击 / 右键由外壳处理，网页只负责画；窗口只在桌宠本体、气泡、名牌、额度上收鼠标（见 update_hit_rects）
        hit = self.hit = Gtk.EventBox()
        hit.set_visible_window(False)
        hit.set_above_child(True)
        hit.add_events(Gdk.EventMask.BUTTON_PRESS_MASK | Gdk.EventMask.BUTTON_RELEASE_MASK | Gdk.EventMask.POINTER_MOTION_MASK)
        hit.connect("button-press-event", self.on_press)
        hit.connect("motion-notify-event", self.on_motion)
        hit.connect("button-release-event", self.on_release)
        hit.connect("grab-broken-event", self.on_grab_broken)
        overlay.add_overlay(hit)
        w.add(overlay)
        w.connect("realize", self.on_realize)
        self.view.connect("load-changed", self.on_load)
        self.view.connect("web-process-terminated", self.on_web_crash)

        # 先「隐形」打开：窗口就在该在的位置，但外形（SHAPE）是空的，屏幕上看不见；人物画好了再恢复外形。
        # 网页画出人物之前，窗口里是空的，直接显示会看到名牌先出来、图片的占位框（2026-11-17 用户说打开时看到方框闪）。
        # 不挪到屏幕外再挪回来：挪回来的头几帧 WebKit 还没重画，同样会闪。藏起来也是把外形设成空（不靠不透明度：
        # 不归窗口管理器管的窗口，GNOME 不一定认它的不透明度）
        self.revealed = False
        self.place_home(initial=True)
        w.move(self.x, self.y)
        w.show_all()
        self.apply_input_shape(force_empty=True)
        gw = w.get_window()
        if gw is not None and hasattr(gw, "get_xid"):
            store.log(f"窗口 0x{gw.get_xid():x}")
        self.view.load_uri(store.page_url("index.html"))

        if store.is_main():
            self.tray = system.make_tray(self.build_menu, self.show_again)
            self.register_hotkey(startup=True)
        system.watch_lock(self.on_lock)
        GLib.timeout_add(300, self._guard(self.poll))
        GLib.timeout_add(1000, self._guard(self.tick))

    @staticmethod
    def _guard(fn):
        def run():
            try:
                fn()
            except Exception as e:
                import traceback
                store.log("定时任务出错: " + "".join(traceback.format_exception(e))[-1500:])
            return True
        return run

    # ================= 网页 =================
    def js(self, code):
        web.run_js(self.view, code)

    def on_load(self, view, event):
        from gi.repository import WebKit2
        if os.environ.get("CLAUDIA_DEBUG"):
            store.log(f"load {event.value_nick} {view.get_uri()}")
        if event == WebKit2.LoadEvent.STARTED:
            self.stop_motion()
            self.ready = False
            self.hit_rects = None
            return
        if event != WebKit2.LoadEvent.FINISHED:
            return
        self.load_characters()
        self.js(f"init({self.manifest_json}); setCharacter({store.dumps(self.current)}, true); "
                f"setAuraMode({store.dumps(self.aura_mode())}); setShowName({'true' if self.show_name() else 'false'}); "
                f"setQuotaStyle({store.dumps(self.quota_style())}); "
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
        GLib.timeout_add(3000, self.reveal)

    def on_web_crash(self, _view, reason):
        store.log(f"网页进程退出：{reason}")
        now = time.monotonic()
        self.crashes = [t for t in self.crashes if now - t < 600]
        if len(self.crashes) >= 3:
            store.log("10 分钟内崩溃太多次，不再自动恢复")
            return
        self.crashes.append(now)
        self.ready = False
        self.view.reload()

    # 人物画出来了没有：当前立绘（或动画画布）有内容，换装的淡入也结束了。名牌、额度先画好，人物晚零点几秒才淡入，
    # 先露出来会看到一个没有人的名牌（2026-11-17）
    DRAWN_JS = ("(() => { const s = document.getElementById('stage'); if (!s || s.classList.contains('swap')) return false;"
                " const i = document.querySelector('#body img.on'); const c = document.querySelector('html.sheet-on canvas.sheet');"
                " return !!(c || i && i.complete && i.naturalWidth > 0); })()")

    def reveal_when_drawn(self):
        if self.revealed:
            return False

        def got(v):
            if v is True:
                GLib.timeout_add(80, self.reveal)   # 再等几帧，让淡入开个头
            else:
                GLib.timeout_add(100, self.reveal_when_drawn)
        web.eval_js(self.view, self.DRAWN_JS, got)
        return False

    def xid(self):
        gw = self.window.get_window() if self.window else None
        return gw.get_xid() if gw is not None and hasattr(gw, "get_xid") else 0

    def on_realize(self, *_):
        xshape.set_visible(self.xid(), False)   # 映射到屏幕之前就设好：一出现就是隐形的
        self.apply_input_shape(force_empty=True)

    def reveal(self):
        if self.revealed or self.window is None:
            return False
        self.revealed = True
        self.apply_visibility()
        return False

    def on_screen(self):
        """现在该不该看得见：画好了、没被藏起来（快捷键 / 菜单），在 Claude 窗口里时 Claude 也看得见、她没被别的窗口盖住，
        桌面模式下没在给全屏窗口让路"""
        return self.revealed and not self.concealed and self.shown and not self.yielding

    def apply_visibility(self):
        vis = self.on_screen()
        if vis == self.vis_applied or self.window is None:
            return
        self.vis_applied = vis
        if not vis:
            self.close_menu()
        xshape.set_visible(self.xid(), vis)
        self.applied_hit = None
        self.apply_input_shape(force_empty=not vis)
        if vis:
            self.top_at = 0
            self.keep_on_top()
            self.update_hit_rects()
        self.update_page_hidden()

    def set_shown(self, v):
        if self.shown == v:
            return
        self.shown = v
        self.apply_visibility()

    def on_web_message(self, m):
        if "hitRects" in m:
            if isinstance(m["hitRects"], list):
                self.hit_rects = m["hitRects"]
                self.update_hit_rects()
                if not self.revealed and not getattr(self, "reveal_polling", False):
                    self.reveal_polling = True
                    GLib.timeout_add(100, self.reveal_when_drawn)
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

    # ================= 读 Claude Code 插件写的状态 =================
    def poll(self):
        if not self.ready:
            return
        cid = "claude"
        cutoff = time.time() - 600
        for kind in ("state", "quota", "sessions"):
            f = store.STATE / f"{cid}-{kind}.json"
            result = store.changed_state(
                f, self.stamps, max_bytes=1_000_000, not_before=cutoff if kind == "state" else None,
            )
            if result is None:
                continue
            value, modified = result
            data = store.dumps(value)
            if kind == "sessions":
                self.js(f"setSessions({store.dumps(cid)}, {data})")
            elif kind == "state":
                notice = legacy_notice(value)
                if notice:
                    self.task_notice(*notice)
                self.js(f"applyCharState({store.dumps(cid)}, {data})")
            else:
                self.js(f"setQuota({store.dumps(cid)}, {data}, {int(modified)})")
        self.poll_session_states(cid, cutoff)
        if store.is_main():
            for notice in pending_notices(self.stamps):
                self.task_notice(*notice)

    def clean_stale_state(self):
        """插件每个会话写一个 claude-session-<sid>.json、一个 claude-notify-<sid>.json，自己从不删。这两种只认最近 10 分钟 / 2 分钟的，
        超过一天的由第一只删掉（启动时一次、之后每小时一次）。Windows 版见 Pet.Web.cs 的 CleanStaleState"""
        now = time.time()
        if not store.is_main() or now - self.state_cleaned_at < 3600:
            return
        self.state_cleaned_at = now
        n = 0
        for pattern in ("claude-session-*.json", "claude-notify-*.json"):
            for f in store.STATE.glob(pattern):
                try:
                    if now - f.stat().st_mtime > 86400:
                        f.unlink()
                        n += 1
                except OSError:
                    pass
        if n:
            store.log(f"清掉 {n} 个一天前的会话状态文件")

    def poll_session_states(self, cid, cutoff):
        changed = []
        for f in store.STATE.glob("claude-session-*.json"):
            result = store.changed_state(f, self.stamps, max_bytes=65536, not_before=cutoff)
            if result:
                changed.append(result[0])
        if changed:
            self.js(f"setSessionStates({store.dumps(cid)}, {store.dumps(changed)})")

    # ---- 任务通知（只从第一只弹） ----
    def task_notice(self, nid, kind, ts):
        if not store.is_main():
            return
        enabled = store.setting_on("notifyWaiting" if kind == "waiting" else "notifyResults")
        window_state = self.claude_watch.state if self.claude_watch and self.claude_watch.available else None
        foreground = bool(window_state and window_state.get("focused") is True)
        if not self.notices.accept(nid, kind, ts, time.time(), enabled, foreground, store.setting_on("notifyBackgroundOnly")):
            return
        title = {"waiting": "Claude 需要你确认", "error": "Claude 任务出错"}.get(kind, "Claude 任务已完成")
        text = {"waiting": "有授权申请或问题等待你处理，回到 Claude 看一下吧。",
                "error": "这一轮未能正常完成，请回到 Claude 查看。"}.get(kind, "这一轮工作已结束，可以回到 Claude 查看结果。")
        system.notify(title, text, warn=kind == "error")

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
        self.follow_place()
        self.update_yield()
        self.clean_stale_state()

    def claude_found(self):
        st = self.claude_watch.state if self.claude_watch else None
        return bool(st and st.get("found"))

    def want_desktop(self):
        """该待在哪：选了桌面就桌面；没有 GNOME 扩展只能待在桌面。最小化不算没找到：最小化时她跟着藏起来。
        扩展找不到 Claude 窗口时分两种（不改存着的设置）：
        - 这次运行里找到过、后来没了：Claude 点 × 收进了托盘。照 Windows 版（Pet.Follow.cs 的 WantDesktop）：
          独立运行才去桌面；跟着 Claude 时跟着藏起来，Claude 窗口出来再露面（2026-11-22 用户说点 × 后桌面上多出一只）。
        - 从启动起一次都没找到：可能是扩展认不出这个 Claude，先待在桌面上，免得整只看不见。"""
        if not (self.claude_watch and self.claude_watch.available):
            return True
        if self.on_desktop():
            return True
        if self.claude_found():
            self.claude_window_seen = True
            return False
        return store.standalone() or not self.claude_window_seen

    def check_area(self):
        desk = self.want_desktop()
        if desk != self.desktop_mode:
            self.switch_area(desk)

    def check_run_mode(self):
        s = store.standalone()
        if s != self.was_standalone:
            self.was_standalone = s
            if not s:
                self.follow_since = time.monotonic()
                self.claude_seen_at = None
            store.log("运行方式：" + ("独立运行" if s else "跟着 Claude"))
            self.push_settings()
        self.check_area()

    def on_claude_window(self, watch):
        """扩展报来新状态（Claude 窗口挪了、改大小、最小化、换工作区、被盖住，或者扩展出现 / 消失了）"""
        was = getattr(self, "ext_seen", None)
        if was != watch.available:
            self.ext_seen = watch.available
            self.push_settings()
        self.check_area()
        self.follow_place()
        self.update_yield()

    def switch_area(self, desktop):
        """真正换地方（不改设置）：改了「待在哪」、扩展出现 / 消失、独立运行时 Claude 开了 / 关了"""
        if self.window is None:
            self.desktop_mode = desktop
            return
        self.stop_walk(None)
        self.stop_motion(False)
        self.desktop_mode = desktop
        self.last_claude = None
        store.log("换到" + ("桌面上" if desktop else " Claude 窗口里"))
        if desktop:
            self.place_on_desktop()
            self.set_shown(True)
        else:
            self.shown = False   # 下面按 Claude 窗口重新摆，摆好了才露出来
            self.apply_visibility()
            self.follow_place()

    def place_home(self, initial=False):
        if self.desktop_mode:
            self.place_on_desktop(initial=initial)
        else:
            self.shown = False
            self.follow_place(initial=initial)

    # ================= 跟着 Claude 窗口 =================
    # 照 Windows 版（Pet.Follow.cs）：位置按「离 Claude 窗口右下角多远」记；Claude 窗口挪动、改大小，她跟着走；
    # 拖到窗口外松手会被推回来。Claude 最小化、不在当前工作区、或者她站的地方被别的窗口盖住时藏起来
    def load_follow_offsets(self):
        self.off_right, self.off_bottom = self.home_off_right(), DEFAULT_OFF_BOTTOM
        f = store.settings.get("follow")
        if isinstance(f, dict):
            try:
                self.off_right = int(f.get("right", self.off_right))
                self.off_bottom = int(f.get("bottom", self.off_bottom))
            except (TypeError, ValueError):
                pass

    def claude_rect(self):
        """Claude 主窗口看得见时它的范围 (left, top, right, bottom)，看不见（没开、最小化、在别的工作区）返回 None"""
        st = self.claude_watch.state if self.claude_watch else None
        if not st or not st.get("found") or not st.get("visible"):
            return None
        try:
            x, y, w, h = (int(v) for v in st["rect"])
        except (KeyError, TypeError, ValueError):
            return None
        return (x, y, x + w, y + h) if w > 0 and h > 0 else None

    def body_rect(self):
        """人物本体在屏幕上的范围（能点到的区域里最大的那块）"""
        best = None
        for r in self.hit_rects or []:
            if isinstance(r, list) and len(r) == 4 and all(isinstance(v, (int, float)) for v in r):
                if best is None or r[2] * r[3] > best[2] * best[3]:
                    best = r
        if best is None:
            return self.x, self.y, self.x + self.w, self.y + self.h
        z = self.zoom()
        return (self.x + best[0] * z, self.y + best[1] * z, self.x + (best[0] + best[2]) * z, self.y + (best[1] + best[3]) * z)

    def covered(self):
        """她站的地方有没有被叠在 Claude 上面的窗口盖住（盖住一点也算：Wayland 下没法只挡一部分，整只藏起来）"""
        st = self.claude_watch.state if self.claude_watch else None
        covers = st.get("covers") if st else None
        if not covers:
            return False
        l, t, r, b = self.body_rect()
        for c in covers:
            try:
                x, y, w, h = (float(v) for v in c)
            except (TypeError, ValueError):
                continue
            if x < r and x + w > l and y < b and y + h > t:
                return True
        return False

    def follow_place(self, initial=False):
        """按 Claude 窗口现在的样子把她摆好：扩展报来新状态、每秒一次都走这里"""
        if self.window is None:
            return
        if self.desktop_mode:
            if not initial:
                self.desktop_place()
            return
        c = self.claude_rect()
        if c is None:
            self.stop_motion(False)
            self.stop_walk(None)
            self.set_shown(False)
            return
        if self.dragging or self.flying:   # 你在拖：随你（Claude 窗口挪了，边界跟着挪）
            self.last_claude = c
            self.update_motion_geometry()
            return
        changed = self.last_claude != c
        if initial or not self.shown or changed:
            x, y = c[2] - self.w - self.off_right, c[3] - self.h - self.off_bottom
        else:
            x, y = self.x, self.y
        self.last_claude = c
        x0, y0, x1, y1 = self.bounds()
        x, y = _clamp(x, x0, x1), _clamp(y, y0, y1)
        if initial:
            self.x, self.y = x, y
        elif not self.walking:
            self.move_to(x, y)
        self.set_shown(not self.covered())

    def save_follow_offsets(self):
        """拖完松手、走完路、改完大小：推回 Claude 窗口里，记下离右下角多远"""
        c = self.claude_rect()
        if c is None:
            return
        x0, y0, x1, y1 = self.bounds()
        x, y = _clamp(self.x, x0, x1), _clamp(self.y, y0, y1)
        self.move_to(x, y)
        self.off_right, self.off_bottom = c[2] - (x + self.w), c[3] - (y + self.h)
        self.last_claude = c
        store.settings["follow"] = {"right": self.off_right, "bottom": self.off_bottom}
        store.save_settings()

    # ================= 窗口位置 =================
    def move_to(self, x, y):
        x, y = int(round(x)), int(round(y))
        if (x, y) != (self.x, self.y):
            self.x, self.y = x, y
            self.window.move(x, y)
        return True

    def work_area(self, at_pointer=False):
        """桌宠能活动的范围：所在显示器的工作区（不含顶栏、任务栏）；拖着她的时候按鼠标所在的显示器算"""
        display = Gdk.Display.get_default()
        if at_pointer:
            _s, px, py = display.get_default_seat().get_pointer().get_position()[:3]
            mon = display.get_monitor_at_point(px, py)
        else:
            mon = display.get_monitor_at_point(self.x + self.w // 2, self.y + self.h // 2)
        if mon is None:
            mon = display.get_primary_monitor() or display.get_monitor(0)
        r = mon.get_workarea()
        return r.x, r.y, r.x + r.width, r.y + r.height

    def area_rect(self):
        """她能活动的范围：桌面上是显示器工作区，Claude 窗口里是 Claude 窗口"""
        if not self.desktop_mode:
            c = self.claude_rect()
            if c is not None:
                return c
        return self.work_area(at_pointer=self.dragging)

    def body_margins(self):
        """窗口比人物大（头顶留着气泡的地方，左右也有空）：人物本体离窗口左、上、右边各多远（窗口像素）。
        本体 = 网页报来的能点到的区域里最大的那块。碰边、走到边都按本体算，不然头离屏幕顶还有一截就被弹回来
        （2026-11-17 用户说「上边的判定有问题」）。拖动、飞行时用开始那一刻的，免得边界跟着气泡变来变去"""
        if (self.dragging or self.flying) and getattr(self, "motion_margins", None):
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
        """窗口左上角能去的范围 (minX, minY, maxX, maxY)：人物本体不出工作区；脚底站在工作区底边上（名牌、额度要看得见）"""
        l, t, r, b = self.area_rect()
        ml, mt, mr = self.body_margins()
        min_x, max_x = l - ml, max(l - ml, r - self.w + mr)
        min_y, max_y = t - mt, max(t - mt, b - self.h)
        return min_x, min_y, max_x, max_y

    def place_on_desktop(self, initial=False):
        """启动时 / 回到默认位置：摆到上次的位置；没记过就放主屏幕右下角，站在任务栏上面"""
        d = store.settings.get("desk")
        display = Gdk.Display.get_default()
        if isinstance(d, dict) and "x" in d and "y" in d:
            x, y = int(d["x"]), int(d["y"])
            mon = display.get_monitor_at_point(x + self.w // 2, y + self.h // 2)
        else:
            mon = display.get_primary_monitor() or display.get_monitor(0)
            wa = mon.get_workarea()
            x, y = wa.x + wa.width - self.w - self.home_off_right(), wa.y + wa.height - self.h
        wa = (mon or display.get_monitor(0)).get_workarea()
        ml, mt, mr = self.body_margins()
        x = _clamp(x, wa.x - ml, wa.x + wa.width - self.w + mr)
        y = _clamp(y, wa.y - mt, wa.y + wa.height - self.h)
        if initial:
            self.x, self.y = x, y
            self.shown = True
        else:
            self.move_to(x, y)
            self.set_shown(True)
        self.top_at = 0
        self.keep_on_top()

    def desktop_place(self):
        if self.dragging or self.flying:
            self.update_motion_geometry()
            return
        if not self.walking:
            # 分辨率、任务栏变了，或者那块屏幕拔掉了：夹回看得见的地方
            x0, y0, x1, y1 = self.bounds()
            x, y = _clamp(self.x, x0, x1), _clamp(self.y, y0, y1)
            if (x, y) != (self.x, self.y):
                self.move_to(x, y)
        self.keep_on_top()

    def keep_on_top(self):
        if not self.on_screen() or self.window is None:
            return
        now = time.monotonic()
        if now - self.top_at < 5:
            return
        self.top_at = now
        gw = self.window.get_window()
        if gw is not None:
            gw.raise_()

    # ---- 桌面上的层级、全屏时让开、鼠标穿透（2026-11-26，Windows 版见 Pet.Fullscreen.cs） ----
    # Linux 只有两档：她是不归窗口管理器管的窗口（override-redirect），没法像普通窗口那样被别人盖住，所以没有「不置顶」。
    #   top    始终在最上面（每 5 秒抬一次，keep_on_top）
    #   yield  默认：她这块屏上的前台窗口全屏（浏览器 F11 / 视频全屏、放映、游戏）就藏起来，退出全屏再出来
    # 全屏看前台窗口的 _NET_WM_STATE_FULLSCREEN（xwin）；GNOME 扩展在时用扩展报的 focusFullscreen（Wayland 原生窗口 X11 看不到）。
    # 每秒查一次（tick），扩展报新状态时也查
    DESK_LAYERS = ("top", "yield")

    def desk_layer(self):
        v = store.settings.get("deskLayer")
        return v if v in self.DESK_LAYERS else "yield"   # Windows 的「不置顶」这里没有，按默认

    def click_through(self):
        return store.is_main() and store.settings.get("clickThrough") is True

    def fullscreen_rect(self):
        st = self.claude_watch.state if self.claude_watch and self.claude_watch.available else None
        if st is not None and "focusFullscreen" in st:
            r = st.get("focusFullscreen")
            try:
                x, y, w, h = (float(v) for v in r)
                return x, y, x + w, y + h
            except (TypeError, ValueError):
                return None
        return xwin.fullscreen_front()

    def should_yield(self):
        if not self.desktop_mode or self.desk_layer() != "yield" or self.dragging or self.flying:
            return False
        r = self.fullscreen_rect()
        if r is None:
            return False
        display = Gdk.Display.get_default()
        mine = display.get_monitor_at_point(self.x + self.w // 2, self.y + self.h // 2)
        theirs = display.get_monitor_at_point(int((r[0] + r[2]) / 2), int((r[1] + r[3]) / 2))
        return mine == theirs

    def update_yield(self):
        y = self.should_yield()
        if y == self.yielding:
            return
        self.yielding = y
        store.log("有全屏窗口：让开" if y else "全屏结束：回到最上面")
        if y:
            self.stop_walk(None)
        self.apply_visibility()

    def set_desk_layer(self, value):
        if value not in self.DESK_LAYERS:
            return
        store.settings["deskLayer"] = value
        store.save_settings()
        store.log("桌面上的层级：" + ("始终在最上面" if value == "top" else "全屏时让开"))
        self.update_yield()
        self.push_settings()

    def set_click_through(self, on):
        if not store.is_main():
            return
        store.settings["clickThrough"] = bool(on)
        store.save_settings()
        store.log("鼠标穿透：" + ("开" if on else "关"))
        self.applied_hit = None
        self.apply_input_shape(force_empty=not self.on_screen())
        self.push_settings()
        if self.tray:   # 托盘菜单是事先做好的：勾选状态跟着变（从设置页改的时候）
            GLib.idle_add(lambda: (self.tray.refresh(), False)[1])

    def save_position(self):
        if not self.desktop_mode:
            self.save_follow_offsets()
            return
        x0, y0, x1, y1 = self.bounds()
        x, y = _clamp(self.x, x0, x1), _clamp(self.y, y0, y1)
        self.move_to(x, y)
        store.settings["desk"] = {"x": x, "y": y}
        store.save_settings()

    def home_off_right(self):
        return HOME_OFF_RIGHT + self.pet_index() * STAGGER_X

    def recenter(self):
        self.stop_motion(False)
        if self.desktop_mode:
            store.settings.pop("desk", None)
            store.save_settings()
            self.place_on_desktop()
        else:
            store.settings.pop("follow", None)
            store.save_settings()
            self.load_follow_offsets()
            self.last_claude = None
            self.follow_place()
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
        gw = self.window.get_window() if self.window else None
        if gw is None or not hasattr(gw, "get_xid"):
            return
        rects = []
        if not force_empty and not self.click_through() and self.on_screen() and self.hit_rects:
            z = self.zoom()
            for r in self.hit_rects:
                if isinstance(r, list) and len(r) == 4:
                    try:
                        rects.append((math.floor(r[0] * z), math.floor(r[1] * z), math.ceil(r[2] * z) + 1, math.ceil(r[3] * z) + 1))
                    except (TypeError, ValueError):
                        pass
        xshape.set_input(gw.get_xid(), rects)

    # ================= 鼠标：单击摸头、拖动、右键菜单 =================
    @staticmethod
    def pointer():
        _s, x, y = Gdk.Display.get_default().get_default_seat().get_pointer().get_position()[:3]
        return x, y

    def on_press(self, _w, e):
        if e.type != Gdk.EventType.BUTTON_PRESS:
            return True
        if e.button == 1:
            self.stop_motion()
            self.down = (e.x_root, e.y_root)
            self.pressed = True
        return True

    def on_motion(self, _w, e):
        if not self.pressed or self.dragging:
            return True
        if abs(e.x_root - self.down[0]) + abs(e.y_root - self.down[1]) <= 4:
            return True
        self.begin_drag(*self.down)
        return True

    def on_grab_broken(self, *_):
        """拖着的时候鼠标被别的东西抢走了（弹出菜单、系统快捷键）：当作松手"""
        if self.dragging:
            self.end_drag()
        self.pressed = False
        self.update_hit_rects()
        return False

    def on_release(self, _w, e):
        if e.button == 3:
            self.popup_menu(e)
            return True
        if e.button != 1:
            return True
        was_dragging = self.dragging
        if self.dragging:
            self.end_drag()
        click = self.pressed and not was_dragging
        self.pressed = False
        self.update_hit_rects()
        if click:
            z = self.zoom()
            self.js(f"handleClick('pat', {round(e.x / z, 1)}, {round(e.y / z, 1)})")
        return True

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
        px, py = self.pointer()
        self.dragging = True
        p = self.motion_packet()
        self.drag_t0 = _ms()
        p.update(x=self.x, y=self.y, downX=down_x, downY=down_y, px=px, py=py, time=0)
        self.last_bounds = store.dumps(p["bounds"])
        self.js(f"setMotionActive(true); petMotion.begin({store.dumps(p)})")
        self.last_ptr = (px, py)
        # 一帧交一次鼠标位置就够了（网页按帧算）：交得更勤只会和画面抢 CPU，虚拟机里尤其明显
        self.drag_timer = GLib.timeout_add(16, self.pump_pointer)

    def button_held(self):
        """左键现在还按着没有（问 X 服务器，不等松开的消息）"""
        gw = self.window.get_window() if self.window else None
        if gw is None:
            return True
        seat = Gdk.Display.get_default().get_default_seat()
        mask = gw.get_device_position(seat.get_pointer())[3]
        return bool(mask & Gdk.ModifierType.BUTTON1_MASK)

    def pump_pointer(self):
        if not self.dragging:
            self.drag_timer = None
            return False
        # 松开的消息可能丢了（鼠标甩出窗口、窗口在鼠标底下挪动时）：每一帧都直接看左键还按没按着，
        # 不然拖动一直不结束，看着像「断触」（2026-11-17 用户说拖拽不跟手还会断触）
        if not self.button_held():
            self.drag_timer = None   # 返回 False 后 GLib 自己删掉这个计时器；不清空的话 stop_motion 会拿旧编号再删一次
            self.end_drag()
            self.pressed = False
            self.update_hit_rects()
            return False
        px, py = self.pointer()
        if (px, py) != self.last_ptr:
            self.last_ptr = (px, py)
            self.js(f"petMotion.pointer({{id:{self.motion_id},px:{px},py:{py},time:{round(_ms() - self.drag_t0, 1)}}})")
        return True

    def end_drag(self):
        if not self.dragging:
            return
        px, py = self.pointer()
        t = round(_ms() - self.drag_t0, 1)
        self.dragging = False
        self.flying = True
        self.js(f"petMotion.pointer({{id:{self.motion_id},px:{px},py:{py},time:{t}}}); "
                f"petMotion.end({{id:{self.motion_id},x:{self.x},y:{self.y},time:{t}}})")

    def stop_motion(self, save=True):
        moved = self.dragging or self.flying
        self.motion_id += 1
        self.dragging = self.flying = False
        self.last_pos = None
        if self.drag_timer:
            GLib.source_remove(self.drag_timer)
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
        ix, iy = float(m.get("impactX") or 0), float(m.get("impactY") or 0)
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
    # 网页定方向、速度（像素 / 秒）、走多久，发来 {walk: {dir, speed, ms}}；这里逐帧平移窗口，只走水平方向
    def handle_walk(self, w):
        if w is None:
            self.stop_walk(None)
            return
        try:
            d, speed, ms = float(w["dir"]), float(w["speed"]), float(w["ms"])
        except (KeyError, TypeError, ValueError):
            return
        if not (0 < speed <= 2000 and 0 < ms <= 60000 and d in (1, -1)):
            return
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
        self.walk_timer = GLib.timeout_add(16, self.walk_frame)

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
        c = None if self.desktop_mode else self.claude_rect()
        if c is not None:
            self.off_right, self.off_bottom = c[2] - (self.x + self.w), c[3] - (self.y + self.h)
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
            GLib.source_remove(self.walk_timer)
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

        if self.ready:
            web.eval_js(self.view, f"setSizes({num(float(target if target is not None else self.scale()))}, "
                                   f"{num(self.text_scale())}, {'true' if self.text_linked() else 'false'})", got)
        else:
            got(None)

    def apply_scale(self, scale, anchor="bottom"):
        old = (self.x, self.y, self.x + self.w, self.y + self.h)
        w, h = max(1, round(self.page_w * scale)), max(1, round(self.page_h * scale))
        self.w, self.h = w, h
        self.window.set_size_request(w, h)
        self.window.resize(w, h)
        self.view.set_zoom_level(scale)
        self.shown_scale = scale
        foot = self.page_foot * w / self.page_w
        if anchor == "corner":
            x, y = old[0], old[1]
        else:
            x = (old[0] + old[2]) // 2 - w // 2
            y = old[3] + round(foot - (self.shown_foot or foot)) - h if anchor == "feet" else old[3] - h
        self.shown_foot = foot
        self.applied_hit = None
        self.update_hit_rects()
        self.move_to(x, y)
        self.save_position()

    def animate_scale(self, target):
        if self.scale_timer:
            GLib.source_remove(self.scale_timer)
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

        self.scale_timer = GLib.timeout_add(16, step)

    # ================= 藏起来 / 露出来 =================
    def set_concealed(self, c):
        if self.window is None or self.concealed == c:
            return
        self.concealed = c
        if c:
            self.stop_motion()
        self.apply_visibility()

    def update_page_hidden(self):
        hidden = self.concealed or self.session_locked or not self.shown
        self.js(f"setPageHidden({'true' if hidden else 'false'})")

    def on_lock(self, locked):
        self.session_locked = locked
        if locked:
            self.stop_motion()
            self.stop_walk(None)
        self.update_page_hidden()

    def set_user_hidden(self, hidden):
        self.user_hidden = hidden
        self.set_concealed(hidden)
        self.write_signal("hide" if hidden else "show")

    def show_again(self):
        if self.user_hidden:
            self.set_user_hidden(False)
        self.set_concealed(False)
        self.write_signal("show")

    def on_command(self, cmd):
        """控制 socket 收到的话（claudia-pet --toggle / --show / 再次打开）"""
        if cmd.startswith("eval ") and os.environ.get("CLAUDIA_DEBUG"):   # 调试用：在网页里求值，结果写进日志
            web.eval_js(self.view, cmd[5:], lambda v: store.log("eval = " + store.dumps(v)))
            return
        if cmd.startswith("py ") and os.environ.get("CLAUDIA_DEBUG"):     # 调试用：看外壳的状态
            try:
                ns = {"pet": self, "store": store, "GLib": GLib}
                exec(cmd[3:], ns)
                store.log("py = " + repr(ns.get("_")))
            except Exception as e:
                store.log("py 出错 " + repr(e))
            return
        cmd = cmd.split()[0] if cmd.split() else ""
        store.log("收到指令：" + cmd)
        if cmd == "toggle":
            self.set_user_hidden(not self.user_hidden)
        elif cmd == "show":
            self.recenter()
        elif cmd == "quit":
            self.user_quit()

    # ================= 快捷键 =================
    def hide_hotkey(self):
        v = store.settings.get("hideHotkey")
        return v if v in ("p", "h", "off") else "off"   # Linux 上默认不加：要往 GNOME 设置里写一条自定义快捷键，由你自己选

    def hotkey_label(self):
        return next((label for i, label, _ in system.CHOICES if i == self.hide_hotkey()), None)

    def register_hotkey(self, startup=False):
        if not store.is_main():
            return
        choice = self.hide_hotkey()
        if startup and choice == "off":
            return
        self.hotkey_failed = system.hotkey_taken(choice) or not system.set_hotkey(choice)

    # ================= 菜单 =================
    def close_menu(self):
        if self.menu is not None:
            self.menu.popdown()

    def build_menu(self):
        menu = Gtk.Menu()

        def item(text, fn, parent=menu):
            i = Gtk.MenuItem(label=text)
            i.connect("activate", lambda *_: self._safe(fn))
            parent.append(i)
            return i

        item("摸摸头", lambda: self.js("handleClick('pat')"))
        item("戳一下", lambda: self.js("handleClick('poke')"))
        sub = self.build_action_menu()
        if sub is not None:
            top = Gtk.MenuItem(label="动作")
            top.set_submenu(sub)
            menu.append(top)
        menu.append(Gtk.SeparatorMenuItem())
        item("多开和位置…", lambda: self.open_settings("pets"))
        item("设置…", lambda: self.open_settings())
        item("回到默认位置", self.recenter)
        label = self.hotkey_label() if store.is_main() else None
        item(f"暂时隐藏（{label}）" if label else "暂时隐藏", lambda: self.set_user_hidden(True))
        if store.is_main():   # 鼠标穿透只给第一只：开了以后点不到她，只能从托盘关
            ct = Gtk.CheckMenuItem(label="鼠标穿透（点击落到后面的窗口）")
            ct.set_active(self.click_through())
            ct.connect("toggled", lambda i: self._safe(lambda: self.set_click_through(i.get_active())))
            menu.append(ct)
        menu.append(Gtk.SeparatorMenuItem())
        if store.is_main():
            item("退出 " + store.APP_NAME, self.user_quit)
        else:
            item("送走这一只", self.dismiss_pet)
            item("送走所有新加的", self.dismiss_extras)
        menu.get_style_context().add_class("claudia-menu")
        menu.show_all()
        return menu

    @staticmethod
    def _safe(fn):
        try:
            fn()
        except Exception as e:
            store.log("菜单出错: " + repr(e))

    def build_action_menu(self):
        d = store.character_dir(self.current)
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
        root = Gtk.Menu()
        used = set()

        def add(parent, pose, label):
            i = Gtk.MenuItem(label=label)
            i.connect("activate", lambda *_: self.play_action(pose))
            parent.append(i)

        for group, poses in actions.GROUPS:
            sub = Gtk.Menu()
            n = 0
            for p in poses:
                if p in have:
                    used.add(p)
                    add(sub, p, actions.NAMES.get(p, p))
                    n += 1
            if n:
                g = Gtk.MenuItem(label=group)
                g.set_submenu(sub)
                root.append(g)
        others = sorted(p for p in have if p not in used and p not in actions.HIDDEN)
        if others:
            sub = Gtk.Menu()
            for p in others:
                add(sub, p, p)
            g = Gtk.MenuItem(label="其他")
            g.set_submenu(sub)
            root.append(g)
        return root

    def play_action(self, pose):
        if any(c in pose for c in "'\"\\\n\r"):
            return
        self.js("(function(p){ if (typeof show !== 'function') return; if (typeof showNow === 'function') showNow(p); else show(p, true);"
                " clearTimeout(window.__claudiaMenuTimer);"
                " if (typeof anim === 'function' && anim(p) && anim(p).walk && typeof startWalk === 'function' && startWalk(p)) return;"
                " if (typeof anim === 'function' && anim(p) && transientMs(p)) return;"
                " window.__claudiaMenuTimer = setTimeout(function(){ if (pose === p) show('idle', true); }, 6000); })('" + pose + "')")

    def popup_menu(self, event):
        self.close_menu()
        self.menu = self.build_menu()
        self.menu.attach_to_widget(self.hit, None)
        self.menu.popup_at_pointer(event)

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
        GLib.timeout_add(5000 if hint else 2000, lambda: (self.quit(), False)[1])

    def quit(self):
        if getattr(self, "_quit_done", False):
            return
        self._quit_done = True
        self.leaving = True
        self.stop_motion()
        if self.tray:
            self.tray.remove()
        Gtk.main_quit()

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
            info = store.read_json(store.character_dir(cid) / "character.json") or {}
            chars.append({"id": cid, "name": name, "defaultName": info.get("name") or cid, "accent": info.get("accent"),
                          "custom": store.names().get(cid) or ""})
        hotkeys = [{"id": "off", "label": "不用快捷键"}] + [{"id": i, "label": label} for i, label, _ in system.CHOICES]
        return {
            "version": store.version() + " Linux", "platform": "linux", "appVersion": store.version(),
            "claudeWindow": bool(self.claude_watch and self.claude_watch.available),
            "size": self.scale(), "textSize": self.text_scale(), "textLinked": self.text_linked(), "showName": self.show_name(),
            "aura": self.aura_mode(), "quotaStyle": self.quota_style(),
            "clickBounce": store.setting_on("clickBounce"), "tumbleActions": store.setting_on("tumbleActions"),
            "idleEnabled": store.setting_on("idleEnabled"), "idleFrequency": self.idle_frequency(),
            "notifyResults": store.setting_on("notifyResults"), "notifyWaiting": store.setting_on("notifyWaiting"),
            "notifyBackgroundOnly": store.setting_on("notifyBackgroundOnly"),
            "physicsEnabled": self.physics_enabled(), "ceilingBounce": store.setting_on("ceilingBounce"),
            "physics": {k: self.physics_value(k) for k in PHYSICS_RANGES},
            "hideHotkey": self.hide_hotkey(), "hotkeyFailed": self.hotkey_failed, "hotkeys": hotkeys,
            "hotkeyHint": "按一下藏起来，再按一下出来（加在 GNOME 设置的「自定义快捷键」里）；也可以从托盘菜单叫她出来",
            "character": self.current, "characters": chars,
            "integrations": claude.integrations(bool(self.claude_watch and self.claude_watch.available)),
            "runMode": "standalone" if store.standalone() else "claude",
            "pets": self.pet_list(), "maxPets": store.MAX_EXTRA_PETS + 1,
            "deskLayer": self.desk_layer(), "deskLayers": list(self.DESK_LAYERS),
            "clickThrough": self.click_through() if store.is_main() else None,   # 只有第一只有（关的入口在托盘）
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
            elif key == "deskLayer":
                self.set_desk_layer(value)
            elif key == "clickThrough":
                if isinstance(value, bool):
                    self.set_click_through(value)
            elif key == "runMode":
                if value in ("claude", "standalone"):
                    store.set_standalone(value == "standalone")
                    self.check_run_mode()
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
            if target in ("plugin", "mcp", "gnome") and action in ("install", "uninstall"):
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
        try:
            from gi.repository import Gio
            uri = target if "://" in target else Path(target).as_uri()
            Gio.AppInfo.launch_default_for_uri(uri, None)
        except Exception as e:
            store.log("打开失败: " + str(e))

    def integrate(self, target, install):
        try:
            message = {"plugin": claude.set_plugin, "mcp": claude.set_mcp, "gnome": claude.set_gnome_extension}[target](install)
        except Exception as e:
            message = "没有完成：" + str(e)
            store.log(f"接入 {target} 失败: {e!r}")
        store.log(f"接入 {target} {'install' if install else 'uninstall'}：{message}".replace("\n", " "))
        parent = self.settings_window.window if self.settings_window else None
        dlg = Gtk.MessageDialog(transient_for=parent, modal=True, message_type=Gtk.MessageType.INFO,
                                buttons=Gtk.ButtonsType.OK, text=store.APP_NAME + " · 接入 Claude")
        dlg.format_secondary_text(message)
        dlg.run()
        dlg.destroy()

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
        GLib.timeout_add(2000, lambda: (self.quit(), False)[1])

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
            GLib.timeout_add(2000, lambda: (self.quit(), False)[1])

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
        store.log("改成待在桌面上" if desktop else "改成待在 Claude 窗口里")
        self.check_area()

    def handle_pet_command(self, msg):
        action, value = msg.get("action"), msg.get("value")
        if action == "add":
            self.add_pet(value == "desktop")
            GLib.timeout_add(1500, lambda: (self.push_settings(), False)[1])
            GLib.timeout_add(4000, lambda: (self.push_settings(), False)[1])
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
        GLib.timeout_add(1500, lambda: (self.push_settings(), False)[1])
        GLib.timeout_add(3500, lambda: (self.push_settings(), False)[1])

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
