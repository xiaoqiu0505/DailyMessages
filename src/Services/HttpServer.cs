using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TodoAssistant.Services
{
    /// <summary>
    /// 极简 HTTP 服务器（基于 TcpListener，自己解析 HTTP/1.1）。
    /// 不用 HttpListener：局域网绑定需要管理员登记 http.sys URL，直接监听端口则普通权限即可。
    /// </summary>
    public sealed class HttpServer : IDisposable
    {
        private readonly Func<HttpRequest, Task<HttpResponse>> _handler;
        private TcpListener _listener;
        private CancellationTokenSource _cts;
        private readonly int _port;

        public int Port => _port;
        public bool Running => _listener != null;

        public HttpServer(int port, Func<HttpRequest, Task<HttpResponse>> handler)
        {
            _port = port;
            _handler = handler;
        }

        /// <summary>监听所有网卡（局域网可访问）。</summary>
        public void Start()
        {
            var listener = new TcpListener(IPAddress.Any, _port);
            listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            listener.Start(64);
            _listener = listener;
            _cts = new CancellationTokenSource();
            _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
            Log.Write($"手机端服务器已启动: 0.0.0.0:{_port}");
        }

        private async Task AcceptLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && _listener != null)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false); }
                catch (ObjectDisposedException) { break; }
                catch (SocketException) when (ct.IsCancellationRequested) { break; }
                catch (Exception ex) { Log.Error("接受连接失败", ex); continue; }
                _ = Task.Run(() => HandleClientAsync(client, ct));
            }
        }

        private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
        {
            try
            {
                using (client)
                {
                    client.NoDelay = true;
                    using var stream = client.GetStream();
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    cts.CancelAfter(TimeSpan.FromSeconds(30));   // 慢连接保护

                    var req = await HttpRequest.ReadAsync(stream, cts.Token).ConfigureAwait(false);
                    if (req == null) return;

                    HttpResponse resp;
                    try { resp = await _handler(req).ConfigureAwait(false); }
                    catch (Exception ex)
                    {
                        Log.Write("请求处理异常: " + ex.Message);
                        resp = HttpResponse.Json(new { error = "服务器内部错误" }, 500);
                    }
                    if (resp == null) resp = HttpResponse.Text("Not Found", 404);

                    resp.WriteTo(stream, req.CloseConnection);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException || ex is OperationCanceledException)
            {
                // 客户端断开 / 超时：忽略
            }
            catch (Exception ex)
            {
                Log.Error("处理连接失败", ex);
            }
        }

        public void Dispose()
        {
            try { _cts?.Cancel(); } catch { }
            try { _listener?.Stop(); } catch { }
            try { _listener?.Server?.Dispose(); } catch { }
            _listener = null;
        }
    }

    /// <summary>HTTP 请求</summary>
    public sealed class HttpRequest
    {
        public string Method { get; private set; } = "GET";
        public string RawUrl { get; private set; } = "/";
        public string Path { get; private set; } = "/";
        public string Query { get; private set; } = "";
        public string Version { get; private set; } = "HTTP/1.1";
        public Dictionary<string, string> Headers { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Cookies { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public byte[] Body { get; private set; } = Array.Empty<byte>();
        public string RemoteAddress { get; set; } = "?";

        public string BodyText => Body.Length == 0 ? "" : Encoding.UTF8.GetString(Body);

        public bool CloseConnection
        {
            get
            {
                if (Headers.TryGetValue("Connection", out var c))
                    return c.IndexOf("close", StringComparison.OrdinalIgnoreCase) >= 0;
                return Version == "HTTP/1.0";
            }
        }

        public string Host => Headers.TryGetValue("Host", out var h) ? h : "";

        /// <summary>同源校验（防 CSRF）：没有 Origin 视为同源</summary>
        public bool SameOrigin
        {
            get
            {
                if (!Headers.TryGetValue("Origin", out var origin) || string.IsNullOrEmpty(origin)) return true;
                try { return string.Equals(new Uri(origin).Authority, Host, StringComparison.OrdinalIgnoreCase); }
                catch { return false; }
            }
        }

        public static async Task<HttpRequest> ReadAsync(Stream stream, CancellationToken ct)
        {
            var req = new HttpRequest();

            // 读请求头（按字节读，支持中文路径/头）
            var headerBytes = new List<byte>(1024);
            byte[] buf = new byte[1];
            int last4 = 0;
            while (true)
            {
                int n = await stream.ReadAsync(buf, 0, 1, ct).ConfigureAwait(false);
                if (n <= 0) return null;
                headerBytes.Add(buf[0]);
                last4 = (last4 << 8) | buf[0];
                if (headerBytes.Count > 65536) return null;
                if (last4 == 0x0D0A0D0A) break;         // \r\n\r\n
                if (headerBytes.Count >= 2 && headerBytes[headerBytes.Count - 1] == 0x0A && headerBytes[headerBytes.Count - 2] == 0x0A)
                    break;                                // \n\n
            }

            string headerText = Encoding.UTF8.GetString(headerBytes.ToArray());
            var lines = headerText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            if (lines.Length == 0 || string.IsNullOrWhiteSpace(lines[0])) return null;

            var first = lines[0].Trim().Split(' ');
            if (first.Length >= 2)
            {
                req.Method = first[0].ToUpperInvariant();
                req.RawUrl = first[1];
                if (first.Length >= 3) req.Version = first[2];
            }
            if (req.RawUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            {
                int slash = req.RawUrl.IndexOf('/', "http://".Length);
                req.RawUrl = slash >= 0 ? req.RawUrl.Substring(slash) : "/";
            }
            int q = req.RawUrl.IndexOf('?');
            req.Path = q >= 0 ? req.RawUrl.Substring(0, q) : req.RawUrl;
            req.Query = q >= 0 ? req.RawUrl.Substring(q + 1) : "";
            if (string.IsNullOrEmpty(req.Path)) req.Path = "/";
            try { req.Path = Uri.UnescapeDataString(req.Path); } catch { }

            for (int i = 1; i < lines.Length; i++)
            {
                string line = lines[i];
                if (string.IsNullOrWhiteSpace(line)) continue;
                int colon = line.IndexOf(':');
                if (colon <= 0) continue;
                string name = line.Substring(0, colon).Trim();
                string value = line.Substring(colon + 1).Trim();
                req.Headers[name] = value;
                if (string.Equals(name, "Cookie", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var part in value.Split(';'))
                    {
                        int eq = part.IndexOf('=');
                        if (eq <= 0) continue;
                        req.Cookies[part.Substring(0, eq).Trim()] = part.Substring(eq + 1).Trim();
                    }
                }
            }

            // 读请求体
            int len = 0;
            if (req.Headers.TryGetValue("Content-Length", out var cl))
                int.TryParse(cl, NumberStyles.Integer, CultureInfo.InvariantCulture, out len);
            if (len > 0)
            {
                len = Math.Min(len, 1024 * 1024);
                var body = new byte[len];
                int read = 0;
                while (read < len)
                {
                    int n = await stream.ReadAsync(body, read, len - read, ct).ConfigureAwait(false);
                    if (n <= 0) break;
                    read += n;
                }
                req.Body = read == len ? body : body.Take(read).ToArray();
            }
            return req;
        }
    }

    /// <summary>HTTP 响应</summary>
    public sealed class HttpResponse
    {
        public int Status { get; set; } = 200;
        public string ContentType { get; set; } = "text/plain; charset=utf-8";
        public byte[] Body { get; set; } = Array.Empty<byte>();
        public Dictionary<string, string> Headers { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public List<string> SetCookies { get; } = new List<string>();

        public static HttpResponse Html(string html, int status = 200)
            => new HttpResponse { Status = status, ContentType = "text/html; charset=utf-8", Body = Encoding.UTF8.GetBytes(html) };

        public static HttpResponse Text(string text, int status = 200, string mime = "text/plain; charset=utf-8")
            => new HttpResponse { Status = status, ContentType = mime, Body = Encoding.UTF8.GetBytes(text ?? "") };

        public static HttpResponse Json(object payload, int status = 200)
            => new HttpResponse
            {
                Status = status,
                ContentType = "application/json; charset=utf-8",
                Body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload,
                    new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping })),
                Headers = { ["Cache-Control"] = "no-store" },
            };

        public static HttpResponse Bytes(byte[] data, string mime, string cacheControl = "public, max-age=3600")
            => new HttpResponse
            {
                ContentType = mime,
                Body = data,
                Headers = { ["Cache-Control"] = cacheControl },
            };

        internal void WriteTo(Stream stream, bool close)
        {
            var sb = new StringBuilder(256);
            sb.Append("HTTP/1.1 ").Append(Status).Append(' ').Append(ReasonPhrase(Status)).Append("\r\n");
            sb.Append("Date: ").Append(DateTime.UtcNow.ToString("R")).Append("\r\n");
            sb.Append("Server: TodoAssistant\r\n");
            sb.Append("Content-Type: ").Append(ContentType).Append("\r\n");
            sb.Append("Content-Length: ").Append(Body?.Length ?? 0).Append("\r\n");
            if (!Headers.ContainsKey("Connection")) sb.Append("Connection: ").Append(close ? "close" : "keep-alive").Append("\r\n");
            foreach (var kv in Headers) sb.Append(kv.Key).Append(": ").Append(kv.Value).Append("\r\n");
            foreach (var c in SetCookies) sb.Append("Set-Cookie: ").Append(c).Append("\r\n");
            sb.Append("X-Content-Type-Options: nosniff\r\n");
            sb.Append("Referrer-Policy: no-referrer\r\n");
            sb.Append("\r\n");

            var head = Encoding.UTF8.GetBytes(sb.ToString());
            stream.Write(head, 0, head.Length);
            if (Body != null && Body.Length > 0) stream.Write(Body, 0, Body.Length);
            stream.Flush();
        }

        private static string ReasonPhrase(int status) => status switch
        {
            200 => "OK",
            204 => "No Content",
            302 => "Found",
            400 => "Bad Request",
            401 => "Unauthorized",
            403 => "Forbidden",
            404 => "Not Found",
            405 => "Method Not Allowed",
            500 => "Internal Server Error",
            _ => "OK",
        };
    }
}
