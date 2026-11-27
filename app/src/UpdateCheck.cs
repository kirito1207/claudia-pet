using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

namespace Claudia
{
    /// <summary>检查更新（2026-11-28 用户定：默认开，只提醒、不自动下载）。Linux 版是 linux/claudia/update.py，两边规则一样。
    /// 只向 GitHub 问一句「最新发布的版本号是多少」（releases/latest），不带任何用户信息，不上传数据。
    /// 由负责人做（一台电脑只问一次）：一天最多一次，当上负责人一分钟后才第一次问；设置「关于」里可以「现在检查」。
    /// 结果记在数据目录的 update.json：{checkedAt 上次问的时间（秒）, latest 最新版本号, url 下载页, error 上次没问成的原因, notified 已经让她说过的版本}。
    /// 每只的设置页都读这个文件显示；有新版本时负责人让她在气泡里说一次（同一个版本只说一次）</summary>
    static class UpdateCheck
    {
        public const string Api = "https://api.github.com/repos/kirito1207/claudia-pet/releases/latest";
        public const string ReleasesPage = "https://github.com/kirito1207/claudia-pet/releases/latest";
        public const int IntervalS = 24 * 3600, FirstDelayS = 60;
        static string StateFile => Path.Combine(Store.Data, "update.json");

        /// <summary>latest 比 current 新：按「数字.数字.数字」比，前面的 v、后面的 -beta 之类不看；认不出来的不算新</summary>
        public static bool Newer(string latest, string current)
        {
            var a = Parts(latest); var b = Parts(current);
            if (a == null || b == null) return false;
            for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
            {
                int x = i < a.Length ? a[i] : 0, y = i < b.Length ? b[i] : 0;
                if (x != y) return x > y;
            }
            return false;
        }
        static int[] Parts(string v)
        {
            v = (v ?? "").Trim().TrimStart('v', 'V');
            var cut = v.IndexOfAny(new[] { '-', '+', ' ' });
            if (cut >= 0) v = v.Substring(0, cut);
            var parts = v.Split('.');
            if (parts.Length == 0 || parts.Length > 4) return null;
            var nums = new int[parts.Length];
            for (var i = 0; i < parts.Length; i++) if (!int.TryParse(parts[i], out nums[i]) || nums[i] < 0) return null;
            return nums;
        }

        /// <summary>从 GitHub 的回答里取版本号和下载页；认不出来返回 null</summary>
        public static (string version, string url)? Parse(string json)
        {
            Dictionary<string, object> d;
            try { d = Store.Json.DeserializeObject(json) as Dictionary<string, object>; } catch { return null; }
            if (d == null || !(d.TryGetValue("tag_name", out var t) && t is string tag) || Parts(tag) == null) return null;
            var url = d.TryGetValue("html_url", out var u) && u is string s && s.StartsWith("https://github.com/", StringComparison.Ordinal) ? s : ReleasesPage;
            return (tag.Trim().TrimStart('v', 'V'), url);
        }

        public static Dictionary<string, object> Load() => Store.ReadJson(StateFile) ?? new Dictionary<string, object>();
        public static void Save(Dictionary<string, object> state) { try { Store.WriteJson(StateFile, state); } catch (Exception e) { Store.Log("记不下检查更新的结果: " + e.Message); } }
        public static double Num(Dictionary<string, object> d, string key) { try { return d.TryGetValue(key, out var v) && v != null ? Convert.ToDouble(v) : 0; } catch { return 0; } }
        public static string Str(Dictionary<string, object> d, string key) => d.TryGetValue(key, out var v) ? v as string : null;

        /// <summary>后台问一次 GitHub（不在界面线程上等）。成功返回 (版本, 下载页)；没问成返回 null，error 是原因</summary>
        public static async Task<((string version, string url)? release, string error)> Fetch()
        {
            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;   // .NET Framework 默认可能还没开 TLS 1.2，GitHub 只认它
                using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) })
                {
                    http.DefaultRequestHeaders.UserAgent.ParseAdd("claudia-pet");   // GitHub 要求带 User-Agent；只写程序名，不带用户信息
                    http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
                    using (var r = await http.GetAsync(Api).ConfigureAwait(false))
                    {
                        if (!r.IsSuccessStatusCode) return (null, "GitHub 回了 " + (int)r.StatusCode);
                        var body = await r.Content.ReadAsStringAsync().ConfigureAwait(false);
                        var release = Parse(body);
                        if (release == null) return (null, "没认出最新版本号");
                        return (release, null);
                    }
                }
            }
            catch (Exception e) { return (null, "没连上 GitHub（" + e.GetBaseException().Message + "）"); }
        }

        /// <summary>给设置页的一份：开没开、最新版本、比现在的新不新、下载页、上次什么时候问的、上次没问成的原因、正在问</summary>
        public static Dictionary<string, object> ForPage(bool checking)
        {
            var d = Load();
            var latest = Str(d, "latest");
            return new Dictionary<string, object>
            {
                ["latest"] = latest, ["newer"] = latest != null && Newer(latest, Store.Version),
                ["url"] = Str(d, "url") ?? ReleasesPage, ["checkedAt"] = Num(d, "checkedAt"),
                ["error"] = Str(d, "error"), ["checking"] = checking,
            };
        }
    }
}
