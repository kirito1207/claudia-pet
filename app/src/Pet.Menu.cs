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
            ("互动", new[] { "pat", "pout", "bye", "bonk", "stagger", "fallen", "faceplant" }),
            ("问候", new[] { "greet_dawn", "greet_morning", "greet_noon", "greet_afternoon", "greet_evening", "greet_night" }),
            ("小动作", new[] { "look", "tap", "cube", "bubbles", "bookspirit", "flower", "dove", "animals", "butterflies", "snack", "token", "mutter", "gamer", "toes", "nails", "scratch", "leg_bounce", "nose_pick", "sole_tickle", "finger_count", "eye_rub", "ear_pull", "tummy", "hand_flick", "wrist_roll", "cheek_puff", "pen_spin", "pillow", "toothbrush", "abacus", "mutter_lazy" }),
            ("才艺", new[] { "juggle", "cards", "flute", "violin", "dance_skirt", "dance_otaku", "dance_swing", "dressup", "leaf_flute", "glass_harp", "air_guitar", "music_box", "beat", "tuning_fork", "maracas", "triangle", "woodfish", "wind_chime", "curtsy" }),
            ("玩耍", new[] { "toycar", "watergun", "gomoku", "top", "cat", "shuttlecock", "rockinghorse", "pagebeat", "jenga", "dandelion", "forehead_flick", "bubble_wrap", "skip_stone", "diabolo", "hat_toss", "apple_toss", "dice", "cats_cradle", "shadow_step", "umbrella_spin", "plate_spin" }),
            ("吃喝", new[] { "lunch", "dinner", "rice", "hotpot", "crab", "watermelon", "tanghulu", "niangao", "qingtuan", "laba", "chongyanggao", "cocoa", "chocolate", "shaved_ice", "chestnut", "pudding", "cracker", "corn", "soup", "fishbone", "onigiri", "soda", "sweet_potato", "honey", "coffee" }),
            ("时令", new[] { "kite", "fan", "leaves", "snowman", "qixi", "zhongyuan", "chongyang", "sunshade", "snow_step", "cicada", "hand_warmer", "snowflake", "raindrop", "warm_breath", "clear_umbrella", "windy" }),
            ("手工", new[] { "beads", "pencil", "dried_flowers", "bell_forge", "paper_boat", "clay", "porcelain", "ink", "paper_umbrella", "spool", "grasshopper", "doll_sew" }),
            ("走路", new[] { "walk_crab", "walk_float", "run_left" }),
            ("余额", new[] { "quota_full", "quota_jingle", "quota_count", "quota_empty", "quota_broke" }),
            ("节日", new[] { "fest_newyear", "fest_spring", "fest_lantern", "fest_dragonboat", "fest_midautumn", "fest_halloween", "fest_christmas" }),
        };
        static readonly Dictionary<string, string> ActionNames = new Dictionary<string, string>
        {
            ["idle"] = "待机", ["hum"] = "哼歌", ["stretch"] = "伸懒腰", ["yawn"] = "打哈欠", ["tired"] = "累了", ["sleeping"] = "睡觉", ["clawd"] = "抱 Clawd",
            ["listening"] = "听你说", ["thinking"] = "思考", ["reading"] = "看书", ["writing"] = "写东西", ["running"] = "敲命令", ["searching"] = "查资料",
            ["delegating"] = "派活", ["compact"] = "整理记忆", ["busy_left"] = "忙碌（左）", ["busy_right"] = "忙碌（右）",
            ["swamped_left"] = "忙不过来（左）", ["swamped_right"] = "忙不过来（右）", ["update"] = "拆礼物",
            ["happy"] = "开心", ["proud"] = "得意", ["surprised"] = "吃惊", ["oops"] = "哎呀", ["sorry"] = "道歉", ["asking"] = "有事问你",
            ["annoyed"] = "不耐烦", ["puffed"] = "鼓脸", ["redhot"] = "红温", ["sulk"] = "赌气", ["coaxed"] = "被哄", ["forgiven"] = "原谅你了", ["dizzy"] = "晕乎乎",
            ["pat"] = "被摸头", ["pout"] = "傲娇", ["bye"] = "再见",
            ["bonk"] = "撞头", ["stagger"] = "站不稳", ["fallen"] = "摔倒", ["faceplant"] = "趴地",
            ["greet_dawn"] = "凌晨", ["greet_morning"] = "早上好", ["greet_noon"] = "中午好", ["greet_afternoon"] = "下午好", ["greet_evening"] = "晚上好", ["greet_night"] = "晚安",
            ["fest_newyear"] = "元旦", ["fest_spring"] = "春节", ["fest_lantern"] = "元宵", ["fest_dragonboat"] = "端午",
            ["look"] = "东张西望", ["tap"] = "敲桌面", ["cube"] = "玩魔方", ["bubbles"] = "吹泡泡",
            ["bookspirit"] = "书灵现世", ["flower"] = "凭空生花", ["dove"] = "变鸽子",
            ["snack"] = "偷吃零食", ["token"] = "吃 Token", ["mutter"] = "对屏碎碎念", ["gamer"] = "玩游戏气急败坏",
            ["animals"] = "动物环绕", ["butterflies"] = "蝴蝶蜜蜂",
            ["juggle"] = "三球抛接", ["cards"] = "扑克魔术", ["flute"] = "吹笛子", ["violin"] = "小提琴", ["dance_skirt"] = "裙摆舞", ["dance_otaku"] = "宅舞", ["dance_swing"] = "摇摆舞", ["dressup"] = "换装试色",
            ["toycar"] = "玩具汽车", ["watergun"] = "玩水枪", ["gomoku"] = "下五子棋", ["top"] = "抽陀螺", ["cat"] = "撸猫", ["shuttlecock"] = "踢毽子", ["rockinghorse"] = "骑木马", ["pagebeat"] = "拍书打节奏",
            ["lunch"] = "吃午餐", ["dinner"] = "吃晚餐", ["rice"] = "吃白饭", ["hotpot"] = "涮火锅", ["crab"] = "吃大闸蟹", ["watermelon"] = "吃西瓜", ["tanghulu"] = "吃糖葫芦", ["niangao"] = "吃年糕", ["qingtuan"] = "吃青团", ["laba"] = "腊八粥", ["chongyanggao"] = "重阳糕",
            ["kite"] = "放风筝", ["fan"] = "摇扇纳凉", ["leaves"] = "被落叶淹没", ["snowman"] = "堆雪人", ["qixi"] = "七夕穿针", ["zhongyuan"] = "中元放河灯", ["chongyang"] = "重阳赏菊",
            ["walk_crab"] = "螃蟹步", ["walk_float"] = "飘着走", ["run_left"] = "往左跑",
            ["quota_full"] = "钱袋满溢", ["quota_jingle"] = "金袋叮当", ["quota_count"] = "数金皱眉", ["quota_empty"] = "袋空如洗", ["quota_broke"] = "分文不剩",
            ["fest_midautumn"] = "中秋",["fest_halloween"] = "万圣节", ["fest_christmas"] = "圣诞",
            // 2026-11-07 接入的作者新动作
            ["toes"] = "伸脚趾", ["nails"] = "咬指甲", ["scratch"] = "抓痒", ["leg_bounce"] = "抖腿", ["nose_pick"] = "抠鼻子", ["sole_tickle"] = "挠脚心", ["finger_count"] = "掰手指", ["eye_rub"] = "揉眼睛", ["ear_pull"] = "揪耳朵", ["tummy"] = "摸肚子", ["hand_flick"] = "甩手甩水", ["wrist_roll"] = "转手腕", ["cheek_puff"] = "憋气鼓腮", ["pen_spin"] = "转笔", ["pillow"] = "拍枕头", ["toothbrush"] = "晨间刷牙（早上）", ["leaf_flute"] = "吹叶笛", ["glass_harp"] = "弹玻璃杯沿", ["air_guitar"] = "空气吉他", ["music_box"] = "手摇八音盒", ["beat"] = "打拍子数拍", ["tuning_fork"] = "按音叉", ["maracas"] = "摇沙锤", ["triangle"] = "敲三角铁", ["woodfish"] = "敲木鱼", ["wind_chime"] = "摇小风铃", ["curtsy"] = "淑女屈膝礼", ["jenga"] = "叠叠乐", ["dandelion"] = "吹蒲公英", ["forehead_flick"] = "弹脑门", ["bubble_wrap"] = "戳泡泡纸", ["skip_stone"] = "打水漂", ["diabolo"] = "抖空竹", ["hat_toss"] = "抛帽接帽", ["apple_toss"] = "抛接小苹果", ["dice"] = "摇骰盅", ["cats_cradle"] = "翻花绳", ["shadow_step"] = "踩影子", ["umbrella_spin"] = "转伞", ["plate_spin"] = "转盘子", ["cocoa"] = "冲可可", ["chocolate"] = "分巧克力", ["shaved_ice"] = "刨冰屑", ["chestnut"] = "剥栗子", ["pudding"] = "吸布丁", ["cracker"] = "咬脆饼", ["corn"] = "啃玉米", ["soup"] = "喝汤吹凉", ["fishbone"] = "挑鱼刺", ["onigiri"] = "捏饭团", ["soda"] = "摇汽水瓶", ["sweet_potato"] = "烤红薯", ["honey"] = "舀蜂蜜", ["coffee"] = "闻咖啡豆", ["sunshade"] = "举手遮阳偷看", ["snow_step"] = "原地踏雪", ["cicada"] = "听蝉", ["hand_warmer"] = "抱暖手炉", ["snowflake"] = "掌心接雪花", ["raindrop"] = "接雨滴", ["warm_breath"] = "搓手哈气取暖", ["clear_umbrella"] = "撑透明小伞", ["windy"] = "被风吹乱发型", ["beads"] = "串珠链", ["pencil"] = "削铅笔", ["dried_flowers"] = "扎束干花", ["bell_forge"] = "打铁小铃", ["paper_boat"] = "折小纸船", ["clay"] = "捏陶土", ["porcelain"] = "描瓷纹", ["ink"] = "磨墨", ["paper_umbrella"] = "糊纸伞骨", ["spool"] = "绕线轴", ["grasshopper"] = "编草蚱蜢", ["doll_sew"] = "缝布偶", ["abacus"] = "拨算盘珠", ["mutter_lazy"] = "犯懒碎碎念",
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
