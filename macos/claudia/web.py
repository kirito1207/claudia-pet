"""WKWebView 网页控件（对应 Windows 版的 WebView2、Linux 版的 WebKitGTK）。

- 自定义协议 claudia://：app 主机给安装目录/web 里的网页，data 主机给数据目录里的立绘（规则见 store.resolve_url）。
  只在 claudia://app/ 里导航，外部网页一律不开。
- 网页里的 window.chrome.webview.postMessage(…) 是 WebView2 的接口：注入一个同名的垫片，转给外壳。
"""
import json
import os
import traceback

import AppKit
import Foundation
import objc
import WebKit

from . import store

_SHIM = """
(function () {
  if (window.chrome && window.chrome.webview) return;
  window.chrome = window.chrome || {};
  window.chrome.webview = {
    postMessage: function (m) {
      try { window.webkit.messageHandlers.claudia.postMessage(JSON.stringify(m)); } catch (e) {}
    }
  };
  // 不要网页自带的右键菜单（重新载入、检查元素那些）；输入框里留着，方便粘贴
  document.addEventListener('contextmenu', function (e) {
    var t = e.target;
    if (!(t && t.closest && t.closest('input, textarea, [contenteditable]'))) e.preventDefault();
  }, true);
})();
"""

DEBUG = bool(os.environ.get("CLAUDIA_DEBUG"))


def _protocols(*names):
    """声明遵守 WebKit 的协议：PyObjC 就按协议里的签名调这些方法。万一系统里找不到某个协议，少声明一个也能用"""
    out = []
    for n in names:
        try:
            out.append(objc.protocolNamed(n))
        except Exception:
            pass
    return out


class ClaudiaSchemeHandler(Foundation.NSObject, protocols=_protocols("WKURLSchemeHandler")):
    """WKURLSchemeHandler：claudia:// 的请求在这里从本地文件回"""

    def webView_startURLSchemeTask_(self, webview, task):
        url = task.request().URL()
        text = str(url.absoluteString()) if url is not None else ""
        try:
            hit = store.resolve_url(text)
            data = hit[0].read_bytes() if hit else None
        except Exception as e:
            store.log(f"读取 {text} 失败: {e}")
            hit, data = None, None
        try:
            if data is None:
                resp = Foundation.NSHTTPURLResponse.alloc().initWithURL_statusCode_HTTPVersion_headerFields_(
                    url, 404, "HTTP/1.1", {"Content-Type": "text/plain; charset=utf-8"})
                task.didReceiveResponse_(resp)
                task.didFinish()
                return
            # 精灵图每张约 2 MB：干活时「思考」「敲命令」来回换，让 WebKit 在内存里留 10 分钟，不用每次都从这里重新读
            headers = {"Content-Type": hit[1], "Content-Length": str(len(data)),
                       "Access-Control-Allow-Origin": "*", "Cache-Control": "max-age=600"}
            resp = Foundation.NSHTTPURLResponse.alloc().initWithURL_statusCode_HTTPVersion_headerFields_(
                url, 200, "HTTP/1.1", headers)
            task.didReceiveResponse_(resp)
            task.didReceiveData_(Foundation.NSData.dataWithBytes_length_(data, len(data)))
            task.didFinish()
        except Exception as e:   # 网页已经不要这个请求了（换页、关窗口）：WebKit 会抛异常，忽略
            if DEBUG:
                store.log(f"回 {text} 时出错: {e!r}")

    def webView_stopURLSchemeTask_(self, webview, task):
        pass


class ClaudiaMessageHandler(Foundation.NSObject, protocols=_protocols("WKScriptMessageHandler")):
    """WKScriptMessageHandler：收网页 postMessage 过来的消息（垫片里已经转成 JSON 字符串）"""

    def userContentController_didReceiveScriptMessage_(self, controller, message):
        try:
            msg = json.loads(str(message.body()))
        except Exception:
            return
        if isinstance(msg, dict):
            try:
                self.fn(msg)
            except Exception:
                store.log("处理网页消息出错: " + traceback.format_exc()[-1500:])


class ClaudiaNavDelegate(Foundation.NSObject, protocols=_protocols("WKNavigationDelegate")):
    """WKNavigationDelegate：开始载入 / 载入完成 / 网页进程退出，交给 on_event("started" | "finished" | "crashed")"""

    @objc.python_method
    def _emit(self, kind):
        try:
            self.fn(kind)
        except Exception:
            store.log("处理网页事件出错: " + traceback.format_exc()[-1500:])

    def webView_didStartProvisionalNavigation_(self, webview, navigation):
        self._emit("started")

    def webView_didFinishNavigation_(self, webview, navigation):
        self._emit("finished")

    def webView_didFailProvisionalNavigation_withError_(self, webview, navigation, error):
        store.log("网页没打开: " + str(error.localizedDescription() if error is not None else "?"))

    def webView_didFailNavigation_withError_(self, webview, navigation, error):
        store.log("网页载入出错: " + str(error.localizedDescription() if error is not None else "?"))

    def webViewWebContentProcessDidTerminate_(self, webview):
        self._emit("crashed")

    def webView_decidePolicyForNavigationAction_decisionHandler_(self, webview, action, handler):
        url = action.request().URL()
        text = str(url.absoluteString()) if url is not None else ""
        allow = text.startswith(f"{store.SCHEME}://{store.APP_HOST}/") or text == "about:blank"
        handler(WebKit.WKNavigationActionPolicyAllow if allow else WebKit.WKNavigationActionPolicyCancel)


_scheme_handler = None


class Web:
    """一个网页控件。on_message(dict) 收网页 postMessage 过来的消息，on_event(kind) 收载入事件"""

    def __init__(self, on_message, on_event=None, transparent=False):
        global _scheme_handler
        if _scheme_handler is None:
            _scheme_handler = ClaudiaSchemeHandler.alloc().init()
        cfg = WebKit.WKWebViewConfiguration.alloc().init()
        cfg.setURLSchemeHandler_forURLScheme_(_scheme_handler, store.SCHEME)
        ucc = cfg.userContentController()
        ucc.addUserScript_(WebKit.WKUserScript.alloc().initWithSource_injectionTime_forMainFrameOnly_(
            _SHIM, WebKit.WKUserScriptInjectionTimeAtDocumentStart, True))
        self.handler = ClaudiaMessageHandler.alloc().init()
        self.handler.fn = on_message
        ucc.addScriptMessageHandler_name_(self.handler, "claudia")
        if DEBUG:
            try:
                cfg.preferences().setValue_forKey_(True, "developerExtrasEnabled")
            except Exception:
                pass
        self.view = WebKit.WKWebView.alloc().initWithFrame_configuration_(((0, 0), (100, 100)), cfg)
        self.nav = ClaudiaNavDelegate.alloc().init()
        self.nav.fn = on_event or (lambda kind: None)
        self.view.setNavigationDelegate_(self.nav)   # 弱引用：自己留着 self.nav
        if DEBUG:
            try:
                self.view.setInspectable_(True)   # macOS 13.3 起：Safari「开发」菜单里能检查
            except Exception:
                pass
        if transparent:
            self.make_transparent()
            # 桌宠窗口刚打开时是完全透明的，系统可能把它当成「被挡住了」，WebKit 就会把动画、定时器停掉，
            # 网页报不来能点的区域。桌宠页自己会按 setPageHidden 停动画，这里关掉 WebKit 的这个判断
            try:
                self.view._setWindowOcclusionDetectionEnabled_(False)
            except Exception:
                pass

    def make_transparent(self):
        # WKWebView 没有公开的「透明背景」开关，大家都用这个 KVC 键（对应内部的 _setDrawsBackground:）
        for key in ("drawsBackground", "drawsTransparentBackground"):
            try:
                self.view.setValue_forKey_(key == "drawsTransparentBackground", key)
                return
            except Exception:
                continue
        store.log("没能把网页背景设成透明")

    def load(self, page):
        url = Foundation.NSURL.URLWithString_(store.page_url(page))
        self.view.loadRequest_(Foundation.NSURLRequest.requestWithURL_(url))

    def reload(self):
        self.view.reload()

    def set_zoom(self, z):
        """网页缩放（和浏览器里 ⌘+ 一样：CSS 像素变大，布局宽度 = 控件宽度 / 缩放）。macOS 11 起有"""
        try:
            self.view.setPageZoom_(float(z))
        except AttributeError:
            store.log("这个系统的 WKWebView 不能缩放网页（要 macOS 11 以上），人物大小调不了")

    def run_js(self, code):
        """执行脚本，不要结果。末尾补一个 void 0：最后一个表达式是函数、元素这类东西时 WebKit 会报「结果类型不支持」"""
        try:
            self.view.evaluateJavaScript_completionHandler_(code + "\n;void 0", None)
        except Exception as e:
            store.log("JS 出错: " + str(e))

    def eval_js(self, code, callback):
        """求一个表达式的值（JSON 能表示的），交给 callback；出错交 None"""
        wrapped = "(function(){ var v = (" + code + "); return v === undefined ? null : JSON.stringify(v); })()"

        def done(result, error):
            value = None
            if error is not None:
                store.log("JS 求值出错: " + str(error.localizedDescription()))
            elif result is not None and not isinstance(result, Foundation.NSNull):
                try:
                    value = json.loads(str(result))
                except ValueError:
                    pass
            try:
                callback(value)
            except Exception:
                store.log("处理 JS 结果出错: " + traceback.format_exc()[-1500:])

        try:
            self.view.evaluateJavaScript_completionHandler_(wrapped, done)
        except Exception as e:
            store.log("JS 出错: " + str(e))
            callback(None)


def fill(view, parent):
    """把 view 铺满 parent，跟着它一起变大小"""
    view.setFrame_(parent.bounds())
    view.setAutoresizingMask_(AppKit.NSViewWidthSizable | AppKit.NSViewHeightSizable)
    parent.addSubview_(view)
