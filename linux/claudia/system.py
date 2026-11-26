"""和桌面打交道的零碎：系统通知、托盘图标、隐藏快捷键、控制用的 socket、锁屏。

Windows 版用的是托盘气泡、RegisterHotKey、命名事件；Linux（GNOME / Wayland）上分别换成：
- 通知：libnotify
- 托盘：Qt 的 QSystemTrayIcon（GNOME 上要有 AppIndicator 扩展才显示，Ubuntu 自带；KDE 等原生支持）
- 快捷键：Wayland 下普通程序抢不到全局按键，改成在 GNOME「自定义快捷键」里加一条，按下时执行 claudia-pet --toggle
- 叫出来 / 藏起来 / 退出：~/.claudia/run/<编号>.sock，一行一句话
"""
import os
import socket
import subprocess

import gi
from gi.repository import Gio, GLib  # noqa: E402

from . import store  # noqa: E402

# ---------------- 通知 ----------------
_notify_ready = None


def notify(title, body, warn=False):
    global _notify_ready
    try:
        if _notify_ready is None:
            gi.require_version("Notify", "0.7")
            from gi.repository import Notify
            _notify_ready = Notify.init(store.APP_NAME)
        from gi.repository import Notify
        icon = str(store.INSTALL / "icon" / "AppIcon.png")
        n = Notify.Notification.new(title, body, icon if os.path.exists(icon) else ("dialog-warning" if warn else "dialog-information"))
        n.set_urgency(Notify.Urgency.NORMAL)
        n.show()
        return True
    except Exception as e:
        store.log("系统通知失败: " + str(e))
        try:
            store.reap(subprocess.Popen(["notify-send", "-a", store.APP_NAME, title, body], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL))
            return True
        except OSError:
            return False


# ---------------- 托盘 ----------------
def make_tray(build_menu, on_activate):
    """build_menu(menu) 把右键菜单的内容填进 menu。托盘的菜单对象一直是同一个，每次打开前清空重填（勾选状态、动作列表都是最新的）；
    refresh() 也能主动重填。中键点 / 单击托盘图标 = 叫她出来"""
    from PySide6.QtGui import QAction, QIcon
    from PySide6.QtWidgets import QMenu, QSystemTrayIcon
    if not QSystemTrayIcon.isSystemTrayAvailable():
        store.log("没有托盘：桌面没有托盘区（GNOME 要装 AppIndicator 扩展）")
        return None
    icon_path = store.INSTALL / "icon" / "AppIcon.png"
    icon = QIcon(str(icon_path)) if icon_path.exists() else QIcon.fromTheme("face-smile")
    tray_icon = QSystemTrayIcon(icon)
    tray_icon.setToolTip(store.APP_NAME)
    menu = QMenu()

    def fill():
        menu.clear()
        show = QAction("叫她出来", menu)
        show.triggered.connect(lambda *_: on_activate())
        menu.addAction(show)
        menu.addSeparator()
        try:
            build_menu(menu)
        except Exception as e:
            store.log("托盘菜单出错: " + repr(e))

    menu.aboutToShow.connect(fill)
    tray_icon.setContextMenu(menu)
    tray_icon.activated.connect(lambda reason: on_activate() if reason in (QSystemTrayIcon.ActivationReason.Trigger,
                                                                            QSystemTrayIcon.ActivationReason.MiddleClick) else None)

    class Tray:
        def refresh(self):
            fill()

        def remove(self):
            try:
                tray_icon.hide()
            except Exception:
                pass

    fill()
    tray_icon.show()
    t = Tray()
    t._keep = (tray_icon, menu)   # 留着引用，不然被回收、图标消失
    return t


# ---------------- 隐藏快捷键（GNOME 自定义快捷键） ----------------
CHOICES = [("p", "Ctrl+Alt+P", "<Control><Alt>p"), ("h", "Ctrl+Alt+H", "<Control><Alt>h")]
_MK = "org.gnome.settings-daemon.plugins.media-keys"
_PATH = "/org/gnome/settings-daemon/plugins/media-keys/custom-keybindings/claudia-pet/"


def _gsettings(*args):
    return subprocess.run(["gsettings", *args], capture_output=True, text=True, timeout=5)


def set_hotkey(choice):
    """choice: p / h / off。返回 True 表示设好了（或已撤掉）"""
    try:
        cur = _gsettings("get", _MK, "custom-keybindings").stdout.strip()
        paths = [] if cur in ("@as []", "") else [p.strip().strip("'") for p in cur.strip("[]").split(",") if p.strip()]
        binding = next((b for i, _, b in CHOICES if i == choice), None)
        if not binding and _PATH not in paths:
            return True   # 本来就没加过：不去动别人的快捷键列表
        paths = [p for p in paths if p != _PATH]
        if binding:
            paths.append(_PATH)
            schema = f"{_MK}.custom-keybinding:{_PATH}"
            _gsettings("set", schema, "name", store.APP_NAME + " 隐藏 / 显示")
            _gsettings("set", schema, "command", f"{store.launcher()} --toggle")
            _gsettings("set", schema, "binding", binding)
        r = _gsettings("set", _MK, "custom-keybindings", "[" + ", ".join(f"'{p}'" for p in paths) + "]")
        if r.returncode != 0:
            store.log("设置快捷键失败: " + r.stderr.strip())
            return False
        return True
    except (OSError, subprocess.SubprocessError) as e:
        store.log("设置快捷键失败: " + str(e))
        return False


def hotkey_taken(choice):
    """别的自定义快捷键已经用了同一组合"""
    binding = next((b for i, _, b in CHOICES if i == choice), None)
    if not binding:
        return False
    try:
        cur = _gsettings("get", _MK, "custom-keybindings").stdout.strip()
        for p in [p.strip().strip("'") for p in cur.strip("[]").split(",") if p.strip() and "@as" not in p]:
            if p == _PATH:
                continue
            b = _gsettings("get", f"{_MK}.custom-keybinding:{p}", "binding").stdout.strip().strip("'")
            if b.lower() == binding.lower():
                return True
    except (OSError, subprocess.SubprocessError):
        pass
    return False


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
    srv.setblocking(False)

    def accept(*_):
        try:
            conn, _ = srv.accept()
        except OSError:
            return True
        try:
            conn.settimeout(1)
            data = conn.recv(65536).decode(errors="ignore").strip()
        except OSError:
            data = ""
        finally:
            conn.close()
        if data:
            GLib.idle_add(lambda: (on_command(data), False)[1])
        return True

    GLib.io_add_watch(srv.fileno(), GLib.IO_IN, accept)
    return srv


# ---------------- 锁屏 ----------------
def watch_lock(on_change):
    """GNOME 锁屏 / 解锁时调 on_change(locked)：锁着时动画全停"""
    try:
        bus = Gio.bus_get_sync(Gio.BusType.SESSION, None)
        bus.signal_subscribe(None, "org.gnome.ScreenSaver", "ActiveChanged", "/org/gnome/ScreenSaver", None,
                             Gio.DBusSignalFlags.NONE, lambda *a: on_change(bool(a[5].unpack()[0])))
    except Exception as e:
        store.log("没能监听锁屏: " + str(e))
