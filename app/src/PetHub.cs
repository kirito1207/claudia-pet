using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;

namespace Claudia
{
    // 多开协调（2026-11-27 重写：每只平级，负责人可以交接。Linux 版是 linux/claudia/pethub.py，两边消息格式一样）。
    //
    // 每只桌宠是一个进程，代码完全一样，界面上也没有区别（菜单一样、都能被送走、「退出」只走自己）。
    // 系统里只能有一份的东西——托盘图标、全局快捷键、任务通知、命令行入口（再次打开程序）、多开名单——由「负责人」管：
    // 谁拿到负责人锁（原来的单实例互斥量 Local\Claudia-用户名，插件 / MCP 看它判断在不在跑）谁就是负责人，
    // 它开一个命名管道（只有当前用户能连），别的几只连上来、一直连着。
    // - 名单（pets.json）只有负责人写，顺序就是界面上的「第几只」。加一只、送走、退出、改名、换地方、藏起来、改共用设置
    //   都是发给负责人的请求（带编号，负责人一定回一句成没成）；改的是别的那只，负责人转给它，它照做后回话，
    //   再把自己的新名字 / 待在哪报上来。名单变了负责人发给所有连着的，各自的设置页收到就刷新。
    // - 负责人走了（退出、被送走）：先把自己从名单去掉、告诉大家下一任是名单上的第一只、马上放开负责人锁再挥手走；
    //   下一任抢到锁就当负责人，别的重新连上它。负责人突然没了（崩溃）：大家按名单顺序轮流试，排第一的马上试，
    //   第二的等 2 秒才试，以此类推，所以还是名单上的下一只接手。
    // - 「退出」只走这一只；名单里最后一只也走了才记下「从菜单退出过」。托盘的「全部退出」大家一起走，名单留着下次一起回来。
    // - 每只正常走之前跟负责人说一声 bye。没说就断了的（崩溃、被杀）负责人 3 秒后把它叫回来；新负责人接手时也把名单上
    //   没在跑的（比如崩溃的上一任）叫回来。同一只 10 分钟内最多叫回 3 次，免得反复崩溃一直重开。
    // 一行一条 JSON：
    //   别的 → 负责人：hello {id, name, mode}；state {name, mode}；done {rid}；bye {}
    //                  req {rid, op, id, action, value}，op：add | leave | pet | quitAll | hide | show | app
    //   命令行 → 负责人：cli {cmd: show | toggle | quit}
    //   负责人 → 别的：roster {leader, pets: [{id, index, name, mode, online}]}；cmd {rid, action: mode | rename | dismiss, value}；
    //                  signal {what: hide | show | quit}；app {}；handoff {next}；reply {rid, ok, error}

    /// <summary>桌宠（Pet）给多开协调用的几样东西。回调都在 Post 交回来的线程（界面线程）上调</summary>
    interface IPetHubHost
    {
        Dictionary<string, object> HubInfo();   // {name, mode}
        void HubApply(string action, string value);
        void HubRosterChanged();
        void HubNotice(string text);
        void HubSignal(string what);              // 藏 / 出 / 一起走，负责人自己也走这里
        void HubAppChanged();                     // 共用设置变了，重读
        void HubLaunch(string id, string mode);   // 负责人用
        bool HubRunning(string id);               // 负责人用：这一只在不在跑
        bool HubLeaving();                        // 负责人用：自己是不是正要走
        void HubToggle();                         // 负责人用：隐藏快捷键
        void HubUserQuit();                       // 负责人用：记下从菜单退出过
        void HubLeaveSelf();                      // 负责人用：放开负责人锁、关掉连接，再挥手走
        bool HubTryLead();                        // 别的几只用：抢负责人锁，抢到就当负责人
        void HubMainLost();                       // 别的几只用：没人负责了，跟着走
        void Post(Action a);                      // 交给界面线程
        void Later(int ms, Action a);             // 界面线程上的计时
    }

    /// <summary>一条连接：读线程收一行交给 onMsg（经 Post 回界面线程），断了调 onClose。
    /// 两头都用 PipeOptions.Asynchronous 打开：同步方式打开的管道，读和写在系统里排队，读线程一直等着读，界面线程就写不出去</summary>
    sealed class PetLink
    {
        readonly PipeStream pipe;
        readonly StreamWriter writer;
        readonly object writeLock = new object();
        public string Id;
        public bool Bye;
        public bool Closed { get; private set; }

        public PetLink(PipeStream pipe, IPetHubHost host, Action<PetLink, Dictionary<string, object>> onMsg, Action<PetLink> onClose)
        {
            this.pipe = pipe;
            writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
            new Thread(() =>
            {
                try
                {
                    using (var reader = new StreamReader(pipe, new UTF8Encoding(false)))
                        for (string line; (line = reader.ReadLine()) != null;)
                        {
                            Dictionary<string, object> msg;
                            try { msg = Store.Json.DeserializeObject(line) as Dictionary<string, object>; } catch { continue; }
                            if (msg != null) host.Post(() => { if (!Closed) onMsg(this, msg); });
                        }
                }
                catch { }
                host.Post(() => { if (!Closed) { Close(); onClose(this); } });
            }) { IsBackground = true, Name = "PetLink" }.Start();
        }

        public void Send(Dictionary<string, object> msg)
        {
            if (Closed) return;
            try { lock (writeLock) writer.WriteLine(Store.Json.Serialize(msg)); }
            catch { Close(); }   // 读线程会发现断了，走 onClose
        }

        public void Close()
        {
            if (Closed) return;
            Closed = true;
            // 负责人这头先断开再关：读线程正等着读的时候只 Dispose，句柄要等那次读完才真的关，对面就一直不知道断了
            if (pipe is NamedPipeServerStream server) try { server.Disconnect(); } catch { }
            try { pipe.Dispose(); } catch { }
        }
    }

    static class PetHubNames
    {
        // 同一个用户、同一个登录会话里才是一家（和 Local\ 下的互斥量一样，远程桌面的另一个会话是另一家）
        public static string Pipe => "Claudia-Pets-" + Environment.UserName + "-" + Process.GetCurrentProcess().SessionId;
        public static readonly string[] Actions = { "mode", "rename", "dismiss" };
        public static string S(Dictionary<string, object> m, string k) => m != null && m.TryGetValue(k, out var v) ? v as string : null;
        public static int I(Dictionary<string, object> m, string k) => m.TryGetValue(k, out var v) && v is int n ? n : 0;
        public static Dictionary<string, object> Info(string name, string mode) =>
            new Dictionary<string, object> { ["name"] = name ?? "", ["mode"] = mode == "desktop" ? "desktop" : "claude" };
        public static Dictionary<string, object> InfoFromSettings(Dictionary<string, object> s) =>
            Info(s.TryGetValue("names", out var nv) && nv is Dictionary<string, object> names && names.TryGetValue("claude", out var cn) ? cn as string : "",
                S(s, "mode"));

        /// <summary>命令行（再次打开程序）交给负责人；没有负责人返回 false</summary>
        public static bool SendCli(string cmd, string pipeName = null, int timeoutMs = 1000)
        {
            try
            {
                using (var p = new NamedPipeClientStream(".", pipeName ?? Pipe, PipeDirection.InOut, PipeOptions.Asynchronous))
                {
                    p.Connect(timeoutMs);   // 管道不存在时 .NET 会一直试到超时：启动时找负责人用短一点的
                    var bytes = new UTF8Encoding(false).GetBytes(Store.Json.Serialize(new Dictionary<string, object> { ["type"] = "cli", ["cmd"] = cmd }) + "\n");
                    p.Write(bytes, 0, bytes.Length);
                    p.Flush();
                    return true;
                }
            }
            catch { return false; }
        }
    }

    interface IPetHub
    {
        void Start();
        void Close();
        List<Dictionary<string, object>> Roster(string selfId);
        void Report();
        void Request(string op, string id = null, string action = null, string value = null, Action<bool, string> reply = null);
    }

    /// <summary>负责人这一头：名单的唯一主人，转发指令、广播名单和信号</summary>
    sealed class PetHubServer : IPetHub
    {
        public int RelaunchMs = 3000;
        const int RelaunchLimit = 3, RelaunchWindowS = 600;
        readonly IPetHubHost host;
        readonly string pipeName, selfId;
        readonly Dictionary<string, PetLink> clients = new Dictionary<string, PetLink>();
        readonly Dictionary<string, Dictionary<string, object>> info = new Dictionary<string, Dictionary<string, object>>();
        readonly Dictionary<int, Action<bool, string>> pending = new Dictionary<int, Action<bool, string>>();
        readonly Dictionary<string, List<int>> relaunched = new Dictionary<string, List<int>>();
        int nextRid;
        volatile bool closed;
        NamedPipeServerStream listening;
        public bool Closed => closed;
        public IEnumerable<string> Connected => clients.Keys;

        public PetHubServer(IPetHubHost host, string pipeName = null, string selfId = null)
        {
            this.host = host; this.pipeName = pipeName ?? PetHubNames.Pipe; this.selfId = selfId ?? Store.PetId;
        }

        public void Start()
        {
            try { File.Delete(Path.Combine(Store.Data, "pets-signal")); } catch { }   // 原来的信号文件、指令目录用不到了
            try { Directory.Delete(Path.Combine(Store.Data, "pets-cmd"), true); } catch { }
            var security = new PipeSecurity();
            security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User, PipeAccessRights.FullControl, AccessControlType.Allow));
            new Thread(() =>
            {
                while (!closed)
                {
                    NamedPipeServerStream ps;
                    try
                    {
                        ps = new NamedPipeServerStream(pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096, security);
                        listening = ps;
                        ps.WaitForConnection();
                    }
                    catch (Exception e)
                    {
                        if (closed) return;
                        Store.Log("多开连接出错: " + e.Message);
                        Thread.Sleep(1000);
                        continue;
                    }
                    if (closed) { ps.Dispose(); return; }
                    // 交给界面线程时 hub 可能已经关了（排在关之后）：那就不收这条连接，免得留下一条没人管的
                    host.Post(() => { if (closed) { try { ps.Disconnect(); } catch { } ps.Dispose(); } else new PetLink(ps, host, OnMsg, OnClose); });
                }
            }) { IsBackground = true, Name = "PetHub" }.Start();
        }

        public void Close()
        {
            if (closed) return;
            closed = true;
            try { listening?.Dispose(); } catch { }
            foreach (var link in clients.Values.ToList()) link.Close();
        }

        // ---- 名单 ----
        static string SettingsFileOf(string id) => id == "main" ? Path.Combine(Store.Data, "settings.json") : Path.Combine(Store.Data, "pets", id, "settings.json");

        public List<Dictionary<string, object>> Roster(string self = null)
        {
            self = self ?? selfId;
            var list = new List<Dictionary<string, object>>();
            var n = 0;
            foreach (var id in Store.Roster())
            {
                var i = id == selfId ? host.HubInfo() : info.TryGetValue(id, out var known) ? known
                    : PetHubNames.InfoFromSettings(Store.ReadJson(SettingsFileOf(id)) ?? new Dictionary<string, object>());
                list.Add(new Dictionary<string, object>
                {
                    ["id"] = id, ["index"] = ++n, ["self"] = id == self, ["name"] = i["name"], ["mode"] = i["mode"],
                    ["online"] = id == selfId || clients.ContainsKey(id),
                });
            }
            return list;
        }

        void BroadcastRoster()
        {
            var pets = Roster().Select(p => p.Where(kv => kv.Key != "self").ToDictionary(kv => kv.Key, kv => kv.Value)).ToArray();
            foreach (var link in clients.Values.ToList()) link.Send(new Dictionary<string, object> { ["type"] = "roster", ["leader"] = selfId, ["pets"] = pets });
            host.HubRosterChanged();
        }

        /// <summary>藏 / 出 / 一起走：发给连着的几只，负责人自己也照做</summary>
        public void Signal(string what)
        {
            foreach (var link in clients.Values.ToList()) link.Send(new Dictionary<string, object> { ["type"] = "signal", ["what"] = what });
            host.HubSignal(what);
        }

        public void Report() => BroadcastRoster();   // 负责人自己的名字 / 待在哪变了

        /// <summary>刚接手：名单上没在跑的（比如崩溃的上一任）叫回来</summary>
        public void Adopt() { foreach (var id in Store.Roster()) if (id != selfId) RelaunchLater(id); }

        void RelaunchLater(string id)
        {
            host.Later(RelaunchMs, () =>
            {
                if (closed || host.HubLeaving() || !Store.Roster().Contains(id) || host.HubRunning(id)) return;
                var now = Environment.TickCount / 1000;
                var recent = (relaunched.TryGetValue(id, out var r) ? r : new List<int>()).Where(t => now - t < RelaunchWindowS).ToList();
                if (recent.Count >= RelaunchLimit) { Store.Log($"{id} 号最近反复退出，先不叫它回来"); return; }
                recent.Add(now); relaunched[id] = recent;
                host.HubLaunch(id, null);
            });
        }

        // ---- 连着的那几只、命令行发来的 ----
        void OnMsg(PetLink link, Dictionary<string, object> msg)
        {
            if (closed) { link.Close(); return; }   // 关了以后才排到的消息：不收
            switch (PetHubNames.S(msg, "type"))
            {
                case "cli":
                    Cli(PetHubNames.S(msg, "cmd"));
                    link.Close();
                    break;
                case "hello":
                    var id = PetHubNames.S(msg, "id");
                    if (!Store.ValidPetId(id) || id == selfId) { link.Close(); return; }
                    if (!Store.Roster().Contains(id)) { Cmd(link, 0, "dismiss", ""); return; }   // 还没出来时就被送走了：叫它走
                    if (clients.TryGetValue(id, out var old) && old != link) old.Close();
                    link.Id = id; clients[id] = link;
                    info[id] = PetHubNames.Info(PetHubNames.S(msg, "name"), PetHubNames.S(msg, "mode"));
                    BroadcastRoster();
                    break;
                case "state":
                    if (link.Id == null) return;
                    info[link.Id] = PetHubNames.Info(PetHubNames.S(msg, "name"), PetHubNames.S(msg, "mode"));
                    BroadcastRoster();
                    break;
                case "req":
                    if (link.Id == null) return;
                    var rid = PetHubNames.I(msg, "rid");
                    Request(PetHubNames.S(msg, "op"), PetHubNames.S(msg, "id"), PetHubNames.S(msg, "action"), PetHubNames.S(msg, "value"),
                        (ok, error) => link.Send(new Dictionary<string, object> { ["type"] = "reply", ["rid"] = rid, ["ok"] = ok, ["error"] = error ?? "" }),
                        link.Id);
                    break;
                case "bye":
                    link.Bye = true;
                    break;
                case "done":
                    var done = PetHubNames.I(msg, "rid");
                    if (pending.TryGetValue(done, out var answer)) { pending.Remove(done); answer(true, ""); }
                    break;
            }
        }

        void OnClose(PetLink link)
        {
            if (link.Id == null || !clients.TryGetValue(link.Id, out var known) || known != link) return;
            clients.Remove(link.Id); info.Remove(link.Id);
            if (closed) return;
            BroadcastRoster();
            if (!link.Bye) { Store.Log($"{link.Id} 号没打招呼就不见了，等一下叫它回来"); RelaunchLater(link.Id); }   // 崩溃、被杀
        }

        void Cli(string cmd)
        {
            if (cmd == "show") Signal("show");   // 再次打开程序：大家都出来
            else if (cmd == "toggle") host.HubToggle();
            else if (cmd == "quit") QuitAll();
        }

        /// <summary>大家一起走，名单留着下次一起回来</summary>
        void QuitAll() { host.HubUserQuit(); Signal("quit"); }

        static void Cmd(PetLink link, int rid, string action, string value) =>
            link.Send(new Dictionary<string, object> { ["type"] = "cmd", ["rid"] = rid, ["action"] = action, ["value"] = value ?? "" });

        // ---- 请求（别的几只发来的，和负责人自己界面上的，走同一条路）----
        public void Request(string op, string id = null, string action = null, string value = null, Action<bool, string> reply = null) =>
            Request(op, id, action, value, reply, selfId);

        void Request(string op, string id, string action, string value, Action<bool, string> reply, string sender)
        {
            reply = reply ?? ((ok, error) => { if (!ok && !string.IsNullOrEmpty(error)) host.HubNotice(error); });
            var ids = Store.Roster();
            switch (op)
            {
                case "add":
                    if (ids.Count >= Store.MaxPets) { reply(false, $"已经有 {ids.Count} 只了，家里住不下啦"); return; }
                    var fresh = Store.FreePetId(ids);
                    // 这个编号以前用过、又送走了：旧设置不留给新来的（main 的设置在最上层，不删）
                    if (fresh != "main") try { var old = Path.Combine(Store.Data, "pets", fresh, "settings.json"); if (File.Exists(old)) File.Delete(old); } catch { }
                    try { Store.SaveRoster(ids.Concat(new[] { fresh })); }
                    catch (Exception e) { Store.Log("保存多开名单失败: " + e.Message); reply(false, "没能记下名单"); return; }
                    // 它连上来报到之前名单就先按要它待的地方显示（不然先显示成默认的 Claude 窗口里，报到后才跳成桌面上）
                    info[fresh] = PetHubNames.Info("", value);
                    host.HubLaunch(fresh, value == "desktop" ? "desktop" : "claude");
                    BroadcastRoster();
                    reply(true, ""); return;
                case "leave": Leave(new List<string> { sender }); reply(true, ""); return;   // 菜单里的「退出」：只走发请求的这一只
                case "quitAll": reply(true, ""); QuitAll(); return;
                case "hide": case "show": Signal(op); reply(true, ""); return;
                case "app":   // 共用设置变了：大家重读
                    foreach (var link in clients.Values.ToList()) link.Send(new Dictionary<string, object> { ["type"] = "app" });
                    host.HubAppChanged();
                    reply(true, ""); return;
            }
            if (op != "pet" || !PetHubNames.Actions.Contains(action) || !ids.Contains(id)) { reply(false, "这一只已经不在了"); return; }
            if (action == "dismiss") { Leave(new List<string> { id }); reply(true, ""); return; }
            if (id == selfId) { host.HubApply(action, value); reply(true, ""); return; }
            if (!clients.TryGetValue(id, out var target)) { reply(false, "那一只还没出来，等一下再试"); return; }
            var rid = ++nextRid;
            pending[rid] = reply;
            host.Later(5000, () => { if (pending.TryGetValue(rid, out var r)) { pending.Remove(rid); r(false, "那一只没有回应"); } });
            Cmd(target, rid, action, value);
        }

        /// <summary>这几只从名单里去掉并挥手走（连着的告诉它，没连着的等它报到时再叫它走）。负责人自己也在里面：交给名单上的下一只</summary>
        void Leave(List<string> ids)
        {
            var keep = Store.Roster().Where(i => !ids.Contains(i)).ToList();
            try { Store.SaveRoster(keep); } catch (Exception e) { Store.Log("保存多开名单失败: " + e.Message); }
            if (keep.Count == 0) host.HubUserQuit();   // 最后一只也走了：之后 Claude 先不把她叫出来
            foreach (var id in ids) if (clients.TryGetValue(id, out var link)) Cmd(link, 0, "dismiss", "");
            BroadcastRoster();
            if (ids.Contains(selfId))
            {
                foreach (var link in clients.Values.ToList()) link.Send(new Dictionary<string, object> { ["type"] = "handoff", ["next"] = keep.FirstOrDefault() });
                host.HubLeaveSelf();
            }
        }
    }

    /// <summary>不是负责人的那几只：连着负责人，名单以它发来的为准；负责人没了按名单顺序接手</summary>
    sealed class PetHubClient : IPetHub
    {
        public int RetryMs = 300, StepMs = 2000, StartWaitMs = 15000, LostWaitMs = 6000;
        readonly IPetHubHost host;
        readonly string pipeName, id;
        PetLink link;
        List<Dictionary<string, object>> pets = new List<Dictionary<string, object>>();
        string leader, successor;
        readonly Dictionary<int, Action<bool, string>> waiting = new Dictionary<int, Action<bool, string>>();
        int nextRid, lostSince;
        bool leaving, everConnected, searching;

        public PetHubClient(IPetHubHost host, string id = null, string pipeName = null)
        {
            this.host = host; this.id = id ?? Store.PetId; this.pipeName = pipeName ?? PetHubNames.Pipe;
        }

        public void Start() => Search();
        public bool Connected => link != null && !link.Closed;
        public string DebugState => $"link={(link == null ? "none" : link.Closed ? "closed" : "open")} searching={searching} leaving={leaving} everConnected={everConnected}";

        /// <summary>连负责人；连不上就按名单顺序看轮不轮到自己接手</summary>
        void Search()
        {
            if (leaving || searching) return;
            searching = true;
            lostSince = Environment.TickCount;
            host.Later(1, Attempt);
        }

        /// <summary>在后台线程上试连一次（Connect 会等一小会儿），结果回界面线程处理</summary>
        void Attempt()
        {
            if (leaving) { searching = false; return; }
            new Thread(() =>
            {
                NamedPipeClientStream pipe = null;
                var p = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                try { p.Connect(250); pipe = p; } catch { p.Dispose(); }
                host.Post(() => AfterAttempt(pipe));
            }) { IsBackground = true, Name = "PetHubConnect" }.Start();
        }

        /// <summary>我排第几个接手（0 = 马上），不在候选里返回 null</summary>
        int? Rank()
        {
            var order = pets.Count > 0
                ? pets.Where(p => PetHubNames.S(p, "id") != leader && !(p.TryGetValue("online", out var o) && o is bool b && !b)).Select(p => PetHubNames.S(p, "id")).ToList()
                : Store.Roster().Where(i => i != leader).ToList();   // 刚启动、还没拿到名单：按名单文件，负责人多半排在前面
            if (successor != null && order.Remove(successor)) order.Insert(0, successor);
            var i = order.IndexOf(id);
            return i < 0 ? (int?)null : i;
        }

        void AfterAttempt(NamedPipeClientStream pipe)
        {
            if (leaving) { pipe?.Dispose(); searching = false; return; }
            if (pipe != null) { searching = false; Attach(pipe); return; }
            var waited = Environment.TickCount - lostSince;
            var rank = Rank();
            if (rank != null && waited >= rank.Value * StepMs && host.HubTryLead())
            {
                Store.Log("接手当负责人");
                leaving = true; searching = false;   // 这一头不用了，桌宠已经换成负责人那一头
                return;
            }
            var limit = (everConnected ? LostWaitMs : StartWaitMs) + (rank ?? 0) * StepMs;
            if (waited >= limit) { Store.Log("找不到负责人，跟着退出"); leaving = true; searching = false; host.HubMainLost(); return; }
            host.Later(RetryMs, Attempt);
        }

        void Attach(NamedPipeClientStream pipe)
        {
            link = new PetLink(pipe, host, OnMsg, OnClose);
            everConnected = true;
            var info = host.HubInfo();
            link.Send(new Dictionary<string, object> { ["type"] = "hello", ["id"] = id, ["name"] = info["name"], ["mode"] = info["mode"] });
        }

        /// <summary>正常走：先跟负责人说再见（没说就断的会被叫回来）</summary>
        public void Close()
        {
            leaving = true;
            var l = link;   // 发再见时对面可能已经断了（断开处理会把 link 清掉），先拿住
            if (l == null) return;
            l.Send(new Dictionary<string, object> { ["type"] = "bye" });
            l.Close();
        }

        /// <summary>测试用：不说再见就断开（模拟崩溃）</summary>
        public void Drop() { leaving = true; link?.Close(); }

        void OnClose(PetLink closed)
        {
            if (closed != link) return;
            link = null;
            foreach (var w in waiting.Values.ToList()) w(false, "和负责人断开了");
            waiting.Clear();
            Search();
        }

        void OnMsg(PetLink from, Dictionary<string, object> msg)
        {
            switch (PetHubNames.S(msg, "type"))
            {
                case "roster":
                    if (msg.TryGetValue("pets", out var p) && p is object[] list)
                    {
                        pets = list.OfType<Dictionary<string, object>>().ToList();
                        leader = PetHubNames.S(msg, "leader");
                        successor = null;
                        host.HubRosterChanged();
                    }
                    break;
                case "cmd":
                    var action = PetHubNames.S(msg, "action");
                    if (!PetHubNames.Actions.Contains(action)) return;
                    if (action == "dismiss") leaving = true;
                    host.HubApply(action, PetHubNames.S(msg, "value"));
                    var rid = PetHubNames.I(msg, "rid");
                    if (rid != 0) from.Send(new Dictionary<string, object> { ["type"] = "done", ["rid"] = rid });
                    break;
                case "signal":
                    var what = PetHubNames.S(msg, "what");
                    if (what != "hide" && what != "show" && what != "quit") return;
                    if (what == "quit") leaving = true;
                    host.HubSignal(what);
                    break;
                case "app":
                    host.HubAppChanged();
                    break;
                case "handoff":
                    successor = PetHubNames.S(msg, "next");
                    break;
                case "reply":
                    var r = PetHubNames.I(msg, "rid");
                    if (waiting.TryGetValue(r, out var done))
                    {
                        waiting.Remove(r);
                        done(msg.TryGetValue("ok", out var ok) && ok is bool b && b, PetHubNames.S(msg, "error") ?? "");
                    }
                    break;
            }
        }

        public List<Dictionary<string, object>> Roster(string selfId = null)
        {
            selfId = selfId ?? id;
            if (pets.Count == 0)   // 还没连上：至少列出自己
            {
                var info = host.HubInfo();
                return new List<Dictionary<string, object>> { new Dictionary<string, object>
                    { ["id"] = selfId, ["index"] = 1, ["self"] = true, ["name"] = info["name"], ["mode"] = info["mode"], ["online"] = true } };
            }
            return pets.Select(p => new Dictionary<string, object>(p) { ["self"] = PetHubNames.S(p, "id") == selfId }).ToList();
        }

        /// <summary>自己的名字 / 待在哪变了：报给负责人（它再广播名单）</summary>
        public void Report()
        {
            if (link == null) return;
            var info = host.HubInfo();
            link.Send(new Dictionary<string, object> { ["type"] = "state", ["name"] = info["name"], ["mode"] = info["mode"] });
        }

        public void Request(string op, string petId = null, string action = null, string value = null, Action<bool, string> reply = null)
        {
            reply = reply ?? ((ok, error) => { if (!ok && !string.IsNullOrEmpty(error)) host.HubNotice(error); });
            if (link == null) { reply(false, "正在换负责人，等一下再试"); return; }
            var rid = ++nextRid;
            waiting[rid] = reply;
            link.Send(new Dictionary<string, object> { ["type"] = "req", ["rid"] = rid, ["op"] = op, ["id"] = petId, ["action"] = action, ["value"] = value ?? "" });
        }
    }
}
