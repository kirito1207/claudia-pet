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
                File.AppendAllText(file, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss ") + text + "\r\n");
            }
            catch { }
        }

        // ---- 设置 ----
        public static Dictionary<string, object> Settings = new Dictionary<string, object>();
        static string SettingsFile => Path.Combine(Data, "settings.json");
        public static void LoadSettings()
        {
            Settings = ReadJson(SettingsFile) ?? new Dictionary<string, object>();
            if (!(Settings.TryGetValue("names", out var n) && n is Dictionary<string, object>)) Settings["names"] = new Dictionary<string, object>();
        }
        public static void SaveSettings() { try { WriteJson(SettingsFile, Settings); } catch (Exception e) { Log("保存设置失败: " + e.Message); } }
        public static bool Flag(string key) => Settings.TryGetValue(key, out var v) && v is bool b && b;
        public static Dictionary<string, object> Names => (Dictionary<string, object>)Settings["names"];

        /// <summary>只在内置素材有变化时更新。用户另外添加的角色目录不参与同步。</summary>
        public static void SyncResources()
        {
            Directory.CreateDirectory(Characters);
            Directory.CreateDirectory(State);
            var src = Path.Combine(Install, "characters");
            if (string.Equals(Path.GetFullPath(src), Path.GetFullPath(Characters), StringComparison.OrdinalIgnoreCase)) return;
            foreach (var dir in Directory.GetDirectories(src))
            {
                var dst = Path.Combine(Characters, Path.GetFileName(dir));
                try { SyncCharacter(dir, dst); }
                catch (Exception e) { Log("同步角色失败 " + dir + ": " + e.Message); }
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

        static void SyncCharacter(string src, string dst)
        {
            // Put scratch data outside Characters so it never appears as another character.
            var work = Path.Combine(Data, "builtin-resource-sync", Path.GetFileName(dst));
            var staged = Path.Combine(work, "next");
            var previous = Path.Combine(work, "previous");
            if (Directory.Exists(previous))
            {
                if (!Directory.Exists(dst)) Directory.Move(previous, dst);
                else Directory.Delete(previous, true);
            }
            if (Directory.Exists(staged)) Directory.Delete(staged, true);
            if (Directory.Exists(dst) && SameResources(src, dst)) return;

            try
            {
                CopyDir(src, staged);
                if (Directory.Exists(dst)) Directory.Move(dst, previous);
                try { Directory.Move(staged, dst); }
                catch
                {
                    if (Directory.Exists(previous)) Directory.Move(previous, dst);
                    throw;
                }
                if (Directory.Exists(previous)) Directory.Delete(previous, true);
            }
            finally { if (Directory.Exists(staged)) Directory.Delete(staged, true); }
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

        static void CopyDir(string from, string to)
        {
            Directory.CreateDirectory(to);
            foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)), true);
            foreach (var d in ResourceDirs(from)) CopyDir(d, Path.Combine(to, Path.GetFileName(d)));
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

        public static Dictionary<string, object> Manifest()
        {
            var chars = new Dictionary<string, object>();
            var dirs = Directory.GetDirectories(Characters).OrderBy(d => d, StringComparer.Ordinal).ToList();
            foreach (var dir in dirs)
            {
                var info = ReadJson(Path.Combine(dir, "character.json"));
                if (info == null) continue;
                var id = Path.GetFileName(dir);
                var (poses, blink, blinkBase) = Sprites(dir);
                if (!(poses.ContainsKey("idle") || poses.ContainsKey("default"))) continue;
                if (Names.TryGetValue(id, out var custom) && custom is string s && s.Trim() != "") info["name"] = s.Trim();
                info["poses"] = poses; info["blink"] = blink; info["blinkBase"] = blinkBase;
                info["sheets"] = Sheets(dir);
                chars[id] = info;
            }
            return new Dictionary<string, object> { ["characters"] = chars };
        }
    }
}
