"""WebKitGTK 网页控件（对应 Windows 版的 WebView2）。

- 自定义协议 claudia://：app 主机给安装目录/web 里的网页，data 主机给数据目录里的立绘（规则见 store.resolve_sprite）。
  只在这两个地址里导航，外部网页一律不开。
- 网页里的 window.chrome.webview.postMessage(…) 是 WebView2 的接口：注入一个同名的垫片，转给外壳。
"""
import json
import mimetypes

import gi

gi.require_version("Gtk", "3.0")
gi.require_version("Gdk", "3.0")
gi.require_version("WebKit2", "4.1")
from gi.repository import Gdk, Gio, GLib, Gtk, WebKit2  # noqa: E402

from . import store  # noqa: E402

try:   # WebKit2 4.1 用的是 libsoup 3：给自定义协议的回复加响应头要它的 MessageHeaders
    gi.require_version("Soup", "3.0")
    from gi.repository import Soup as _soup  # noqa: E402
except (ValueError, ImportError):
    _soup = None

_SHIM = """
(function () {
  if (window.chrome && window.chrome.webview) return;
  window.chrome = window.chrome || {};
  window.chrome.webview = {
    postMessage: function (m) {
      try { window.webkit.messageHandlers.claudia.postMessage(JSON.stringify(m)); } catch (e) {}
    }
  };
})();
"""

_context = None


def _reply(request, data, mime):
    stream = Gio.MemoryInputStream.new_from_bytes(GLib.Bytes.new(data))
    # 网页在 claudia://app、素材在 claudia://data，不同源：<img> 不管，fetch 要响应里带 Access-Control-Allow-Origin，
    # 不然读不出来（动画视频先 fetch 成 blob 再播，2026-11-26 发现 Linux 上视频全都退回精灵图，就是这里）。
    # 加响应头要 WebKitGTK 2.36+ 的 URISchemeResponse；老版本照旧，视频退回精灵图
    if hasattr(WebKit2, "URISchemeResponse") and _soup is not None:
        try:
            resp = WebKit2.URISchemeResponse.new(stream, len(data))
            resp.set_content_type(mime)
            headers = _soup.MessageHeaders.new(_soup.MessageHeadersType.RESPONSE)
            headers.append("Access-Control-Allow-Origin", "*")
            resp.set_http_headers(headers)
            request.finish_with_response(resp)
            return
        except Exception as e:
            store.log("带响应头回复失败，改用普通回复: " + repr(e))
            stream = Gio.MemoryInputStream.new_from_bytes(GLib.Bytes.new(data))
    request.finish(stream, len(data), mime)


def _fail(request, message):
    request.finish_error(GLib.Error.new_literal(Gio.io_error_quark(), message, int(Gio.IOErrorEnum.NOT_FOUND)))


def _on_scheme(request):
    uri = request.get_uri()
    try:
        rest = uri.split("://", 1)[1]
        host, _, path = rest.partition("/")
        path = "/" + path.split("?", 1)[0].split("#", 1)[0]
        if host == store.APP_HOST:
            root = (store.INSTALL / "web").resolve()
            from urllib.parse import unquote
            full = (root / unquote(path.lstrip("/"))).resolve()
            if root not in full.parents or not full.is_file():
                return _fail(request, "not found: " + path)
            mime = mimetypes.guess_type(full.name)[0] or "application/octet-stream"
            if full.suffix == ".js":
                mime = "text/javascript"
            return _reply(request, full.read_bytes(), mime)
        if host == store.DATA_HOST:
            hit = store.resolve_sprite(path)
            if hit is None or not hit[0].is_file():
                return _fail(request, "not found: " + path)
            return _reply(request, hit[0].read_bytes(), hit[1])
        _fail(request, "unknown host")
    except Exception as e:  # 回调里漏出去的异常会让请求一直挂着
        store.log(f"读取 {uri} 失败: {e}")
        try:
            _fail(request, str(e))
        except Exception:
            pass


def context():
    """所有网页共用一个上下文（同一个进程里的桌宠页和设置页）；本地存储放在这一只自己的目录里"""
    global _context
    if _context is not None:
        return _context
    base = str(store.webview_dir())
    try:
        dm = WebKit2.WebsiteDataManager(base_data_directory=base, base_cache_directory=base + "/cache")
        _context = WebKit2.WebContext.new_with_website_data_manager(dm)
    except TypeError:
        _context = WebKit2.WebContext.get_default()
    _context.register_uri_scheme(store.SCHEME, _on_scheme)
    sec = _context.get_security_manager()
    sec.register_uri_scheme_as_secure(store.SCHEME)
    sec.register_uri_scheme_as_cors_enabled(store.SCHEME)
    _context.set_cache_model(WebKit2.CacheModel.DOCUMENT_VIEWER)
    return _context


def create(on_message, transparent=False):
    """新建一个网页控件。on_message(dict) 收网页 postMessage 过来的消息"""
    ucm = WebKit2.UserContentManager()
    ucm.add_script(WebKit2.UserScript.new(_SHIM, WebKit2.UserContentInjectedFrames.TOP_FRAME,
                                          WebKit2.UserScriptInjectionTime.START, None, None))

    def received(_m, result):
        try:
            v = result.get_js_value() if hasattr(result, "get_js_value") else result
            msg = json.loads(v.to_string())
        except Exception:
            return
        if isinstance(msg, dict):
            try:
                on_message(msg)
            except Exception as e:
                store.log("处理网页消息出错: " + repr(e))

    ucm.connect("script-message-received::claudia", received)
    ucm.register_script_message_handler("claudia")
    view = WebKit2.WebView(web_context=context(), user_content_manager=ucm)
    s = view.get_settings()
    s.set_enable_developer_extras(bool(__import__("os").environ.get("CLAUDIA_DEBUG")))
    s.set_enable_write_console_messages_to_stdout(bool(__import__("os").environ.get("CLAUDIA_DEBUG")))
    s.set_hardware_acceleration_policy(WebKit2.HardwareAccelerationPolicy.NEVER if __import__("os").environ.get("CLAUDIA_NO_GPU") else WebKit2.HardwareAccelerationPolicy.ON_DEMAND)
    if transparent:
        view.set_background_color(Gdk.RGBA(0, 0, 0, 0))

    def decide(_v, decision, kind):
        if kind == WebKit2.PolicyDecisionType.NAVIGATION_ACTION:
            uri = decision.get_navigation_action().get_request().get_uri()
            if not uri.startswith(f"{store.SCHEME}://{store.APP_HOST}/"):
                decision.ignore()
                return True
        elif kind == WebKit2.PolicyDecisionType.NEW_WINDOW_ACTION:
            decision.ignore()
            return True
        return False

    view.connect("decide-policy", decide)
    view.connect("context-menu", lambda *a: True)   # 不要网页自带的右键菜单
    return view


def run_js(view, code):
    if view is None:
        return
    try:
        if hasattr(view, "evaluate_javascript"):
            view.evaluate_javascript(code, -1, None, None, None, None, None)
        else:
            view.run_javascript(code, None, None, None)
    except Exception as e:
        store.log("JS 出错: " + str(e))


def eval_js(view, code, callback):
    """执行脚本并把结果（JSON 能表示的值）交给 callback；出错交 None"""
    def done(v, res):
        value = None
        try:
            if hasattr(v, "evaluate_javascript_finish"):
                r = v.evaluate_javascript_finish(res)
            else:
                r = v.run_javascript_finish(res).get_js_value()
            text = r.to_json(0)
            value = json.loads(text) if text else None
        except Exception as e:
            store.log("JS 求值出错: " + str(e))
        callback(value)

    try:
        if hasattr(view, "evaluate_javascript"):
            view.evaluate_javascript(code, -1, None, None, None, done)
        else:
            view.run_javascript(code, None, done)
    except Exception as e:
        store.log("JS 出错: " + str(e))
        callback(None)
