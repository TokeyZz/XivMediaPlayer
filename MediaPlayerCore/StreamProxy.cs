using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MediaPlayerCore
{
    public class StreamProxy : IDisposable
    {
        private static readonly Lazy<StreamProxy> _instance = new Lazy<StreamProxy>(() => new StreamProxy());
        public static StreamProxy Instance => _instance.Value;

        public static System.Net.IWebProxy? OutboundProxy { get; set; }

        private HttpListener _listener;
        private int _port;
        private CancellationTokenSource _cts;
        private ConcurrentDictionary<string, ProxySession> _sessions = new ConcurrentDictionary<string, ProxySession>();

        public class ProxySession
        {
            public string OriginalM3u8Url { get; set; }
            public string PreFetchedM3u8Content { get; set; }
            public Dictionary<string, string> Headers { get; set; }
            public HttpClient Client { get; set; }
            public DateTime LastAccessedUtc { get; set; } = DateTime.UtcNow;
        }

        private StreamProxy()
        {
            var rng = new Random();
            for (int attempt = 0; attempt < 5; attempt++)
            {
                _port = 40000 + rng.Next(1000);
                _listener = new HttpListener();
                _listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
                try
                {
                    _listener.Start();
                    Debug.WriteLine($"[StreamProxy] Started on port {_port} (attempt {attempt})");
                    break;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[StreamProxy] Port {_port} failed: {ex.Message}");
                    _listener.Close();
                    if (attempt == 4) throw;
                }
            }
            _cts = new CancellationTokenSource();
        }

        private bool _acceptLoopRunning;

        public void Start()
        {
            if (_acceptLoopRunning) return;
            lock (_listener)
            {
                if (_acceptLoopRunning) return;
                if (!_listener.IsListening)
                {
                    try { _listener.Start(); } catch (Exception ex) { Debug.WriteLine($"[StreamProxy] Listener start failed: {ex.Message}"); return; }
                }
                _acceptLoopRunning = true;
                Debug.WriteLine($"[StreamProxy] AcceptLoop starting on port {_port}");
                Task.Run(() => AcceptLoop(_cts.Token));
            }
        }

        public string RegisterStream(string m3u8Url, Dictionary<string, string> headers, string preFetchedM3u8Content = null)
        {
            Start();
            string sessionId = Guid.NewGuid().ToString("N");
            Debug.WriteLine($"[StreamProxy] Session: action=create, id={sessionId}");
            
            var client = CreateClient(headers);

            _sessions[sessionId] = new ProxySession
            {
                OriginalM3u8Url = m3u8Url,
                PreFetchedM3u8Content = preFetchedM3u8Content,
                Headers = headers,
                Client = client
            };

            return $"http://127.0.0.1:{_port}/stream.m3u8?sid={sessionId}";
        }
        public string RegisterDirectMediaSession(string mediaUrl, Dictionary<string, string>? headers = null)
        {
            if (string.IsNullOrEmpty(mediaUrl)) return string.Empty;
            string sessionId = Guid.NewGuid().ToString("N");
            Debug.WriteLine($"[StreamProxy] Session: action=create, id={sessionId}");

            var client = CreateClient(headers);

            _sessions[sessionId] = new ProxySession { OriginalM3u8Url = mediaUrl, Headers = headers, Client = client };

            string targetBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(mediaUrl));
            return $"http://127.0.0.1:{_port}/proxy_media?sid={sessionId}&target={Uri.EscapeDataString(targetBase64)}";
        }

        public string GetOriginalUrl(string sessionId)
        {
            if (!string.IsNullOrEmpty(sessionId) && _sessions.TryGetValue(sessionId, out var session))
            {
                return session.OriginalM3u8Url;
            }
            return null;
        }

        public void CleanupExpiredSessions()
        {
            var cutoff = DateTime.UtcNow - TimeSpan.FromMinutes(30);
            foreach (var kvp in _sessions)
            {
                if (kvp.Value.LastAccessedUtc < cutoff)
                {
                    if (_sessions.TryRemove(kvp.Key, out var session))
                    {
                        Debug.WriteLine($"[StreamProxy] Session: action=expire, id={kvp.Key}");
                        try { session.Client?.Dispose(); } catch { }
                    }
                }
            }
        }

        private static HttpClient CreateClient(Dictionary<string, string>? headers)
        {
            var handler = new HttpClientHandler { UseCookies = false };
            if (OutboundProxy != null) handler.Proxy = OutboundProxy;
            handler.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
            bool hasUA = false, hasAccept = false;
            if (headers != null)
            {
                foreach (var kvp in headers)
                {
                    if (kvp.Key.Equals("User-Agent", StringComparison.OrdinalIgnoreCase)) hasUA = true;
                    if (kvp.Key.Equals("Accept", StringComparison.OrdinalIgnoreCase)) hasAccept = true;
                    try { client.DefaultRequestHeaders.TryAddWithoutValidation(kvp.Key, kvp.Value); } catch { }
                }
            }
            if (!hasUA) client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            if (!hasAccept) client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "*/*");
            return client;
        }

        private static async Task<HttpResponseMessage?> FetchWithRetry(HttpClient client, string url, int maxRetries = 2)
        {
            for (int attempt = 0; attempt <= maxRetries; attempt++)
            {
                try { return await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead); }
                catch (TaskCanceledException) { if (attempt >= maxRetries) return null; await Task.Delay(500 * (attempt + 1)); }
                catch (HttpRequestException) { if (attempt >= maxRetries) return null; await Task.Delay(500 * (attempt + 1)); }
            }
            return null;
        }

        private async Task AcceptLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var context = await _listener.GetContextAsync();
                    _ = Task.Run(() => HandleRequest(context));
                }
                catch (Exception ex) { Debug.WriteLine($"[StreamProxy] AcceptLoop error: {ex.Message}"); }
            }
        }

        private async Task HandleRequest(HttpListenerContext context)
        {
            try
            {
                var req = context.Request;
                var res = context.Response;

                Debug.WriteLine($"[StreamProxy] Request: {req.Url.LocalPath} from {req.RemoteEndPoint}");
                string path = req.Url.LocalPath;
                string sid = req.QueryString["sid"];

                if (string.IsNullOrEmpty(sid) || !_sessions.TryGetValue(sid, out var session))
                {
                    CleanupExpiredSessions();
                    res.StatusCode = 404;
                    res.Close();
                    return;
                }

                session.LastAccessedUtc = DateTime.UtcNow;

                if (path == "/stream.m3u8")
                {
                    string m3u8Url = req.QueryString["target"] != null 
                        ? Encoding.UTF8.GetString(Convert.FromBase64String(req.QueryString["target"]))
                        : session.OriginalM3u8Url;

                    string text = "";
                    if (req.QueryString["target"] == null && !string.IsNullOrEmpty(session.PreFetchedM3u8Content))
                    {
                        text = session.PreFetchedM3u8Content;
                    }
                    else
                    {
                        var m3u8Response = await FetchWithRetry(session.Client, m3u8Url);
                        if (m3u8Response == null || !m3u8Response.IsSuccessStatusCode)
                        {
                            Debug.WriteLine($"[StreamProxy] Proxy error: 502 for {m3u8Url}");
                            res.StatusCode = 502;
                            res.Close();
                            return;
                        }
                        text = await m3u8Response.Content.ReadAsStringAsync();
                    }

                    Uri baseUri = new Uri(m3u8Url);
                    var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    var sb = new StringBuilder();

                    foreach (var line in lines)
                    {
                        if (line.StartsWith("#"))
                        {
                            sb.AppendLine(line);
                        }
                        else
                        {
                            Uri absoluteUrl;
                            if (!Uri.TryCreate(baseUri, line, out absoluteUrl))
                            {
                                sb.AppendLine(line);
                                continue;
                            }

                            if (absoluteUrl.ToString().Contains(".m3u8"))
                            {
                                string targetBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(absoluteUrl.ToString()));
                                sb.AppendLine($"http://127.0.0.1:{_port}/stream.m3u8?sid={sid}&target={Uri.EscapeDataString(targetBase64)}");
                            }
                            else
                            {
                                sb.AppendLine(absoluteUrl.ToString());
                            }
                        }
                    }

                    byte[] outBytes = Encoding.UTF8.GetBytes(sb.ToString());
                    res.ContentType = "application/vnd.apple.mpegurl";
                    res.ContentLength64 = outBytes.Length;
                    await res.OutputStream.WriteAsync(outBytes, 0, outBytes.Length);
                }
                else if (path == "/stream.ts")
                {
                    string targetUrl = Encoding.UTF8.GetString(Convert.FromBase64String(req.QueryString["target"]));
                    var tsResponse = await FetchWithRetry(session.Client, targetUrl);
                    if (tsResponse == null || !tsResponse.IsSuccessStatusCode)
                    {
                        Debug.WriteLine($"[StreamProxy] Proxy error: 502 for {targetUrl}");
                        res.StatusCode = 502;
                        res.Close();
                        return;
                    }
                    using (tsResponse)
                    {
                        res.ContentType = tsResponse.Content.Headers.ContentType?.ToString() ?? "video/MP2T";
                        if (tsResponse.Content.Headers.ContentLength.HasValue)
                            res.ContentLength64 = tsResponse.Content.Headers.ContentLength.Value;

                        await tsResponse.Content.CopyToAsync(res.OutputStream);
                    }
                }
                else if (path == "/proxy_media")
                {
                    string targetUrl = Encoding.UTF8.GetString(Convert.FromBase64String(req.QueryString["target"]));
                    long requestedOffset = 0;
                    string rangeHeader = req.Headers["Range"];
                    if (!string.IsNullOrEmpty(rangeHeader) && rangeHeader.StartsWith("bytes="))
                    {
                        string rangeVal = rangeHeader.Substring("bytes=".Length).Split('-')[0];
                        long.TryParse(rangeVal, out requestedOffset);
                    }

                    HttpResponseMessage? response = null;
                    for (int attempt = 0; attempt <= 2; attempt++)
                    {
                        try
                        {
                            var reqMsg = new HttpRequestMessage(HttpMethod.Get, targetUrl);
                            if (requestedOffset > 0)
                                reqMsg.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(requestedOffset, null);
                            response = await session.Client.SendAsync(reqMsg, HttpCompletionOption.ResponseHeadersRead);
                            break;
                        }
                        catch (TaskCanceledException) { if (attempt >= 2) { response = null; break; } await Task.Delay(500 * (attempt + 1)); }
                        catch (HttpRequestException) { if (attempt >= 2) { response = null; break; } await Task.Delay(500 * (attempt + 1)); }
                    }

                    if (response == null)
                    {
                        Debug.WriteLine($"[StreamProxy] Proxy error: 502 for {targetUrl}");
                        res.StatusCode = 502;
                        res.Close();
                        return;
                    }

                    using (response)
                    {
                        res.ContentType = response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";

                        using var stream = await response.Content.ReadAsStreamAsync();

                    if (response.StatusCode == HttpStatusCode.OK && requestedOffset > 0)
                    {
                        long bytesToDiscard = requestedOffset;
                        byte[] discardBuffer = new byte[81920];
                        while (bytesToDiscard > 0)
                        {
                            int toRead = (int)Math.Min(bytesToDiscard, discardBuffer.Length);
                            int read = await stream.ReadAsync(discardBuffer, 0, toRead);
                            if (read == 0) break;
                            bytesToDiscard -= read;
                        }

                        res.StatusCode = 206;
                        long totalLength = response.Content.Headers.ContentLength ?? 0;
                        if (totalLength > 0)
                        {
                            res.ContentLength64 = totalLength - requestedOffset;
                            res.Headers["Content-Range"] = $"bytes {requestedOffset}-{totalLength - 1}/{totalLength}";
                        }
                        else
                        {
                            res.SendChunked = true;
                            res.Headers["Content-Range"] = $"bytes {requestedOffset}-/*";
                        }
                    }
                    else
                    {
                        res.StatusCode = (int)response.StatusCode;
                        if (response.Content.Headers.ContentLength.HasValue)
                        {
                            res.ContentLength64 = response.Content.Headers.ContentLength.Value;
                        }
                        else
                        {
                            res.SendChunked = true;
                        }

                        if (response.StatusCode == HttpStatusCode.PartialContent)
                        {
                            res.Headers["Content-Range"] = response.Content.Headers.ContentRange?.ToString();
                        }
                    }

                    await stream.CopyToAsync(res.OutputStream);
                        }
                    }
                else
                {
                    res.StatusCode = 404;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[StreamProxy] Error handling request: {ex.Message}");
                try { context.Response.StatusCode = 500; } catch { }
            }
            finally
            {
                try { context.Response.Close(); } catch { }
            }

            CleanupExpiredSessions();
        }

        public void Dispose()
        {
            _cts?.Cancel();
            try { _listener?.Stop(); _listener?.Close(); } catch { }
            foreach (var session in _sessions.Values) { try { session.Client?.Dispose(); } catch { } }
            _sessions.Clear();
        }
    }
}
