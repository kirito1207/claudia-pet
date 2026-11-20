using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
        // 和角色立绘 https://claudia.data/characters/…（数据目录，用户可以改；程序自己读文件交出去，见 SpriteResources）
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

        // ---- 多开 ----
        // 每只桌宠是一个独立的进程：第一只叫 main（Claude 拉起的就是它），多开的那几只由它带起来（claudia-pet.exe --pet 2）。
        // 各有各的设置文件和网页数据目录，所以名字、大小、待在哪、闲着做什么都互不影响；额度和工作状态读的是同一份
        public static string PetId = "main";
        public static bool IsMain => PetId == "main";
        public const int MaxExtraPets = 4;
        public static string PetDir => IsMain ? Data : Path.Combine(Data, "pets", PetId);
        public static string WebViewDir => Path.Combine(PetDir, "webview2");
        public static bool ValidPetId(string id) => id != null && id.Length >= 1 && id.Length <= 8 && id != "main"
            && id.All(c => c >= '0' && c <= '9' || c >= 'a' && c <= 'z');
        static string PetsFile => Path.Combine(Data, "pets.json");
        /// <summary>除了第一只之外还开着哪几只（按编号）</summary>
        public static List<string> ExtraPets()
        {
            var d = ReadJson(PetsFile);
            return d != null && d.TryGetValue("ids", out var v) && v is object[] a
                ? a.OfType<string>().Where(ValidPetId).Distinct().Take(MaxExtraPets).ToList() : new List<string>();
        }
        public static void SaveExtraPets(IEnumerable<string> ids) =>
            WriteJson(PetsFile, new Dictionary<string, object> { ["ids"] = ids.ToArray() });

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
        public static void SaveSettings() { try { WriteJson(SettingsFile, Settings); } catch (Exception e) { Log("保存设置失败: " + e.Message); } }
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

        /// <summary>程序自带的角色（安装目录\characters）。直接从这里读，不再往数据目录拷一份：
        /// 原来每次启动把它镜像到数据目录，立绘（约 283 MB）在磁盘上存两份（2026-11-20）。
        /// 数据目录\characters 现在只放用户另外添加的角色；和自带角色重名的以自带的为准。</summary>
        public static string BuiltinCharacters => Path.Combine(Install, "characters");
        static bool IsBuiltin(string id) => !string.IsNullOrEmpty(id) && id.IndexOfAny(new[] { '/', '\\', ':' }) < 0 && id != "." && id != ".."
            && Directory.Exists(Path.Combine(BuiltinCharacters, id));
        /// <summary>这个角色的文件夹：自带的在安装目录，另外添加的在数据目录</summary>
        public static string CharacterDir(string id) => Path.Combine(IsBuiltin(id) ? BuiltinCharacters : Characters, id ?? "");
        /// <summary>立绘地址 /characters/&lt;角色&gt;/… 该到哪个 characters 目录下找（路径本身的校验在 SpritePath.TryResolve）</summary>
        public static string CharactersRootFor(string absolutePath)
        {
            var parts = (absolutePath ?? "").Split('/');
            string id = null;
            try { if (parts.Length > 2) id = Uri.UnescapeDataString(parts[2]); } catch { }
            return IsBuiltin(id) ? BuiltinCharacters : Characters;
        }

        /// <summary>自带角色不再拷进数据目录；以前版本留在那里的镜像，和自带的一模一样就删掉，不一样（被改过）的留着不动。
        /// 用户另外添加的角色目录不碰。</summary>
        public static void SyncResources()
        {
            Directory.CreateDirectory(Characters);
            Directory.CreateDirectory(State);
            var src = BuiltinCharacters;
            if (string.Equals(Path.GetFullPath(src), Path.GetFullPath(Characters), StringComparison.OrdinalIgnoreCase)) return;
            foreach (var dir in Directory.GetDirectories(src))
            {
                var old = Path.Combine(Characters, Path.GetFileName(dir));
                try
                {
                    if (!Directory.Exists(old)) continue;
                    if (SameResources(dir, old)) { Directory.Delete(old, true); Log("自带角色改为直接从安装目录读，删掉数据目录里的镜像：" + old); }
                    else Log("数据目录里的 " + old + " 和自带角色不一样，留着没删；显示用的是安装目录里的那份");
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

        static IEnumerable<string> ResourceDirs(string path) => Directory.EnumerateDirectories(path)
            .Where(dir => !string.Equals(Path.GetFileName(dir), "raw", StringComparison.OrdinalIgnoreCase));

        static bool SameResources(string src, string dst)
        {
            var files = Directory.GetFiles(src);
            if (files.Length != Directory.GetFiles(dst).Length ||
                files.Any(file => !SameFile(file, Path.Combine(dst, Path.GetFileName(file))))) return false;
            var dirs = ResourceDirs(src).ToArray();
            return dirs.Length == Directory.GetDirectories(dst).Length && dirs.All(dir =>
            {
                var copy = Path.Combine(dst, Path.GetFileName(dir));
                return Directory.Exists(copy) && SameResources(dir, copy);
            });
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

        /// <summary>characters/&lt;id&gt;/anim/：动画精灵图（文件名 → 地址），character.json 的 animations 按文件名引用</summary>
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

        /// <summary>character.json 的 animations.idle 用到的精灵图里，至少有一张真的在 anim\ 里</summary>
        static bool HasIdleSheet(Dictionary<string, object> info, Dictionary<string, object> sheets) =>
            info.TryGetValue("animations", out var a) && a is Dictionary<string, object> anims &&
            anims.TryGetValue("idle", out var i) && i is Dictionary<string, object> idle &&
            idle.TryGetValue("sheets", out var n) && n is object[] names && names.OfType<string>().Any(sheets.ContainsKey);

        public static Dictionary<string, object> Manifest()
        {
            var chars = new Dictionary<string, object>();
            // 自带的（安装目录）加上用户另外添加的（数据目录）；重名的以自带的为准
            var builtin = Directory.Exists(BuiltinCharacters) ? Directory.GetDirectories(BuiltinCharacters) : new string[0];
            var dirs = builtin.Concat(Directory.GetDirectories(Characters).Where(d => !IsBuiltin(Path.GetFileName(d))))
                .OrderBy(d => Path.GetFileName(d), StringComparer.Ordinal).ToList();
            foreach (var dir in dirs)
            {
                var info = ReadJson(Path.Combine(dir, "character.json"));
                if (info == null) continue;
                var id = Path.GetFileName(dir);
                var (poses, blink, blinkBase) = Sprites(dir);
                var sheets = Sheets(dir);
                // 有待机立绘，或者有待机动画（2026-11-18 起 Claude 角色不再带静态的 idle.webp），才算一个角色
                if (!(poses.ContainsKey("idle") || poses.ContainsKey("default") || HasIdleSheet(info, sheets))) continue;
                if (Names.TryGetValue(id, out var custom) && custom is string s && s.Trim() != "") info["name"] = s.Trim();
                info["poses"] = poses; info["blink"] = blink; info["blinkBase"] = blinkBase;
                info["sheets"] = sheets;
                chars[id] = info;
            }
            return new Dictionary<string, object> { ["characters"] = chars };
        }
    }
}
