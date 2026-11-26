"""网页控件：Qt WebEngine（Chromium 内核，和 Windows 版的 WebView2 同一个内核）。

2026-11-27 从 WebKitGTK 换过来：这台虚拟机上实测，WebKitGTK 经 GStreamer 解透明 VP9 时好时坏（派活每次失败）、
连着换视频会整页卡死，同一个页面播视频 CPU 23～28%；Qt WebEngine 32/32、157 段连播都正常，CPU 5～6%。

- 自定义协议 claudia://：app 主机给安装目录/web 里的网页，data 主机给数据目录里的立绘和视频（规则见 store.resolve_sprite）。
  只在这两个地址里导航，外部网页一律不开。
- 网页里的 window.chrome.webview.postMessage(…) 是 WebView2 的接口：注入一个同名的垫片，经 console 消息转给外壳
  （带一个不会和普通日志撞上的前缀；不用 QWebChannel，免得网页一加载就发消息时通道还没连上）。
- 协议要在 QApplication 之前注册：main.py 启动时先调 register_scheme()。
"""
import json
import mimetypes
import os
from urllib.parse import unquote

from PySide6.QtCore import QBuffer, QByteArray, QIODevice, Qt, QUrl
from PySide6.QtGui import QColor
from PySide6.QtWebEngineCore import (QWebEnginePage, QWebEngineProfile, QWebEngineScript, QWebEngineSettings,
                                     QWebEngineUrlRequestJob, QWebEngineUrlScheme, QWebEngineUrlSchemeHandler)
from PySide6.QtWebEngineWidgets import QWebEngineView

from . import store

_PREFIX = "\u0001claudia-msg:"
_SHIM = """
(function () {
  if (window.chrome && window.chrome.webview) return;
  window.chrome = window.chrome || {};
  window.chrome.webview = {
    postMessage: function (m) { try { console.debug('%s' + JSON.stringify(m)); } catch (e) {} }
  };
})();
""" % _PREFIX.replace("\u0001", "\\u0001")


def register_scheme():
    """claudia:// 当安全、可跨主机读取（网页在 app 主机、素材在 data 主机，视频要 fetch 成 blob）的本地协议。QApplication 之前调"""
    s = QWebEngineUrlScheme(store.SCHEME.encode())
    s.setSyntax(QWebEngineUrlScheme.Syntax.Host)
    s.setFlags(QWebEngineUrlScheme.Flag.SecureScheme | QWebEngineUrlScheme.Flag.LocalAccessAllowed
               | QWebEngineUrlScheme.Flag.CorsEnabled | QWebEngineUrlScheme.Flag.FetchApiAllowed
               | QWebEngineUrlScheme.Flag.ContentSecurityPolicyIgnored)
    QWebEngineUrlScheme.registerScheme(s)


class _Scheme(QWebEngineUrlSchemeHandler):
    def requestStarted(self, job):
        url = job.requestUrl()
        try:
            host, path = url.host(), unquote(url.path())
            if host == store.APP_HOST:
                root = (store.INSTALL / "web").resolve()
                full = (root / path.lstrip("/")).resolve()
                if root not in full.parents or not full.is_file():
                    return job.fail(QWebEngineUrlRequestJob.Error.UrlNotFound)
                mime = "text/javascript" if full.suffix == ".js" else (mimetypes.guess_type(full.name)[0] or "application/octet-stream")
                return self._reply(job, full.read_bytes(), mime)
            if host == store.DATA_HOST:
                hit = store.resolve_sprite(path)
                if hit is None or not hit[0].is_file():
                    return job.fail(QWebEngineUrlRequestJob.Error.UrlNotFound)
                return self._reply(job, hit[0].read_bytes(), hit[1])
            job.fail(QWebEngineUrlRequestJob.Error.UrlNotFound)
        except Exception as e:  # 漏出去的异常会让请求一直挂着
            store.log(f"读取 {url.toString()} 失败: {e}")
            try:
                job.fail(QWebEngineUrlRequestJob.Error.RequestFailed)
            except Exception:
                pass

    @staticmethod
    def _reply(job, data, mime):
        # 网页在 claudia://app、素材在 claudia://data，不同源：fetch 要响应里带 Access-Control-Allow-Origin（Qt 6.6 起能加响应头）
        if hasattr(job, "setAdditionalResponseHeaders"):
            try:
                job.setAdditionalResponseHeaders({QByteArray(b"Access-Control-Allow-Origin"): QByteArray(b"*")})
            except Exception:
                pass
        buf = QBuffer(parent=job)
        buf.setData(QByteArray(data))
        buf.open(QIODevice.OpenModeFlag.ReadOnly)
        job.reply(mime.encode(), buf)


_profile = None
_handler = None


def profile():
    """所有网页共用一个配置（同一个进程里的桌宠页和设置页）；本地存储放在这一只自己的目录里"""
    global _profile, _handler
    if _profile is not None:
        return _profile
    base = str(store.webview_dir())
    _profile = QWebEngineProfile("claudia-" + store.pet_id if getattr(store, "pet_id", None) else "claudia")
    _profile.setPersistentStoragePath(base)
    _profile.setCachePath(os.path.join(base, "cache"))
    _handler = _Scheme()
    _profile.installUrlSchemeHandler(store.SCHEME.encode(), _handler)
    shim = QWebEngineScript()
    shim.setName("claudia-shim")
    shim.setSourceCode(_SHIM)
    shim.setInjectionPoint(QWebEngineScript.InjectionPoint.DocumentCreation)
    shim.setWorldId(QWebEngineScript.ScriptWorldId.MainWorld)
    shim.setRunsOnSubFrames(False)
    _profile.scripts().insert(shim)
    return _profile


class _Page(QWebEnginePage):
    def __init__(self, on_message, parent=None):
        super().__init__(profile(), parent)
        self.on_message = on_message

    def javaScriptConsoleMessage(self, level, message, line, source):
        if message.startswith(_PREFIX):
            try:
                msg = json.loads(message[len(_PREFIX):])
            except ValueError:
                return
            if isinstance(msg, dict):
                try:
                    self.on_message(msg)
                except Exception as e:
                    store.log("处理网页消息出错: " + repr(e))
        elif os.environ.get("CLAUDIA_DEBUG"):
            print(f"[网页] {message} ({source}:{line})", flush=True)

    def acceptNavigationRequest(self, url, nav_type, is_main_frame):
        # 只在自己的页面里导航；外部网页一律不开
        return url.toString().startswith(f"{store.SCHEME}://{store.APP_HOST}/")

    def createWindow(self, _type):
        return None


class WebView(QWebEngineView):
    """沿用原来 WebKitGTK 封装的几个方法名，外壳少改：load_uri / set_zoom_level / reload。
    on_started()：开始加载；on_loaded(ok)：加载完；on_crash(reason)：网页进程挂了"""

    def __init__(self, on_message, transparent=False, parent=None):
        super().__init__(parent)
        page = _Page(on_message, self)
        self.setPage(page)
        self.setContextMenuPolicy(Qt.ContextMenuPolicy.NoContextMenu)   # 不要网页自带的右键菜单
        s = page.settings()
        s.setAttribute(QWebEngineSettings.WebAttribute.PlaybackRequiresUserGesture, False)
        s.setAttribute(QWebEngineSettings.WebAttribute.ShowScrollBars, False)
        if transparent:
            page.setBackgroundColor(QColor(0, 0, 0, 0))
            self.setAttribute(Qt.WidgetAttribute.WA_TranslucentBackground)
            self.setStyleSheet("background: transparent")
        self.on_started = None
        self.on_loaded = None
        self.on_crash = None
        page.loadStarted.connect(lambda: self.on_started and self.on_started())
        page.loadFinished.connect(lambda ok: self.on_loaded and self.on_loaded(ok))
        page.renderProcessTerminated.connect(lambda status, code: self.on_crash and self.on_crash(f"{status} {code}"))

    def load_uri(self, uri):
        self.setUrl(QUrl(uri))

    def set_zoom_level(self, z):
        self.setZoomFactor(max(0.25, min(5.0, float(z))))

    def reload(self):
        self.page().triggerAction(QWebEnginePage.WebAction.Reload)


def create(on_message, transparent=False):
    """新建一个网页控件。on_message(dict) 收网页 postMessage 过来的消息"""
    return WebView(on_message, transparent)


def run_js(view, code):
    if view is None:
        return
    try:
        view.page().runJavaScript(code, 0)
    except Exception as e:
        store.log("JS 出错: " + str(e))


def eval_js(view, code, callback):
    """执行脚本并把结果（JSON 能表示的值）交给 callback；出错交 None。
    结果先在网页里 JSON.stringify 再取回，和原来 WebKitGTK 版一样拿到的是普通的 dict / list / 数字"""
    # 用 eval 执行：表达式、多条语句（调试命令）都行，取最后一个表达式的值
    wrapped = ("(function(){ try { const v = (0, eval)(" + json.dumps(code) + "); return v === undefined ? null : JSON.stringify(v) }"
               " catch (e) { return JSON.stringify({__error: String(e)}) } })()")

    def done(text):
        value = None
        try:
            value = json.loads(text) if isinstance(text, str) else None
            if isinstance(value, dict) and "__error" in value and len(value) == 1:
                store.log("JS 求值出错: " + str(value["__error"]))
                value = None
        except Exception as e:
            store.log("JS 求值出错: " + str(e))
        callback(value)

    try:
        view.page().runJavaScript(wrapped, 0, done)
    except Exception as e:
        store.log("JS 出错: " + str(e))
        callback(None)
