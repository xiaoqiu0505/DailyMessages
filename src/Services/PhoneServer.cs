using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TodoAssistant.Models;

namespace TodoAssistant.Services
{
    /// <summary>
    /// 手机端网页服务器：在局域网内提供「迷你版待办界面」。
    /// 同一份 todos.json：电脑上改手机能看到，手机上改电脑立刻刷新。
    /// 安全：首次访问需输入 PIN → 下发 HttpOnly Cookie；数据只在局域网内传输，不上传互联网。
    /// </summary>
    public sealed class PhoneServer
    {
        private const string CookieName = "tauth";

        private HttpServer _server;
        private readonly TodoStore _store;
        private readonly SettingsStore _settings;

        private readonly Dictionary<string, byte[]> _resources = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _mime = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<string, DateTime> _sessions = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        private readonly object _sessionGate = new object();

        /// <summary>手机端发起的修改（App 用来刷新界面）</summary>
        public event Action DataChangedByPhone;

        public bool IsRunning => _server != null && _server.Running;
        public int Port { get; private set; }
        public string LastError { get; private set; }
        public DateTime StartedAt { get; private set; }

        private long _requestCount;
        public long RequestCount => Interlocked.Read(ref _requestCount);
        public string LastRequest { get; private set; }

        public PhoneServer(TodoStore store, SettingsStore settings)
        {
            _store = store;
            _settings = settings;
            LoadResources();
        }

        private void LoadResources()
        {
            _mime[".html"] = "text/html; charset=utf-8";
            _mime[".js"] = "application/javascript; charset=utf-8";
            _mime[".css"] = "text/css; charset=utf-8";
            _mime[".json"] = "application/json; charset=utf-8";
            _mime[".webmanifest"] = "application/manifest+json; charset=utf-8";
            _mime[".png"] = "image/png";
            _mime[".ico"] = "image/x-icon";

            var asm = typeof(PhoneServer).Assembly;
            var names = new[]
            {
                "web/index.html", "web/app.js", "web/style.css", "web/manifest.webmanifest", "web/sw.js",
                "web/icon-192.png", "web/icon-512.png", "web/apple-touch-icon.png",
            };
            foreach (var n in names)
            {
                try
                {
                    using var s = asm.GetManifestResourceStream(n);
                    if (s == null) { Log.Write("缺少嵌入资源: " + n); continue; }
                    using var ms = new MemoryStream();
                    s.CopyTo(ms);
                    _resources["/" + n.Replace("web/", "")] = ms.ToArray();
                }
                catch (Exception ex) { Log.Write("读取嵌入资源失败 " + n + ": " + ex.Message); }
            }
        }

        // ---------------- 生命周期 ----------------

        public bool Start(int port)
        {
            Stop();
            try
            {
                var server = new HttpServer(port, HandleAsync);
                server.Start();
                _server = server;
                Port = port;
                StartedAt = DateTime.Now;
                LastError = null;
                Log.Write($"手机端服务器已启动: http://{GetBestLanIp()}:{port}/");
                return true;
            }
            catch (Exception ex)
            {
                var inner = ex;
                while (inner.InnerException != null) inner = inner.InnerException;
                LastError = $"端口 {port} 无法监听：{inner.Message}（可能被占用，请在设置里换一个端口）";
                _server = null;
                Log.Write("手机端服务器启动失败: " + LastError);
                return false;
            }
        }

        public void Stop()
        {
            try { _server?.Dispose(); } catch { }
            _server = null;
        }

        // ---------------- 鉴权 ----------------

        private bool AuthEnabled => !string.IsNullOrWhiteSpace(_settings.Current.PhonePin);

        private bool IsAuthorized(HttpRequest req)
        {
            if (!AuthEnabled) return true;
            if (!req.Cookies.TryGetValue(CookieName, out var token) || string.IsNullOrEmpty(token)) return false;
            lock (_sessionGate)
            {
                if (!_sessions.TryGetValue(token, out var exp)) return false;
                if (exp < DateTime.Now) { _sessions.Remove(token); return false; }
                return true;
            }
        }

        private void IssueSession(HttpResponse resp)
        {
            string token = Guid.NewGuid().ToString("N");
            lock (_sessionGate)
            {
                foreach (var k in _sessions.Where(kv => kv.Value < DateTime.Now).Select(kv => kv.Key).ToList())
                    _sessions.Remove(k);
                _sessions[token] = DateTime.Now.AddYears(1);
            }
            resp.SetCookies.Add($"{CookieName}={token}; Path=/; Max-Age=31536000; HttpOnly; SameSite=Lax");
        }

        // ---------------- 路由 ----------------

        private async Task<HttpResponse> HandleAsync(HttpRequest req)
        {
            Interlocked.Increment(ref _requestCount);
            string path = req.Path.ToLowerInvariant();
            string method = req.Method;
            LastRequest = method + " " + req.RawUrl;

            // ---- 无需登录 ----
            if (path == "/api/login" && method == "POST") return await HandleLoginAsync(req).ConfigureAwait(false);

            if (path == "/api/config" && method == "GET")
            {
                var s = _settings.Current;
                return HttpResponse.Json(new
                {
                    pinRequired = AuthEnabled,
                    appName = "待办小助手",
                    leadMinutes = s.LeadMinutes,
                    lanUrl = $"http://{GetBestLanIp()}:{Port}/",
                });
            }
            if (path == "/api/ping")
                return HttpResponse.Json(new { ok = true, time = DateTime.Now.ToString("HH:mm:ss"), port = Port });

            if (path == "/" || path == "/index.html")
            {
                if (!IsAuthorized(req) && AuthEnabled) return HttpResponse.Html(LoginHtml, 200);
                return ServeResource("/index.html");
            }
            if (_resources.ContainsKey(path)) return ServeResource(path);

            // ---- 需要登录 ----
            if (path.StartsWith("/api/"))
            {
                if (!IsAuthorized(req)) return HttpResponse.Json(new { error = "未登录", code = "unauthorized" }, 401);
                return await HandleApiAsync(req, path, method).ConfigureAwait(false);
            }

            return HttpResponse.Text("Not Found", 404);
        }

        private HttpResponse ServeResource(string path)
        {
            if (!_resources.TryGetValue(path, out byte[] bytes)) return HttpResponse.Text("Not Found", 404);
            _mime.TryGetValue(Path.GetExtension(path), out var mime);
            // 页面、脚本、样式、Service Worker 都必须禁缓存：
            // 否则电脑上更新程序后，手机浏览器/Service Worker 会继续用旧文件，
            // 表现为「新按钮点了没反应」。
            bool noCache = path == "/sw.js" || path == "/index.html" ||
                           path.EndsWith(".js") || path.EndsWith(".css") ||
                           path == "/" || path.EndsWith(".webmanifest");
            string cache = noCache
                ? "no-cache, no-store, must-revalidate"
                : "public, max-age=3600";
            return HttpResponse.Bytes(bytes, mime ?? "application/octet-stream", cache);
        }

        private async Task<HttpResponse> HandleLoginAsync(HttpRequest req)
        {
            string pin = "";
            try
            {
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(req.BodyText) ? "{}" : req.BodyText);
                if (doc.RootElement.TryGetProperty("pin", out var p) && p.ValueKind == JsonValueKind.String)
                    pin = p.GetString() ?? "";
            }
            catch { }

            if (!AuthEnabled || string.Equals(pin.Trim(), _settings.Current.PhonePin, StringComparison.OrdinalIgnoreCase))
            {
                var resp = HttpResponse.Json(new { ok = true });
                IssueSession(resp);
                Log.Write("手机端登录成功: " + req.RemoteAddress);
                return resp;
            }
            await Task.Delay(400).ConfigureAwait(false);
            Log.Write("手机端登录失败: " + req.RemoteAddress);
            return HttpResponse.Json(new { ok = false, error = "PIN 不正确" }, 401);
        }

        private async Task<HttpResponse> HandleApiAsync(HttpRequest req, string path, string method)
        {
            bool write = method == "POST" || method == "PUT" || method == "DELETE";
            if (write && !req.SameOrigin) return HttpResponse.Json(new { error = "跨站请求被拒绝" }, 403);

            switch (path)
            {
                case "/api/today":
                case "/api/items":
                    if (method == "GET") return HttpResponse.Json(BuildItemsPayload());
                    if (method == "POST") return await HandleCreateAsync(req).ConfigureAwait(false);
                    break;
                case "/api/toggle" when method == "POST": return await HandleToggleAsync(req).ConfigureAwait(false);
                case "/api/delete" when method == "POST": return await HandleDeleteAsync(req).ConfigureAwait(false);
                case "/api/move" when method == "POST": return await HandleMoveAsync(req).ConfigureAwait(false);
                case "/api/status":
                    return HttpResponse.Json(new { ok = true, port = Port, requests = RequestCount, bestIp = GetBestLanIp() });
            }
            return HttpResponse.Json(new { error = "未知接口 " + path }, 404);
        }

        private object BuildItemsPayload()
        {
            var now = DateTime.Now;
            var days = new List<object>();
            for (int i = 0; i < 5; i++)
            {
                var day = now.Date.AddDays(i);
                var items = _store.GetRange(day, day).Select(it => new
                {
                    id = it.Id,
                    date = it.Date,
                    start = it.Start,
                    end = it.End,
                    title = it.Title,
                    notes = it.Notes,
                    done = it.Done,
                    repeat = it.Repeat.ToString(),
                    repeatText = it.RepeatText,
                    leadMinutes = it.EffectiveLead,
                    startAt = it.StartTime?.ToString("o"),
                    // DDL
                    hasDeadline = it.HasDeadline,
                    deadline = it.DeadlineAt?.ToString("o"),
                    deadlineText = it.DeadlineText,
                    deadlineOverdue = it.HasDeadline && !it.Done && it.IsDeadlinePassed(now),
                }).ToList();
                days.Add(new
                {
                    date = day.ToString("yyyy-MM-dd"),
                    label = i == 0 ? "今天" : i == 1 ? "明天" : i == 2 ? "后天" : i == 3 ? "大后天" : "4 天后",
                    items,
                });
            }
            return new
            {
                now = now.ToString("o"),
                days,
                stats = new
                {
                    openToday = _store.CountOpen(now.Date),
                    totalToday = _store.GetRange(now.Date, now.Date).Count,
                },
            };
        }

        private static string GetStr(JsonElement el, string name)
            => el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() : null;

        private Task<HttpResponse> HandleCreateAsync(HttpRequest req)
        {
            var now = DateTime.Now;
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(req.BodyText) ? "{}" : req.BodyText);
            var r = doc.RootElement;

            string date = GetStr(r, "date");
            string mode = GetStr(r, "dateMode");
            if (string.Equals(mode, "tomorrow", StringComparison.OrdinalIgnoreCase)) date = now.Date.AddDays(1).ToString("yyyy-MM-dd");
            else if (string.Equals(mode, "dayafter", StringComparison.OrdinalIgnoreCase)) date = now.Date.AddDays(2).ToString("yyyy-MM-dd");
                else if (string.Equals(mode, "day4", StringComparison.OrdinalIgnoreCase)) date = now.Date.AddDays(3).ToString("yyyy-MM-dd");
                else if (string.Equals(mode, "day5", StringComparison.OrdinalIgnoreCase)) date = now.Date.AddDays(4).ToString("yyyy-MM-dd");
            else if (string.Equals(mode, "today", StringComparison.OrdinalIgnoreCase)) date = now.Date.ToString("yyyy-MM-dd");
            if (string.IsNullOrWhiteSpace(date) || !TodoItem.TryParseDate(date, out var parsed)) parsed = now.Date;

            var item = new TodoItem
            {
                Date = parsed.ToString("yyyy-MM-dd"),
                Start = string.IsNullOrWhiteSpace(GetStr(r, "start")) ? now.AddHours(1).ToString("HH:mm") : GetStr(r, "start"),
                End = GetStr(r, "end"),
                Title = GetStr(r, "title"),
                Notes = GetStr(r, "notes"),
                LeadMinutes = r.TryGetProperty("leadMinutes", out var lm) && lm.ValueKind == JsonValueKind.Number
                    ? lm.GetInt32() : _settings.Current.LeadMinutes,
                Repeat = ParseRepeat(GetStr(r, "repeat")),
            };
            if (!item.Normalize())
                return Task.FromResult(HttpResponse.Json(new { ok = false, error = "时间格式不正确，请用 HH:mm" }, 400));

            var saved = _store.Upsert(item);
            Log.Write($"手机端新增待办: {saved.Date} {saved.Start} {saved.Title}");
            NotifyPhoneChange();
            return Task.FromResult(HttpResponse.Json(new { ok = true, id = saved.Id }));
        }

        public static RepeatMode ParseRepeat(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return RepeatMode.None;
            return Enum.TryParse<RepeatMode>(s, true, out var m) && Enum.IsDefined(typeof(RepeatMode), m) ? m : RepeatMode.None;
        }

        private Task<HttpResponse> HandleToggleAsync(HttpRequest req)
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(req.BodyText) ? "{}" : req.BodyText);
            var r = doc.RootElement;
            string id = GetStr(r, "id");
            bool done = r.TryGetProperty("done", out var d) && d.ValueKind == JsonValueKind.True;
            string date = GetStr(r, "date");
            var item = _store.FindRaw(id);
            if (item == null) return Task.FromResult(HttpResponse.Json(new { ok = false, error = "待办不存在" }, 404));

            if (item.Repeat != RepeatMode.None && !string.IsNullOrEmpty(date) && TodoItem.TryParseDate(date, out var day))
            {
                var inst = item.Clone();
                inst.Date = day.ToString("yyyy-MM-dd");
                inst.Done = done;
                _store.CompleteRecurringInstance(inst, done, DateTime.Today);
            }
            else _store.SetDone(id, done);

            NotifyPhoneChange();
            return Task.FromResult(HttpResponse.Json(new { ok = true }));
        }

        private Task<HttpResponse> HandleDeleteAsync(HttpRequest req)
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(req.BodyText) ? "{}" : req.BodyText);
            _store.Delete(GetStr(doc.RootElement, "id"));
            NotifyPhoneChange();
            return Task.FromResult(HttpResponse.Json(new { ok = true }));
        }

        private Task<HttpResponse> HandleMoveAsync(HttpRequest req)
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(req.BodyText) ? "{}" : req.BodyText);
            string id = GetStr(doc.RootElement, "id");
            string date = GetStr(doc.RootElement, "date");
            if (!TodoItem.TryParseDate(date, out var day))
                return Task.FromResult(HttpResponse.Json(new { ok = false, error = "日期格式错误" }, 400));
            _store.MoveToDay(id, day);
            NotifyPhoneChange();
            return Task.FromResult(HttpResponse.Json(new { ok = true }));
        }

        private void NotifyPhoneChange() => DataChangedByPhone?.Invoke();

        // ---------------- 登录页 ----------------

        private const string LoginHtml = @"<!doctype html><html lang='zh-CN'><head><meta charset='utf-8'>
<meta name='viewport' content='width=device-width,initial-scale=1,viewport-fit=cover'>
<meta name='theme-color' content='#3b82f6'><title>待办小助手 · 登录</title>
<style>
*{box-sizing:border-box}body{margin:0;min-height:100vh;display:flex;align-items:center;justify-content:center;
background:linear-gradient(160deg,#eef4ff,#f7f8fc);font-family:-apple-system,'PingFang SC','Microsoft YaHei',sans-serif;color:#1e293b;padding:24px}
.card{background:#fff;border-radius:20px;padding:28px 24px;box-shadow:0 12px 40px rgba(30,41,59,.12);width:100%;max-width:360px;text-align:center}
h1{font-size:19px;margin:0 0 6px}p{color:#64748b;font-size:13.5px;margin:0 0 20px}
input{width:100%;font-size:22px;letter-spacing:6px;text-align:center;padding:14px;border-radius:12px;border:1.5px solid #dbe3ef;outline:none;background:#f8fafc}
input:focus{border-color:#3b82f6;background:#fff}
button{margin-top:16px;width:100%;padding:14px;font-size:16px;font-weight:600;color:#fff;border:0;border-radius:12px;background:#3b82f6}
button:disabled{opacity:.6}.err{color:#ef4444;font-size:13px;height:18px;margin-top:10px}
</style></head><body><div class='card'><h1>待办小助手</h1><p>请输入电脑上显示的配对 PIN</p>
<input id='pin' inputmode='latin' autocomplete='off' autofocus placeholder='●●●●●●'>
<button id='go'>进入</button><div class='err' id='err'></div></div>
<script>
var i=document.getElementById('pin'),b=document.getElementById('go'),e=document.getElementById('err');
async function go(){e.textContent='';var pin=i.value.trim();if(!pin){e.textContent='请输入 PIN';return}
 b.disabled=true;try{var r=await fetch('/api/login',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({pin:pin})});
 if(r.ok){location.replace('/')}else{e.textContent='PIN 不正确，请核对电脑端'}}catch(x){e.textContent='网络错误：'+x.message}finally{b.disabled=false}}
b.onclick=go;i.onkeydown=function(ev){if(ev.key==='Enter')go()};
</script></body></html>";

        // ---------------- 网络信息 ----------------

        /// <summary>最佳局域网 IPv4（用于显示二维码 / 提示）</summary>
        public static string GetBestLanIp()
        {
            try
            {
                var candidates = new List<(string Ip, int Score)>();
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    var props = ni.GetIPProperties();
                    if (props.GatewayAddresses.Count == 0) continue;
                    foreach (var ua in props.UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        string ip = ua.Address.ToString();
                        if (ip.StartsWith("127.") || ip.StartsWith("169.254.")) continue;
                        int score = 0;
                        if (ip.StartsWith("192.168.")) score += 30;
                        else if (ip.StartsWith("10.")) score += 25;
                        else if (ip.StartsWith("172.")) score += 20;
                        string name = (ni.Name + " " + ni.Description).ToLowerInvariant();
                        if (name.Contains("wi-fi") || name.Contains("wlan") || name.Contains("wireless")) score += 20;
                        if (name.Contains("ethernet") || name.Contains("以太网")) score += 15;
                        if (name.Contains("radmin") || name.Contains("vpn") || name.Contains("virtual") || name.Contains("vmware")) score -= 40;
                        candidates.Add((ip, score));
                    }
                }
                var best = candidates.OrderByDescending(c => c.Score).FirstOrDefault();
                if (best.Ip != null) return best.Ip;
            }
            catch (Exception ex) { Log.Error("获取局域网 IP 失败", ex); }
            try
            {
                using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                s.Connect("223.5.5.5", 53);
                return ((IPEndPoint)s.LocalEndPoint).Address.ToString();
            }
            catch { }
            return "127.0.0.1";
        }

        /// <summary>所有可用局域网地址</summary>
        public static List<string> GetAllLanIps()
        {
            var list = new List<string>();
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        string ip = ua.Address.ToString();
                        if (ip.StartsWith("127.") || ip.StartsWith("169.254.")) continue;
                        if (!list.Contains(ip)) list.Add(ip);
                    }
                }
            }
            catch { }
            return list;
        }
    }
}
