using Dalamud.Game.Config;
using Dalamud.Game.Text;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using XivMediaPlayer.GameObjects;
using XivMediaPlayer.Windows;
using MediaPlayerCore;
using MediaPlayerCore.Compositing;
using MediaPlayerCore.Twitch;
using MediaPlayerCore.YtDlp;
using XivMediaPlayer.Compositing;
using Dalamud.Bindings.ImGui;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using System.Collections.Concurrent;


namespace XivMediaPlayer
{
    public sealed partial class Plugin
    {

        private static string CleanUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return url;
            url = url.Trim();
            if (url.StartsWith("\"") && url.EndsWith("\"") && url.Length >= 2)
            {
                url = url.Substring(1, url.Length - 2);
            }
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.IsFile)
            {
                url = uri.LocalPath;
            }

            // Un-proxy local URLs (e.g. VRCVideoCacher) to ensure room sync sends the real underlying URL
            if (url.StartsWith("http://127.0.0.1") && url.Contains("target"))
            {
                int targetIndex = url.IndexOf("target");
                if (targetIndex > 0)
                {
                    string b64 = url.Substring(targetIndex + 6);
                    try
                    {
                        b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
                        string decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(b64));
                        if (decoded.StartsWith("http")) url = decoded;
                    }
                    catch { }
                }
            }

            // Handle stream.m3u8?sid= proxy URLs by recovering the original URL from the active StreamProxy session
            if (url.StartsWith("http://127.0.0.1") && url.Contains("sid="))
            {
                try
                {
                    int sidIndex = url.IndexOf("sid=");
                    if (sidIndex > 0)
                    {
                        string sid = url.Substring(sidIndex + 4);
                        int ampIndex = sid.IndexOf('&');
                        if (ampIndex > 0) sid = sid.Substring(0, ampIndex);

                        string originalUrl = MediaPlayerCore.StreamProxy.Instance.GetOriginalUrl(sid);
                        if (!string.IsNullOrEmpty(originalUrl))
                        {
                            url = originalUrl;
                        }
                    }
                }
                catch { }
            }

            // Clean YouTube tracking noise (si, feature, pp, etc.)
            if (url.Contains("youtube.com") || url.Contains("youtu.be"))
            {
                try
                {
                    var ytUri = new Uri(url);
                    if (ytUri.Host.Contains("youtu.be"))
                    {
                        url = ytUri.GetLeftPart(UriPartial.Path);
                    }
                    else if (ytUri.Host.Contains("youtube.com") && ytUri.AbsolutePath.Contains("/watch"))
                    {
                        string q = ytUri.Query;
                        if (q.StartsWith("?")) q = q.Substring(1);
                        var parts = q.Split('&');
                        var keep = new List<string>();
                        foreach (var part in parts)
                        {
                            if (part.StartsWith("v=") || part.StartsWith("list=") || part.StartsWith("t="))
                                keep.Add(part);
                        }
                        url = ytUri.GetLeftPart(UriPartial.Path) + (keep.Count > 0 ? "?" + string.Join("&", keep) : "");
                    }
                }
                catch { }
            }

            return url;
        }

        private static int ExtractYouTubeStartTimeMs(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return 0;
            try
            {
                var uri = new Uri(url);
                string q = uri.Query;
                if (q.StartsWith("?")) q = q.Substring(1);
                var parts = q.Split('&');
                foreach (var part in parts)
                {
                    if (part.StartsWith("t="))
                    {
                        string tVal = part.Substring(2);
                        return ParseYouTubeTime(tVal);
                    }
                }
            }
            catch { }
            return 0;
        }

        private static int ParseYouTubeTime(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return 0;
            value = Uri.UnescapeDataString(value);
            int totalSeconds = 0;
            if (int.TryParse(value, out int secs)) return secs * 1000;
            foreach (var part in value.Split('h', 'H', 'm', 'M', 's', 'S'))
            {
                if (string.IsNullOrEmpty(part)) continue;
                if (!int.TryParse(part, out int num)) return 0;
            }
            var matchH = System.Text.RegularExpressions.Regex.Match(value, @"(\d+)\s*h", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            var matchM = System.Text.RegularExpressions.Regex.Match(value, @"(\d+)\s*m", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            var matchS = System.Text.RegularExpressions.Regex.Match(value, @"(\d+)\s*s", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (matchH.Success) totalSeconds += int.Parse(matchH.Groups[1].Value) * 3600;
            if (matchM.Success) totalSeconds += int.Parse(matchM.Groups[1].Value) * 60;
            if (matchS.Success) totalSeconds += int.Parse(matchS.Groups[1].Value);
            return totalSeconds * 1000;
        }

        private static string RemoveSpecialSymbols(string value)
        {
            return Regex.Replace(value, @"[^a-zA-Z0-9:/._\-]", "");
        }

        internal enum ChatSeverity { Error, Important, Info, Debug }

        private void PrintVerbose(string message)
        {
            PrintChat(message, ChatSeverity.Debug);
        }

        internal void PrintChat(string message, ChatSeverity severity = ChatSeverity.Important)
        {
            if (_disposed) return;
            var filter = _config.ChatMessageFilter;
            if (filter == ChatMessageLevel.Mute) return;
            if (filter == ChatMessageLevel.Important && (severity == ChatSeverity.Info || severity == ChatSeverity.Debug)) return;
            _chat.Print(message);
        }

        internal void PrintChatError(string message)
        {
            if (_disposed) return;
            if (_config.ChatMessageFilter == ChatMessageLevel.Mute) return;
            _chat.PrintError(message);
        }

        /// <summary>
        /// Reads proxy config and applies it to yt-dlp, VLC, and StreamProxy.
        /// </summary>
        private void ApplyProxySettings()
        {
            bool hasProxy = !string.IsNullOrEmpty(_config.ProxyType)
                         && !string.IsNullOrEmpty(_config.ProxyHost)
                         && _config.ProxyPort > 0;

            if (!hasProxy)
            {
                _ytDlpManager.YtDlpProxy = null;
                if (_mediaManager != null) _mediaManager.VlcProxyArgs = "";
                MediaPlayerCore.StreamProxy.OutboundProxy = null;
                return;
            }

            // Build auth part
            string auth = "";
            if (!string.IsNullOrEmpty(_config.ProxyUsername) && !string.IsNullOrEmpty(_config.ProxyPassword))
            {
                auth = $"{Uri.EscapeDataString(_config.ProxyUsername)}:{Uri.EscapeDataString(_config.ProxyPassword)}@";
            }

            // yt-dlp proxy URL: both "http" and "https" config types map to http://
            // because "https://" means "connect to proxy over TLS" (not "proxy HTTPS targets")
            // and local proxies (Clash/V2Ray) speak plain HTTP, not HTTPS.
            string ytDlpProxyScheme = _config.ProxyType.ToLowerInvariant() == "socks5" ? "socks5" : "http";
            string ytDlpProxyUrl = $"{ytDlpProxyScheme}://{auth}{_config.ProxyHost}:{_config.ProxyPort}";

            // yt-dlp: just pass the proxy URL
            _ytDlpManager.YtDlpProxy = ytDlpProxyUrl;
            Debug.WriteLine($"[Proxy] ApplyProxySettings: yt-dlp proxy = {ytDlpProxyUrl}");

            // VLC proxy args
            string vlcArgs = "";
            switch (_config.ProxyType.ToLowerInvariant())
            {
                case "socks5":
                    // VLC 3.x --socks only supports host:port, no auth
                    vlcArgs = $"--socks={_config.ProxyHost}:{_config.ProxyPort}";
                    break;
                case "http":
                case "https":
                    // VLC --http-proxy also expects http:// not https://
                    vlcArgs = $"--http-proxy={ytDlpProxyUrl}";
                    break;
            }
            if (_mediaManager != null) _mediaManager.VlcProxyArgs = vlcArgs;

            // StreamProxy: create WebProxy
            try
            {
                var webProxy = new System.Net.WebProxy(_config.ProxyHost, _config.ProxyPort);
                if (!string.IsNullOrEmpty(_config.ProxyUsername) && !string.IsNullOrEmpty(_config.ProxyPassword))
                {
                    webProxy.Credentials = new System.Net.NetworkCredential(_config.ProxyUsername, _config.ProxyPassword);
                }
                MediaPlayerCore.StreamProxy.OutboundProxy = webProxy;
            }
            catch
            {
                MediaPlayerCore.StreamProxy.OutboundProxy = null;
            }
        }

        private void EnqueueFrameworkAction(Action action)
        {
            if (!_disposed)
            {
                _frameworkActions.Enqueue(action);
            }
        }

    }
}
