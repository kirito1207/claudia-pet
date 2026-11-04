using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace Claudia
{
    sealed partial class Pet
    {
        // ---------------- 设置页（settings.html）的状态和命令 ----------------
        // 接入（Claude Code 插件、桌面端 MCP）由 Claudia 自己写配置，见 Pet.Claude.cs。

        public Dictionary<string, object> SettingsState()
        {
            return new Dictionary<string, object>
            {
                ["version"] = Store.Version + " Windows", ["platform"] = "windows", ["appVersion"] = Store.Version,
                ["size"] = Scale, ["textSize"] = TextScale, ["textLinked"] = TextLinked, ["showName"] = ShowName,
                ["aura"] = AuraMode, ["powerSave"] = PowerSave, ["quotaStyle"] = QuotaStyle,
                ["clickBounce"] = SettingOn("clickBounce"), ["tumbleActions"] = SettingOn("tumbleActions"), ["idleEnabled"] = SettingOn("idleEnabled"), ["idleFrequency"] = IdleFrequency, ["idleActions"] = IdleActions,
                ["notifyResults"] = SettingOn("notifyResults"), ["notifyWaiting"] = SettingOn("notifyWaiting"), ["notifyBackgroundOnly"] = SettingOn("notifyBackgroundOnly"),
                ["physicsEnabled"] = PhysicsEnabled, ["ceilingBounce"] = CeilingBounce,
                ["physics"] = PhysicsRanges.ToDictionary(k => k.Key, k => PhysicsValue(k.Key)),
                ["hideHotkey"] = HideHotkey, ["hotkeyFailed"] = hotkeyFailed,
                ["hotkeys"] = new[] { new Dictionary<string, object> { ["id"] = "off", ["label"] = "不用快捷键" } }
                    .Concat(HotkeyChoices.Select(h => new Dictionary<string, object> { ["id"] = h.id, ["label"] = h.label })).ToList(),
                ["character"] = current,
                ["characters"] = Characters().Select(c =>
                {
                    var info = Store.ReadJson(Path.Combine(Store.Characters, c.id, "character.json"));
                    var d = new Dictionary<string, object>
                    {
                        ["id"] = c.id, ["name"] = c.name,
                        ["defaultName"] = info?["name"] as string ?? c.id,
                        ["accent"] = info?["accent"] as string,
                        ["custom"] = Store.Names.TryGetValue(c.id, out var n) ? n as string ?? "" : "",
                    };
                    return d;
                }).ToList(),
                ["integrations"] = ClaudeIntegrations(),
            };
        }

        public void HandleSettings(Dictionary<string, object> msg)
        {
            var cmd = msg.TryGetValue("cmd", out var c) ? c as string : null;
            switch (cmd)
            {
                case "set":
                    var key = msg.TryGetValue("key", out var k) ? k as string : null;
                    msg.TryGetValue("value", out var value);
                    var on = value is bool b && b;
                    switch (key)
                    {
                        case "clickBounce": case "tumbleActions": case "idleEnabled": case "idleFrequency": case "idleActions": SetBehavior(key, value); break;
                        case "notifyResults": case "notifyWaiting": case "notifyBackgroundOnly":
                            if (value is bool) { Store.Settings[key] = value; Store.SaveSettings(); } break;
                        case "physicsEnabled": case "ceilingBounce":
                        case "throwPower": case "gravity": case "restitution": case "groundFriction": case "elasticity":
                            SetPhysics(key, value); break;
                        case "size":
                            var v = Math.Min(1.6, Math.Max(0.6, Math.Round(Convert.ToDouble(value) * 100) / 100));
                            Store.Settings["size"] = v; Store.SaveSettings(); ApplySizes(v);
                            break;
                        case "textSize":   // 单独调文字：同时记为解锁
                            Store.Settings["textSize"] = Math.Min(1.6, Math.Max(0.6, Math.Round(Convert.ToDouble(value) * 100) / 100));
                            Store.Settings["textLinked"] = false; Store.SaveSettings(); ApplySizes();
                            break;
                        case "textLinked": Store.Settings["textLinked"] = !(value is bool lb) || lb; Store.SaveSettings(); ApplySizes(); break;
                        case "showName": Store.Settings["showName"] = on; Store.SaveSettings(); Js($"setShowName({(on ? "true" : "false")})"); break;
                        case "hideHotkey": if (value is string hk) { Store.Settings["hideHotkey"] = hk; Store.SaveSettings(); RegisterHideHotKey(); } break;
                        case "quotaStyle":
                            if (value is string qs && (qs == "text" || qs == "bars")) { Store.Settings["quotaStyle"] = qs; Store.SaveSettings(); Js($"setQuotaStyle({Q(qs)})"); }
                            break;
                        case "powerSave": Store.Settings["powerSave"] = on; Store.SaveSettings(); Js($"setPowerSave({(on ? "true" : "false")})"); break;
                        case "aura":
                            if (value is string m && (m == "auto" || m == "on" || m == "off")) { Store.Settings["auraMode"] = m; Store.SaveSettings(); Js($"setAuraMode({Q(m)})"); }
                            break;
                    }
                    break;
                case "resetPhysics":
                    StopMotion();
                    foreach (var setting in PhysicsRanges) Store.Settings.Remove(setting.Key);
                    Store.Settings.Remove("physicsEnabled"); Store.Settings.Remove("ceilingBounce");
                    Store.SaveSettings(); break;
                case "rename":
                    if (msg.TryGetValue("id", out var rid) && rid is string cid && Characters().Any(x => x.id == cid))
                    {
                        var name = (msg.TryGetValue("name", out var nm) ? nm as string ?? "" : "").Trim();
                        if (name == "") Store.Names.Remove(cid); else Store.Names[cid] = name;
                        Store.SaveSettings();
                        LoadCharacters();
                        Js($"setName({Q(cid)}, {Q(Characters().First(x => x.id == cid).name)})");
                    }
                    break;
                case "integrate":
                    if (msg.TryGetValue("target", out var t) && t is string target && msg.TryGetValue("action", out var a) && a is string action)
                        ClaudeIntegrate(target, action == "install");
                    break;
                case "action":
                    switch (msg.TryGetValue("name", out var an) ? an as string : null)
                    {
                        case "openCharacters": Open(Store.Characters); break;
                        case "openData": Open(Store.Data); break;
                        case "openUpstream": Open("https://github.com/lokicorvus/crosspet"); break;
                        case "openDshPet": Open("https://github.com/PC2005-cloud/dsh-pet"); break;
                    }
                    break;
            }
            PushSettings();
        }

        /// <summary>背景光晕：auto（跟着系统深浅色，深色模式下关）/ on / off</summary>
        static string AuraMode => Store.Settings.TryGetValue("auraMode", out var v) && v is string s && (s == "on" || s == "off") ? s : "auto";

        static void Open(string target) { try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); } catch (Exception e) { Store.Log("打开失败: " + e.Message); } }
    }
}
