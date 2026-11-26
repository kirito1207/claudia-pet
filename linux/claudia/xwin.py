"""别的窗口现在是什么样（X11，ctypes 调 libX11）：前台窗口是谁、是不是全屏、在屏幕上的范围。

桌面模式「全屏时让开」用（2026-11-26）：窗口管理器给全屏窗口打 _NET_WM_STATE_FULLSCREEN（浏览器 F11 / 视频全屏、
LibreOffice 放映、游戏都会打），前台窗口看 root 上的 _NET_ACTIVE_WINDOW。只看得到 X11 窗口：GNOME Wayland 下
原生 Wayland 窗口这里看不到，那边由 GNOME 扩展报（state 里的 focusFullscreen）。
"""
import ctypes
import ctypes.util

from . import store

_x11 = _dpy = None
_atoms = {}


def _init():
    global _x11, _dpy
    if _dpy is not None:
        return bool(_dpy)
    try:
        _x11 = ctypes.CDLL(ctypes.util.find_library("X11") or "libX11.so.6")
        _x11.XOpenDisplay.restype = ctypes.c_void_p
        _x11.XOpenDisplay.argtypes = [ctypes.c_char_p]
        _x11.XDefaultRootWindow.restype = ctypes.c_ulong
        _x11.XDefaultRootWindow.argtypes = [ctypes.c_void_p]
        _x11.XInternAtom.restype = ctypes.c_ulong
        _x11.XInternAtom.argtypes = [ctypes.c_void_p, ctypes.c_char_p, ctypes.c_int]
        _x11.XGetWindowProperty.argtypes = [
            ctypes.c_void_p, ctypes.c_ulong, ctypes.c_ulong, ctypes.c_long, ctypes.c_long, ctypes.c_int, ctypes.c_ulong,
            ctypes.POINTER(ctypes.c_ulong), ctypes.POINTER(ctypes.c_int), ctypes.POINTER(ctypes.c_ulong),
            ctypes.POINTER(ctypes.c_ulong), ctypes.POINTER(ctypes.c_void_p)]
        _x11.XFree.argtypes = [ctypes.c_void_p]
        _x11.XGetGeometry.argtypes = [
            ctypes.c_void_p, ctypes.c_ulong, ctypes.POINTER(ctypes.c_ulong), ctypes.POINTER(ctypes.c_int), ctypes.POINTER(ctypes.c_int),
            ctypes.POINTER(ctypes.c_uint), ctypes.POINTER(ctypes.c_uint), ctypes.POINTER(ctypes.c_uint), ctypes.POINTER(ctypes.c_uint)]
        _x11.XTranslateCoordinates.argtypes = [
            ctypes.c_void_p, ctypes.c_ulong, ctypes.c_ulong, ctypes.c_int, ctypes.c_int,
            ctypes.POINTER(ctypes.c_int), ctypes.POINTER(ctypes.c_int), ctypes.POINTER(ctypes.c_ulong)]
        # 窗口刚关掉时去读它会收到 BadWindow：默认的错误处理会让整个程序退出，换成只忽略
        handler_t = ctypes.CFUNCTYPE(ctypes.c_int, ctypes.c_void_p, ctypes.c_void_p)
        global _handler
        _handler = handler_t(lambda *_: 0)
        _x11.XSetErrorHandler.argtypes = [handler_t]
        _x11.XSetErrorHandler(_handler)
        _dpy = _x11.XOpenDisplay(None) or 0
        return bool(_dpy)
    except (OSError, AttributeError) as e:
        store.log("没能加载 X11 库，看不到别的窗口是否全屏: " + str(e))
        _dpy = 0
        return False


_handler = None


def _atom(name):
    if name not in _atoms:
        _atoms[name] = _x11.XInternAtom(_dpy, name.encode(), 0)
    return _atoms[name]


def _longs(xid, prop, max_items=64):
    """读一个 32 位（ATOM / WINDOW / CARDINAL）属性，返回整数列表；读不到返回 []"""
    typ, fmt, n, after, data = ctypes.c_ulong(), ctypes.c_int(), ctypes.c_ulong(), ctypes.c_ulong(), ctypes.c_void_p()
    ok = _x11.XGetWindowProperty(_dpy, xid, _atom(prop), 0, max_items, 0, 0,  # AnyPropertyType
                                 ctypes.byref(typ), ctypes.byref(fmt), ctypes.byref(n), ctypes.byref(after), ctypes.byref(data))
    if ok != 0 or not data.value:
        return []
    try:
        if fmt.value != 32:
            return []
        arr = ctypes.cast(data, ctypes.POINTER(ctypes.c_ulong))   # Xlib 把 32 位属性存成 long
        return [arr[i] for i in range(n.value)]
    finally:
        _x11.XFree(data)


def active_window():
    """前台窗口（_NET_ACTIVE_WINDOW）；窗口管理器不报或没有前台窗口时返回 0"""
    if not _init():
        return 0
    v = _longs(_x11.XDefaultRootWindow(_dpy), "_NET_ACTIVE_WINDOW", 1)
    return v[0] if v else 0


def is_fullscreen(xid):
    return bool(xid) and _init() and _atom("_NET_WM_STATE_FULLSCREEN") in _longs(xid, "_NET_WM_STATE")


def is_hidden(xid):
    return bool(xid) and _init() and _atom("_NET_WM_STATE_HIDDEN") in _longs(xid, "_NET_WM_STATE")


def rect(xid):
    """窗口在屏幕上的范围 (左, 上, 右, 下)；读不到返回 None"""
    if not xid or not _init():
        return None
    root, x, y, w, h, bw, depth = ctypes.c_ulong(), ctypes.c_int(), ctypes.c_int(), ctypes.c_uint(), ctypes.c_uint(), ctypes.c_uint(), ctypes.c_uint()
    if not _x11.XGetGeometry(_dpy, xid, ctypes.byref(root), ctypes.byref(x), ctypes.byref(y),
                             ctypes.byref(w), ctypes.byref(h), ctypes.byref(bw), ctypes.byref(depth)):
        return None
    ax, ay, child = ctypes.c_int(), ctypes.c_int(), ctypes.c_ulong()
    if not _x11.XTranslateCoordinates(_dpy, xid, root.value, 0, 0, ctypes.byref(ax), ctypes.byref(ay), ctypes.byref(child)):
        return None
    return ax.value, ay.value, ax.value + w.value, ay.value + h.value


def fullscreen_front():
    """前台窗口是全屏的话返回它的范围 (左, 上, 右, 下)，否则 None"""
    w = active_window()
    if not w or not is_fullscreen(w) or is_hidden(w):
        return None
    return rect(w)
