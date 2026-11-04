using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;

namespace Claudia
{
    sealed partial class Pet
    {
        // ---------------- 接入 Claude（设置页「接入 Claude」） ----------------
        // 两项，Claudia 自己改配置，不跑任何脚本；改之前先备份（<文件>.bak-claudia-<时间>）：
        //   plugin  Claude Code 插件：把安装目录里的 plugin 文件夹加进 ~/.claude/settings.json 的 env.CLAUDE_CODE_PLUGIN_DIRS
        //           （多个目录用分号隔开，别人的照留）。新开的 Code 会话生效：传工作状态和额度，会话开始时叫她出来
        //   mcp     桌面端 MCP：在 claude_desktop_config.json 的 mcpServers 里加 claudia-pet → claudia-pet.exe --mcp。
        //           重开 Claude 生效：Claude 一打开就把她叫出来，只用对话时也在
        // 0.1.0 叫 Claudia：装在 Programs\Claudia、MCP 配置项叫 claudia。接入 / 撤销时把这两个旧条目一起清掉
        static string PluginDir => Path.Combine(Store.Install, "plugin");
        static string OldPluginDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Claudia", "plugin");
        const string OldMcpKey = "claudia";
        static string ClaudeSettings => Path.Combine(Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is string d && d != "" ? d
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude"), "settings.json");

        /// <summary>桌面端的配置文件：商店版在 %LOCALAPPDATA%\Claude-Data\claude_*（或 Packages\Claude_*\LocalCache）下，安装版在 %APPDATA%\Claude。
        /// 已经有的里面挑最近改过的；都没有就按商店版的位置新建</summary>
        static string DesktopConfig
        {
            get
            {
                var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                var found = new List<string>();
                void Add(string dir, string pattern, string sub) { try { if (Directory.Exists(dir)) foreach (var x in Directory.GetDirectories(dir, pattern)) found.Add(Path.Combine(x, sub, "claude_desktop_config.json")); } catch { } }
                Add(Path.Combine(local, "Claude-Data"), "claude_*", "");
                Add(Path.Combine(local, "Packages"), "Claude_*", Path.Combine("LocalCache", "Roaming", "Claude"));
                found.Add(Path.Combine(roaming, "Claude", "claude_desktop_config.json"));
                var existing = found.Where(File.Exists).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
                if (existing != null) return existing;
                return found.FirstOrDefault(f => Directory.Exists(Path.GetDirectoryName(f))) ?? found.Last();
            }
        }

        static List<string> PluginDirs(Dictionary<string, object> settings) =>
            settings != null && settings.TryGetValue("env", out var e) && e is Dictionary<string, object> env && env.TryGetValue("CLAUDE_CODE_PLUGIN_DIRS", out var v) && v is string s
                ? s.Split(';').Where(x => x.Trim() != "").ToList() : new List<string>();
        static bool SamePath(string a, string b)
        {
            try { return string.Equals(Path.GetFullPath(Environment.ExpandEnvironmentVariables(a)).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }
        static bool PluginInstalled() => PluginDirs(Store.ReadJson(ClaudeSettings)).Any(d => SamePath(d, PluginDir));
        static bool McpInstalled()
        {
            var cfg = Store.ReadJson(DesktopConfig);
            return cfg != null && cfg.TryGetValue("mcpServers", out var m) && m is Dictionary<string, object> servers && servers.ContainsKey(Store.AppName);
        }

        static string SeenText()
        {
            try
            {
                var f = Path.Combine(Store.State, "seen-plugin");
                if (!File.Exists(f) || !double.TryParse(File.ReadAllText(f).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var ts)) return "还没收到过消息：新开一个 Code 会话、让她干点活试试";
                var ago = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - ts;
                return "上次收到消息：" + (ago < 60 ? "刚刚" : ago < 3600 ? $"{(int)(ago / 60)} 分钟前" : ago < 86400 ? $"{(int)(ago / 3600)} 小时前" : $"{(int)(ago / 86400)} 天前");
            }
            catch { return null; }
        }

        List<Dictionary<string, object>> ClaudeIntegrations()
        {
            var plugin = PluginInstalled();
            var mcp = McpInstalled();
            return new List<Dictionary<string, object>>
            {
                new Dictionary<string, object> { ["id"] = "plugin", ["label"] = "Claude Code 插件", ["installed"] = plugin,
                    ["note"] = "Code 会话的工作状态和额度；会话开始时叫她出来。新开的会话生效",
                    ["detail"] = plugin ? SeenText() : null },
                new Dictionary<string, object> { ["id"] = "mcp", ["label"] = "Claude 桌面端（MCP）", ["installed"] = mcp,
                    ["note"] = "Claude 一打开就叫她出来，只用对话时也在（对话里没有工作状态）。重开 Claude 生效" },
            };
        }

        void ClaudeIntegrate(string target, bool install)
        {
            string message;
            try
            {
                if (target == "plugin") message = SetPlugin(install);
                else if (target == "mcp") message = SetMcp(install);
                else return;
            }
            catch (Exception e) { message = "没有完成：" + e.Message; Store.Log($"接入 {target} 失败: {e}"); }
            Store.Log($"接入 {target} {(install ? "install" : "uninstall")}：{message}");
            MessageBox.Show(message, Store.AppName + " · 接入 Claude", MessageBoxButton.OK, MessageBoxImage.Information);
            PushSettings();
        }

        internal static string SetPlugin(bool install)
        {
            if (install && !File.Exists(Path.Combine(PluginDir, ".claude-plugin", "plugin.json"))) return "没找到插件文件夹：" + PluginDir + "\n请用完整的安装包重新安装 " + Store.AppName + "。";
            var file = ClaudeSettings;
            var settings = Store.ReadJson(file);
            if (settings == null && File.Exists(file)) return "读不懂 " + file + "，为了不弄坏它，这次没有改。";
            settings = settings ?? new Dictionary<string, object>();
            var dirs = PluginDirs(settings).Where(d => !SamePath(d, PluginDir) && !SamePath(d, OldPluginDir)).ToList();
            if (install) dirs.Add(PluginDir);
            if (!(settings.TryGetValue("env", out var e) && e is Dictionary<string, object> env)) settings["env"] = env = new Dictionary<string, object>();
            if (dirs.Count > 0) env["CLAUDE_CODE_PLUGIN_DIRS"] = string.Join(";", dirs); else env.Remove("CLAUDE_CODE_PLUGIN_DIRS");
            if (env.Count == 0) settings.Remove("env");
            var backup = Backup(file);
            Store.WriteJson(file, settings);
            return (install ? "已接入 Claude Code 插件。新开的 Code 会话生效。" : "已撤销 Claude Code 插件。")
                + $"\n\n改了：{file}" + (backup != null ? $"\n备份：{backup}" : "");
        }

        internal static string SetMcp(bool install)
        {
            var file = DesktopConfig;
            var cfg = Store.ReadJson(file);
            if (cfg == null && File.Exists(file)) return "读不懂 " + file + "，为了不弄坏它，这次没有改。";
            cfg = cfg ?? new Dictionary<string, object>();
            if (!(cfg.TryGetValue("mcpServers", out var m) && m is Dictionary<string, object> servers)) cfg["mcpServers"] = servers = new Dictionary<string, object>();
            servers.Remove(OldMcpKey);
            if (install) servers[Store.AppName] = new Dictionary<string, object> { ["command"] = Process.GetCurrentProcess().MainModule.FileName, ["args"] = new[] { "--mcp" } };
            else servers.Remove(Store.AppName);
            if (servers.Count == 0) cfg.Remove("mcpServers");
            var backup = Backup(file);
            Store.WriteJson(file, cfg);
            return (install ? "已接入 Claude 桌面端。完全退出 Claude 再打开后生效。" : "已撤销 Claude 桌面端的接入。重开 Claude 后生效。")
                + $"\n\n改了：{file}" + (backup != null ? $"\n备份：{backup}" : "");
        }

        /// <summary>改之前留一份：&lt;文件&gt;.bak-claudia-时间；只留最近 3 份</summary>
        static string Backup(string file)
        {
            if (!File.Exists(file)) return null;
            var copy = file + ".bak-claudia-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            File.Copy(file, copy, true);
            try
            {
                foreach (var old in Directory.GetFiles(Path.GetDirectoryName(file), Path.GetFileName(file) + ".bak-claudia-*").OrderByDescending(x => x).Skip(3))
                    File.Delete(old);
            }
            catch { }
            return copy;
        }
    }
}
