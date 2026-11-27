"""目录、设置、日志、内置素材同步、角色清单（对应 Windows 版 app/src/Store.cs）。

数据目录和 Windows 版一样是 ~/.claudia：Claude Code 插件往 ~/.claudia/state 写状态，两边的插件代码共用。
"""
import hashlib
import json
import os
import shutil
import sys
import time
import urllib.parse
import uuid
from pathlib import Path

APP_NAME = "claudia-pet"
# 网页从两个「主机」读文件：claudia://app/（安装目录/web）和 claudia://data/characters/…（安装目录或数据目录的立绘），见 web.py
SCHEME = "claudia"
APP_HOST, DATA_HOST = "app", "data"
ANIM_DIR = "anim"


def _env(name):
    v = os.environ.get(name, "").strip()
    return v or None


# 安装目录：linux/claudia/store.py 往上两级（开发时就是仓库根目录，装好后是 ~/.local/share/claudia-pet）
INSTALL = Path(__file__).resolve().parent.parent.parent
DATA = Path(_env("CLAUDIA_DATA_DIR") or Path.home() / ".claudia")
STATE = Path(_env("CLAUDIA_STATE_DIR") or DATA / "state")
CHARACTERS = DATA / "characters"
RUN = DATA / "run"   # 单实例锁和控制用的 socket


def page_url(page):
    return f"{SCHEME}://{APP_HOST}/{page}"


def version():
    try:
        return (INSTALL / "VERSION").read_text(encoding="utf-8").strip()
    except OSError:
        return "0"


def launcher():
    """自己的启动命令（多开、--ensure、MCP 配置里都用它）。装好后是 ~/.local/bin/claudia-pet，开发时是仓库里的 linux/claudia-pet"""
    exe = _env("CLAUDIA_EXE")
    if exe:
        return exe
    return str(INSTALL / "linux" / "claudia-pet")


# ---------------- JSON / 日志 ----------------
def read_text(path):
    try:
        return Path(path).read_text(encoding="utf-8")
    except (OSError, UnicodeDecodeError):
        return None


def read_json(path):
    text = read_text(path)
    if text is None:
        return None
    try:
        v = json.loads(text.lstrip("﻿"))
    except ValueError:
        return None
    return v if isinstance(v, dict) else None


def write_json(path, data):
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    tmp = path.with_name(path.name + ".tmp")
    tmp.write_text(json.dumps(data, ensure_ascii=False), encoding="utf-8")
    os.replace(tmp, path)


def dumps(v):
    return json.dumps(v, ensure_ascii=False)


def changed_state(path, stamps, *, max_bytes, not_before=None):
    """Read a changed plugin file. A partial write is retried on the next poll."""
    try:
        stat = path.stat()
    except OSError:
        stamps.pop(path, None)
        return None
    if stat.st_size > max_bytes or not_before is not None and stat.st_mtime < not_before:
        stamps.pop(path, None)
        return None
    stamp = stat.st_mtime_ns, stat.st_size
    if stamps.get(path) == stamp:
        return None
    value = read_json(path)
    if value is None:
        return None
    stamps[path] = stamp
    return value, stat.st_mtime


pet_id = "main"
join_token = None   # 负责人加这一只时给的标记（--join），报到时带回去，好和取消前叫出来的旧进程分开


def is_main():
    return pet_id == "main"


def log(text):
    try:
        DATA.mkdir(parents=True, exist_ok=True)
        f = DATA / "linux.log"
        if f.exists() and f.stat().st_size > 1_000_000:
            f.unlink()
        with open(f, "a", encoding="utf-8") as out:
            out.write(time.strftime("%Y-%m-%d %H:%M:%S ") + ("" if is_main() else f"[{pet_id}] ") + str(text) + "\n")
    except OSError:
        pass
    if os.environ.get("CLAUDIA_DEBUG"):
        print(text, file=sys.stderr)


# ---------------- 多开（2026-11-27 起每只平级）----------------
# 每只是一个独立进程，编号只用来找它自己的设置和网页数据（main 的在数据目录最上层——原来第一只的，不用搬；别的在 pets/<编号>/）。
# 界面上的「第几只」是它在名单里的位置；谁是负责人见 pethub.py
MAX_PETS = 5


def pet_dir():
    return DATA if is_main() else DATA / "pets" / pet_id


def webview_dir():
    return pet_dir() / "webkit"


def valid_pet_id(i):
    return i == "main" or (isinstance(i, str) and 1 <= len(i) <= 8 and i.isdigit())


PETS_FILE = DATA / "pets.json"


def roster():
    """名单：现在有哪几只、按什么顺序（第几只）。旧格式 {ids: [多开的几只]} 前面补上原来的第一只"""
    ids = try_roster()
    return ids if ids is not None else []


def try_roster():
    """读名单，分清「确实是空的」（没有这个文件，返回 []）和「读不到」（正被写、坏了，返回 None）。
    负责人只在接手时读一次，读不到不能当成空名单，不然连上来的几只都会被当成已经送走（2026-11-27 Codex 复审）"""
    if not PETS_FILE.exists():
        return []
    d = read_json(PETS_FILE)
    if d is None:
        return None
    ids = d.get("pets") if isinstance(d.get("pets"), list) else (["main"] + d["ids"]) if isinstance(d.get("ids"), list) else []
    out = []
    for i in ids:
        if valid_pet_id(i) and i not in out:
            out.append(i)
    return out[:MAX_PETS]


def save_roster(ids):
    """只有负责人写"""
    write_json(PETS_FILE, {"pets": list(ids)})


_physics_ranges = None


def physics_ranges():
    """物理参数的范围、默认值、步长：三端共用 web/physics.json（2026-11-27 Codex 复审：原来 Windows、Linux、设置页各写一份）。
    编号 → {min, max, default, step}；读不到的项跳过"""
    global _physics_ranges
    if _physics_ranges is None:
        raw = read_json(INSTALL / "web" / "physics.json")
        table = {}
        for k, r in (raw.items() if isinstance(raw, dict) else ()):
            try:
                table[k] = {f: float(r[f]) for f in ("min", "max", "default", "step")}
            except (TypeError, KeyError, ValueError):
                log(f"web/physics.json 里 {k} 这一项不对，跳过")
        if not all(k in table for k in ("throwPower", "gravity", "restitution", "groundFriction")):
            log("web/physics.json 读不到或不全：拖动直接跟手、不扔不弹（安装不完整？）")
        _physics_ranges = table
    return _physics_ranges


# 所有可用的编号，按先用谁排：main（它的设置在最上层），再 2、3……比名额多一个：刚送走的那只挥手时占着编号也能再加
PET_IDS = ["main"] + [str(n) for n in range(2, MAX_PETS + 2)]


def free_pet_id(usable):
    """新来的一只用哪个编号：按 PET_IDS 的顺序第一个 usable 的；都不行返回 None。启动时和「再来一只」都走这里"""
    return next((i for i in PET_IDS if usable(i)), None)


# ---------------- 所有桌宠共用的设置（2026-11-27）----------------
# 隐藏快捷键、三项任务通知、鼠标穿透、待在桌面上时的层级不跟着某一只走：放在 app.json。第一次从原来第一只的设置里搬过来
APP_FILE = DATA / "app.json"
APP_KEYS = ("hideHotkey", "notifyResults", "notifyWaiting", "notifyBackgroundOnly", "clickThrough", "deskLayer", "checkUpdates")
app = {}


def load_app():
    global app
    d = read_json(APP_FILE)
    if d is None:
        old = read_json(DATA / "settings.json") or {}
        d = {k: old[k] for k in APP_KEYS if k in old}
        try:
            write_json(APP_FILE, d)
        except OSError as e:
            log("保存共用设置失败: " + str(e))
    app = d


def save_app(d=None):
    try:
        write_json(APP_FILE, app if d is None else d)
        return True
    except OSError as e:
        log("保存共用设置失败: " + str(e))
        return False


def set_app(key, text):
    """负责人改一项共用设置（别的几只把「哪一项、改成什么」交给它，只有它写 app.json，免得互相盖掉）。
    值按文字传：开关是 true / false。返回 (成没成, 没成的原因)；文件先写成功才换内存里的，写不进去就保留旧设置"""
    if key in ("notifyResults", "notifyWaiting", "notifyBackgroundOnly", "clickThrough", "checkUpdates"):
        if text not in ("true", "false"):
            return False, "这一项设置没认出来"
        value = text == "true"
    elif key == "hideHotkey" and text in ("p", "h", "off"):
        value = text
    elif key == "deskLayer" and text in ("top", "yield", "normal"):
        value = text
    else:
        return False, "这一项设置没认出来"
    if not save_app(dict(app, **{key: value})):
        return False, "设置没能保存，还是原来的样子"
    app[key] = value
    return True, ""


def app_on(key):
    """没写过的开关：通知、检查更新默认开，鼠标穿透默认关"""
    v = app.get(key)
    return v if isinstance(v, bool) else key != "clickThrough"


# ---------------- 设置 ----------------
settings = {}


def settings_file():
    return pet_dir() / "settings.json"


def load_settings():
    global settings
    own = read_json(settings_file())
    if own is None and not is_main():
        # 新来的一只：先照第一只的设置（大小、手感这些），位置、待在哪、名字用自己的
        own = read_json(DATA / "settings.json") or {}
        for k in ("follow", "desk", "mode", "names"):
            own.pop(k, None)
    settings = own or {}
    if not isinstance(settings.get("names"), dict):
        settings["names"] = {}


def save_settings():
    global _save_timer
    if _save_timer:
        from gi.repository import GLib
        GLib.source_remove(_save_timer)
        _save_timer = None
    try:
        write_json(settings_file(), settings)
    except OSError as e:
        log("保存设置失败: " + str(e))


# 拖大小滑块、缩放动画每一帧都会改设置（大小、相对位置）：内存里马上改，文件等停下 0.4 秒再写一次（2026-11-27）。
# 计时器在主循环里跑，和改设置的代码同一个线程；退出前 flush_settings 补写
_save_timer = None


def save_settings_soon():
    global _save_timer
    from gi.repository import GLib
    if _save_timer:
        GLib.source_remove(_save_timer)

    def due():
        global _save_timer
        _save_timer = None
        save_settings()
        return False

    _save_timer = GLib.timeout_add(400, due)


def flush_settings():
    if _save_timer:
        save_settings()


def flag(key):
    return settings.get(key) is True


def setting_on(key):
    """没存过算开"""
    v = settings.get(key)
    return not isinstance(v, bool) or v


def names():
    return settings["names"]


# ---------------- 运行方式：跟着 Claude / 独立运行（所有几只共用 run.json） ----------------
RUN_FILE = DATA / "run.json"
_standalone = False
_run_stamp = None
_run_checked = 0.0


def has_run_mode():
    return RUN_FILE.exists()


def standalone():
    """按 run.json 的修改时间缓存，最多一秒看一次：别的那只改了，这一只一秒内跟上"""
    global _standalone, _run_stamp, _run_checked
    now = time.monotonic()
    if now - _run_checked < 1:
        return _standalone
    _run_checked = now
    try:
        stamp = RUN_FILE.stat().st_mtime_ns if RUN_FILE.exists() else None
        if stamp != _run_stamp:
            _run_stamp = stamp
            d = read_json(RUN_FILE)
            _standalone = bool(d) and d.get("mode") == "standalone"
    except OSError:
        pass
    return _standalone


def set_standalone(on):
    global _standalone, _run_checked
    try:
        write_json(RUN_FILE, {"mode": "standalone" if on else "claude"})
    except OSError as e:
        log("保存运行方式失败: " + str(e))
    _standalone = on
    _run_checked = 0.0


# ---------------- 内置素材 ----------------
_character_dirs = {}


def character_dir(cid):
    return _character_dirs.get(cid, CHARACTERS / cid)


def _same_file(src, dst):
    try:
        a, b = src.stat(), dst.stat()
    except OSError:
        return False
    return a.st_size == b.st_size and int(a.st_mtime) == int(b.st_mtime)


def _digest(path):
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(256 * 1024), b""):
            digest.update(block)
    return digest.digest()


def _same_resources(src, dst):
    if src.is_symlink() or dst.is_symlink() or os.path.ismount(dst) or not dst.is_dir():
        return False
    originals = {p.name: p for p in src.iterdir() if not (p.is_dir() and p.name.lower() == "raw")}
    copies = {p.name: p for p in dst.iterdir()}
    if originals.keys() != copies.keys():
        return False
    for name, copy in copies.items():
        original = originals[name]
        if original.is_symlink() or copy.is_symlink() or original.is_dir() != copy.is_dir():
            return False
        if not copy.is_dir() and (
            not original.is_file() or not copy.is_file() or original.stat().st_size != copy.stat().st_size
        ):
            return False
    for name, copy in copies.items():
        original = originals[name]
        if copy.is_dir():
            if not _same_resources(original, copy):
                return False
        elif _digest(original) != _digest(copy):
            return False
    return True


def _match_mirror_layout(directory, expected, files):
    if directory.is_symlink() or os.path.ismount(directory) or not directory.is_dir():
        return False
    entries = {path.name: path for path in directory.iterdir()}
    if entries.keys() != expected.keys():
        return False
    for name, path in entries.items():
        item = expected[name]
        if path.is_symlink():
            return False
        if isinstance(item, dict):
            if not _match_mirror_layout(path, item, files):
                return False
        elif (not isinstance(item, list) or len(item) != 2 or type(item[0]) is not int
              or not isinstance(item[1], str) or not path.is_file() or path.stat().st_size != item[0]):
            return False
        else:
            files.append((path, item[1]))
    return True


def _match_legacy_mirror(directory, releases):
    if not isinstance(releases, dict):
        return None
    for version, expected in releases.items():
        files = []
        # A leaf is [byte length, uppercase SHA-256]. Check the entire layout before reading asset contents.
        if isinstance(expected, dict) and _match_mirror_layout(directory, expected, files) and files:
            if all(_digest(path).hex().upper() == checksum for path, checksum in files):
                return version
    return None


def sync_resources():
    CHARACTERS.mkdir(parents=True, exist_ok=True)
    STATE.mkdir(parents=True, exist_ok=True)
    src = INSTALL / "characters"
    if not src.is_dir() or src.resolve() == CHARACTERS.resolve() or CHARACTERS.is_symlink():
        return
    legacy = None
    history_root = DATA / "builtin-resource-sync"
    for d in sorted(src.iterdir()):
        if not d.is_dir():
            continue
        dst = CHARACTERS / d.name
        try:
            history = history_root / d.name
            if history_root.is_symlink() or history.is_symlink():
                continue
            previous = history / "previous"
            # Once the mirror has been retired, keep any old interrupted-sync backup out of character selection.
            if (not dst.exists() and not dst.is_symlink() and previous.is_dir() and not previous.is_symlink()
                    and not any(path.is_dir() for path in history.glob("retired-*"))):
                os.replace(previous, dst)
            if not dst.is_dir() or dst.is_symlink():
                continue
            if not _same_resources(d, dst):
                if legacy is None:
                    legacy = read_json(src / "legacy-mirrors.json") or {}
                version = _match_legacy_mirror(dst, legacy.get(d.name))
                if version is None or _read_character(d) is None:
                    continue
                history.mkdir(parents=True, exist_ok=True)
                backup = history / ("retired-" + uuid.uuid4().hex)
                os.rename(dst, backup)
                log(f"已备份 {version} 内置角色镜像 {d.name}：{backup}")
                continue
            # Leave the active directory intact if moving it fails. A failed cleanup
            # after moving cannot make a partially deleted character visible.
            duplicate = DATA / ("builtin-duplicate-" + uuid.uuid4().hex)
            os.rename(dst, duplicate)
            try:
                shutil.rmtree(duplicate)
            except OSError as e:
                log(f"清理已验证镜像失败 {duplicate}: {e}")
        except OSError as e:
            log(f"清理角色镜像失败 {dst}: {e}")
    for md in src.glob("*.md"):
        try:
            if not _same_file(md, CHARACTERS / md.name):
                shutil.copy2(md, CHARACTERS / md.name)
        except OSError as e:
            log(f"同步说明文件失败 {md}: {e}")

# ---------------- 角色清单 ----------------
def is_sprite(stem):
    return stem not in ("portrait", "portrait-card", "avatar")


def _url(cid, *parts):
    return f"{SCHEME}://{DATA_HOST}/characters/" + "/".join(urllib.parse.quote(p, safe="") for p in (cid, *parts))


def _sprites(d):
    cid = d.name
    poses, blink, blink_base = {}, None, None
    files = sorted((f for f in d.iterdir() if f.is_file() and f.suffix.lower() in (".webp", ".png")), key=lambda f: f.name)
    for f in files:
        stem = f.stem
        if not is_sprite(stem):
            continue
        url = _url(cid, f.name)
        if stem == "idle-blink":
            blink = url
            continue
        if stem == "idle":
            blink_base = url
        poses.setdefault(stem.split("-")[0], []).append(url)
    return poses, blink, blink_base


def _sheets(d):
    anim = d / ANIM_DIR
    if not anim.is_dir():
        return {}
    return {f.stem: _url(d.name, ANIM_DIR, f.name) for f in sorted(anim.glob("*.webp"), key=lambda f: f.name)}


def _videos(d):
    """anim/ 里的 .webm（VP9 + 透明通道）：character.json 的 animations 按文件名引用，有就播视频；
    同名精灵图可以没有（2026-11-27 起 Claude 角色只留视频），没有视频才播精灵图"""
    anim = d / ANIM_DIR
    if not anim.is_dir():
        return {}
    return {f.stem: _url(d.name, ANIM_DIR, f.name) for f in sorted(anim.glob("*.webm"), key=lambda f: f.name)}


def _read_character(directory):
    info = read_json(directory / "character.json")
    if info is None:
        return None
    poses, blink, blink_base = _sprites(directory)
    sheets, videos = _sheets(directory), _videos(directory)
    if "idle" not in poses and "default" not in poses:
        animations = info.get("animations")
        idle = animations.get("idle") if isinstance(animations, dict) else None
        idle_sheets = idle.get("sheets") if isinstance(idle, dict) else None
        if not isinstance(idle_sheets, list) or not any(
            isinstance(name, str) and (name in sheets or name in videos) for name in idle_sheets
        ):
            return None
    info.update(poses=poses, blink=blink, blinkBase=blink_base, sheets=sheets, videos=videos)
    return info


def manifest():
    global _character_dirs
    chars, selected = {}, {}
    dirs = [d for root in (CHARACTERS, INSTALL / "characters") if root.is_dir()
            for d in root.iterdir() if d.is_dir()]
    for d in sorted(dirs, key=lambda x: x.name):
        if d.name in selected:
            continue
        info = _read_character(d)
        if info is None:
            continue
        info["defaultName"] = info.get("name") if isinstance(info.get("name"), str) else d.name   # 改名留空时恢复成它
        custom = names().get(d.name)
        if isinstance(custom, str) and custom.strip():
            info["name"] = custom.strip()
        chars[d.name] = info
        selected[d.name] = d
    _character_dirs = selected
    return {"characters": chars}


def resolve_sprite(path):
    """claudia://data/characters/<角色>/<图> 或 …/<角色>/anim/<图> → 文件。只给 .webp / .png，别的一律不给"""
    parts = path.split("/")
    anim = len(parts) == 5 and parts[3] == ANIM_DIR
    if (len(parts) != 4 and not anim) or parts[0] != "" or parts[1] != "characters":
        return None
    character, name = urllib.parse.unquote(parts[2]), urllib.parse.unquote(parts[-1])
    for seg in (character, name):
        if not seg or seg in (".", "..") or "/" in seg or "\\" in seg or "\0" in seg:
            return None
    ext = os.path.splitext(name)[1].lower()
    if ext not in (".webp", ".png") and not (anim and ext == ".webm"):   # .webm 只在 anim/ 里（2026-11-25 试验视频播放）
        return None
    root = character_dir(character).parent.resolve()
    full = (root / character / ANIM_DIR / name) if anim else (root / character / name)
    full = full.resolve()
    if root not in full.parents:
        return None
    return full, {".webp": "image/webp", ".webm": "video/webm"}.get(ext, "image/png")


def reap(proc):
    """子进程退出后把它收掉（后台线程等它），不然一直挂着一个 <defunct> 僵尸进程（2026-11-26：notify-send、picom -b 都留过）"""
    import threading
    threading.Thread(target=proc.wait, daemon=True, name="reap").start()
    return proc
