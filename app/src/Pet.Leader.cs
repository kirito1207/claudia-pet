using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Forms = System.Windows.Forms;

namespace Claudia
{
    // 负责人的活（2026-11-27 收拢：原来散在 Pet.Pets.cs、Poll 和好几个函数里各自判断 isLeader）。
    // 系统里只能有一份的事都在这里：BecomeLeader 一次开齐（协调中心、托盘、快捷键），GiveUpLeading 一次关掉；
    // 每 0.3 秒的活都从 LeaderTick 进（扫会话发给大家、弹任务通知、每小时清旧状态文件），不看自己的网页好没好。
    // 别的地方只剩几处看 isLeader：快捷键注册、通知（托盘在负责人这儿）、设置页显示快捷键占用。谁当负责人、怎么交接见 PetHub.cs 开头
    sealed partial class Pet
    {
        /// <summary>抢负责人锁（就是原来的单实例互斥量，插件 / MCP 看它判断在不在跑）；抢不到返回 null</summary>
        public static Mutex TakeLeaderLock()
        {
            var m = new Mutex(false, Ensure.MutexName);
            try { if (m.WaitOne(0)) return m; }
            catch (AbandonedMutexException) { return m; }   // 上一任崩溃没放：等于放了
            m.Dispose(); return null;
        }

        /// <summary>当负责人：开协调中心、托盘、隐藏快捷键（每 0.3 秒的活见 LeaderTick）</summary>
        void BecomeLeader()
        {
            isLeader = true;
            Store.Log("当负责人");
            leaderSince = DateTime.UtcNow; updateCheckedAt = UpdateCheck.Num(UpdateCheck.Load(), "checkedAt");
            try { Directory.CreateDirectory(Store.State); File.WriteAllText(Path.Combine(Store.State, "pet.pid"), Process.GetCurrentProcess().Id.ToString()); } catch { }
            hub?.Close();
            hub = new PetHubServer(this, sessions: sessionBook);
            hub.Start();
            tray = new Forms.NotifyIcon { Text = Store.AppName, Visible = true };
            try { tray.Icon = new System.Drawing.Icon(Path.Combine(Store.Install, "AppIcon.ico")); } catch { tray.Icon = System.Drawing.SystemIcons.Application; }
            var trayMenu = new Forms.ContextMenuStrip();
            trayMenu.Opening += (_, e) => { FillTrayMenu(trayMenu); e.Cancel = false; };
            tray.ContextMenuStrip = trayMenu;
            tray.DoubleClick += (_, __) => ShowAgain();   // 只叫出来、不挪位置（2026-11-07 用户定）；要回默认位置用菜单
            RegisterHideHotKey();
            PushSettings();
        }

        /// <summary>不当负责人了（要走了）：马上放开锁、关掉托盘和快捷键，下一任才能立刻接手</summary>
        void GiveUpLeading()
        {
            if (!isLeader) return;
            isLeader = false;
            if (hwnd != IntPtr.Zero) UnregisterHotKey(hwnd, HotKeyId);
            if (tray != null) { tray.Visible = false; tray.Dispose(); tray = null; }
            hub?.Close();
            if (LeaderLock != null) { try { LeaderLock.ReleaseMutex(); } catch { } LeaderLock.Dispose(); LeaderLock = null; }
        }

        public bool HubTryLead()
        {
            var m = TakeLeaderLock();
            if (m == null) return false;
            LeaderLock = m;
            BecomeLeader();
            ((PetHubServer)hub).Adopt();   // 崩溃的上一任、没在跑的几只叫回来
            return true;
        }

        /// <summary>负责人扫会话 / 通知文件用的记号，和网页那份（stamps）分开：网页重新加载不用整份重扫、重发给大家</summary>
        readonly Dictionary<string, string> leaderStamps = new Dictionary<string, string>();

        void LeaderTick()
        {
            if (!isLeader) return;
            if (!Paused) ScanSessions();
            PollTaskNotices();
            CleanStaleState();
            UpdateTick();
        }

        // ---- 检查更新（规则见 UpdateCheck.cs）：负责人一天最多问一次 GitHub，有新版本让她说一次 ----
        DateTime leaderSince;
        double updateCheckedAt;   // 上次问的时间（秒），当上负责人时从 update.json 读
        bool updateChecking;
        string updateToSay;       // 有新版本、还没说：等她露着脸、网页好了再说
        void UpdateTick()
        {
            if (updateToSay != null && ready && !concealed && !userHidden && !Paused)
            {
                var v = updateToSay; updateToSay = null;
                Js($"say({Q($"有新版本 {v} 啦，去设置「关于」里看看～")})");
                var d = UpdateCheck.Load(); d["notified"] = v; UpdateCheck.Save(d);   // 同一个版本只说一次
            }
            if (updateChecking || !Store.AppOn("checkUpdates")) return;
            if ((DateTime.UtcNow - leaderSince).TotalSeconds < UpdateCheck.FirstDelayS) return;   // 刚启动先不抢网络
            if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() - updateCheckedAt < UpdateCheck.IntervalS) return;
            CheckUpdateNow();
        }
        /// <summary>现在问一次（到点了、或者设置里点了「现在检查」）。问的时候在后台，问完回界面线程记下结果</summary>
        void CheckUpdateNow()
        {
            if (updateChecking) return;
            updateChecking = true;
            updateCheckedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();   // 没问成也算问过：一天后再试，不每 0.3 秒重试
            PushSettings();
            UpdateCheck.Fetch().ContinueWith(t => ui.BeginInvoke(new Action(() => UpdateDone(t.Result.release, t.Result.error))));
        }
        void UpdateDone((string version, string url)? release, string error)
        {
            updateChecking = false;
            var d = UpdateCheck.Load();
            d["checkedAt"] = updateCheckedAt;
            if (release.HasValue)
            {
                var v = release.Value.version;
                d["latest"] = v; d["url"] = release.Value.url; d.Remove("error");
                var isNew = UpdateCheck.Newer(v, Store.Version);
                Store.Log(isNew ? $"有新版本 {v}（现在 {Store.Version}）" : $"检查更新：已是最新（{v}）");
                if (isNew && UpdateCheck.Str(d, "notified") != v) updateToSay = v;
            }
            else { d["error"] = error; Store.Log("检查更新没成：" + error); }
            UpdateCheck.Save(d);
            (hub as PetHubServer)?.TellAppChanged();   // 别的几只的设置页也跟着显示
            PushSettings();
        }
        public void HubCheckUpdate() { if (isLeader) CheckUpdateNow(); }

        /// <summary>各个 Claude Code 会话的状态：只有负责人扫目录，扫到的变化自己用、经 PetHub 发给大家</summary>
        void ScanSessions()
        {
            var cutoff = DateTime.UtcNow.AddSeconds(-SessionBook.KeepS);
            var changed = new List<Dictionary<string, object>>();
            try
            {
                foreach (var file in new DirectoryInfo(Store.State).EnumerateFiles("claude-session-*.json"))
                {
                    var path = file.FullName;
                    if (!file.Exists || file.LastWriteTimeUtc < cutoff || file.Length > 65536)
                    {
                        leaderStamps.Remove(path);
                        continue;
                    }
                    var stamp = file.LastWriteTimeUtc.Ticks + ":" + file.Length;
                    if (leaderStamps.TryGetValue(path, out var old) && old == stamp) continue;
                    var state = Store.ReadJson(path);
                    if (state == null) continue;
                    leaderStamps[path] = stamp;
                    changed.Add(state);
                }
            }
            catch (IOException) { }   // 下次轮询重试。
            catch (UnauthorizedAccessException) { }
            // 记进 sessionBook（协调中心拿的是同一份）、发给大家，再交给自己的网页
            ShowSessions(((PetHubServer)hub).ShareSessions(changed));
        }

        /// <summary>各个会话最近的状态（负责人自己扫的、别的几只收到的都记在这），网页重新加载时重放</summary>
        readonly SessionBook sessionBook = new SessionBook();
        /// <summary>会话状态（负责人自己扫到的，或者负责人发来的）：记下来，网页好了就交给它。坏记录只跳过那一条</summary>
        void ReceiveSessions(List<Dictionary<string, object>> states) => ShowSessions(sessionBook.Merge(states));
        void ShowSessions(List<Dictionary<string, object>> good)
        {
            if (good.Count != 0 && ready) Js($"setSessionStates('claude', {Store.Json.Serialize(good)})");
        }

        // 插件每个会话写一个 claude-session-<sid>.json、一个 claude-notify-<sid>.json，自己从不删：会话多了 state 目录一直攒小文件。
        // 这两种只认最近 10 分钟 / 2 分钟的，超过一天的由负责人删掉（当上负责人时一次、之后每小时一次；几只一起删会抢）
        DateTime stateCleanedAt = DateTime.MinValue;
        void CleanStaleState()
        {
            if ((DateTime.UtcNow - stateCleanedAt).TotalHours < 1) return;
            stateCleanedAt = DateTime.UtcNow;
            int n = 0;
            try
            {
                var cutoff = DateTime.UtcNow.AddDays(-1);
                foreach (var pattern in new[] { "claude-session-*.json", "claude-notify-*.json" })
                    foreach (var file in new DirectoryInfo(Store.State).EnumerateFiles(pattern))
                        try { if (file.LastWriteTimeUtc < cutoff) { file.Delete(); n++; } } catch { }
            }
            catch (Exception e) { Store.Log("清理旧会话状态出错: " + e.Message); }
            if (n > 0) Store.Log($"清掉 {n} 个一天前的会话状态文件");
        }
    }
}
