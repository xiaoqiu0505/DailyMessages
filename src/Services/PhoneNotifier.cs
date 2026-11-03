using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using TodoAssistant.Models;
using System.Threading;
using System.Threading.Tasks;

namespace TodoAssistant.Services
{
    /// <summary>
    /// 手机推送（ntfy）：电脑端把提醒 POST 到 ntfy 主题，手机装 ntfy App 订阅同一主题即可收到。
    /// 主题相当于密码，请勿泄露。全部失败不影响电脑端提醒。
    /// </summary>
    public static class PhoneNotifier
    {
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };

        /// <summary>由 Notifier 注入的 tag 生成器（ntfy x-tags 只允许字母数字）</summary>
        public static Func<string, string> TagGenerator { get; set; } = t => "ta";

        /// <summary>发送一条推送。返回 true 表示至少一个通道发送成功。</summary>
        public static async Task<bool> PushAsync(AppSettings settings, string title, string message, bool urgent, string tag)
        {
            if (settings == null || !settings.NtfyEnabled || string.IsNullOrWhiteSpace(settings.NtfyTopic))
                return false;

            string baseUrl = (settings.NtfyServer ?? "https://ntfy.sh").Trim().TrimEnd('/');
            if (string.IsNullOrEmpty(baseUrl)) baseUrl = "https://ntfy.sh";
            string url = baseUrl + "/" + Uri.EscapeDataString(settings.NtfyTopic.Trim());
            string xTag = TagGenerator(tag ?? "");

            bool ok = await PostJson(url, settings, title, message, urgent, xTag).ConfigureAwait(false);
            if (!ok) ok = await PostHeaders(url, message, urgent, xTag).ConfigureAwait(false);
            return ok;
        }

        private static async Task<bool> PostJson(string url, AppSettings s, string title, string message, bool urgent, string xTag)
        {
            try
            {
                var payload = new Dictionary<string, object>
                {
                    ["topic"] = s.NtfyTopic.Trim(),
                    ["message"] = message,
                    ["title"] = title,
                    ["priority"] = urgent && s.NtfyHighPriority ? 5 : 4,
                    ["tags"] = new[] { "bell", "alarm_clock" },
                    ["click"] = HomeUrl(s),
                    ["actions"] = new object[]
                    {
                        new Dictionary<string, object> { ["action"] = "view", ["label"] = "打开待办", ["url"] = HomeUrl(s), ["clear"] = true },
                    },
                };
                string body = JsonSerializer.Serialize(payload);
                using var req = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                };
                req.Headers.TryAddWithoutValidation("X-Tags", xTag);
                req.Headers.TryAddWithoutValidation("User-Agent", "TodoAssistant/1.0");
                using var resp = await Http.SendAsync(req).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode) { Log.Write("手机推送成功(JSON): " + url); return true; }
                Log.Write("手机推送失败(JSON): HTTP " + (int)resp.StatusCode + " " + await SafeBody(resp).ConfigureAwait(false));
            }
            catch (Exception ex)
            {
                Log.Write("手机推送失败(JSON): " + ex.GetType().Name + ": " + ex.Message);
            }
            return false;
        }

        private static async Task<bool> PostHeaders(string url, string message, bool urgent, string xTag)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(message ?? "", Encoding.UTF8, "text/plain"),
                };
                req.Headers.TryAddWithoutValidation("Tags", urgent ? "bell,alarm_clock" : "bell");
                req.Headers.TryAddWithoutValidation("Priority", urgent ? "5" : "4");
                req.Headers.TryAddWithoutValidation("X-Title", Uri.EscapeDataString("待办小助手"));
                req.Headers.TryAddWithoutValidation("X-Tags", xTag);
                req.Headers.TryAddWithoutValidation("User-Agent", "TodoAssistant/1.0");
                using var resp = await Http.SendAsync(req).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode) return true;
                Log.Write("手机推送失败(text): HTTP " + (int)resp.StatusCode);
            }
            catch (Exception ex)
            {
                Log.Write("手机推送失败(text): " + ex.GetType().Name + ": " + ex.Message);
            }
            return false;
        }

        private static string HomeUrl(AppSettings s)
        {
            try
            {
                if (s.PhoneServerEnabled)
                {
                    string ip = PhoneServer.GetBestLanIp();
                    if (!string.IsNullOrEmpty(ip)) return $"http://{ip}:{s.PhonePort}/";
                }
            }
            catch { }
            return "http://ntfy.sh/" + (s.NtfyTopic ?? "");
        }

        private static async Task<string> SafeBody(HttpResponseMessage resp)
        {
            try { return (await resp.Content.ReadAsStringAsync().ConfigureAwait(false)); }
            catch { return ""; }
        }

        /// <summary>发送测试推送</summary>
        public static Task<bool> PushTestAsync(AppSettings s)
            => PushAsync(s, "待办小助手 · 测试推送", "如果你在手机上看到这条通知，说明推送配置成功 🎉", true, "test");
    }
}
