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
            ("互动", new[] { "pat", "bye", "bonk", "stagger", "fallen", "faceplant" }),
            ("问候", new[] { "greet_dawn", "greet_morning", "greet_noon", "greet_afternoon", "greet_evening", "greet_night" }),
            ("小动作", new[] { "look", "tap", "cube", "bubbles", "bookspirit", "flower", "dove", "animals", "butterflies", "snack", "token", "mutter", "gamer" }),
            ("才艺", new[] { "juggle", "cards", "flute", "violin", "dance_skirt", "dance_otaku", "dance_swing", "dressup" }),
            ("玩耍", new[] { "toycar", "watergun", "gomoku", "top", "cat", "shuttlecock", "rockinghorse", "pagebeat" }),
            ("吃喝", new[] { "lunch", "dinner", "rice", "hotpot", "crab", "watermelon", "tanghulu", "niangao", "qingtuan", "laba", "chongyanggao" }),
            ("时令", new[] { "kite", "fan", "leaves", "snowman", "qixi", "zhongyuan", "chongyang" }),
            ("走路", new[] { "walk_crab", "walk_float", "run_left" }),
            ("余额", new[] { "quota_full", "quota_jingle", "quota_count", "quota_empty", "quota_broke" }),
            ("节日", new[] { "fest_newyear", "fest_spring", "fest_lantern", "fest_dragonboat", "fest_midautumn", "fest_halloween", "fest_christmas" }),
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
            ["bonk"] = "撞头", ["stagger"] = "站不稳", ["fallen"] = "摔倒", ["faceplant"] = "趴地",
            ["greet_dawn"] = "凌晨", ["greet_morning"] = "早上好", ["greet_noon"] = "中午好", ["greet_afternoon"] = "下午好", ["greet_evening"] = "晚上好", ["greet_night"] = "晚安",
            ["fest_newyear"] = "元旦", ["fest_spring"] = "春节", ["fest_lantern"] = "元宵", ["fest_dragonboat"] = "端午",
            ["look"] = "东张西望", ["tap"] = "敲桌面", ["cube"] = "玩魔方", ["bubbles"] = "吹泡泡",
            ["bookspirit"] = "书灵现世", ["flower"] = "凭空生花", ["dove"] = "变鸽子",
            ["snack"] = "偷吃零食（点她会被抓包）", ["token"] = "吃 Token", ["mutter"] = "对屏碎碎念", ["gamer"] = "玩游戏气急败坏",
            ["animals"] = "动物环绕", ["butterflies"] = "蝴蝶蜜蜂",
            ["juggle"] = "三球抛接", ["cards"] = "扑克魔术", ["flute"] = "吹笛子", ["violin"] = "小提琴", ["dance_skirt"] = "裙摆舞", ["dance_otaku"] = "宅舞", ["dance_swing"] = "摇摆舞", ["dressup"] = "换装试色",
            ["toycar"] = "玩具汽车", ["watergun"] = "玩水枪", ["gomoku"] = "下五子棋", ["top"] = "抽陀螺", ["cat"] = "撸猫", ["shuttlecock"] = "踢毽子", ["rockinghorse"] = "骑木马", ["pagebeat"] = "拍书打节奏",
            ["lunch"] = "吃午餐", ["dinner"] = "吃晚餐", ["rice"] = "吃白饭", ["hotpot"] = "涮火锅", ["crab"] = "吃大闸蟹", ["watermelon"] = "吃西瓜", ["tanghulu"] = "吃糖葫芦", ["niangao"] = "吃年糕", ["qingtuan"] = "吃青团", ["laba"] = "腊八粥", ["chongyanggao"] = "重阳糕",
            ["kite"] = "放风筝", ["fan"] = "摇扇纳凉", ["leaves"] = "被落叶淹没", ["snowman"] = "堆雪人", ["qixi"] = "七夕穿针", ["zhongyuan"] = "中元放河灯", ["chongyang"] = "重阳赏菊",
            ["walk_crab"] = "螃蟹步", ["walk_float"] = "飘着走", ["run_left"] = "往左跑",
            ["quota_full"] = "钱袋满溢", ["quota_jingle"] = "金袋叮当", ["quota_count"] = "数金皱眉", ["quota_empty"] = "袋空如洗", ["quota_broke"] = "分文不剩",
            ["fest_midautumn"] = "中秋",["fest_halloween"] = "万圣节", ["fest_christmas"] = "圣诞",
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
            // 只有动画、没有静态立绘的姿态（被动画替换的那些、东张西望、余额钱袋……）：精灵图在 anim\ 里才列出来；hidden 的不进菜单
            if (Store.ReadJson(Path.Combine(dir, "character.json"))?.TryGetValue("animations", out var a) == true && a is Dictionary<string, object> anims)
                foreach (var kv in anims)
                {
                    if (!(kv.Value is Dictionary<string, object> cfg) || (cfg.TryGetValue("hidden", out var h) && h is bool hb && hb)) continue;
                    if (cfg.TryGetValue("sheets", out var s) && s is object[] names &&
                        names.OfType<string>().Any(n => File.Exists(Path.Combine(dir, SpritePath.AnimDir, n + ".webp")))) have.Add(kv.Key);
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
            // 动画动作按片长自己回到待机，不再 6 秒后被切掉
            Js("(function(p){ if (typeof show !== 'function') return; show(p, true);" +
               " clearTimeout(window.__claudiaMenuTimer);" +
               " if (typeof anim === 'function' && anim(p) && anim(p).walk && typeof startWalk === 'function' && startWalk(p)) return;" +
               " if (typeof anim === 'function' && anim(p) && transientMs(p)) return;" +
               " window.__claudiaMenuTimer = setTimeout(function(){ if (pose === p) show('idle', true); }, 6000); })('" + pose + "')");
        }
    }
}
