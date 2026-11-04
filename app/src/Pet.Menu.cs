using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace Claudia
{
    sealed partial class Pet
    {
        // ---------------- 菜单 ----------------
        void FillMenu(Forms.ContextMenuStrip menu)
        {
            menu.Items.Clear();
            Forms.ToolStripMenuItem Item(string text, Action click, bool? check = null)
            {
                var i = new Forms.ToolStripMenuItem(text);
                if (check.HasValue) i.Checked = check.Value;
                i.Click += (_, __) => { try { click(); } catch (Exception e) { Store.Log("菜单出错: " + e); } };
                return i;
            }
            void Sep() => menu.Items.Add(new Forms.ToolStripSeparator());

            menu.Items.Add(Item("摸摸头", () => Js("handleClick('pat')")));
            menu.Items.Add(Item("戳一下", () => Js("handleClick('poke')")));
            var actions = BuildActionMenu(Item);
            if (actions != null) menu.Items.Add(actions);
            Sep();
            // 层级不在这里选：她始终紧贴在 Claude 窗口上方一层（见 Pet.Follow.cs）
            menu.Items.Add(Item("设置…", () => OpenSettings()));
            menu.Items.Add(Item("回到默认位置", Recenter));
            menu.Items.Add(Item(HotkeyLabel != null ? $"暂时隐藏（{HotkeyLabel}）" : "暂时隐藏", () => SetUserHidden(true)));
            Sep();
            menu.Items.Add(Item("退出 " + Store.AppName, UserQuit));
            SoftMenu.Apply(menu);
        }

        // ---------------- 右键「动作」（本地加的，仿 dsh-pet：动作 → 分组 → 具体动作） ----------------
        // 动作列表从当前角色文件夹里的立绘读（和 Store.Sprites 同一套规则：文件名去掉 -2/-3 是姿态名），
        // 认得的姿态按分组配中文名，不认得的放「其他」。点一下演 6 秒，然后回到待机。
        static readonly (string group, string[] poses)[] ActionGroups =
        {
            ("日常", new[] { "idle", "hum", "stretch", "yawn", "tired", "sleeping", "clawd" }),
            ("工作", new[] { "listening", "thinking", "reading", "writing", "running", "searching", "delegating", "compact", "busy_left", "busy_right", "swamped_left", "swamped_right", "update" }),
            ("心情", new[] { "happy", "proud", "surprised", "oops", "sorry", "asking", "annoyed", "puffed", "redhot", "sulk", "coaxed", "forgiven", "dizzy" }),
            ("互动", new[] { "pat", "bye" }),
            ("问候", new[] { "greet_dawn", "greet_morning", "greet_noon", "greet_afternoon", "greet_evening", "greet_night" }),
            ("节日", new[] { "fest_newyear", "fest_spring", "fest_lantern", "fest_dragonboat", "fest_midautumn", "fest_national", "fest_halloween", "fest_christmas" }),
        };
        static readonly Dictionary<string, string> ActionNames = new Dictionary<string, string>
        {
            ["idle"] = "待机", ["hum"] = "哼歌", ["stretch"] = "伸懒腰", ["yawn"] = "打哈欠", ["tired"] = "累了", ["sleeping"] = "睡觉", ["clawd"] = "抱 Clawd",
            ["listening"] = "听你说", ["thinking"] = "思考", ["reading"] = "看书", ["writing"] = "写东西", ["running"] = "敲命令", ["searching"] = "查资料",
            ["delegating"] = "派活", ["compact"] = "整理记忆", ["busy_left"] = "忙碌（左）", ["busy_right"] = "忙碌（右）",
            ["swamped_left"] = "忙不过来（左）", ["swamped_right"] = "忙不过来（右）", ["update"] = "抱礼物",
            ["happy"] = "开心", ["proud"] = "得意", ["surprised"] = "吃惊", ["oops"] = "哎呀", ["sorry"] = "道歉", ["asking"] = "有事问你",
            ["annoyed"] = "不耐烦", ["puffed"] = "鼓脸", ["redhot"] = "红温", ["sulk"] = "赌气", ["coaxed"] = "被哄", ["forgiven"] = "原谅你了", ["dizzy"] = "晕乎乎",
            ["pat"] = "被摸头", ["bye"] = "再见",
            ["greet_dawn"] = "凌晨", ["greet_morning"] = "早上好", ["greet_noon"] = "中午好", ["greet_afternoon"] = "下午好", ["greet_evening"] = "晚上好", ["greet_night"] = "晚安",
            ["fest_newyear"] = "元旦", ["fest_spring"] = "春节", ["fest_lantern"] = "元宵", ["fest_dragonboat"] = "端午",
            ["fest_midautumn"] = "中秋", ["fest_national"] = "国庆", ["fest_halloween"] = "万圣节", ["fest_christmas"] = "圣诞",
        };

        Forms.ToolStripMenuItem BuildActionMenu(Func<string, Action, bool?, Forms.ToolStripMenuItem> item)
        {
            if (string.IsNullOrEmpty(current)) return null;
            var dir = Path.Combine(Store.Characters, current);
            if (!Directory.Exists(dir)) return null;
            var have = new HashSet<string>(StringComparer.Ordinal);
            foreach (var f in Directory.GetFiles(dir))
            {
                var ext = Path.GetExtension(f);
                if (!ext.Equals(".webp", StringComparison.OrdinalIgnoreCase) && !ext.Equals(".png", StringComparison.OrdinalIgnoreCase)) continue;
                var stem = Path.GetFileNameWithoutExtension(f);
                if (!SpritePath.IsSprite(stem) || stem == "idle-blink") continue;
                var p = stem.Split('-')[0];
                have.Add(p);
            }
            if (have.Count == 0) return null;

            var root = new Forms.ToolStripMenuItem("动作");
            var used = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (group, poses) in ActionGroups)
            {
                var sub = new Forms.ToolStripMenuItem(group);
                foreach (var p in poses)
                {
                    if (!have.Contains(p)) continue;
                    used.Add(p);
                    var pose = p;
                    sub.DropDownItems.Add(item(ActionNames.TryGetValue(p, out var n) ? n : p, () => PlayAction(pose), null));
                }
                if (sub.DropDownItems.Count > 0) root.DropDownItems.Add(sub);
            }
            var others = have.Where(p => !used.Contains(p)).OrderBy(p => p, StringComparer.Ordinal).ToList();
            if (others.Count > 0)
            {
                var sub = new Forms.ToolStripMenuItem("其他");
                foreach (var p in others) { var pose = p; sub.DropDownItems.Add(item(p, () => PlayAction(pose), null)); }
                root.DropDownItems.Add(sub);
            }
            return root.DropDownItems.Count > 0 ? root : null;
        }

        void PlayAction(string pose)
        {
            // 姿态名只来自文件名，这里再挡一次引号/反斜杠，免得拼进脚本出问题
            if (pose.IndexOfAny(new[] { '\'', '"', '\\', '\n', '\r' }) >= 0) return;
            Js("(function(p){ if (typeof show !== 'function') return; show(p, true);" +
               " clearTimeout(window.__claudiaMenuTimer);" +
               " window.__claudiaMenuTimer = setTimeout(function(){ if (pose === p) show('idle', true); }, 6000); })('" + pose + "')");
        }
    }
}
