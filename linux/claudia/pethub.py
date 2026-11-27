"""多开协调（2026-11-27 重写：每只平级，负责人可以交接。Windows 版是 app/src/PetHub.cs，两边消息格式一样）。

每只桌宠是一个进程，代码完全一样，界面上也没有区别（菜单一样、都能被送走、「退出」只走自己）。
系统里只能有一份的东西——托盘图标、全局快捷键、任务通知、命令行入口（claudia-pet --show / --toggle / --quit）、
多开名单——由「负责人」管：谁拿到负责人锁（run/leader.lock）谁就是负责人，它开一个本机连接
（run/pets.sock，只有自己能连），别的几只连上来、一直连着。

- 名单（pets.json）只有负责人写，顺序就是界面上的「第几只」。加一只、送走、退出、改名、换地方、藏起来、
  改共用设置都是发给负责人的请求（带编号，负责人一定回一句成没成）；改的是别的那只，负责人转给它，
  它照做后回话，再把自己的新名字 / 待在哪报上来。名单变了负责人发给所有连着的，各自的设置页收到就刷新。
- 负责人走了（退出、被送走）：先把自己从名单去掉、告诉大家下一任是名单上的第一只、马上放开负责人锁再挥手走；
  下一任抢到锁就当负责人，别的重新连上它。负责人突然没了（崩溃）：大家按名单顺序轮流试，排第一的马上试，
  第二的等 2 秒才试，以此类推，所以还是名单上的下一只接手。
- 「退出」只走这一只；名单里最后一只也走了才记下「从菜单退出过」。托盘的「全部退出」大家一起走，名单留着下次一起回来。
- 「全部退出」时留一个记号（run/stopping），一直留到用户手动打开程序（见 main.py 的 choose_identity）：
  有它在，退出前就已经在启动、来晚了的那只不接手、不叫回别人，直接跟着走；负责人挥手那两秒里才来报到的，直接叫它走。
- 加一只：新的那只报到了才写进名单、才算成功（2026-11-27 Codex 复审：原来先写名单，负责人换了、全部退出、
  清理失败都可能留下一条没加成的，下次打开又被叫出来）。报到前它只在负责人的内存里（PetAdds，名单上显示
  「还在出来的路上」、占一个名额）；每次添加有自己的编号，30 秒没报到只把这一次去掉。起不来就马上说没成。
  新的那只启动时拿到这次添加的标记（--join），报到时带回来；对不上的（取消前叫出来、来晚了的旧进程）叫它走。
  刚取消的编号 30 秒内先不再用（旧进程可能还在启动、还没拿到这个编号的锁）。
  刚送走的那只还在挥手、编号都被占着时，先不加，叫用户等它走了再试。
- 共用设置只有负责人写：哪一只改，就把「哪一项、改成什么」交给负责人（appSet），它先写 app.json，写成了才叫大家重读。
- 各个 Claude Code 会话的状态（state 目录里的 claude-session-*.json）只有负责人扫，扫到变化发给大家（sessions）；
  新报到、断线重连的先收到一份完整的（负责人手上留着最近 10 分钟的），换了负责人新的那只第一次扫时整份发一遍。
  原来每只每 0.3 秒各扫一遍目录（2026-11-27 Codex 复审）。
- 每只正常走之前跟负责人说一声 bye。没说就断了的（崩溃、被杀）负责人 3 秒后把它叫回来；新负责人接手时也把名单上
  没在跑的（比如崩溃的上一任）叫回来。同一只 10 分钟内最多叫回 3 次，免得反复崩溃一直重开。

一行一条 JSON：
  别的 → 负责人：hello {id, name, mode, join}；state {name, mode}；done {rid}；bye {}
                 req {rid, op, id, action, value}，op：add | leave | pet | quitAll | hide | show | appSet（action = 哪一项）
  命令行 → 负责人：cli {cmd: show | toggle | quit}
  负责人 → 别的：roster {leader, pets: [{id, index, name, mode, online}]}；cmd {rid, action: mode | rename | dismiss, value}；
                 signal {what: hide | show | quit}；app {}；handoff {next}；sessions {states}；reply {rid, ok, error}

api 是桌宠（Pet）：hub_info() → {name, mode}；hub_apply(action, value)；hub_roster_changed()；hub_notice(text)；
hub_signal(what)（藏 / 出 / 一起走，负责人自己也走这里）；hub_app_changed()（共用设置变了，重读）；
别的几只还要 hub_sessions(states)（负责人扫到的会话状态）；
负责人还要 hub_launch(id, mode, join)、hub_running(id)、hub_leaving()、hub_toggle()、hub_user_quit()（记下从菜单退出过）、hub_leave_self()；
别的几只还要 hub_try_lead()（抢负责人锁，抢到就当负责人，返回 True）、hub_main_lost()（没人负责了，跟着走）。
"""
import itertools
import json
import math
import os
import secrets
import socket
import time

from gi.repository import GLib

from . import store

ACTIONS = ("mode", "rename", "dismiss")


def hub_path():
    return store.RUN / "pets.sock"


def stop_path():
    return store.RUN / "stopping"


def stopping():
    """「全部退出」过、用户还没手动打开（2026-11-27 Codex 复审：原来 30 秒后就放行，来得更晚的又会把大家叫回来）"""
    return stop_path().exists()


def clear_stopping():
    try:
        os.unlink(stop_path())
    except OSError:
        pass


def valid_session(s):
    """一条会话状态能不能用：会话编号是非空字符串、时间戳是有限的数字（不是 True / False，也不是 NaN、Infinity——
    Python 读 JSON 会把它们当数字收下，跟谁比都不对，既淘汰不掉也分不出新旧）。坏记录只跳过那一条，不拖累整批"""
    ts = s.get("ts") if isinstance(s, dict) else None
    return (isinstance(s, dict) and isinstance(s.get("sid"), str) and s["sid"] != ""
            and isinstance(ts, (int, float)) and not isinstance(ts, bool) and math.isfinite(ts))


class SessionBook:
    """会话编号 → 最近的状态，只留 10 分钟内的。负责人用它给新报到的补一份完整的，每只桌宠用它在网页重新加载时重放
    （2026-11-27 收拢：原来负责人、桌宠各写了一份同样的合并 / 过期）"""

    KEEP_S = 600

    def __init__(self):
        self.by_sid = {}

    def merge(self, states):
        """收下一批：坏记录跳过，同一个会话留新的，过期的扔掉。返回收下的（原样顺序）"""
        good = [s for s in states if valid_session(s)]
        for st in good:
            old = self.by_sid.get(st["sid"])
            if not old or old["ts"] <= st["ts"]:
                self.by_sid[st["sid"]] = st
        cutoff = time.time() - self.KEEP_S
        self.by_sid = {k: v for k, v in self.by_sid.items() if v["ts"] >= cutoff}
        return good

    def values(self):
        return list(self.by_sid.values())

    def __iter__(self):
        return iter(self.by_sid)

    def __len__(self):
        return len(self.by_sid)


class PetAdds:
    """新加桌宠的几种情况都在这一处（2026-11-27 收拢：原来散在 adding / cancelled 两张表和好几处判断里）。
    一个编号要么空着，要么是下面两种之一：
      等报到：叫出来了，手上有这次添加的暗号和回话。报到、暗号对上 → confirm 交出回话，负责人这时才写名单；
              暗号对不上（取消前叫出来、来晚了的旧进程）→ 叫它走，这一次照旧等着
      暂留：叫出来过但没加成（等不到、取消），旧进程可能还在启动、还没拿到这个编号的锁，hold 秒内先不再用，过了自然空出来
    等报到 → 没成（起不来 / 等不到 / 取消）：end 交出回话；起不来的根本没有进程，不用暂留。
    每次添加的暗号不同：超时只去掉自己那一次，同一个编号后来又加的一次不受影响"""

    def __init__(self):
        self.waiting = {}   # 编号 → (暗号, 回话)
        self.held = {}      # 编号 → 什么时候没加成（monotonic 秒）

    def __contains__(self, pid):
        return pid in self.waiting

    def ids(self):
        return list(self.waiting)

    def free(self, pid, now, hold):
        return pid not in self.waiting and now - self.held.get(pid, -1e9) >= hold

    def begin(self, pid, reply):
        token = secrets.token_hex(6)   # 随机的：换了负责人也不会和旧进程手上的撞上
        self.waiting[pid] = (token, reply)
        return token

    def token(self, pid):
        return self.waiting[pid][0] if pid in self.waiting else None

    def confirm(self, pid):
        return self.waiting.pop(pid)[1]

    def end(self, pid, token, hold, now):
        """这一次没成：交出回话。token 不是现在这一次的（编号又被加了一次）返回 None，什么都不动"""
        current = self.waiting.get(pid)
        if not current or current[0] != token:
            return None
        del self.waiting[pid]
        if hold:
            self.held[pid] = now
        return current[1]


def send_cli(cmd, path=None):
    """命令行（--show / --toggle / --quit、再次打开）交给负责人；没有负责人返回 False"""
    s = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
    try:
        s.settimeout(2)
        s.connect(str(path or hub_path()))
        s.sendall((json.dumps({"type": "cli", "cmd": cmd}) + "\n").encode())
        return True
    except OSError:
        return False
    finally:
        s.close()


class Link:
    """一条连接：收到的一行一条 JSON 交给 on_msg(link, msg)，断了调 on_close(link)。都在主循环里调"""

    def __init__(self, sock, on_msg, on_close):
        self.sock, self.on_msg, self.on_close = sock, on_msg, on_close
        self.buf, self.closed, self.id, self.bye = b"", False, None, False
        sock.settimeout(2)   # 只在读得到时才读；写的时候最多等 2 秒，对面卡死就当断了
        self.watch = GLib.io_add_watch(sock.fileno(), GLib.PRIORITY_DEFAULT, GLib.IO_IN | GLib.IO_HUP | GLib.IO_ERR, self._ready)

    def _ready(self, _fd, _cond):
        try:
            data = self.sock.recv(65536)
        except (BlockingIOError, socket.timeout):
            return True
        except OSError:
            data = b""
        if not data:
            self.close()
            return False
        self.buf += data
        while not self.closed and b"\n" in self.buf:
            line, self.buf = self.buf.split(b"\n", 1)
            try:
                msg = json.loads(line)
            except ValueError:
                continue
            if isinstance(msg, dict):
                self.on_msg(self, msg)
        return not self.closed

    def send(self, msg):
        if self.closed:
            return
        try:
            self.sock.sendall((json.dumps(msg, ensure_ascii=False) + "\n").encode())
        except OSError:
            self.close()

    def close(self):
        if self.closed:
            return
        self.closed = True
        if GLib.main_context_default().find_source_by_id(self.watch):
            GLib.source_remove(self.watch)
        try:
            self.sock.close()
        except OSError:
            pass
        self.on_close(self)


def _pet_settings(pid):
    f = store.DATA / "settings.json" if pid == "main" else store.DATA / "pets" / pid / "settings.json"
    return store.read_json(f) or {}


def _info(name, mode):
    return {"name": name if isinstance(name, str) else "", "mode": "desktop" if mode == "desktop" else "claude"}


def _info_from_settings(s):
    names = s.get("names") if isinstance(s.get("names"), dict) else {}
    return _info(names.get("claude"), s.get("mode"))


class HubServer:
    """负责人这一头：名单的唯一主人，转发指令、广播名单和信号"""

    RELAUNCH_MS, RELAUNCH_LIMIT, RELAUNCH_WINDOW_S = 3000, 3, 600
    ADD_WAIT_MS = 30000   # 新加的那只多久没报到就算没成

    def __init__(self, api, path=None, self_id=None):
        self.api, self.path = api, path or hub_path()
        self.self_id = self_id or store.pet_id
        self.relaunched = {}   # 编号 → 最近几次叫回来的时间（秒）
        self.clients = {}    # 编号 → Link（已经报过到的）
        self.info = {}       # 编号 → {name, mode}（报上来的）
        self.pending = {}    # 转发出去的编号 → (回话函数, 超时计时器)
        self.rids = itertools.count(1)
        self.srv = None
        self.closed = False
        self.stopping = False   # 全部退出了：再来报到的叫它走
        self.sessions = SessionBook()   # 新报到的先拿到一份完整的
        self.adds = PetAdds()           # 新加、还没报到的；报到了才写进名单
        self._broadcasting = self._again = False

    def start(self):
        store.RUN.mkdir(parents=True, exist_ok=True)
        try:   # 拿着负责人锁才会到这里：留下的旧 socket 文件是上一任的
            os.unlink(self.path)
        except OSError:
            pass
        srv = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
        srv.bind(str(self.path))
        os.chmod(self.path, 0o600)
        srv.listen(8)
        srv.setblocking(False)
        self.srv = srv
        self.watch = GLib.io_add_watch(srv.fileno(), GLib.PRIORITY_DEFAULT, GLib.IO_IN, self._accept)
        for f in (store.DATA / "pets-signal",):   # 原来的信号文件用不到了
            try:
                f.unlink()
            except OSError:
                pass

    def close(self):
        if self.closed:
            return
        self.closed = True
        if self.srv:
            if GLib.main_context_default().find_source_by_id(self.watch):
                GLib.source_remove(self.watch)
            self.srv.close()
            try:
                os.unlink(self.path)
            except OSError:
                pass
        for link in list(self.clients.values()):
            link.close()

    def _accept(self, *_):
        if self.closed:
            return False
        try:
            conn, _ = self.srv.accept()
        except OSError:
            return True
        Link(conn, self._on_msg, self._on_close)
        return True

    # ---- 名单 ----
    def members(self):
        """名单上的，加上新加、还没报到的（排在后面）"""
        ids = store.roster()
        return ids + [i for i in self.adds.ids() if i not in ids]

    def roster(self, self_id=None):
        self_id = self_id or self.self_id
        out = []
        for n, pid in enumerate(self.members(), 1):
            if pid == self.self_id:
                info = self.api.hub_info()
            else:
                info = self.info.get(pid) or _info_from_settings(_pet_settings(pid))
            out.append({"id": pid, "index": n, "self": pid == self_id, "name": info["name"], "mode": info["mode"],
                        "online": pid == self.self_id or pid in self.clients})
        return out

    def broadcast_roster(self):
        # 发的时候某一只断了，会在这一轮里面又要发一份（标它离线）；外面这一轮接着发的还是旧的（标它在线），
        # 大家最后收到的就是旧名单（2026-11-27 Codex 复审）。所以里面那次只记一笔，这一轮发完再按最新的补发
        if self._broadcasting:
            self._again = True
            return
        self._broadcasting = True
        try:
            while True:
                self._again = False
                pets = [{k: v for k, v in p.items() if k != "self"} for p in self.roster()]
                for link in list(self.clients.values()):
                    link.send({"type": "roster", "leader": self.self_id, "pets": pets})
                if not self._again:
                    break
        finally:
            self._broadcasting = False
        self.api.hub_roster_changed()

    def signal(self, what):
        """藏 / 出 / 一起走：发给连着的几只，负责人自己也照做"""
        for link in list(self.clients.values()):
            link.send({"type": "signal", "what": what})
        self.api.hub_signal(what)

    def report(self):
        """负责人自己的名字 / 待在哪变了"""
        self.broadcast_roster()

    def share_sessions(self, changed):
        """负责人扫到的会话状态变化：记下来（新报到的先拿到一份完整的），发给连着的几只"""
        changed = self.sessions.merge(changed)
        if changed:
            for link in list(self.clients.values()):
                link.send({"type": "sessions", "states": changed})

    # ---- 连着的那几只、命令行发来的 ----
    def _on_msg(self, link, msg):
        if self.closed:   # 关了以后才排到的消息：不收
            link.close()
            return
        t = msg.get("type")
        if t == "cli":
            self.cli(msg.get("cmd"))
            link.close()
        elif t == "hello":
            pid = msg.get("id")
            if not store.valid_pet_id(pid) or pid == self.self_id:
                link.close()
                return
            if self.stopping:   # 大家正在一起走：晚到的也跟着走
                link.send({"type": "signal", "what": "quit"})
                return
            if pid in self.adds:
                if msg.get("join") != self.adds.token(pid):   # 取消前叫出来的旧进程来晚了：不是这一次的，叫它走
                    link.send({"type": "cmd", "rid": 0, "action": "dismiss", "value": ""})
                    return
                if not self._confirm_add(pid, link):
                    return
            if pid not in store.roster():   # 已经被送走（还没出来时就送走了）：叫它走
                link.send({"type": "cmd", "rid": 0, "action": "dismiss", "value": ""})
                return
            old = self.clients.get(pid)
            if old and old is not link:
                old.close()
            link.id = pid
            self.clients[pid] = link
            self.info[pid] = _info(msg.get("name"), msg.get("mode"))
            self.broadcast_roster()
            if self.sessions:   # 新报到、断线重连：先给一份完整的会话状态
                link.send({"type": "sessions", "states": list(self.sessions.values())})
        elif t == "state" and link.id:
            self.info[link.id] = _info(msg.get("name"), msg.get("mode"))
            self.broadcast_roster()
        elif t == "req" and link.id:
            rid = msg.get("rid")
            self.request(msg.get("op"), msg.get("id"), msg.get("action"), msg.get("value"),
                         lambda ok, error="": link.send({"type": "reply", "rid": rid, "ok": ok, "error": error}),
                         sender=link.id)
        elif t == "bye":
            link.bye = True
        elif t == "done":
            p = self.pending.pop(msg.get("rid"), None)
            if p:
                GLib.source_remove(p[1])
                p[0](True)

    def _on_close(self, link):
        if link.id and self.clients.get(link.id) is link:
            del self.clients[link.id]
            self.info.pop(link.id, None)
            if not self.closed:
                self.broadcast_roster()
                if not link.bye:   # 没说再见就断了：崩溃、被杀
                    store.log(f"{link.id} 号没打招呼就不见了，等一下叫它回来")
                    self.relaunch_later(link.id)

    def adopt(self):
        """刚接手：名单上没在跑的（比如崩溃的上一任）叫回来"""
        for pid in store.roster():
            if pid != self.self_id:
                self.relaunch_later(pid)

    def relaunch_later(self, pid):
        def go():
            if (self.closed or self.stopping or stopping() or self.api.hub_leaving()
                    or pid not in store.roster() or self.api.hub_running(pid)):
                return False
            now = GLib.get_monotonic_time() / 1e6
            recent = [t for t in self.relaunched.get(pid, []) if now - t < self.RELAUNCH_WINDOW_S]
            if len(recent) >= self.RELAUNCH_LIMIT:
                store.log(f"{pid} 号最近反复退出，先不叫它回来")
                return False
            self.relaunched[pid] = recent + [now]
            self.api.hub_launch(pid, None)
            return False
        GLib.timeout_add(self.RELAUNCH_MS, go)

    def cli(self, cmd):
        if cmd == "show":   # 再次打开程序、托盘：大家都出来
            self.signal("show")
        elif cmd == "toggle":   # 隐藏快捷键
            self.api.hub_toggle()
        elif cmd == "quit":
            self.quit_all()

    def quit_all(self):
        """大家一起走，名单留着下次一起回来；留下记号，正在启动的那只别接手再把大家叫回来"""
        self.stopping = True
        try:
            store.RUN.mkdir(parents=True, exist_ok=True)
            stop_path().write_text("")
        except OSError:
            pass
        self.api.hub_user_quit()
        self.signal("quit")

    # ---- 请求（别的几只发来的，和负责人自己界面上的，走同一条路）----
    def request(self, op, pid=None, action=None, value=None, reply=None, sender=None):
        reply = reply or (lambda ok, error="": self.api.hub_notice(error) if not ok and error else None)
        sender = sender or self.self_id
        ids = self.members()
        if op == "add":
            if len(ids) >= store.MAX_PETS:
                return reply(False, f"已经有 {len(ids)} 只了，家里住不下啦")
            # 刚送走的那只还在挥手、占着它的编号：跳过还在跑的编号，不然新进程拿不到锁、起不来；都被占着就先不加
            now = time.monotonic()
            free = [i for i in ["main"] + [str(n) for n in range(2, store.MAX_PETS + 2)]
                    if i not in ids and not self.api.hub_running(i)
                    and self.adds.free(i, now, self.ADD_WAIT_MS / 1000)]
            if not free:
                return reply(False, "刚送走的那只还在挥手，等它走了再试")
            new = free[0]
            if new != "main":   # 这个编号以前用过、又送走了：旧设置不留给新来的（main 的设置在最上层，不删）
                try:
                    (store.DATA / "pets" / new / "settings.json").unlink()
                except OSError:
                    pass
            mode = "desktop" if value == "desktop" else "claude"
            token = self.adds.begin(new, reply)   # 报到了才写进名单、才回话
            # 它连上来报到之前名单就先按要它待的地方显示（不然先显示成默认的 Claude 窗口里，报到后才跳成桌面上）
            self.info[new] = _info("", mode)
            if not self.api.hub_launch(new, mode, token):
                self._end_add(new, token, "没能叫出新的一只", reserve=False)
                return None
            self.broadcast_roster()

            def timeout():   # 这一次等不到它报到：只去掉这一次（同一个编号后来又加了一次，那是另一次，不碰）
                if not self.closed:
                    self._end_add(new, token, "新的一只没能出来，名额腾出来了")
                return False

            GLib.timeout_add(self.ADD_WAIT_MS, timeout)
            return None   # 等它报到再回话
        if op == "leave":   # 菜单里的「退出」：只走发请求的这一只
            return reply(True) if self.leave([sender]) else reply(False, "没能记下名单，先没走")
        if op == "quitAll":
            reply(True)
            return self.quit_all()
        if op in ("hide", "show"):
            self.signal(op)
            return reply(True)
        if op == "appSet":   # 改一项共用设置：只有负责人写 app.json，写成了才叫大家重读
            ok, error = store.set_app(action, value)
            if not ok:
                return reply(False, error)
            for link in list(self.clients.values()):
                link.send({"type": "app"})
            self.api.hub_app_changed()
            return reply(True)
        if op != "pet" or action not in ACTIONS or pid not in ids:
            return reply(False, "这一只已经不在了")
        if pid in self.adds:   # 还在出来的路上
            if action != "dismiss":
                return reply(False, "那一只还没出来，等一下再试")
            self._end_add(pid, self.adds.token(pid), "这次添加取消了")
            return reply(True)
        if action == "dismiss":
            return reply(True) if self.leave([pid]) else reply(False, "没能记下名单，先没送走")
        if pid == self.self_id:
            self.api.hub_apply(action, value)
            return reply(True)
        link = self.clients.get(pid)
        if not link:
            return reply(False, "那一只还没出来，等一下再试")
        rid = next(self.rids)

        def timeout():
            p = self.pending.pop(rid, None)
            if p:
                p[0](False, "那一只没有回应")
            return False

        self.pending[rid] = (lambda ok, error="": reply(ok, error), GLib.timeout_add(5000, timeout))
        link.send({"type": "cmd", "rid": rid, "action": action, "value": value if isinstance(value, str) else ""})

    def _end_add(self, pid, token, error, reserve=True):
        """这一次添加没成（起不来、等不到、取消）：去掉、回话；不是这一次（编号又被加了一次）就不碰。没写过名单，不用改文件。
        reserve：叫出来过的（取消、等不到）编号先空一阵——那个进程可能还在启动；根本没起来的不用留（2026-11-27 Codex 复审）"""
        reply = self.adds.end(pid, token, reserve, time.monotonic())
        if not reply:
            return
        if pid not in self.clients:
            self.info.pop(pid, None)
        store.log(f"{pid} 号没加成：{error}")
        reply(False, error)
        self.broadcast_roster()

    def _confirm_add(self, pid, link):
        """新加的那只报到了：这时才写进名单、才回成功。写不进去就说没成，叫它走"""
        reply = self.adds.confirm(pid)
        try:
            store.save_roster([i for i in store.roster() if i != pid] + [pid])
        except OSError as e:
            store.log("保存多开名单失败: " + str(e))
            link.send({"type": "cmd", "rid": 0, "action": "dismiss", "value": ""})
            self.info.pop(pid, None)
            reply(False, "没能记下名单，新的一只先走了")
            self.broadcast_roster()
            return False
        reply(True)
        return True

    def leave(self, pids):
        """这几只从名单里去掉并挥手走（连着的告诉它，没连着的等它报到时再叫它走）。负责人自己也在里面：交给名单上的下一只"""
        keep = [i for i in store.roster() if i not in pids]
        try:
            store.save_roster(keep)
        except OSError as e:   # 没记下来就先不走：不然下次打开又会把它叫回来
            store.log("保存多开名单失败: " + str(e))
            return False
        if not keep:   # 最后一只也走了：之后 Claude 先不把她叫出来
            self.api.hub_user_quit()
        for pid in pids:
            link = self.clients.get(pid)
            if link:
                link.send({"type": "cmd", "rid": 0, "action": "dismiss", "value": ""})
        self.broadcast_roster()
        if self.self_id in pids:
            for link in list(self.clients.values()):
                link.send({"type": "handoff", "next": keep[0] if keep else None})
            self.api.hub_leave_self()   # 放开负责人锁、关掉连接，再挥手走
        return True


class HubClient:
    """不是负责人的那几只：连着负责人，名单以它发来的为准；负责人没了按名单顺序接手"""

    RETRY_MS, STEP_MS = 300, 2000
    START_WAIT_MS, LOST_WAIT_MS = 15000, 6000

    def __init__(self, api, path=None, self_id=None, join=None):
        self.api, self.path = api, path or hub_path()
        self.self_id = self_id or store.pet_id
        self.join = join if join is not None else store.join_token
        self.link = None
        self.pets = []           # 负责人最近发来的名单
        self.leader = None       # 现在的负责人是谁
        self.successor = None    # 负责人走之前说的下一任
        self.waiting = {}        # 请求编号 → 回话函数
        self.rids = itertools.count(1)
        self.leaving = False
        self.lost_since = None   # 断开（或还没连上）是从什么时候（毫秒）开始的
        self.ever_connected = False
        self.timer = None

    def start(self):
        self._search()

    def _search(self):
        """连负责人；连不上就按名单顺序看轮不轮到自己接手"""
        if self.leaving or self.timer:
            return
        self.lost_since = GLib.get_monotonic_time() // 1000
        self.timer = GLib.timeout_add(1, self._try)

    def _rank(self):
        """我排第几个接手（0 = 马上），不在候选里返回 None"""
        if self.pets:
            order = [p["id"] for p in self.pets if p.get("id") != self.leader and p.get("online", True)]
        else:   # 刚启动、还没拿到名单：按名单文件，负责人多半排在前面
            order = [i for i in store.roster() if i != self.leader]
        if self.successor in order:
            order.remove(self.successor)
            order.insert(0, self.successor)
        return order.index(self.self_id) if self.self_id in order else None

    def _try(self):
        self.timer = None
        if self.leaving:
            return False
        if self._connect():
            return False
        waited = GLib.get_monotonic_time() // 1000 - self.lost_since
        if stopping():   # 大家刚一起退出：不接手、不把大家叫回来，跟着走
            store.log("大家刚一起退出了，跟着走")
            self.leaving = True
            self.api.hub_main_lost()
            return False
        rank = self._rank()
        if rank is not None and waited >= rank * self.STEP_MS and self.api.hub_try_lead():
            store.log("接手当负责人")
            self.leaving = True   # 这一头不用了，桌宠已经换成负责人那一头
            return False
        limit = (self.LOST_WAIT_MS if self.ever_connected else self.START_WAIT_MS) + (rank or 0) * self.STEP_MS
        if waited >= limit:
            store.log("找不到负责人，跟着退出")
            self.leaving = True
            self.api.hub_main_lost()
            return False
        self.timer = GLib.timeout_add(self.RETRY_MS, self._try)
        return False

    def _connect(self):
        s = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
        try:
            s.settimeout(2)
            s.connect(str(self.path))
        except OSError:
            s.close()
            return False
        self.link = Link(s, self._on_msg, self._on_close)
        self.ever_connected = True
        info = self.api.hub_info()
        self.link.send({"type": "hello", "id": self.self_id, "name": info["name"], "mode": info["mode"],
                        "join": self.join})
        return True

    def close(self):
        """正常走：先跟负责人说再见（没说就断的会被叫回来）"""
        self.leaving = True
        if self.timer:
            GLib.source_remove(self.timer)
            self.timer = None
        link = self.link   # 发再见时对面可能已经断了（断开处理会把 self.link 清掉），先拿住
        if link:
            link.send({"type": "bye"})
            link.close()

    def _on_close(self, link):
        if link is not self.link:
            return
        self.link = None
        for done in self.waiting.values():
            done(False, "和负责人断开了")
        self.waiting.clear()
        self._search()

    def _on_msg(self, link, msg):
        t = msg.get("type")
        if t == "roster" and isinstance(msg.get("pets"), list):
            self.pets = [p for p in msg["pets"] if isinstance(p, dict)]
            self.leader = msg.get("leader")
            self.successor = None
            self.api.hub_roster_changed()
        elif t == "cmd" and msg.get("action") in ACTIONS:
            if msg["action"] == "dismiss":
                self.leaving = True
            self.api.hub_apply(msg["action"], msg.get("value"))
            if msg.get("rid"):
                link.send({"type": "done", "rid": msg["rid"]})
        elif t == "signal" and msg.get("what") in ("hide", "show", "quit"):
            if msg["what"] == "quit":
                self.leaving = True
            self.api.hub_signal(msg["what"])
        elif t == "app":
            self.api.hub_app_changed()
        elif t == "sessions" and isinstance(msg.get("states"), list):
            self.api.hub_sessions([s for s in msg["states"] if isinstance(s, dict)])
        elif t == "handoff":
            self.successor = msg.get("next")
        elif t == "reply":
            done = self.waiting.pop(msg.get("rid"), None)
            if done:
                done(bool(msg.get("ok")), msg.get("error") or "")

    def roster(self, self_id=None):
        self_id = self_id or self.self_id
        if not self.pets:   # 还没连上：至少列出自己
            info = self.api.hub_info()
            return [{"id": self_id, "index": 1, "self": True, "name": info["name"], "mode": info["mode"], "online": True}]
        return [dict(p, self=p.get("id") == self_id) for p in self.pets]

    def report(self):
        """自己的名字 / 待在哪变了：报给负责人（它再广播名单）"""
        if self.link:
            info = self.api.hub_info()
            self.link.send({"type": "state", "name": info["name"], "mode": info["mode"]})

    def request(self, op, pid=None, action=None, value=None, reply=None):
        reply = reply or (lambda ok, error="": self.api.hub_notice(error) if not ok and error else None)
        if not self.link:
            return reply(False, "正在换负责人，等一下再试")
        rid = next(self.rids)
        self.waiting[rid] = reply
        self.link.send({"type": "req", "rid": rid, "op": op, "id": pid, "action": action,
                        "value": value if isinstance(value, str) else ""})
