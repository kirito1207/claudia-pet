"""接入 Claude、找 Claude 桌面端进程、--ensure、--mcp（对应 Windows 版 Pet.Claude.cs、Ensure.cs、Mcp.cs）。

两项接入，claudia-pet 自己改配置，改之前先备份（<文件>.bak-claudia-<时间>，只留 3 份）：
  plugin  Claude Code 插件：把安装目录里的 plugin 文件夹加进 ~/.claude/settings.json 的 env.CLAUDE_CODE_PLUGIN_DIRS
          （Linux 上多个目录用冒号隔开，和 PATH 一样；别人的照留）
  mcp     桌面端 MCP：在 ~/.config/Claude/claude_desktop_config.json 的 mcpServers 里加 claudia-pet → claudia-pet --mcp
"""
import fcntl
import json
import os
import shutil
import subprocess
import sys
import time
from pathlib import Path

from . import store

PLUGIN_SEP = ":"


def plugin_dir():
    return store.INSTALL / "plugin"


def claude_settings():
    base = os.environ.get("CLAUDE_CONFIG_DIR") or str(Path.home() / ".claude")
    return Path(base) / "settings.json"


def desktop_config():
    base = os.environ.get("XDG_CONFIG_HOME") or str(Path.home() / ".config")
    return Path(base) / "Claude" / "claude_desktop_config.json"


def _plugin_dirs(settings):
    env = settings.get("env") if isinstance(settings, dict) else None
    v = env.get("CLAUDE_CODE_PLUGIN_DIRS") if isinstance(env, dict) else None
    return [x for x in v.split(PLUGIN_SEP) if x.strip()] if isinstance(v, str) else []


def _same_path(a, b):
    try:
        return os.path.realpath(os.path.expanduser(os.path.expandvars(a.strip()))) == os.path.realpath(str(b))
    except Exception:
        return False


def plugin_installed():
    return any(_same_path(d, plugin_dir()) for d in _plugin_dirs(store.read_json(claude_settings())))


def mcp_installed():
    cfg = store.read_json(desktop_config())
    servers = cfg.get("mcpServers") if cfg else None
    return isinstance(servers, dict) and store.APP_NAME in servers


def connected():
    return plugin_installed() or mcp_installed()


def _backup(path):
    path = Path(path)
    if not path.exists():
        return None
    copy = path.with_name(path.name + ".bak-claudia-" + time.strftime("%Y%m%d-%H%M%S"))
    shutil.copy2(path, copy)
    try:
        for old in sorted(path.parent.glob(path.name + ".bak-claudia-*"), reverse=True)[3:]:
            old.unlink()
    except OSError:
        pass
    return copy


def set_plugin(install):
    pd = plugin_dir()
    if install and not (pd / ".claude-plugin" / "plugin.json").exists():
        raise RuntimeError(f"没找到插件文件夹：{pd}\n请用完整的安装包重新安装 {store.APP_NAME}。")
    f = claude_settings()
    if not install and not plugin_installed():
        return "没有接入过 Claude Code 插件，不用撤销。"
    settings = store.read_json(f)
    if settings is None and f.exists():
        raise RuntimeError(f"读不懂 {f}，这次没有改。")
    settings = settings or {}
    dirs = [d for d in _plugin_dirs(settings) if not _same_path(d, pd)]
    if install:
        dirs.append(str(pd))
    env = settings.get("env")
    if not isinstance(env, dict):
        env = settings["env"] = {}
    if dirs:
        env["CLAUDE_CODE_PLUGIN_DIRS"] = PLUGIN_SEP.join(dirs)
    else:
        env.pop("CLAUDE_CODE_PLUGIN_DIRS", None)
    if not env:
        settings.pop("env", None)
    backup = _backup(f)
    store.write_json(f, settings)
    msg = "已接入 Claude Code 插件。新开的 Code 会话生效。" if install else "已撤销 Claude Code 插件。"
    return msg + f"\n\n改了：{f}" + (f"\n备份：{backup}" if backup else "")


def set_mcp(install):
    f = desktop_config()
    if not install and not mcp_installed():
        return "没有接入过 Claude 桌面端，不用撤销。"
    cfg = store.read_json(f)
    if cfg is None and f.exists():
        raise RuntimeError(f"读不懂 {f}，这次没有改。")
    cfg = cfg or {}
    servers = cfg.get("mcpServers")
    if not isinstance(servers, dict):
        servers = cfg["mcpServers"] = {}
    if install:
        servers[store.APP_NAME] = {"command": store.launcher(), "args": ["--mcp"]}
    else:
        servers.pop(store.APP_NAME, None)
    if not servers:
        cfg.pop("mcpServers", None)
    backup = _backup(f)
    store.write_json(f, cfg)
    msg = "已接入 Claude 桌面端。完全退出 Claude 再打开后生效。" if install else "已撤销 Claude 桌面端的接入。重开 Claude 后生效。"
    return msg + f"\n\n改了：{f}" + (f"\n备份：{backup}" if backup else "")


def seen_text():
    try:
        ts = float((store.STATE / "seen-plugin").read_text().strip())
    except (OSError, ValueError):
        return "还没收到过消息：新开一个 Code 会话、让她干点活试试"
    ago = time.time() - ts
    if ago < 60:
        when = "刚刚"
    elif ago < 3600:
        when = f"{int(ago // 60)} 分钟前"
    elif ago < 86400:
        when = f"{int(ago // 3600)} 小时前"
    else:
        when = f"{int(ago // 86400)} 天前"
    return "上次收到消息：" + when


# ---------------- GNOME Shell 扩展（跟着 Claude 窗口） ----------------
# 装到 ~/.local/share/gnome-shell/extensions/<uuid>，再把 uuid 加进 org.gnome.shell 的 enabled-extensions。
# Wayland 下 GNOME Shell 只在登录时加载扩展：装好、更新、撤销都要注销重新登录一次（是电脑的登录，不是 Claude 账号）
EXTENSION_UUID = "claudia-pet@kirito1207.github.io"


def gnome_session():
    return "GNOME" in (os.environ.get("XDG_CURRENT_DESKTOP") or "").upper() and shutil.which("gnome-shell") is not None


def extension_src():
    return store.INSTALL / "linux" / "gnome-extension" / EXTENSION_UUID


def extension_dst():
    base = os.environ.get("XDG_DATA_HOME") or str(Path.home() / ".local" / "share")
    return Path(base) / "gnome-shell" / "extensions" / EXTENSION_UUID


def _shell_settings():
    from gi.repository import Gio
    src = Gio.SettingsSchemaSource.get_default()
    if src is None or src.lookup("org.gnome.shell", True) is None:
        return None
    return Gio.Settings.new("org.gnome.shell")


def extension_enabled():
    st = _shell_settings()
    return bool(st) and EXTENSION_UUID in st.get_strv("enabled-extensions")


def extension_installed():
    return (extension_dst() / "metadata.json").exists() and extension_enabled()


def set_gnome_extension(install):
    from gi.repository import Gio
    st = _shell_settings()
    if st is None:
        raise RuntimeError("这台电脑不是 GNOME 桌面，用不了这个扩展；她会一直待在桌面上。")
    dst = extension_dst()
    if not install and not dst.exists() and EXTENSION_UUID not in st.get_strv("enabled-extensions"):
        return "没有装过 GNOME 扩展，不用撤销。"
    enabled = [u for u in st.get_strv("enabled-extensions") if u != EXTENSION_UUID]
    disabled = [u for u in st.get_strv("disabled-extensions") if u != EXTENSION_UUID]
    if install:
        src = extension_src()
        if not (src / "metadata.json").exists():
            raise RuntimeError(f"没找到扩展文件夹：{src}\n请用完整的安装包重新安装 {store.APP_NAME}。")
        tmp = dst.with_name(dst.name + ".new")
        if tmp.exists():
            shutil.rmtree(tmp)
        shutil.copytree(src, tmp)
        if dst.exists():
            shutil.rmtree(dst)
        os.replace(tmp, dst)
        enabled.append(EXTENSION_UUID)
    else:
        if dst.exists():
            shutil.rmtree(dst)
    st.set_strv("enabled-extensions", enabled)
    st.set_strv("disabled-extensions", disabled)
    Gio.Settings.sync()
    if install:
        return ("已装好 GNOME 扩展。\n\n注销电脑、重新登录后生效（是 Ubuntu 的登录，Claude 账号不受影响）。"
                "生效后，在「多开」里把她改成待在「Claude 窗口里」。" + f"\n\n装在：{dst}")
    return "已撤销 GNOME 扩展。注销重新登录后完全卸掉；在那之前她已经待在桌面上了。"


def integrations(extension_running=False):
    plugin, mcp = plugin_installed(), mcp_installed()
    rows = [
        {"id": "plugin", "label": "Claude Code 插件", "installed": plugin,
         "note": "Code 会话的工作状态和额度；会话开始时叫她出来。新开的会话生效",
         "detail": seen_text() if plugin else None},
        {"id": "mcp", "label": "Claude 桌面端（MCP）", "installed": mcp,
         "note": "Claude 一打开就叫她出来，只用对话时也在（对话里没有工作状态）。重开 Claude 生效"},
    ]
    if gnome_session():
        installed = extension_installed()
        rows.append({
            "id": "gnome", "label": "GNOME 扩展（跟着 Claude 窗口）", "installed": installed,
            "note": "Wayland 下她自己看不到 Claude 的窗口，要这个扩展告诉她。接入后注销电脑、重新登录一次生效（不是 Claude 账号）",
            "detail": "正在运行：可以在「多开」里让她待在 Claude 窗口里" if extension_running
            else "已装好，注销电脑、重新登录后生效" if installed else None,
        })
    return rows


# ---------------- Claude 桌面端进程 ----------------
_CLK = os.sysconf("SC_CLK_TCK") if hasattr(os, "sysconf") else 100


def _boot_time():
    try:
        for line in open("/proc/stat"):
            if line.startswith("btime "):
                return float(line.split()[1])
    except OSError:
        pass
    return 0.0


def claude_app_pids():
    """Claude 桌面端的主进程（…/claude-desktop，不带 --type= 的那个）；Claude Code 命令行（claude）不算"""
    found = {}
    for entry in os.scandir("/proc"):
        if not entry.name.isdigit():
            continue
        try:
            exe = os.readlink(f"/proc/{entry.name}/exe")
        except OSError:
            continue
        if os.path.basename(exe).replace(" (deleted)", "") not in ("claude-desktop", "Claude"):
            continue
        try:
            cmd = open(f"/proc/{entry.name}/cmdline", "rb").read().split(b"\0")
        except OSError:
            continue
        if any(a.startswith(b"--type=") for a in cmd):
            continue
        found[int(entry.name)] = exe
    return set(found)


def claude_started_at():
    """Claude 桌面端最早的那个主进程是什么时候开的（unix 秒）；没开返回 None"""
    first, boot = None, _boot_time()
    for pid in claude_app_pids():
        try:
            stat = open(f"/proc/{pid}/stat").read()
            start = boot + int(stat.rsplit(")", 1)[1].split()[19]) / _CLK
        except (OSError, ValueError, IndexError):
            continue
        first = start if first is None else min(first, start)
    return first


# ---------------- 单实例 ----------------
def lock_path(pid_id):
    return store.RUN / f"{pid_id}.lock"


def socket_path(pid_id="main"):
    return store.RUN / f"{pid_id}.sock"


def pet_running(pid_id="main"):
    store.RUN.mkdir(parents=True, exist_ok=True)
    try:
        fd = os.open(lock_path(pid_id), os.O_RDWR | os.O_CREAT, 0o600)
    except OSError:
        return False
    try:
        fcntl.flock(fd, fcntl.LOCK_EX | fcntl.LOCK_NB)
        fcntl.flock(fd, fcntl.LOCK_UN)
        return False
    except OSError:
        return True
    finally:
        os.close(fd)


def take_lock(pid_id):
    """拿到这一只的锁返回文件描述符（进程退出前一直开着），已经有一个在跑返回 None"""
    store.RUN.mkdir(parents=True, exist_ok=True)
    fd = os.open(lock_path(pid_id), os.O_RDWR | os.O_CREAT, 0o600)
    try:
        fcntl.flock(fd, fcntl.LOCK_EX | fcntl.LOCK_NB)
        return fd
    except OSError:
        os.close(fd)
        return None


def send_command(cmd, pid_id="main"):
    """给正在跑的那一只发一句话（show / toggle / quit），发出去了返回 True"""
    import socket
    s = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
    try:
        s.settimeout(2)
        s.connect(str(socket_path(pid_id)))
        s.sendall(cmd.encode() + b"\n")
        return True
    except OSError:
        return False
    finally:
        s.close()


# ---------------- --ensure / --mcp ----------------
BY_CLAUDE_ARG = "--by-claude"
USER_QUIT_FLAG = store.DATA / "user-quit"


def launch(args):
    """起一个独立的进程（新会话、不继承输入输出）：插件和 MCP 都是 Claude 的子进程，Claude 关掉时不能把她一起带走"""
    try:
        # MCP 服务一直开着：桌宠退出后要收掉，不然留一个僵尸进程
        p = store.reap(subprocess.Popen([store.launcher(), *args], stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL,
                                        stderr=subprocess.DEVNULL, start_new_session=True, close_fds=True, cwd=str(Path.home())))
        return f"已打开（独立进程 {p.pid}）"
    except OSError as e:
        return "打开失败：" + str(e)


def ensure():
    """Claude Code 插件、桌面端 MCP 用它把桌宠叫出来；返回这次的结果（写进日志）"""
    if pet_running():
        return "已经在跑"
    if USER_QUIT_FLAG.exists():
        quit_at = USER_QUIT_FLAG.stat().st_mtime
        claude_at = claude_started_at()
        if claude_at is None or claude_at <= quit_at:
            return "不打开：你从菜单退出过，这个 Claude 是在那之前就开着的"
    return launch([BY_CLAUDE_ARG])


def mcp_serve():
    """写在 Claude 桌面端配置里的本地 MCP 服务：不提供工具，只借 MCP 的生命周期把她叫出来。一行一个 JSON-RPC 消息"""
    store.log("MCP 服务启动：" + ensure())
    out = sys.stdout
    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue
        try:
            msg = json.loads(line)
        except ValueError:
            continue
        if not isinstance(msg, dict) or msg.get("id") is None:
            continue   # 通知（notifications/*）不用回
        method = msg.get("method")
        reply = {"jsonrpc": "2.0", "id": msg["id"]}
        if method == "initialize":
            p = msg.get("params") or {}
            reply["result"] = {
                "protocolVersion": p.get("protocolVersion") if isinstance(p.get("protocolVersion"), str) else "2024-11-05",
                "capabilities": {"tools": {}},
                "serverInfo": {"name": store.APP_NAME, "version": store.version()},
                "instructions": store.APP_NAME + " 是 Claude 娘桌宠，只借这个连接跟着 Claude 打开和关闭，不提供工具。",
            }
        elif method == "ping":
            reply["result"] = {}
        elif method == "tools/list":
            reply["result"] = {"tools": []}
        else:
            reply["error"] = {"code": -32601, "message": f"{store.APP_NAME} 不提供这个功能：{method}"}
        try:
            out.write(json.dumps(reply, ensure_ascii=False) + "\n")
            out.flush()
        except OSError:
            break
    store.log("MCP 服务结束（Claude 关掉了连接）")
