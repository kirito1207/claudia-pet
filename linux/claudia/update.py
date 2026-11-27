"""检查更新（2026-11-28 用户定：默认开，只提醒、不自动下载）。Windows 版是 app/src/UpdateCheck.cs，两边规则一样。

只向 GitHub 问一句「最新发布的版本号是多少」（releases/latest），不带任何用户信息，不上传数据。
由负责人做（一台电脑只问一次）：一天最多一次，当上负责人一分钟后才第一次问；设置「关于」里可以「现在检查」。
结果记在数据目录的 update.json：{checkedAt 上次问的时间（秒）, latest 最新版本号, url 下载页, error 上次没问成的原因,
notified 已经让她说过的版本}。每只的设置页都读这个文件显示；有新版本时负责人让她在气泡里说一次（同一个版本只说一次）。
"""
import json
import threading
import urllib.request

from . import store

API = "https://api.github.com/repos/kirito1207/claudia-pet/releases/latest"
RELEASES_PAGE = "https://github.com/kirito1207/claudia-pet/releases/latest"
INTERVAL_S, FIRST_DELAY_S = 24 * 3600, 60


def _parts(v):
    v = str(v or "").strip().lstrip("vV")
    for sep in "-+ ":
        v = v.split(sep, 1)[0]
    parts = v.split(".")
    if not parts or len(parts) > 4 or not all(p.isdigit() for p in parts):
        return None
    return [int(p) for p in parts]


def newer(latest, current):
    """latest 比 current 新：按「数字.数字.数字」比，前面的 v、后面的 -beta 之类不看；认不出来的不算新"""
    a, b = _parts(latest), _parts(current)
    if a is None or b is None:
        return False
    n = max(len(a), len(b))
    return a + [0] * (n - len(a)) > b + [0] * (n - len(b))


def parse(text):
    """从 GitHub 的回答里取 (版本号, 下载页)；认不出来返回 None"""
    try:
        d = json.loads(text)
    except ValueError:
        return None
    tag = d.get("tag_name") if isinstance(d, dict) else None
    if not isinstance(tag, str) or _parts(tag) is None:
        return None
    url = d.get("html_url")
    if not (isinstance(url, str) and url.startswith("https://github.com/")):
        url = RELEASES_PAGE
    return tag.strip().lstrip("vV"), url


def state_file():
    return store.DATA / "update.json"


def load():
    return store.read_json(state_file()) or {}


def save(state):
    try:
        store.write_json(state_file(), state)
    except OSError as e:
        store.log("记不下检查更新的结果: " + str(e))


def fetch(done):
    """后台线程问一次 GitHub，问完在那个线程上调 done(release, error)：release 是 (版本, 下载页) 或 None。
    调用方自己回主线程（GLib.idle_add）"""
    def run():
        try:
            req = urllib.request.Request(API, headers={"User-Agent": "claudia-pet",   # GitHub 要求带；只写程序名，不带用户信息
                                                       "Accept": "application/vnd.github+json"})
            with urllib.request.urlopen(req, timeout=15) as r:
                release = parse(r.read(1_000_000).decode("utf-8", "replace"))
            done(release, None if release else "没认出最新版本号")
        except Exception as e:   # 网络、证书、HTTP 错误都一样：记下原因，下次再问
            code = getattr(e, "code", None)
            done(None, f"GitHub 回了 {code}" if code else f"没连上 GitHub（{e}）")
    threading.Thread(target=run, daemon=True, name="update-check").start()


def for_page(checking):
    """给设置页的一份：最新版本、比现在的新不新、下载页、上次什么时候问的、上次没问成的原因、正在问"""
    d = load()
    latest = d.get("latest") if isinstance(d.get("latest"), str) else None
    return {"latest": latest, "newer": bool(latest and newer(latest, store.version())),
            "url": d.get("url") if isinstance(d.get("url"), str) else RELEASES_PAGE,
            "checkedAt": d.get("checkedAt") if isinstance(d.get("checkedAt"), (int, float)) else 0,
            "error": d.get("error") if isinstance(d.get("error"), str) else None, "checking": checking}
