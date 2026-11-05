using System;
using System.Collections.Generic;
using System.Linq;

namespace Claudia
{
    sealed partial class Pet
    {
        // clawd 之后是动画小动作的分组：发呆 / 魔法 / 偷吃零食 / Claude 梗 / 才艺 / 玩耍 / 吃喝 / 时令 / 走路（见 character.json 的 animations[].idle）
        static readonly string[] IdleChoices = { "hum", "stretch", "yawn", "clawd", "fidget", "magic", "snack", "claude", "talent", "play", "food", "season", "walk" };
        static bool SettingOn(string key) => !Store.Settings.TryGetValue(key, out var v) || !(v is bool b) || b;
        static string IdleFrequency => Store.Settings.TryGetValue("idleFrequency", out var v) && v is string s &&
            new[] { "quiet", "normal", "lively" }.Contains(s) ? s : "normal";
        static string[] IdleActions => Store.Settings.TryGetValue("idleActions", out var v) && v is object[] a
            ? IdleChoices.Where(k => a.Contains(k)).ToArray() : IdleChoices;
        Dictionary<string, object> BehaviorState() => new Dictionary<string, object> {
            ["idleEnabled"] = SettingOn("idleEnabled"), ["idleFrequency"] = IdleFrequency, ["idleActions"] = IdleActions,
            ["clickBounce"] = SettingOn("clickBounce"), ["tumbleActions"] = SettingOn("tumbleActions"),
        };
        void SetBehavior(string key, object value)
        {
            if (key == "idleEnabled" || key == "clickBounce" || key == "tumbleActions") { if (!(value is bool)) return; Store.Settings[key] = value; }
            else if (key == "idleFrequency") {
                if (!(value is string s) || !new[] { "quiet", "normal", "lively" }.Contains(s)) return;
                Store.Settings[key] = s;
            } else if (key == "idleActions") {
                if (!(value is object[] a) || a.Any(x => !(x is string s) || !IdleChoices.Contains(s))) return;
                Store.Settings[key] = a.Distinct().ToArray();
            } else return;
            Store.SaveSettings(); Js($"setBehavior({Store.Json.Serialize(BehaviorState())})");
        }
    }
}
