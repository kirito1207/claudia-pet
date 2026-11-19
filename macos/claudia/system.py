"""和桌面打交道的零碎：系统通知、菜单栏图标、隐藏快捷键、控制用的 socket、锁屏。

Windows 版用的是托盘气泡、RegisterHotKey、命名事件；macOS 上分别换成：
- 通知：osascript 的 display notification（不在 .app 里的 Python 用不了 UserNotifications；
  通知会显示成「脚本编辑器」发的，第一次可能要在系统设置里允许）
- 托盘：菜单栏右上角的图标（NSStatusItem）
- 快捷键：Carbon 的 RegisterEventHotKey。不要「辅助功能」权限，也不改系统设置
- 叫出来 / 藏起来 / 退出：~/.claudia/run/<编号>.sock，一行一句话（和 Linux 版一样）
- 锁屏：com.apple.screenIsLocked / screenIsUnlocked 通知，加上屏幕睡眠 / 唤醒
"""
import ctypes
import os
import socket
import subprocess
import threading

import AppKit
import Foundation
import objc

from . import store, ui

# ---------------- 通知 ----------------
_SCRIPT = ["-e", "on run argv", "-e", "display notification (item 2 of argv) with title (item 1 of argv)", "-e", "end run"]


def notify(title, body, warn=False):
    try:
        subprocess.Popen(["/usr/bin/osascript", *_SCRIPT, title, body], stdin=subprocess.DEVNULL,
                         stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        return True
    except OSError as e:
        store.log("系统通知失败: " + str(e))
        return False


# ---------------- 菜单栏图标 ----------------
class ClaudiaMenuFiller(Foundation.NSObject):
    """NSMenuDelegate：菜单每次打开前现做一遍（动作列表跟着当前角色变）"""

    def menuNeedsUpdate_(self, menu):
        fn = getattr(self, "fn", None)
        if fn is not None:
            try:
                fn(menu)
            except Exception as e:
                store.log("菜单出错: " + repr(e))


def make_tray(fill_menu, on_activate):
    """fill_menu(menu) 往菜单里加桌宠的右键菜单项；最上面再加一项「叫她出来」"""
    try:
        bar = AppKit.NSStatusBar.systemStatusBar()
        item = bar.statusItemWithLength_(AppKit.NSVariableStatusItemLength)
    except Exception as e:
        store.log("没有菜单栏图标: " + repr(e))
        return None
    button = item.button()
    icon = store.INSTALL / "icon" / "AppIcon.png"
    img = AppKit.NSImage.alloc().initWithContentsOfFile_(str(icon)) if icon.exists() else None
    if img is not None:
        img.setSize_((18, 18))
        button.setImage_(img)
    else:
        button.setTitle_("claudia")
    button.setToolTip_(store.APP_NAME)

    def fill(menu):
        menu.removeAllItems()
        menu.addItem_(ui.menu_item("叫她出来", on_activate))
        menu.addItem_(AppKit.NSMenuItem.separatorItem())
        fill_menu(menu)

    filler = ClaudiaMenuFiller.alloc().init()
    filler.fn = fill
    menu = ui.new_menu(store.APP_NAME)
    menu.setDelegate_(filler)   # 弱引用：Tray 自己留着 filler
    item.setMenu_(menu)

    class Tray:
        keep = (item, menu, filler)

        def refresh(self):
            pass   # 每次打开时现做，不用刷新

        def remove(self):
            try:
                bar.removeStatusItem_(item)
            except Exception:
                pass

    return Tray()


# ---------------- 隐藏快捷键（Carbon RegisterEventHotKey） ----------------
# (编号, 显示的名字, 虚拟键码)；修饰键都是 Control + Option（和 Windows 版的 Ctrl+Alt 对应）
CHOICES = [("p", "⌃⌥P", 0x23), ("h", "⌃⌥H", 0x04)]
_CONTROL, _OPTION = 0x1000, 0x0800


class _HotKeyID(ctypes.Structure):
    _fields_ = [("signature", ctypes.c_uint32), ("id", ctypes.c_uint32)]


class _EventTypeSpec(ctypes.Structure):
    _fields_ = [("eventClass", ctypes.c_uint32), ("eventKind", ctypes.c_uint32)]


_HANDLER = ctypes.CFUNCTYPE(ctypes.c_int32, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p)


def _fourcc(s):
    return int.from_bytes(s.encode("ascii"), "big")


_carbon = None
_handler_c = None      # C 回调本身：要一直留着，被回收了系统一调就崩
_handler_ref = ctypes.c_void_p()
_hotkey_ref = None
_on_hotkey = None


def _lib():
    global _carbon
    if _carbon is not None:
        return _carbon or None
    for path in ("/System/Library/Frameworks/Carbon.framework/Carbon",
                 "/System/Library/Frameworks/Carbon.framework/Frameworks/HIToolbox.framework/HIToolbox"):
        try:
            lib = ctypes.CDLL(path)
            lib.GetApplicationEventTarget.restype = ctypes.c_void_p
            lib.GetApplicationEventTarget.argtypes = []
            lib.InstallEventHandler.restype = ctypes.c_int32
            lib.InstallEventHandler.argtypes = [ctypes.c_void_p, _HANDLER, ctypes.c_ulong, ctypes.POINTER(_EventTypeSpec),
                                                ctypes.c_void_p, ctypes.POINTER(ctypes.c_void_p)]
            lib.RegisterEventHotKey.restype = ctypes.c_int32
            lib.RegisterEventHotKey.argtypes = [ctypes.c_uint32, ctypes.c_uint32, _HotKeyID, ctypes.c_void_p,
                                                ctypes.c_uint32, ctypes.POINTER(ctypes.c_void_p)]
            lib.UnregisterEventHotKey.restype = ctypes.c_int32
            lib.UnregisterEventHotKey.argtypes = [ctypes.c_void_p]
            _carbon = lib
            return lib
        except (OSError, AttributeError) as e:
            store.log(f"载入 {path} 失败: {e}")
    _carbon = False
    return None


def _on_event(_call, _event, _data):
    if _on_hotkey is not None:
        ui.call_soon(_on_hotkey)
    return 0


def _install_handler(lib):
    global _handler_c
    if _handler_c is not None:
        return True
    cb = _HANDLER(_on_event)
    spec = _EventTypeSpec(_fourcc("keyb"), 5)   # kEventClassKeyboard, kEventHotKeyPressed
    err = lib.InstallEventHandler(lib.GetApplicationEventTarget(), cb, 1, ctypes.byref(spec), None,
                                  ctypes.byref(_handler_ref))
    if err != 0:
        store.log(f"装快捷键回调失败：{err}")
        return False
    _handler_c = cb
    return True


def set_hotkey(choice, on_press=None):
    """choice: p / h / off。返回 True 表示设好了（或已撤掉）；被别的程序占了、出错了返回 False（只是没有快捷键，别的照常）"""
    try:
        return _set_hotkey(choice, on_press)
    except Exception as e:
        store.log("设置快捷键出错: " + repr(e))
        return False


def _set_hotkey(choice, on_press):
    global _hotkey_ref, _on_hotkey
    spec = next((c for c in CHOICES if c[0] == choice), None)
    lib = _lib()
    if lib is None:
        return spec is None
    if _hotkey_ref is not None:
        lib.UnregisterEventHotKey(_hotkey_ref)
        _hotkey_ref = None
    if spec is None:
        return True
    if not _install_handler(lib):
        return False
    _on_hotkey = on_press
    ref = ctypes.c_void_p()
    err = lib.RegisterEventHotKey(spec[2], _CONTROL | _OPTION, _HotKeyID(_fourcc("clpt"), 1),
                                  lib.GetApplicationEventTarget(), 0, ctypes.byref(ref))
    if err != 0:
        store.log(f"注册快捷键 {spec[1]} 失败：{err}（-9878 是被别的程序占了）")
        return False
    _hotkey_ref = ref.value
    return True


# ---------------- 控制 socket ----------------
def serve(path, on_command):
    """在 path 上收一行一句的指令，交给 on_command(str)（在界面线程里调）"""
    try:
        os.unlink(path)
    except OSError:
        pass
    srv = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
    srv.bind(str(path))
    os.chmod(path, 0o600)
    srv.listen(4)

    def loop():
        while True:
            try:
                conn, _ = srv.accept()
            except OSError:
                return   # 关掉了
            try:
                conn.settimeout(1)
                data = conn.recv(65536).decode(errors="ignore").strip()
            except OSError:
                data = ""
            finally:
                conn.close()
            if data:
                ui.call_soon(lambda d=data: on_command(d))

    threading.Thread(target=loop, name="claudia-control", daemon=True).start()
    return srv


# ---------------- 锁屏 ----------------
class ClaudiaLockWatcher(Foundation.NSObject):
    def lockOn_(self, note):
        self.set("lock", True)

    def lockOff_(self, note):
        self.set("lock", False)

    def sleepOn_(self, note):
        self.set("sleep", True)

    def sleepOff_(self, note):
        self.set("sleep", False)

    @objc.python_method
    def set(self, key, value):
        self.state[key] = value
        try:
            self.fn(self.state["lock"] or self.state["sleep"])
        except Exception as e:
            store.log("处理锁屏出错: " + repr(e))


_watchers = []


def watch_lock(on_change):
    """锁屏 / 屏幕睡着时调 on_change(True)，回来时调 on_change(False)：锁着时动画全停"""
    try:
        w = ClaudiaLockWatcher.alloc().init()
        w.fn = on_change
        w.state = {"lock": False, "sleep": False}
        # 程序不在前台时，系统默认把跨进程通知攒着不发（她几乎从来不在前台）：要求立刻送到
        now = getattr(Foundation, "NSNotificationSuspensionBehaviorDeliverImmediately", 4)
        dist = Foundation.NSDistributedNotificationCenter.defaultCenter()
        dist.addObserver_selector_name_object_suspensionBehavior_(w, "lockOn:", "com.apple.screenIsLocked", None, now)
        dist.addObserver_selector_name_object_suspensionBehavior_(w, "lockOff:", "com.apple.screenIsUnlocked", None, now)
        ws = AppKit.NSWorkspace.sharedWorkspace().notificationCenter()
        ws.addObserver_selector_name_object_(w, "sleepOn:", AppKit.NSWorkspaceScreensDidSleepNotification, None)
        ws.addObserver_selector_name_object_(w, "sleepOff:", AppKit.NSWorkspaceScreensDidWakeNotification, None)
        _watchers.append(w)
    except Exception as e:
        store.log("没能监听锁屏: " + repr(e))
