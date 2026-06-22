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

        private void OnMediaCommand(string command, string args)
        {
            if (_disposed) return;

            string[] splitArgs = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (splitArgs.Length == 0)
            {
                _settingsWindow.IsOpen = true;
                return;
            }

            switch (splitArgs[0].ToLower())
            {
                case "depth":
                    _depthPreviewWindow.IsOpen = !_depthPreviewWindow.IsOpen;
                    PrintChat($"[媒体播放器] 深度预览 {(_depthPreviewWindow.IsOpen ? "已开启" : "已关闭")}。", ChatSeverity.Info);
                    break;
                case "twitch":
                    if (splitArgs.Length > 1 && splitArgs[1].Contains("twitch.tv"))
                    {
                        if (_playerObject != null)
                        {
                            _lastStreamObject = CurrentAudioSource;
                            PlayRouted(splitArgs[1], CurrentAudioSource, 0);
                        }
                    }
                    else
                    {
                        // Open twitch chat for current streamer
                        if (!string.IsNullOrEmpty(_currentStreamer))
                        {
                            try
                            {
                                Process.Start(new ProcessStartInfo()
                                {
                                    FileName = @"https://www.twitch.tv/popout/" + _currentStreamer + @"/chat?popout=",
                                    UseShellExecute = true,
                                    Verb = "OPEN"
                                });
                            }
                            catch (Exception e)
                            {
                                _pluginLog.Warning(e, e.Message);
                            }
                        }
                        else
                        {
                            PrintChatError("[媒体播放器] 没有活动流, 使用: /media twitch <url>");
                        }
                    }
                    break;

                case "rtmp":
                    if (splitArgs.Length > 1 && splitArgs[1].Contains("rtmp"))
                    {
                        if (_playerObject != null)
                        {
                            _lastStreamObject = CurrentAudioSource;
                            TuneIntoStream(splitArgs[1], CurrentAudioSource, 0);
                        }
                    }
                    break;

                case "play":
                    if (splitArgs.Length > 1)
                    {
                        string url = splitArgs[1];
                        if (_playerObject == null)
                        {
                            PrintChatError("[媒体播放器] 尚未初始化, 请确认已登录游戏");
                            _pluginLog.Warning("[Media Player] _playerObject is null. _hasBeenInitialized=" + _hasBeenInitialized);
                            break;
                        }
                        _lastStreamObject = CurrentAudioSource;
                        /* if (url.Contains("twitch.tv")) {
                           TuneIntoStream(url, CurrentAudioSource, false);
                         } else if (url.StartsWith("rtmp")) {
                           TuneIntoStream(url, CurrentAudioSource, true);
                         } else */
                        if (YtDlpManager.IsUrlSupported(url))
                        {
                            // Invoke yt-dlp resolution
                            PrintChat("[媒体播放器] 正在通过 yt-dlp 解析链接...", ChatSeverity.Info);
                            PlayRouted(url, CurrentAudioSource);
                        }
                        else
                        {
                            // Fallback — direct URL to VLC
                            TuneIntoStream(url, CurrentAudioSource, 0);
                        }
                    }
                    else
                    {
                        PrintChatError("[媒体播放器] 用法: /media play <链接>");
                    }
                    break;

                case "ytdlp-update":
                    if (_ytDlpManager.IsAvailable())
                    {
                        PrintChat("[媒体播放器] 正在更新 yt-dlp...", ChatSeverity.Info);
                        Task.Run(async () =>
                        {
                            bool success = await _ytDlpManager.SelfUpdate();
                            EnqueueFrameworkAction(() => PrintChat(success ? "[媒体播放器] yt-dlp 已更新" : "[媒体播放器] yt-dlp 更新失败", ChatSeverity.Info));
                        });
                    }
                    else
                    {
                        PrintChatError("[媒体播放器] 未找到 yt-dlp, 请在 /media 设置中配置");
                    }
                    break;

                case "stop":
                    _mediaManager?.StopStream();
                    RestoreBgm();
                    ResetStreamValues();
                    PrintChat("[媒体播放器] 播放已停止");
                    break;

                case "fixaudio":
                    RestoreBgm();
                    FixWindowsVolume();
                    PrintChat("[媒体播放器] 游戏音频已恢复");
                    break;

                case "video":
                    _videoWindow.IsOpen = !_videoWindow.IsOpen;
                    break;

                case "emulate":
                    if (splitArgs.Length >= 3)
                    {
                        string ip = splitArgs[1];
                        string session = splitArgs[2];
                        _ = ConnectEmulationAsync(ip, session);
                    }
                    else
                    {
                        PrintChatError("[媒体播放器] 用法: /media emulate <IP> <会话>");
                    }
                    break;

                case "listen":
                    if (!string.IsNullOrEmpty(_potentialStream) && _playerObject != null)
                    {
                        PlayRouted(_potentialStream, CurrentAudioSource, 0);
                    }
                    break;

                case "tv":
                case "screen":
                    string locKey = LocationKey;
                    bool isOutdoors = !string.IsNullOrEmpty(locKey) && locKey.StartsWith("zone_");
                    bool isIsland = !string.IsNullOrEmpty(locKey) && locKey.StartsWith("island_");
                    bool hasPrivileges = isOutdoors || isIsland || IsHousingMenuOpen;
                    
                    if (!hasPrivileges)
                    {
                        PrintChatError("[媒体播放器] 屏幕设置菜单只能在'布置家具'菜单打开时或室外使用");
                        break;
                    }

                    if (splitArgs.Length < 2)
                    {
                        // No subcommand: toggle the settings window
                        _screenSettingsWindow.Toggle();
                    }
                    else
                    {
                        HandleScreenCommand(splitArgs);
                    }
                    break;

                case "help":
                    PrintChat("[媒体播放器] 命令列表:\n" +
                      " /media — Open settings\n" +
                      " /media twitch <url> — Tune into a Twitch stream\n" +
                      " /media rtmp <url> — Tune into an RTMP stream\n" +
                      " /media play <url> — Play a media URL\n" +
                      " /media stop — Stop current stream\n" +
                      " /media video — Toggle video window\n" +
                      " /media emulate <ip> <session> — Connect to emulation server\n" +
                      " /media screen [place|move|rotate|scale|reset|save] — 3D screen\n" +
                      " /media listen — Tune into a shared stream\n" +
                      " /media ytdlp-update — Update yt-dlp\n" +
                      " /media help — Show this help");
                    break;

                default:
                    _settingsWindow.Toggle();
                    break;
            }
        }

        private async Task ConnectEmulationAsync(string ip, string session)
        {
            PrintChat($"[媒体播放器] 正在连接到模拟服务器 {ip}...", ChatSeverity.Info);
            string rtsp = await Networking.EmulationClient.GetRtspUrlAsync(ip, session);
            if (string.IsNullOrEmpty(rtsp))
            {
                PrintChatError("[媒体播放器] 无法从模拟服务器获取流信息");
                return;
            }

            _emulationClient?.Dispose();
            _emulationClient = new Networking.EmulationClient(ip, session);
            _controllerService?.Dispose();
            _controllerService = new Networking.ControllerService(ip, session);
            _controllerService.Start();

            // Start FFmpeg backend instead of VLC for extreme low latency
            _mediaManager?.PlayFFmpegStream(rtsp);
            _lastStreamURL = rtsp;
            // Deprecated: v2 heartbeat handles pushing state
        }

        internal void SendEmulationMouseState(float normX, float normY, float scroll, bool lmb, bool rmb)
        {
            if (_emulationClient != null)
            {
                byte xByte = (byte)(Math.Clamp(normX, 0f, 1f) * 255f);
                byte yByte = (byte)(Math.Clamp(1f - normY, 0f, 1f) * 255f);
                _emulationClient.SendMouseState(xByte, yByte, scroll, lmb, rmb);
            }
        }

    }
}
