"""和 AppKit 打交道的底层小工具：定时器、主线程调用、事件循环、屏幕坐标换算、菜单项。

坐标约定：桌宠这边（pet.py、网页里的物理公式）一律用「主屏幕左上角为原点、y 向下」的点坐标（point，不是物理像素），
和 Linux 版的 GDK 逻辑像素一样；Cocoa 自己是「主屏幕左下角为原点、y 向上」。两套之间的换算只在这里做。
"""
import time
import traceback

import AppKit
import Foundation
import objc  # noqa: F401  （先载入 PyObjC 本体，下面的 NSObject 子类要用）
from PyObjCTools import AppHelper

from . import store


# ---------------- 定时器：照 GLib.timeout_add 的用法，回调返回真值就接着来，返回假值就停 ----------------
class ClaudiaTimerTarget(Foundation.NSObject):
    def fire_(self, timer):
        fn = getattr(self, "fn", None)
        if fn is not None:
            fn()


_timers = {}
_seq = [0]


def timeout_add(ms, fn):
    """每 ms 毫秒调一次 fn()，直到它返回假值或者 source_remove；返回编号。
    加在 common 模式里：弹出菜单、拖窗口的时候也照常走（不然右键菜单开着时她就定住了）"""
    _seq[0] += 1
    tid = _seq[0]

    def fire():
        again = False
        try:
            again = fn()
        except Exception:
            store.log("定时任务出错: " + traceback.format_exc()[-1500:])
        if not again:
            source_remove(tid)

    target = ClaudiaTimerTarget.alloc().init()
    target.fn = fire
    t = Foundation.NSTimer.timerWithTimeInterval_target_selector_userInfo_repeats_(
        max(1, ms) / 1000.0, target, "fire:", None, True)
    Foundation.NSRunLoop.mainRunLoop().addTimer_forMode_(t, Foundation.NSRunLoopCommonModes)
    _timers[tid] = t
    return tid


def source_remove(tid):
    t = _timers.pop(tid, None)
    if t is not None:
        t.invalidate()


def _safe_call(fn):
    try:
        fn()
    except Exception:
        store.log("主线程任务出错: " + traceback.format_exc()[-1500:])


def call_soon(fn):
    """从哪个线程都能调：把 fn 交给主线程（界面线程）跑"""
    AppHelper.callAfter(_safe_call, fn)


# ---------------- 程序本身 ----------------
class ClaudiaScreenWatcher(Foundation.NSObject):
    def changed_(self, note):
        _ph[1] = -10.0   # 显示器插拔、改分辨率：下次换算坐标时重新量主屏幕


_keep = []


def setup_app():
    """建 NSApplication：不进程序坞、不出现在 ⌘Tab 里（Accessory），只在菜单栏有个图标"""
    app = AppKit.NSApplication.sharedApplication()
    app.setActivationPolicy_(AppKit.NSApplicationActivationPolicyAccessory)
    icon = store.INSTALL / "icon" / "AppIcon.png"
    if icon.exists():
        img = AppKit.NSImage.alloc().initWithContentsOfFile_(str(icon))
        if img is not None:
            app.setApplicationIconImage_(img)   # 提示框、设置窗口用
    # 主菜单：Accessory 程序不显示菜单栏，但 ⌘C / ⌘V / ⌘W 这些快捷键还是走它；没有的话设置页里的输入框不能粘贴
    main = AppKit.NSMenu.alloc().initWithTitle_("")
    app_item = AppKit.NSMenuItem.alloc().initWithTitle_action_keyEquivalent_(store.APP_NAME, None, "")
    app_menu = AppKit.NSMenu.alloc().initWithTitle_(store.APP_NAME)
    app_menu.addItemWithTitle_action_keyEquivalent_("关闭窗口", "performClose:", "w")
    app_item.setSubmenu_(app_menu)
    main.addItem_(app_item)
    edit_item = AppKit.NSMenuItem.alloc().initWithTitle_action_keyEquivalent_("编辑", None, "")
    edit = AppKit.NSMenu.alloc().initWithTitle_("编辑")
    for title, action, key in (("撤销", "undo:", "z"), ("重做", "redo:", "Z"), (None, None, None),
                               ("剪切", "cut:", "x"), ("拷贝", "copy:", "c"), ("粘贴", "paste:", "v"),
                               ("全选", "selectAll:", "a")):
        if title is None:
            edit.addItem_(AppKit.NSMenuItem.separatorItem())
        else:
            edit.addItemWithTitle_action_keyEquivalent_(title, action, key)
    edit_item.setSubmenu_(edit)
    main.addItem_(edit_item)
    app.setMainMenu_(main)
    # 不让系统给她打「App Nap」：窗口一直在屏幕上，定时器被拖慢的话动作和拖动会一卡一卡的
    opts = getattr(Foundation, "NSActivityUserInitiatedAllowingIdleSystemSleep", 0x00EFFFFF)
    _keep.append(Foundation.NSProcessInfo.processInfo().beginActivityWithOptions_reason_(opts, "desktop pet animation"))
    watcher = ClaudiaScreenWatcher.alloc().init()
    Foundation.NSNotificationCenter.defaultCenter().addObserver_selector_name_object_(
        watcher, "changed:", AppKit.NSApplicationDidChangeScreenParametersNotification, None)
    _keep.append(watcher)
    return app


def activate():
    """把自己提到前台（打开设置窗口、弹提示框之前）"""
    app = AppKit.NSApplication.sharedApplication()
    try:
        app.activateIgnoringOtherApps_(True)
    except Exception:
        try:
            app.activate()
        except Exception:
            pass


_stopping = [False]


def run():
    app = AppKit.NSApplication.sharedApplication()
    errors = 0
    while not _stopping[0]:
        try:
            app.run()
        except Exception:
            errors += 1
            store.log("事件循环出错: " + traceback.format_exc()[-1500:])
            if errors > 20:
                break
            continue
        break


def stop():
    """让 run() 返回（不直接 terminate：退出前还要收拾 socket、pid 文件）"""
    _stopping[0] = True
    app = AppKit.NSApplication.sharedApplication()
    app.stop_(None)
    # stop: 要等下一个事件处理完才生效：自己塞一个空事件进去
    kind = getattr(AppKit, "NSEventTypeApplicationDefined", getattr(AppKit, "NSApplicationDefined", 15))
    ev = AppKit.NSEvent.otherEventWithType_location_modifierFlags_timestamp_windowNumber_context_subtype_data1_data2_(
        kind, Foundation.NSMakePoint(0, 0), 0, 0.0, 0, None, 0, 0, 0)
    app.postEvent_atStart_(ev, False)


# ---------------- 坐标 ----------------
_ph = [0.0, -10.0]   # 主屏幕高度、上次量的时间


def primary_height():
    now = time.monotonic()
    if now - _ph[1] > 2:
        ss = AppKit.NSScreen.screens()
        _ph[0] = float(ss[0].frame().size.height) if ss is not None and len(ss) else 0.0
        _ph[1] = now
    return _ph[0]


def _flip(r, height):
    return (float(r.origin.x), height - float(r.origin.y) - float(r.size.height), float(r.size.width), float(r.size.height))


def screens():
    """[(整块屏幕, 可用区域)]，都是 (x, y, w, h)、左上角原点。第一个是主屏幕（菜单栏所在的那块）。
    可用区域 = visibleFrame：去掉了菜单栏和程序坞"""
    ss = AppKit.NSScreen.screens()
    if ss is None or not len(ss):
        return []
    height = float(ss[0].frame().size.height)
    return [(_flip(s.frame(), height), _flip(s.visibleFrame(), height)) for s in ss]


def _ltrb(r):
    x, y, w, h = r
    return int(round(x)), int(round(y)), int(round(x + w)), int(round(y + h))


def work_area_at(px, py):
    """点 (px, py) 所在（不在任何屏幕上就取最近的那块）屏幕的可用区域 (左, 上, 右, 下)"""
    best, best_d = None, None
    for frame, visible in screens():
        fx, fy, fw, fh = frame
        dx = max(fx - px, 0, px - (fx + fw))
        dy = max(fy - py, 0, py - (fy + fh))
        d = dx * dx + dy * dy
        if best is None or d < best_d:
            best, best_d = visible, d
    return _ltrb(best) if best else (0, 0, 1440, 900)


def primary_work_area():
    ss = screens()
    return _ltrb(ss[0][1]) if ss else (0, 0, 1440, 900)


def cocoa_rect(x, y, w, h):
    """左上角原点的 (x, y, w, h) → Cocoa 的 NSRect"""
    return ((float(x), primary_height() - float(y) - float(h)), (float(w), float(h)))


def cocoa_origin(x, y, h):
    return (float(x), primary_height() - float(y) - float(h))


def pointer():
    """鼠标现在在哪（左上角原点）"""
    p = AppKit.NSEvent.mouseLocation()
    return int(round(p.x)), int(round(primary_height() - p.y))


def button_held():
    """左键现在还按着没有（直接问系统，不等松开的消息；不用辅助功能权限）"""
    return bool(AppKit.NSEvent.pressedMouseButtons() & 1)


def frontmost_pid():
    app = AppKit.NSWorkspace.sharedWorkspace().frontmostApplication()
    return int(app.processIdentifier()) if app is not None else None


# ---------------- 菜单项 ----------------
class ClaudiaAction(Foundation.NSObject):
    def run_(self, sender):
        fn = getattr(self, "fn", None)
        if fn is not None:
            _safe_call(fn)


def menu_item(title, fn, enabled=True):
    """一个点了就调 fn() 的菜单项。动作对象挂在 representedObject 上，跟着菜单项活（target 是弱引用）"""
    a = ClaudiaAction.alloc().init()
    a.fn = fn
    item = AppKit.NSMenuItem.alloc().initWithTitle_action_keyEquivalent_(title, "run:", "")
    item.setTarget_(a)
    item.setRepresentedObject_(a)
    item.setEnabled_(enabled)
    return item


def submenu_item(title, menu):
    item = AppKit.NSMenuItem.alloc().initWithTitle_action_keyEquivalent_(title, None, "")
    item.setSubmenu_(menu)
    return item


def new_menu(title=""):
    m = AppKit.NSMenu.alloc().initWithTitle_(title)
    m.setAutoenablesItems_(False)
    return m


_alerts = []


def alert(title, text, window=None):
    """提示框：有窗口就挂在窗口上（不挡别的），没有就单独弹"""
    a = AppKit.NSAlert.alloc().init()
    a.setMessageText_(title)
    a.setInformativeText_(text)
    a.addButtonWithTitle_("好")
    if window is not None and window.isVisible():
        a.beginSheetModalForWindow_completionHandler_(window, None)
        _alerts.append(a)
        del _alerts[:-5]
    else:
        activate()
        a.runModal()
