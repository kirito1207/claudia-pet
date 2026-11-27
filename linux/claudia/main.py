"""claudia-pet Linux 版入口（对应 Windows 版 app/src/Program.cs）。

  claudia-pet                       打开（已经开着就让她回到默认位置）
  claudia-pet --toggle              藏起来 / 再按一次出来（GNOME 自定义快捷键调的就是这个）
  claudia-pet --show | --quit       叫出来 / 退出
  claudia-pet --ensure              给 Claude 用：没开就打开（Claude Code 插件、桌面端 MCP 调）
  claudia-pet --mcp                 给 Claude 桌面端用的 MCP 服务（stdio）
  claudia-pet --connect [plugin|mcp|gnome|all] / --disconnect [...]   接入 / 撤销（gnome = 跟着 Claude 窗口的 GNOME 扩展）
  claudia-pet --pet 2 [--mode desktop|claude]                   多开的那几只，由第一只带起来
"""
import faulthandler
import os
import sys
import time

# 进程被 C 层面的错误弄死时（段错误、glibc 报 corrupted double-linked list 后中止）把当时每个线程正在跑的
# Python 调用栈打到 stderr（~/.claudia/stderr.log）。2026-11-22 拖动后崩过好几次，没留下别的线索
faulthandler.enable()

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

from claudia import claude, store  # noqa: E402

MENU_QSS = """
QMenu { background: #fffaf6; border: 1px solid #ead8ca; border-radius: 10px; padding: 4px 0; }
QMenu::item { padding: 6px 22px 6px 14px; border-radius: 6px; margin: 0 4px; color: #3b2a20; }
QMenu::item:selected { background-color: #fde3cf; color: #3b2a20; }
QMenu::separator { height: 1px; background: #ead8ca; margin: 4px 10px; }
QMenu::indicator { width: 14px; height: 14px; left: 4px; }
"""


def arg_after(args, flag):
    i = args.index(flag)
    return args[i + 1] if i + 1 < len(args) else None


def choose_run_mode(fresh, by_claude):
    """还没选过运行方式时选一次：第一次用的一律「独立运行」并待在桌面上（2026-11-27 用户定：有人没装 Claude 桌面端、
    只用 Claude Code，「跟着 Claude」找不到桌面端窗口 30 秒就退出）；以前用过的（已有设置）照旧「跟着 Claude」"""
    try:
        connected = claude.connected()
    except Exception:
        connected = False
    standalone = fresh
    store.set_standalone(standalone)
    if standalone and "mode" not in store.settings:
        store.settings["mode"] = "desktop"
        store.save_settings()
    store.log(f"运行方式：{'独立运行' if standalone else '跟着 Claude'}（第一次用 = {fresh}，Claude 叫出来的 = {by_claude}，已接入 Claude = {connected}）")


SESSION_VARS = ("DISPLAY", "XAUTHORITY", "WAYLAND_DISPLAY", "XDG_RUNTIME_DIR", "DBUS_SESSION_BUS_ADDRESS",
                "XDG_SESSION_TYPE", "XDG_CURRENT_DESKTOP")


def env_from_ancestors():
    """沿父进程一级级往上找，第一个有 DISPLAY / WAYLAND_DISPLAY 的进程，把它的会话变量补进自己的环境（只补没有的）。
    有的桌面（2026-11-23 那台 Debian 13 + Openbox，Claude 桌面端外面套着 cage）给 MCP 的环境只剩几个变量，
    claude-desktop 自己的环境里也没有 DISPLAY，再往上的启动脚本才有；systemd 用户会话里也没有，X 也不是 :0。
    读的是自己这个用户的进程的 /proc/<pid>/environ。返回找到的那个进程号，没找到返回 0"""
    pid = os.getppid()
    for _ in range(16):
        if pid <= 1:
            return 0
        try:
            with open(f"/proc/{pid}/environ", "rb") as f:
                env = dict(kv.split("=", 1) for kv in f.read().decode("utf-8", "replace").split("\0") if "=" in kv)
        except OSError:
            env = {}
        if env.get("DISPLAY") or env.get("WAYLAND_DISPLAY"):
            for k in SESSION_VARS:
                if env.get(k) and not os.environ.get(k):
                    os.environ[k] = env[k]
            return pid
        try:
            with open(f"/proc/{pid}/stat") as f:
                pid = int(f.read().rsplit(")", 1)[1].split()[1])   # 「pid (名字) 状态 父进程号 …」
        except (OSError, ValueError, IndexError):
            return 0
    return 0


def fill_session_env(override=False):
    """Claude 桌面端随登录自启时，交给 MCP 的环境里可能还没有 DISPLAY、XAUTHORITY：先从父进程链里找（env_from_ancestors），
    再从 systemd 用户会话里补"""
    import glob
    import subprocess
    if not (os.environ.get("DISPLAY") or os.environ.get("WAYLAND_DISPLAY")):
        found = env_from_ancestors()
        if found:
            store.log(f"图形界面的环境从父进程 {found} 那里补上：DISPLAY={os.environ.get('DISPLAY')!r}")
    run = f"/run/user/{os.getuid()}"
    if not os.environ.get("XDG_RUNTIME_DIR") and os.path.isdir(run):
        os.environ["XDG_RUNTIME_DIR"] = run
    if not os.environ.get("DBUS_SESSION_BUS_ADDRESS") and os.path.exists(run + "/bus"):
        os.environ["DBUS_SESSION_BUS_ADDRESS"] = f"unix:path={run}/bus"
    try:
        out = subprocess.run(["systemctl", "--user", "show-environment"], capture_output=True, text=True, timeout=5).stdout
    except (OSError, subprocess.SubprocessError):
        out = ""
    for line in out.splitlines():
        k, _, v = line.partition("=")
        if k in SESSION_VARS and v and (override or not os.environ.get(k)):
            os.environ[k] = v
    if not os.environ.get("DISPLAY") and os.path.exists("/tmp/.X11-unix/X0"):
        os.environ["DISPLAY"] = ":0"
    if not os.environ.get("XAUTHORITY"):
        auth = sorted(glob.glob(run + "/.mutter-Xwaylandauth.*"), key=os.path.getmtime)
        if auth:
            os.environ["XAUTHORITY"] = auth[-1]


def ensure_compositor():
    """X11 下没有合成器（_NET_WM_CM_S0 没人占）时，透明窗口透明的地方是黑的。装了 picom 就用最朴素的参数把它拉起来：
    不读默认配置（那份会给所有窗口加阴影、淡入淡出）、纯软件合成（虚拟机没有 3D 加速也稳）。
    2026-11-23 那台 Debian 13 + Openbox（外面套着 cage，会话脚本是系统的、没有给用户留自启动入口）就是这样：
    跟着桌宠一起起来，重启电脑后也不用另外设自启。已经有合成器（GNOME 等）或 Wayland 时什么也不做"""
    import ctypes
    import ctypes.util
    import shutil
    import subprocess
    import time
    if os.environ.get("WAYLAND_DISPLAY") or not os.environ.get("DISPLAY"):
        return

    def cm_owner():
        try:
            x = ctypes.CDLL(ctypes.util.find_library("X11") or "libX11.so.6")
            x.XOpenDisplay.restype = ctypes.c_void_p
            x.XOpenDisplay.argtypes = [ctypes.c_char_p]
            x.XInternAtom.restype = ctypes.c_ulong
            x.XInternAtom.argtypes = [ctypes.c_void_p, ctypes.c_char_p, ctypes.c_int]
            x.XGetSelectionOwner.restype = ctypes.c_ulong
            x.XGetSelectionOwner.argtypes = [ctypes.c_void_p, ctypes.c_ulong]
            x.XCloseDisplay.argtypes = [ctypes.c_void_p]
            d = x.XOpenDisplay(None)
            if not d:
                return None
            owner = x.XGetSelectionOwner(d, x.XInternAtom(d, b"_NET_WM_CM_S0", 0))
            x.XCloseDisplay(d)
            return owner
        except OSError:
            return None

    if cm_owner() != 0:   # 有合成器，或者查不了（查不了就别乱动）
        return
    exe = shutil.which("picom")
    if not exe:
        store.log("没有合成器：透明的地方会是黑的（装 picom 后桌宠会自己把它拉起来：sudo apt install picom）")
        return
    try:
        # --no-use-damage：每次整屏重画。只画「变了的区域」在 Xvnc（TigerVNC）上会漏画，出现黑方块、偶尔整屏发黑
        store.reap(subprocess.Popen([exe, "--config", "/dev/null", "--backend", "xrender", "--no-use-damage", "--no-vsync",
                                     "--log-level", "warn", "--log-file", str(store.DATA / "picom.log"), "-b"],
                                    stdin=subprocess.DEVNULL,
                                    stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, start_new_session=True, close_fds=True))
    except OSError as e:
        store.log("启动 picom 失败: " + str(e))
        return
    for _ in range(30):   # 最多等 3 秒，等它占上合成器的位置再开窗口
        time.sleep(0.1)
        if cm_owner():
            store.log("没有合成器，已启动 picom（不加阴影、不加淡入淡出、整屏重画）")
            return
    store.log("启动了 picom，但 3 秒内没接管合成（透明的地方可能还是黑的）")


def x_display_ok():
    import ctypes
    import ctypes.util
    try:
        x = ctypes.CDLL(ctypes.util.find_library("X11") or "libX11.so.6")
        x.XOpenDisplay.restype = ctypes.c_void_p
        x.XOpenDisplay.argtypes = [ctypes.c_char_p]
        x.XCloseDisplay.argtypes = [ctypes.c_void_p]
        d = x.XOpenDisplay(None)
        if d:
            x.XCloseDisplay(d)
            return True
    except OSError:
        pass
    return False


def wait_for_display(seconds=30):
    """连得上 XWayland 才开窗口；刚登录时可能还没好，等一会儿再试"""
    import time
    deadline = time.monotonic() + seconds
    attempt = 0
    while True:
        fill_session_env(override=attempt > 0)
        if x_display_ok():
            if attempt:
                store.log(f"等了 {attempt} 次才连上图形界面（DISPLAY={os.environ.get('DISPLAY')}）")
            return True
        attempt += 1
        if time.monotonic() > deadline:
            return False
        time.sleep(2)


def choose_identity():
    """直接打开（双击、Claude 叫出来）的这一只用哪个编号：已经有负责人在跑（或马上要有）就请它叫大家出来，返回 None；
    不然抢负责人锁，用名单上第一个没在跑的编号（名单是空的就从 main 开始），返回 (编号, 负责人锁)"""
    from claudia import pethub
    for _ in range(25):   # 最多等约 5 秒：正在换负责人时锁会空一下
        if pethub.send_cli("show"):
            return None
        fd = claude.take_lock("leader")
        if fd is not None:
            pethub.clear_stopping()   # 打开了：「全部退出」的记号作废
            ids = store.roster()
            free = [i for i in ids if not claude.pet_running(i)]
            if free or not ids:
                pid = free[0] if free else store.free_pet_id(lambda i: i not in ids)
                if pid not in ids:
                    store.save_roster(ids + [pid])
                return pid, fd
            os.close(fd)   # 名单上的几只都在跑、只是在换负责人：等新负责人
        time.sleep(0.2)
    store.log("等不到负责人，这次不打开")
    return None


def main(args):
    if "--ensure" in args or "--mcp" in args:
        # 这两个会把桌宠拉起来（子进程继承这里的环境）：先补好图形界面的变量。等桌宠自己再补时，
        # MCP 进程往往已经退了、父进程链断了，就找不到了
        fill_session_env()
    if "--ensure" in args:
        store.log("--ensure：" + claude.ensure())
        return 0
    if "--mcp" in args:
        claude.mcp_serve()
        return 0
    for flag, install in (("--connect", True), ("--disconnect", False)):
        if flag not in args:
            continue
        what = arg_after(args, flag) or "all"
        store.DATA.mkdir(parents=True, exist_ok=True)
        code = 0
        targets = ["plugin", "mcp"] + (["gnome"] if claude.gnome_session() else []) if what == "all" else [what]
        for t in targets:
            try:
                if t == "plugin":
                    msg = claude.set_plugin(install)
                elif t == "mcp":
                    msg = claude.set_mcp(install)
                elif t == "gnome":
                    msg = claude.set_gnome_extension(install)
                else:
                    raise ValueError("不认识：" + t)
            except Exception as e:
                code = 1
                msg = "没有完成：" + str(e)
            store.log(f"{flag} {t}：{msg}".replace("\n", " "))
            print(f"[{t}] {msg}")
        return code
    for flag in ("--toggle", "--show", "--quit"):
        if flag in args:
            from claudia import pethub
            if pethub.send_cli(flag[2:]):   # 交给负责人（谁是负责人见 pethub.py）
                return 0
            if flag != "--show":
                print(store.APP_NAME + " 没在运行", file=sys.stderr)
                return 1
            break   # --show 而她没开：照常打开

    # 每只平级（2026-11-27）：--pet <编号> 是负责人叫出来的那几只；直接打开的那一只当负责人（见 choose_identity）
    first_mode, leader = None, None
    if "--pet" in args:
        pid = arg_after(args, "--pet")
        if not store.valid_pet_id(pid):
            return 2
        store.pet_id = pid
        if "--mode" in args and arg_after(args, "--mode") in ("desktop", "claude"):
            first_mode = arg_after(args, "--mode")
        if "--join" in args:
            store.join_token = arg_after(args, "--join")
    else:
        picked = choose_identity()
        if picked is None:
            return 0
        store.pet_id, leader = picked

    lock = claude.take_lock(store.pet_id)
    if lock is None:   # 这一只已经在跑
        if leader is not None:
            os.close(leader)
        return 0

    os.environ.setdefault("GDK_BACKEND", "x11")   # Wayland 下普通窗口摆不了位置、置不了顶：走 XWayland
    os.environ.setdefault("QT_QPA_PLATFORM", "xcb")   # Qt 也一样走 XWayland（X11 外形、override-redirect 都靠它）
    # 窗口坐标、X11 外形、物理都按 X 服务器的屏幕像素算：关掉 Qt 自己的高分屏缩放，人物大小由「人物大小」设置和网页缩放管
    os.environ["QT_ENABLE_HIGHDPI_SCALING"] = "0"
    os.environ.setdefault("QT_SCALE_FACTOR", "1")
    if not wait_for_display():
        store.log(f"打不开图形界面：DISPLAY={os.environ.get('DISPLAY')!r}，XAUTHORITY={os.environ.get('XAUTHORITY')!r}")
        print(store.APP_NAME + "：打不开图形界面", file=sys.stderr)
        return 1
    ensure_compositor()   # 没有合成器时透明处是黑的：开窗口前先拉起 picom（装了的话）
    if not os.environ.get("CLAUDIA_DEBUG"):
        # WebKit、EGL、托盘库的警告（虚拟机里没有 3D 加速时一大串）不往终端刷：写进 ~/.claudia/stderr.log
        try:
            store.DATA.mkdir(parents=True, exist_ok=True)
            err = store.DATA / "stderr.log"
            if err.exists() and err.stat().st_size > 1_000_000:
                err.unlink()
            fd = os.open(err, os.O_WRONLY | os.O_CREAT | os.O_APPEND, 0o600)
            os.dup2(fd, 2)
            os.close(fd)
        except OSError:
            pass
    import gi
    from gi.repository import GLib
    from PySide6.QtWidgets import QApplication

    from claudia import system, web
    from claudia.pet import Pet

    store.pet_dir().mkdir(parents=True, exist_ok=True)
    if leader is not None:
        try:
            claude.USER_QUIT_FLAG.unlink()   # 打开了：恢复跟着 Claude 出现
        except OSError:
            pass
    store.log(f"启动 {store.version()}，{os.uname().sysname} {os.uname().release}，"
              f"会话 {os.environ.get('XDG_SESSION_TYPE', '?')}，Qt {os.environ.get('QT_QPA_PLATFORM')}")
    fresh = leader is not None and not (store.DATA / "settings.json").exists()
    store.load_settings()
    store.load_app()   # 所有桌宠共用的设置；第一次从原来第一只的设置里搬过来
    if first_mode and "mode" not in store.settings:
        store.settings["mode"] = first_mode
        store.save_settings()
    if leader is not None and not store.has_run_mode():
        choose_run_mode(fresh, claude.BY_CLAUDE_ARG in args)

    web.register_scheme()   # claudia:// 要在 QApplication 之前注册
    app = QApplication(sys.argv[:1])
    app.setApplicationName(store.APP_NAME)
    app.setDesktopFileName(store.APP_NAME)
    app.setQuitOnLastWindowClosed(False)   # 关掉设置窗口不能把桌宠也带走
    app.setStyleSheet(MENU_QSS)

    pet = Pet()
    pet.leader_lock = leader
    pet.start()
    srv = system.serve(claude.socket_path(store.pet_id), pet.on_command)
    # 注册退出信号的函数各版本叫法不一样：新的 GLib 在 GLibUnix 里叫 signal_add；Debian 13（GLib 2.84、PyGObject 3.50）
    # 有 GLibUnix 却只有 signal_add_full，老的 GLib.unix_signal_add 还在（2026-11-23：以前只接 ValueError / ImportError，
    # 这里抛 AttributeError，一启动就崩）。按顺序找第一个有的
    signal_add = None
    try:
        gi.require_version("GLibUnix", "2.0")
        from gi.repository import GLibUnix
        signal_add = getattr(GLibUnix, "signal_add", None)
    except (ValueError, ImportError):
        GLibUnix = None
    signal_add = signal_add or getattr(GLib, "unix_signal_add", None) or getattr(GLibUnix, "signal_add_full", None)
    for sig in (2, 15):   # SIGINT、SIGTERM：好好退出
        signal_add(GLib.PRIORITY_DEFAULT, sig, lambda *_: (pet.quit(), False)[1])
    if leader is not None:
        for pid in store.roster():   # 把名单上别的几只也叫出来
            if pid != store.pet_id and not claude.pet_running(pid):
                GLib.timeout_add(1500, lambda p=pid: (Pet.launch_pet(p), False)[1])
    app.exec()
    store.flush_settings()   # 还没写进文件的大小、位置（save_settings_soon）
    pet.hub.close()
    srv.close()
    pid_file = store.STATE / "pet.pid"
    mine = store.read_text(pid_file) == str(os.getpid())
    for f in (claude.socket_path(store.pet_id), pid_file if mine else None):
        if f:
            try:
                f.unlink()
            except OSError:
                pass
    store.log("退出")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
