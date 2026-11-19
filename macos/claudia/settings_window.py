"""设置窗口（右键「设置…」）：加载 web/settings.html。
页面 postMessage 发 {cmd, ...}，Pet.handle_settings 改完设置后用 push 把最新状态推回页面。

窗口只建一次：关掉只是收起来，下次再打开同一个。每次新建的话，网页的消息处理器和网页控件在 Python、
Objective-C 两边互相拉着，关掉后谁也释放不了，每开关一次就漏一个网页进程。"""
import AppKit

from . import store, ui, web


class SettingsWindow:
    def __init__(self, pet):
        self.pet = pet
        self.loaded = False
        self.pending_tab = None
        _l, top, _r, bottom = ui.primary_work_area()
        height = max(300, min(780, bottom - top - 40))
        style = (AppKit.NSWindowStyleMaskTitled | AppKit.NSWindowStyleMaskClosable
                 | AppKit.NSWindowStyleMaskMiniaturizable | AppKit.NSWindowStyleMaskResizable)
        w = self.window = AppKit.NSWindow.alloc().initWithContentRect_styleMask_backing_defer_(
            ((0, 0), (560, height)), style, AppKit.NSBackingStoreBuffered, False)
        w.setTitle_(store.APP_NAME + " 设置")
        w.setReleasedWhenClosed_(False)   # 关掉只是收起来，show() 时还能再拿出来
        w.setContentMinSize_((420, 300))
        w.center()
        self.web = web.Web(self.on_message, on_event=self.on_event)
        web.fill(self.web.view, w.contentView())
        self.web.load("settings.html")

    def on_message(self, msg):
        try:
            self.pet.handle_settings(msg)
        except Exception as e:
            store.log("设置出错: " + repr(e))

    def on_event(self, kind):
        if kind != "finished":
            return
        self.loaded = True
        self.push()
        if self.pending_tab:
            t, self.pending_tab = self.pending_tab, None
            self.show_tab(t)

    def show(self):
        self.push()   # 收起来的这段时间里设置可能变了（接入状态、多开名单）
        ui.activate()
        self.window.makeKeyAndOrderFront_(None)
        self.window.orderFrontRegardless()

    def show_tab(self, tab):
        if not self.loaded:
            self.pending_tab = tab
            return
        self.web.run_js(f"showTab({store.dumps(tab)})")

    def push(self):
        if self.loaded:
            self.web.run_js(f"setState({store.dumps(self.pet.settings_state())})")
