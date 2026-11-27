using System;
using System.Collections.Generic;
using System.Linq;

namespace Claudia
{
    sealed partial class Pet
    {
        static bool SettingOn(string key) => !Store.Settings.TryGetValue(key, out var v) || !(v is bool b) || b;
        static string IdleFrequency => Store.Settings.TryGetValue("idleFrequency", out var v) && v is string s &&
            new[] { "quiet", "normal", "lively" }.Contains(s) ? s : "normal";
        Dictionary<string, object> BehaviorState() => new Dictionary<string, object> {
            ["idleEnabled"] = SettingOn("idleEnabled"), ["idleFrequency"] = IdleFrequency,
            ["clickBounce"] = SettingOn("clickBounce"), ["tumbleActions"] = SettingOn("tumbleActions"),
            ["squashAmount"] = PhysicsValue("elasticity"),   // Q 弹幅度：点击和落地共用（2026-11-27），网页自己压，不再由这里转交撞击
        };
        void SetBehavior(string key, object value)
        {
            if (key == "idleEnabled" || key == "clickBounce" || key == "tumbleActions") { if (!(value is bool)) return; Store.Settings[key] = value; }
            else if (key == "idleFrequency") {
                if (!(value is string s) || !new[] { "quiet", "normal", "lively" }.Contains(s)) return;
                Store.Settings[key] = s;
            } else return;
            Store.SaveSettings(); Js($"setBehavior({Store.Json.Serialize(BehaviorState())})");
        }
    }
}
