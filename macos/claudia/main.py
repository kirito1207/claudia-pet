"""claudia-pet macOS 版入口（对应 Windows 版 app/src/Program.cs，照 Linux 版 linux/claudia/main.py 改）。

  claudia-pet                       打开（已经开着就让她回到默认位置）
  claudia-pet --toggle              藏起来 / 再执行一次出来
  claudia-pet --show | --quit       叫出来 / 退出
  claudia-pet --ensure              给 Claude 用：没开就打开（Claude Code 插件、桌面端 MCP 调）
  claudia-pet --mcp                 给 Claude 桌面端用的 MCP 服务（stdio）
  claudia-pet --connect [plugin|mcp|all] / --disconnect [...]   接入 / 撤销
  claudia-pet --pet 2 [--mode desktop|claude]                   多开的那几只，由第一只带起来
"""
import os
import platform
import signal
import sys

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

from claudia import claude, store  # noqa: E402


def arg_after(args, flag):
    i = args.index(flag)
    return args[i + 1] if i + 1 < len(args) else None


def choose_run_mode(fresh, by_claude):
    """还没选过运行方式时选一次：第一次用、自己打开、又没接入 Claude 的，才设成「独立运行」"""
    try:
        connected = claude.connected()
    except Exception:
        connected = False
    standalone = fresh and not by_claude and not connected
    store.set_standalone(standalone)
    if standalone and "mode" not in store.settings:
        store.settings["mode"] = "desktop"
        store.save_settings()
    store.log(f"运行方式：{'独立运行' if standalone else '跟着 Claude'}（第一次用 = {fresh}，Claude 叫出来的 = {by_claude}，已接入 Claude = {connected}）")


def main(args):
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
        for t in (["plugin", "mcp"] if what == "all" else [what]):
            try:
                if t == "plugin":
                    msg = claude.set_plugin(install)
                elif t == "mcp":
                    msg = claude.set_mcp(install)
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
            if claude.send_command(flag[2:]):
                return 0
            if flag != "--show":
                print(store.APP_NAME + " 没在运行", file=sys.stderr)
                return 1
            break   # --show 而她没开：照常打开

    if sys.platform != "darwin":
        print("这是 claudia-pet 的 macOS 版；Linux 请用 linux/claudia-pet", file=sys.stderr)
        return 2
    first_mode = None
    if "--pet" in args:
        pid = arg_after(args, "--pet")
        if not store.valid_pet_id(pid):
            return 2
        store.pet_id = pid
        if "--mode" in args and arg_after(args, "--mode") in ("desktop", "claude"):
            first_mode = arg_after(args, "--mode")

    main_pet = store.is_main()
    lock = claude.take_lock(store.pet_id)
    if lock is None:
        if main_pet:
            claude.send_command("show")
        return 0
    if not main_pet and not claude.pet_running("main"):
        store.log("第一只没在跑，不单独打开")
        return 0

    if not os.environ.get("CLAUDIA_DEBUG"):
        # WebKit、AppKit 的警告不往终端刷：写进 ~/.claudia/stderr.log
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
    try:
        import AppKit  # noqa: F401
        import WebKit  # noqa: F401
    except ImportError as e:
        store.log("缺少 PyObjC：" + str(e))
        print(store.APP_NAME + "：缺少 PyObjC，请重新运行 macos/install.sh（" + str(e) + "）", file=sys.stderr)
        return 1

    from claudia import system, ui
    from claudia.pet import Pet

    store.pet_dir().mkdir(parents=True, exist_ok=True)
    if main_pet:
        try:
            claude.USER_QUIT_FLAG.unlink()   # 自己打开了：恢复跟着 Claude 出现
        except OSError:
            pass
        store.STATE.mkdir(parents=True, exist_ok=True)
        (store.STATE / "pet.pid").write_text(str(os.getpid()))
        Pet.clear_signal()
    store.log(f"启动 {store.version()}，macOS {platform.mac_ver()[0]}（{platform.machine()}），Python {platform.python_version()}")
    fresh = main_pet and not (store.DATA / "settings.json").exists()
    store.load_settings()
    if first_mode and "mode" not in store.settings:
        store.settings["mode"] = first_mode
        store.save_settings()
    if main_pet and not store.has_run_mode():
        choose_run_mode(fresh, claude.BY_CLAUDE_ARG in args)

    global GUI_STARTED
    GUI_STARTED = True
    ui.setup_app()
    pet = Pet()
    pet.start()
    srv = system.serve(claude.socket_path(store.pet_id), pet.on_command)
    # SIGINT、SIGTERM：好好退出。Python 的信号处理要等下一段 Python 代码跑起来才执行，定时器 0.3 秒一次，来得及
    for sig in (signal.SIGINT, signal.SIGTERM):
        signal.signal(sig, lambda *_: pet.quit())
    if main_pet:
        for pid in store.extra_pets():   # 把上次多开的几只也叫出来
            ui.timeout_add(1500, lambda p=pid: (Pet.launch_pet(p), False)[1])
    ui.run()
    try:
        srv.close()
    except OSError:
        pass
    for f in (claude.socket_path(store.pet_id), store.STATE / "pet.pid" if main_pet else None):
        if f:
            try:
                f.unlink()
            except OSError:
                pass
    store.log("退出")
    return 0


GUI_STARTED = False

if __name__ == "__main__":
    code = main(sys.argv[1:])
    if GUI_STARTED:
        # 开过窗口的：收拾完直接结束进程。让 Python 慢慢回收 AppKit、WebKit 的对象，偶尔会在退出时崩一下，
        # 弹「Python 意外退出」
        sys.stdout.flush()
        os._exit(code)
    sys.exit(code)
