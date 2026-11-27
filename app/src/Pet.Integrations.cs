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
                ["aura"] = AuraMode, ["quotaStyle"] = QuotaStyle,
                ["clickBounce"] = SettingOn("clickBounce"), ["tumbleActions"] = SettingOn("tumbleActions"), ["idleEnabled"] = SettingOn("idleEnabled"), ["idleFrequency"] = IdleFrequency,
                ["notifyResults"] = Store.AppOn("notifyResults"), ["notifyWaiting"] = Store.AppOn("notifyWaiting"), ["notifyBackgroundOnly"] = Store.AppOn("notifyBackgroundOnly"),
                ["physicsEnabled"] = PhysicsEnabled, ["ceilingBounce"] = CeilingBounce,
                ["physics"] = PhysicsRanges.ToDictionary(k => k.Key, k => PhysicsValue(k.Key)),
                ["hideHotkey"] = HideHotkey, ["hotkeyFailed"] = hotkeyFailed && isLeader,
                ["hotkeys"] = new[] { new Dictionary<string, object> { ["id"] = "off", ["label"] = "不用快捷键" } }
                    .Concat(HotkeyChoices.Select(h => new Dictionary<string, object> { ["id"] = h.id, ["label"] = h.label })).ToList(),
                ["character"] = current,
                ["characters"] = Characters().Select(c =>
                {
                    var info = Store.ReadJson(Path.Combine(Store.CharacterDir(c.id), "character.json"));
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
                ["runMode"] = Store.Standalone ? "standalone" : "claude",
                ["pets"] = PetList(), ["maxPets"] = Store.MaxPets,
                ["deskLayer"] = DeskLayer,
                ["clickThrough"] = ClickThrough,   // 所有桌宠共用
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
                        case "clickBounce": case "tumbleActions": case "idleEnabled": case "idleFrequency": SetBehavior(key, value); break;
                        case "notifyResults": case "notifyWaiting": case "notifyBackgroundOnly":   // 共用设置
                            if (value is bool) { Store.App[key] = value; Store.SaveApp(); AppChanged(); } break;
                        case "physicsEnabled": case "ceilingBounce":
                        case "throwPower": case "gravity": case "restitution": case "groundFriction": case "elasticity":
                            SetPhysics(key, value); break;
                        case "size":
                            var v = Math.Min(1.6, Math.Max(0.6, Math.Round(Convert.ToDouble(value) * 100) / 100));
                            // 拖滑块时每一下都会来：只改内存、跟着缩放，文件停下来再写；这一页自己已经显示了新值，不用整页重推
                            Store.Settings["size"] = v; Store.SaveSettingsSoon(); ApplySizes(v);
                            return;
                        case "textSize":   // 单独调文字：同时记为解锁
                            Store.Settings["textSize"] = Math.Min(1.6, Math.Max(0.6, Math.Round(Convert.ToDouble(value) * 100) / 100));
                            Store.Settings["textLinked"] = false; Store.SaveSettingsSoon(); ApplySizes();
                            return;   // 同上；解锁状态这一页自己已经改了
                        case "textLinked": Store.Settings["textLinked"] = !(value is bool lb) || lb; Store.SaveSettings(); ApplySizes(); break;
                        case "showName": Store.Settings["showName"] = on; Store.SaveSettings(); Js($"setShowName({(on ? "true" : "false")})"); break;
                        case "hideHotkey": if (value is string hk) { Store.App["hideHotkey"] = hk; Store.SaveApp(); AppChanged(); } break;   // 共用设置：负责人重新注册
                        case "quotaStyle":
                            if (value is string qs && (qs == "text" || qs == "bars")) { Store.Settings["quotaStyle"] = qs; Store.SaveSettings(); Js($"setQuotaStyle({Q(qs)})"); }
                            break;
                        case "runMode":   // 所有几只共用（Store.SetStandalone），别的那几只一秒内跟上
                            if (value is string rm && (rm == "claude" || rm == "standalone")) { Store.SetStandalone(rm == "standalone"); CheckRunMode(); }
                            break;
                        case "deskLayer": SetDeskLayer(value); break;
                        case "clickThrough": if (value is bool) SetClickThrough(on); break;
                        case "aura":
                            if (value is string m && (m == "auto" || m == "on" || m == "off")) { Store.Settings["auraMode"] = m; Store.SaveSettings(); Js($"setAuraMode({Q(m)})"); }
                            break;
                    }
                    break;
                case "resetPhysics":
                    StopMotion();
                    // Q 弹幅度（elasticity）2026-11-27 挪到「Q 弹」那组，不归「恢复默认手感」管
                    foreach (var setting in PhysicsRanges) if (setting.Key != "elasticity") Store.Settings.Remove(setting.Key);
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
                        hub?.Report();   // 这个名字也是「位置与多开」里这一只的名字
                    }
                    break;
                case "pet": HandlePetCommand(msg); break;
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
                        case "openAboutmePets": Open("https://github.com/hunmer/aboutme-pets"); break;
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
