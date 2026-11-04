using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Claudia
{
    /// <summary>
    /// claudia-pet.exe --mcp：写在 Claude 桌面端的 claude_desktop_config.json 里的本地 MCP 服务。
    /// 它不给 Claude 提供任何工具，只借 MCP 的生命周期：Claude 一打开就拉起它，它把桌宠叫出来（Ensure），
    /// 然后一直等着，Claude 关掉时关掉它。这样只用对话、不开 Code 会话时，桌宠也跟着 Claude 出现。
    /// 协议：一行一个 JSON-RPC 消息（MCP 的 stdio 传输），只回 initialize、ping 和空的 tools/list。
    /// </summary>
    static class Mcp
    {
        public static void Run()
        {
            Store.Log("MCP 服务启动：" + Ensure.Run());
            var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
            var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
            string line;
            while ((line = input.ReadLine()) != null)
            {
                if (line.Trim() == "") continue;
                Dictionary<string, object> msg;
                try { msg = Store.Json.DeserializeObject(line) as Dictionary<string, object>; } catch { continue; }
                if (msg == null || !msg.TryGetValue("id", out var id) || id == null) continue;   // 通知（notifications/*）不用回
                var method = msg.TryGetValue("method", out var m) ? m as string : null;
                object result = null, error = null;
                switch (method)
                {
                    case "initialize":
                        var p = msg.TryGetValue("params", out var pv) ? pv as Dictionary<string, object> : null;
                        result = new Dictionary<string, object>
                        {
                            ["protocolVersion"] = p != null && p.TryGetValue("protocolVersion", out var ver) && ver is string v ? v : "2024-11-05",
                            ["capabilities"] = new Dictionary<string, object> { ["tools"] = new Dictionary<string, object>() },
                            ["serverInfo"] = new Dictionary<string, object> { ["name"] = Store.AppName, ["version"] = Store.Version },
                            ["instructions"] = Store.AppName + " 是 Claude 娘桌宠，只借这个连接跟着 Claude 打开和关闭，不提供工具。",
                        };
                        break;
                    case "ping": result = new Dictionary<string, object>(); break;
                    case "tools/list": result = new Dictionary<string, object> { ["tools"] = new object[0] }; break;
                    default: error = new Dictionary<string, object> { ["code"] = -32601, ["message"] = Store.AppName + " 不提供这个功能：" + method }; break;
                }
                var reply = new Dictionary<string, object> { ["jsonrpc"] = "2.0", ["id"] = id };
                if (error != null) reply["error"] = error; else reply["result"] = result;
                try { output.WriteLine(Store.Json.Serialize(reply)); } catch { break; }
            }
            Store.Log("MCP 服务结束（Claude 关掉了连接）");
        }
    }
}
