"""设置窗口（右键「设置…」）：加载 web/settings.html。
页面 postMessage 发 {cmd, ...}，Pet.handle_settings 改完设置后用 push 把最新状态推回页面。
2026-11-27 从 GTK 换成 Qt（和桌宠同一套 Qt WebEngine）。"""
from PySide6.QtCore import Qt
from PySide6.QtGui import QGuiApplication, QIcon
from PySide6.QtWidgets import QVBoxLayout, QWidget

from . import store, web


class _Window(QWidget):
    def __init__(self, on_close):
        super().__init__()
        self._on_close = on_close

    def closeEvent(self, e):
        self._on_close()
        super().closeEvent(e)


class SettingsWindow:
    def __init__(self, pet):
        self.pet = pet
        self.loaded = False
        self.pending_tab = None
        w = self.window = _Window(self.on_destroy)
        w.setWindowTitle(store.APP_NAME + " 设置")
        w.setAttribute(Qt.WidgetAttribute.WA_DeleteOnClose)
        icon = store.INSTALL / "icon" / "AppIcon.png"
        if icon.exists():
            w.setWindowIcon(QIcon(str(icon)))
        area = QGuiApplication.primaryScreen().availableGeometry()
        w.resize(560, min(780, area.height() - 40))
        w.setMinimumSize(420, 300)
        w.move(area.x() + (area.width() - w.width()) // 2, area.y() + (area.height() - w.height()) // 2)
        lay = QVBoxLayout(w)
        lay.setContentsMargins(0, 0, 0, 0)
        self.view = web.create(self.on_message)
        lay.addWidget(self.view)
        self.view.on_loaded = self.on_load
        self.view.load_uri(store.page_url("settings.html"))

    def on_message(self, msg):
        try:
            self.pet.handle_settings(msg)
        except Exception as e:
            store.log("设置出错: " + repr(e))

    def on_load(self, ok=True):
        self.loaded = True
        self.push()
        if self.pending_tab:
            t, self.pending_tab = self.pending_tab, None
            self.show_tab(t)

    def on_destroy(self):
        self.pet.settings_window = None

    def show(self):
        self.window.show()
        self.window.raise_()
        self.window.activateWindow()

    def show_tab(self, tab):
        if not self.loaded:
            self.pending_tab = tab
            return
        web.run_js(self.view, f"showTab({store.dumps(tab)})")

    def push(self):
        if self.loaded:
            web.run_js(self.view, f"setState({store.dumps(self.pet.settings_state())})")
