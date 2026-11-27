using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Web.Script.Serialization;

namespace Claudia
{
    /// <summary>目录、设置、日志、内置资源同步、角色清单。</summary>
    static class Store
    {
        public static readonly string Install = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\', '/');
        // 数据放 %USERPROFILE%\.claudia，不放 AppData：Claude 商店版（MSIX）拉起的程序往 AppData 新建的东西
        // 会被重定向到 Packages\Claude_*\LocalCache，资源管理器看不到，换个方式启动的 Claudia 也读不到同一份
        public static readonly string Data = Env("CLAUDIA_DATA_DIR") ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claudia");
        static string OldData => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Claudia");

        /// <summary>0.1.0 的数据在 %LOCALAPPDATA%\Claudia：新目录还没有设置时，把旧设置搬过来（只复制，不删旧的）</summary>
        public static void MigrateOldData()
        {
            try
            {
                var dst = Path.Combine(Data, "settings.json");
                var src = Path.Combine(OldData, "settings.json");
                if (Env("CLAUDIA_DATA_DIR") != null || File.Exists(dst) || !File.Exists(src)) return;
                Directory.CreateDirectory(Data);
                File.Copy(src, dst);
                Log("从旧位置搬来设置：" + src);
            }
            catch (Exception e) { Log("搬旧设置失败: " + e.Message); }
        }
        public static readonly string State = Env("CLAUDIA_STATE_DIR") ?? Path.Combine(Data, "state");   // Claude Code 插件往这里写状态
        public static string Characters => Path.Combine(Data, "characters");
        public static string Version => ReadText(Path.Combine(Install, "VERSION"))?.Trim() ?? "0";

        // 网页从两个虚拟域名读文件：程序自带的网页 https://claudia.app/（安装目录\web，文件夹映射）
        // 和角色立绘 https://claudia.data/characters/…（安装目录或数据目录，见 SpriteResources）
        public const string AppHost = "claudia.app", DataHost = "claudia.data";

        /// <summary>软件名（程序、安装目录、插件、MCP 配置项、窗口标题）。桌宠角色本身叫 claudia，见 characters\claude\character.json</summary>
        public const string AppName = "claudia-pet";
        public static string PageUrl(string page) => $"https://{AppHost}/{page}";

        public static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

        static string Env(string name) { var v = Environment.GetEnvironmentVariable(name); return string.IsNullOrWhiteSpace(v) ? null : v; }
        public static string ReadText(string path) { try { return File.ReadAllText(path); } catch { return null; } }

        public static Dictionary<string, object> ReadJson(string path)
        {
            var text = ReadText(path);
            if (text == null) return null;
            try { return Json.DeserializeObject(text.TrimStart('﻿')) as Dictionary<string, object>; } catch { return null; }
        }

        public static void WriteJson(string path, object data)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, Json.Serialize(data));
            if (File.Exists(path)) File.Replace(tmp, path, null); else File.Move(tmp, path);
        }

        public static void Log(string text)
        {
            try
            {
                Directory.CreateDirectory(Data);
                var file = Path.Combine(Data, "windows.log");
                if (File.Exists(file) && new FileInfo(file).Length > 1_000_000) File.Delete(file);
                File.AppendAllText(file, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss ") + (IsMain ? "" : "[" + PetId + "] ") + text + "\r\n");
            }
            catch { }
        }

        // ---- 多开（2026-11-27 起每只平级）----
        // 每只桌宠是一个独立进程，编号只用来找它自己的设置和网页数据（main 的在数据目录最上层——原来第一只的，不用搬；
        // 别的在 pets\<编号>\）。界面上的「第几只」是它在名单里的位置；谁是负责人见 PetHub.cs
        public static string PetId = "main";
        public static string JoinToken;   // 负责人加这一只时给的标记（--join），报到时带回去，好和取消前叫出来的旧进程分开
        public static bool IsMain => PetId == "main";   // 只表示「设置放在最上层」，不再有别的特权
        public const int MaxPets = 5;
        public static string PetDir => IsMain ? Data : Path.Combine(Data, "pets", PetId);
        public static string WebViewDir => Path.Combine(PetDir, "webview2");
        public static bool ValidPetId(string id) => id == "main" || (id != null && id.Length >= 1 && id.Length <= 8 && id.All(c => c >= '0' && c <= '9'));
        static string PetsFile => Path.Combine(Data, "pets.json");
        /// <summary>名单：现在有哪几只、按什么顺序（第几只）。旧格式 {ids: [多开的几只]} 前面补上原来的第一只</summary>
        public static List<string> Roster() => TryRoster(out var ids) ? ids : new List<string>();
        /// <summary>读名单，分清「确实是空的」（没有这个文件）和「读不到」（正被写、坏了）：读不到返回 false。
        /// 负责人只在接手时读一次，读不到不能当成空名单，不然连上来的几只都会被当成已经送走（2026-11-27 Codex 复审）</summary>
        public static bool TryRoster(out List<string> roster)
        {
            roster = new List<string>();
            if (!File.Exists(PetsFile)) return true;
            var d = ReadJson(PetsFile);
            if (d == null) return false;
            var ids = d.TryGetValue("pets", out var p) && p is object[] pets ? pets.OfType<string>()
                : d.TryGetValue("ids", out var v) && v is object[] old ? new[] { "main" }.Concat(old.OfType<string>()) : Enumerable.Empty<string>();
            roster = ids.Where(ValidPetId).Distinct().Take(MaxPets).ToList();
            return true;
        }
        /// <summary>只有负责人写</summary>
        public static void SaveRoster(IEnumerable<string> ids) =>
            WriteJson(PetsFile, new Dictionary<string, object> { ["pets"] = ids.ToArray() });
        /// <summary>所有可用的编号，按先用谁排：main（它的设置在最上层），再 2、3……比名额多一个：刚送走的那只挥手时占着编号也能再加</summary>
        public static readonly string[] PetIds = new[] { "main" }.Concat(Enumerable.Range(2, MaxPets).Select(n => n.ToString())).ToArray();
        /// <summary>新来的一只用哪个编号：按 PetIds 的顺序第一个 usable 的；都不行返回 null。启动时和「再来一只」都走这里</summary>
        public static string FreePetId(Func<string, bool> usable) => PetIds.FirstOrDefault(usable);

        // ---- 所有桌宠共用的设置（2026-11-27）----
        // 隐藏快捷键、三项任务通知、鼠标穿透、待在桌面上时的层级不跟着某一只走：放在 app.json。第一次从原来第一只的设置里搬过来
        static string AppFile => Path.Combine(Data, "app.json");
        static readonly string[] AppKeys = { "hideHotkey", "notifyResults", "notifyWaiting", "notifyBackgroundOnly", "clickThrough", "deskLayer", "checkUpdates" };
        public static Dictionary<string, object> App = new Dictionary<string, object>();
        public static void LoadApp()
        {
            var d = ReadJson(AppFile);
            if (d == null)
            {
                var old = ReadJson(Path.Combine(Data, "settings.json")) ?? new Dictionary<string, object>();
                d = AppKeys.Where(old.ContainsKey).ToDictionary(k => k, k => old[k]);
                SaveApp(d);
            }
            App = d;
        }
        public static bool SaveApp(Dictionary<string, object> d = null)
        {
            try { WriteJson(AppFile, d ?? App); return true; } catch (Exception e) { Log("保存共用设置失败: " + e.Message); return false; }
        }
        /// <summary>负责人改一项共用设置（别的几只把「哪一项、改成什么」交给它，只有它写 app.json，免得互相盖掉）。
        /// 值按文字传：开关是 true / false。文件先写成功才换内存里的，写不进去就保留旧设置，error 说原因</summary>
        public static bool SetApp(string key, string text, out string error)
        {
            object value;
            error = "这一项设置没认出来";
            if (key == "notifyResults" || key == "notifyWaiting" || key == "notifyBackgroundOnly" || key == "clickThrough" || key == "checkUpdates")
            {
                if (text != "true" && text != "false") return false;
                value = text == "true";
            }
            else if (key == "hideHotkey" && (text == "p" || text == "h" || text == "off")) value = text;
            else if (key == "deskLayer" && (text == "top" || text == "yield" || text == "normal")) value = text;
            else return false;
            var next = new Dictionary<string, object>(App) { [key] = value };
            if (!SaveApp(next)) { error = "设置没能保存，还是原来的样子"; return false; }
            App = next; error = "";
            return true;
        }
        /// <summary>没写过的开关：通知、检查更新默认开，鼠标穿透默认关</summary>
        public static bool AppOn(string key) => App.TryGetValue(key, out var v) && v is bool b ? b : key != "clickThrough";

        // ---- 设置 ----
        public static Dictionary<string, object> Settings = new Dictionary<string, object>();
        static string SettingsFile => Path.Combine(PetDir, "settings.json");
        public static void LoadSettings()
        {
            var own = ReadJson(SettingsFile);
            if (own == null && !IsMain)
            {
                // 新来的一只：先照第一只的设置（大小、手感这些），位置、待在哪、名字用自己的
                own = ReadJson(Path.Combine(Data, "settings.json")) ?? new Dictionary<string, object>();
                foreach (var k in new[] { "follow", "desk", "mode", "names" }) own.Remove(k);
            }
            Settings = own ?? new Dictionary<string, object>();
            if (!(Settings.TryGetValue("names", out var n) && n is Dictionary<string, object>)) Settings["names"] = new Dictionary<string, object>();
        }
        public static void SaveSettings()
        {
            saveTimer?.Stop();
            try { WriteJson(SettingsFile, Settings); } catch (Exception e) { Log("保存设置失败: " + e.Message); }
        }
        // 拖大小滑块、缩放动画每一帧都会改设置（大小、相对位置）：内存里马上改，文件等停下 0.4 秒再写一次（2026-11-27）。
        // 计时器在界面线程上跑，和改设置的代码同一个线程，不会写到一半被改；退出前 FlushSettings 补写
        static System.Windows.Threading.DispatcherTimer saveTimer;
        public static void SaveSettingsSoon()
        {
            if (saveTimer == null)
            {
                saveTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
                saveTimer.Tick += (_, __) => SaveSettings();
            }
            saveTimer.Stop(); saveTimer.Start();
        }
        public static void FlushSettings() { if (saveTimer?.IsEnabled == true) SaveSettings(); }
        public static bool Flag(string key) => Settings.TryGetValue(key, out var v) && v is bool b && b;
        public static Dictionary<string, object> Names => (Dictionary<string, object>)Settings["names"];

        // ---- 运行方式（2026-11-16）----
        // 「跟着 Claude」：Claude 打开时由插件 / MCP 叫出来，Claude 关了就走（0.4.1 及以前都是这样）；
        // 「独立运行」：双击程序就出来，不需要 Claude，只有从菜单退出才走。
        // 所有几只共用一份，单独存在 run.json：放进第一只的 settings.json 的话，别的几只改了会被第一只存设置时盖掉
        static string RunFile => Path.Combine(Data, "run.json");
        public static bool HasRunMode => File.Exists(RunFile);
        static bool standalone;
        static DateTime runStamp = DateTime.MinValue, runCheckedAt = DateTime.MinValue;
        /// <summary>是不是独立运行。按 run.json 的修改时间缓存，最多一秒看一次文件：别的那只改了，这一只一秒内跟上</summary>
        public static bool Standalone
        {
            get
            {
                var now = DateTime.UtcNow;
                if ((now - runCheckedAt).TotalSeconds < 1) return standalone;
                runCheckedAt = now;
                try
                {
                    var stamp = File.Exists(RunFile) ? File.GetLastWriteTimeUtc(RunFile) : DateTime.MinValue;
                    if (stamp != runStamp)
                    {
                        runStamp = stamp;
                        var d = ReadJson(RunFile);
                        standalone = d != null && d.TryGetValue("mode", out var m) && m as string == "standalone";
                    }
                }
                catch { }
                return standalone;
            }
        }
        public static void SetStandalone(bool on)
        {
            try { WriteJson(RunFile, new Dictionary<string, object> { ["mode"] = on ? "standalone" : "claude" }); }
            catch (Exception e) { Log("保存运行方式失败: " + e.Message); }
            standalone = on; runCheckedAt = DateTime.MinValue;
        }

        /// <summary>内置角色直接从安装目录读取；数据目录里有效的同名角色仍可覆盖。</summary>
        public static string BuiltinCharacters => Path.Combine(Install, "characters");
        static Dictionary<string, string> characterDirs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public static string CharacterDir(string id) =>
            characterDirs.TryGetValue(id, out var dir) ? dir : Path.Combine(Characters, id);
        /// <summary>立绘地址 /characters/&lt;角色&gt;/… 该到哪个 characters 目录下找（路径本身的校验在 SpritePath.TryResolve）</summary>
        public static string CharactersRootFor(string absolutePath)
        {
            var parts = (absolutePath ?? "").Split('/');
            string id = null;
            try { if (parts.Length > 2) id = Uri.UnescapeDataString(parts[2]); } catch { }
            return id != null && characterDirs.TryGetValue(id, out var dir) ? Path.GetDirectoryName(dir) : Characters;
        }

        /// <summary>恢复旧同步遗留的备份，退役已逐文件验证的内置角色镜像。</summary>
        public static void SyncResources(string source = null)
        {
            Directory.CreateDirectory(Characters);
            Directory.CreateDirectory(State);
            var src = source ?? BuiltinCharacters;
            if (!Directory.Exists(src) ||
                string.Equals(Path.GetFullPath(src), Path.GetFullPath(Characters), StringComparison.OrdinalIgnoreCase)) return;
            // 数据目录可能经由联接指向安装目录。此时只加载素材，不迁移。
            for (var dir = new DirectoryInfo(Characters); dir != null; dir = dir.Parent)
                if ((dir.Attributes & FileAttributes.ReparsePoint) != 0) return;
            var legacy = new Lazy<Dictionary<string, object>>(() => ReadJson(Path.Combine(src, "legacy-mirrors.json")));
            foreach (var dir in Directory.GetDirectories(src))
            {
                var id = Path.GetFileName(dir);
                var old = Path.Combine(Characters, id);
                try
                {
                    var historyRoot = Path.Combine(Data, "builtin-resource-sync");
                    var history = Path.Combine(historyRoot, id);
                    if ((Directory.Exists(historyRoot) && IsLink(historyRoot)) || (Directory.Exists(history) && IsLink(history))) continue;
                    var previous = Path.Combine(history, "previous");
                    // 已迁移的角色使用安装目录，旧同步残留的 previous 不再恢复到角色目录。
                    if (!Directory.Exists(old) && Directory.Exists(previous) && !IsLink(previous) &&
                        !Directory.EnumerateDirectories(history, "retired-*").Any()) Directory.Move(previous, old);
                    if (!Directory.Exists(old)) continue;
                    using (var hash = SHA256.Create())
                    {
                        if (!SameResources(dir, old, hash))
                        {
                            var version = MatchLegacyMirror(old, id, legacy.Value, hash);
                            if (version == null || ReadCharacter(dir) == null) continue;
                            Directory.CreateDirectory(history);
                            var backup = Path.Combine(history, "retired-" + Guid.NewGuid().ToString("N"));
                            Directory.Move(old, backup);
                            Log($"已备份 {version} 内置角色镜像 {id}：{backup}");
                            continue;
                        }
                    }
                    // 先完整移出角色目录。即使个别文件被锁导致清理失败，也不会加载半删的角色。
                    var duplicate = Path.Combine(Data, "builtin-duplicate-" + Guid.NewGuid().ToString("N"));
                    Directory.Move(old, duplicate);
                    try { Directory.Delete(duplicate, true); }
                    catch (Exception e) { Log("清理已验证镜像失败 " + duplicate + ": " + e.Message); }
                }
                catch (Exception e) { Log("清理角色镜像失败 " + old + ": " + e.Message); }
            }
            // 下面这些复制失败（文件被别的程序占着）只记一笔：旧的那份还能用，不能因为一个说明文件把桌宠拦在启动前
            // （更新失败回退后重开旧版本，characters\LICENSE.md 还被占着，整个启动就失败了）
            foreach (var md in Directory.GetFiles(src, "*.md"))
                try
                {
                    var dst = Path.Combine(Characters, Path.GetFileName(md));
                    if (!SameFile(md, dst)) File.Copy(md, dst, true);
                }
                catch (Exception e) { Log("同步说明文件失败 " + md + ": " + e.Message); }
        }

        static bool SameFile(string src, string dst)
        {
            var source = new FileInfo(src);
            var copy = new FileInfo(dst);
            return copy.Exists && copy.Length == source.Length && copy.LastWriteTimeUtc == source.LastWriteTimeUtc;
        }

        static bool SameResources(string src, string dst, HashAlgorithm hash)
        {
            if (!Directory.Exists(dst) || IsLink(src) || IsLink(dst)) return false;
            var source = Directory.GetFileSystemEntries(src)
                .Where(entry => !Directory.Exists(entry) || !string.Equals(Path.GetFileName(entry), "raw", StringComparison.OrdinalIgnoreCase))
                .ToDictionary(entry => Path.GetFileName(entry), StringComparer.Ordinal);
            var copies = Directory.GetFileSystemEntries(dst);
            if (source.Count != copies.Length) return false;
            // 先对比整个树的名字、类型和长度。旧版本通常在这一步就能排除。
            foreach (var copy in copies)
            {
                if (!source.TryGetValue(Path.GetFileName(copy), out var original) || IsLink(original) || IsLink(copy)) return false;
                if (Directory.Exists(original) != Directory.Exists(copy)) return false;
                if (File.Exists(original) && new FileInfo(original).Length != new FileInfo(copy).Length) return false;
            }
            foreach (var copy in copies)
            {
                var original = source[Path.GetFileName(copy)];
                if (Directory.Exists(copy))
                {
                    if (!SameResources(original, copy, hash)) return false;
                }
                else
                {
                    using (var a = File.OpenRead(original))
                    using (var b = File.OpenRead(copy))
                        if (!hash.ComputeHash(a).SequenceEqual(hash.ComputeHash(b))) return false;
                }
            }
            return true;
        }

        static bool IsLink(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

        static string MatchLegacyMirror(string dir, string id, Dictionary<string, object> manifest, HashAlgorithm hash)
        {
            if (manifest == null || !manifest.TryGetValue(id, out var raw) || !(raw is Dictionary<string, object> releases)) return null;
            foreach (var release in releases)
            {
                var files = new List<(string path, string checksum)>();
                if (!(release.Value is Dictionary<string, object> expected) || !MatchMirrorLayout(dir, expected, files)) continue;
                if (files.Count != 0 && files.All(file =>
                {
                    using (var stream = File.OpenRead(file.path))
                        return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "") == file.checksum;
                })) return release.Key;
            }
            return null;
        }

        // The release manifest mirrors the directory tree. A leaf is [byte length, uppercase SHA-256].
        // Reject extra files and directories before hashing any asset contents.
        static bool MatchMirrorLayout(string dir, Dictionary<string, object> expected, List<(string, string)> files)
        {
            if (IsLink(dir)) return false;
            var entries = Directory.GetFileSystemEntries(dir);
            if (entries.Length != expected.Count) return false;
            foreach (var path in entries)
            {
                if (!expected.TryGetValue(Path.GetFileName(path), out var item) || IsLink(path)) return false;
                if (item is Dictionary<string, object> child)
                {
                    if (!Directory.Exists(path) || !MatchMirrorLayout(path, child, files)) return false;
                }
                else
                {
                    if (!(item is object[] leaf) || leaf.Length != 2 || !(leaf[1] is string checksum) ||
                        !(leaf[0] is int || leaf[0] is long) || !File.Exists(path) ||
                        new FileInfo(path).Length != Convert.ToInt64(leaf[0])) return false;
                    files.Add((path, checksum));
                }
            }
            return true;
        }

        /// <summary>扫描 characters/&lt;id&gt;/：character.json + 立绘</summary>
        // 立绘：<文件名去掉 -2、-3> 是姿态名，idle-blink 是眨眼帧（照着 idle 本身画的）
        static (Dictionary<string, List<string>> poses, string blink, string blinkBase) Sprites(string dir)
        {
            var id = Path.GetFileName(dir);
            var poses = new Dictionary<string, List<string>>();
            string blink = null, blinkBase = null;
            foreach (var f in Directory.GetFiles(dir).Where(f => f.EndsWith(".webp", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                                                      .OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal))
            {
                var stem = Path.GetFileNameWithoutExtension(f);
                if (!SpritePath.IsSprite(stem)) continue;   // 头像与展示图不作为动作帧
                var url = $"https://{DataHost}/characters/{Uri.EscapeDataString(id)}/{Uri.EscapeDataString(Path.GetFileName(f))}";
                if (stem == "idle-blink") { blink = url; continue; }
                if (stem == "idle") blinkBase = url;
                var pose = stem.Split('-')[0];
                if (!poses.ContainsKey(pose)) poses[pose] = new List<string>();
                poses[pose].Add(url);
            }
            return (poses, blink, blinkBase);
        }

        /// <summary>characters/&lt;id&gt;/anim/：动画精灵图（文件名 → 地址），character.json 的 animations 按文件名引用。
        /// 2026-11-27 起 Claude 角色的精灵图全部删掉、只留同名 .webm（见 Videos），这里只剩别的角色自带精灵图时才有东西</summary>
        static Dictionary<string, object> Sheets(string dir)
        {
            var sheets = new Dictionary<string, object>();
            var anim = Path.Combine(dir, SpritePath.AnimDir);
            if (!Directory.Exists(anim)) return sheets;
            var id = Path.GetFileName(dir);
            foreach (var f in Directory.GetFiles(anim, "*.webp").OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal))
                sheets[Path.GetFileNameWithoutExtension(f)] =
                    $"https://{DataHost}/characters/{Uri.EscapeDataString(id)}/{SpritePath.AnimDir}/{Uri.EscapeDataString(Path.GetFileName(f))}";
            return sheets;
        }

        /// <summary>characters/&lt;id&gt;/anim/ 里的 .webm（VP9 + 透明通道）：character.json 的 animations 按文件名引用，有就播视频；
        /// 同名精灵图可以没有（2026-11-27 起 Claude 角色只留视频），没有视频才播精灵图</summary>
        static Dictionary<string, object> Videos(string dir)
        {
            var videos = new Dictionary<string, object>();
            var anim = Path.Combine(dir, SpritePath.AnimDir);
            if (!Directory.Exists(anim)) return videos;
            var id = Path.GetFileName(dir);
            foreach (var f in Directory.GetFiles(anim, "*.webm").OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal))
                videos[Path.GetFileNameWithoutExtension(f)] =
                    $"https://{DataHost}/characters/{Uri.EscapeDataString(id)}/{SpritePath.AnimDir}/{Uri.EscapeDataString(Path.GetFileName(f))}";
            return videos;
        }

        /// <summary>character.json 的 animations.idle 用到的动画里，至少有一段真的在 anim\ 里（精灵图或视频都算）</summary>
        static bool HasIdleSheet(Dictionary<string, object> info, Dictionary<string, object> sheets, Dictionary<string, object> videos) =>
            info.TryGetValue("animations", out var a) && a is Dictionary<string, object> anims &&
            anims.TryGetValue("idle", out var i) && i is Dictionary<string, object> idle &&
            idle.TryGetValue("sheets", out var n) && n is object[] names && names.OfType<string>().Any(k => sheets.ContainsKey(k) || videos.ContainsKey(k));

        static Dictionary<string, object> ReadCharacter(string dir)
        {
            var info = ReadJson(Path.Combine(dir, "character.json"));
            if (info == null) return null;
            var (poses, blink, blinkBase) = Sprites(dir);
            var sheets = Sheets(dir);
            var videos = Videos(dir);
            // 有待机立绘或待机动画才算是可用的角色。
            if (!(poses.ContainsKey("idle") || poses.ContainsKey("default") || HasIdleSheet(info, sheets, videos))) return null;
            info["poses"] = poses; info["blink"] = blink; info["blinkBase"] = blinkBase;
            info["sheets"] = sheets;
            info["videos"] = videos;
            return info;
        }

        public static Dictionary<string, object> Manifest()
        {
            var chars = new Dictionary<string, object>();
            var selected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var builtin = Directory.Exists(BuiltinCharacters) ? Directory.GetDirectories(BuiltinCharacters) : new string[0];
            var user = Directory.Exists(Characters) ? Directory.GetDirectories(Characters) : new string[0];
            // 同名角色先尝试数据目录；无有效待机素材时回退到内置。
            var dirs = user.Concat(builtin).OrderBy(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase);
            foreach (var dir in dirs)
            {
                var id = Path.GetFileName(dir);
                if (selected.ContainsKey(id)) continue;
                var info = ReadCharacter(dir);
                if (info == null) continue;
                info["defaultName"] = info.TryGetValue("name", out var dn) && dn is string dns ? dns : id;   // character.json 里的名字：改名留空时恢复成它
                if (Names.TryGetValue(id, out var custom) && custom is string s && s.Trim() != "") info["name"] = s.Trim();
                chars[id] = info;
                selected[id] = dir;
            }
            characterDirs = selected;
            return new Dictionary<string, object> { ["characters"] = chars };
        }
    }
}
