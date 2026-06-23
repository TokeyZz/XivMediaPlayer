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

        private unsafe void OnDraw()
        {
            if (_worldRenderer != null) _worldRenderer.UseDepthOcclusion = _config.DepthOcclusionEnabled;

            bool useDifferenceFallback = false;
            if (_config.EnableWanderersCampfireFix && _objectTable != null) {
                foreach (var obj in _objectTable) {
                    if (obj == null || obj.Name == null) continue;
                    var name = obj.Name.ToString();
                    // Wanderer's Campfire, Feu de camp du vagabond, Wanderers Lagerfeuer
                    if (obj.DataId == 197274 || (name.Contains("Wanderer's Campfire", StringComparison.OrdinalIgnoreCase) ||
                                         name.Contains("Wanderers Lagerfeuer", StringComparison.OrdinalIgnoreCase) ||
                                         name.Contains("Feu de camp du vagabond", StringComparison.OrdinalIgnoreCase) ||
                                         name.Contains("放浪神の焚き火", StringComparison.OrdinalIgnoreCase))) {
                        useDifferenceFallback = true;
                        break;
                    }
                }
            }

            // Reset per-frame depth capture flag
            _depthCapture?.BeginFrame();

            if (!_dependencyManager.IsReady)
            {
                if (_dependencyManager.IsDownloading || _dependencyManager.HasError)
                {
                    ImGui.SetNextWindowPos(new System.Numerics.Vector2(ImGui.GetIO().DisplaySize.X / 2 - 200, ImGui.GetIO().DisplaySize.Y / 2 - 50));
                    ImGui.SetNextWindowSize(new System.Numerics.Vector2(400, 100));
                    if (ImGui.Begin("XivMediaPlayer - Initial Setup", ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoMove))
                    {
                        ImGui.TextWrapped(_dependencyManager.Status);
                        if (_dependencyManager.IsDownloading)
                        {
                            ImGui.ProgressBar(_dependencyManager.DownloadProgress, new System.Numerics.Vector2(-1, 0));
                        }
                        if (_dependencyManager.HasError)
                        {
                            ImGui.TextColored(new System.Numerics.Vector4(1, 0, 0, 1), _dependencyManager.ErrorMessage);
                            if (ImGui.Button("Retry Download"))
                            {
                                _ = _dependencyManager.DownloadDependenciesAsync();
                            }
                        }
                        ImGui.End();
                    }
                }
                return;
            }

            if (_uiCapture != null)
            {
                _uiCapture.CaptureFrame();
            }

            // Decode frames every tick, even if the video window is closed,
            // so the world-space renderer always has fresh textures.
            _videoWindow.UpdateFrame();

            _windowSystem.Draw();

            // World-space video rendering
            if (_worldRenderer?.IsActive == true && _clientState.IsLoggedIn)
            {
                // Only read depth to CPU when occlusion is on
                if (_depthCapture != null)
                    _depthCapture.ReadDepthEnabled = _worldRenderer.UseDepthOcclusion;

                _videoWindow.GetCurrentVideoTexture(out IntPtr videoSrv, out int videoWidth, out int videoHeight, out int videoTrueWidth, out int videoTrueHeight);
                if (videoSrv != IntPtr.Zero)
                {
                    // Get camera info for depth occlusion
                    System.Numerics.Vector3? cameraPos = null;
                    System.Numerics.Vector3? cameraForward = null;
                    float nearPlane = 0.1f, farPlane = 10000f;
                    float fovY = 0.785f;
                    float aspectRatio = 1.0f;
                    System.Numerics.Vector3 cameraRight = System.Numerics.Vector3.UnitX;
                    System.Numerics.Vector3 cameraUp = System.Numerics.Vector3.UnitY;
                    System.Numerics.Matrix4x4? viewProjMatrix = null;

                    if (_camera != null)
                    {
                        try
                        {
                            var sceneCamera = _camera->CameraBase.SceneCamera;
                            var rawView = sceneCamera.RenderCamera != null ? sceneCamera.RenderCamera->ViewMatrix : sceneCamera.ViewMatrix;
                            if (sceneCamera.RenderCamera == null) return;
                            var rawProj = sceneCamera.RenderCamera->ProjectionMatrix;
                            var view = System.Runtime.CompilerServices.Unsafe.As<
                              FFXIVClientStructs.FFXIV.Common.Math.Matrix4x4,
                              System.Numerics.Matrix4x4>(ref rawView);

                            // FFXIV matrices often leave the 4th column uninitialized or zeroed.
                            // We MUST set M44 = 1.0 to make it an affine transformation matrix so Invert() works!
                            view.M14 = 0f;
                            view.M24 = 0f;
                            view.M34 = 0f;
                            view.M44 = 1f;

                            System.Numerics.Matrix4x4.Invert(view, out var invView);
                            
                            cameraPos = invView.Translation;
                            cameraRight = System.Numerics.Vector3.Normalize(new System.Numerics.Vector3(invView.M11, invView.M12, invView.M13));
                            cameraUp = System.Numerics.Vector3.Normalize(new System.Numerics.Vector3(invView.M21, invView.M22, invView.M23));
                            cameraForward = System.Numerics.Vector3.Normalize(new System.Numerics.Vector3(invView.M31, invView.M32, invView.M33));
                            
                            fovY = sceneCamera.RenderCamera != null ? sceneCamera.RenderCamera->FoV : 0.785f;
                            aspectRatio = sceneCamera.RenderCamera != null ? sceneCamera.RenderCamera->AspectRatio : 1.0f;
                            nearPlane = sceneCamera.RenderCamera != null ? sceneCamera.RenderCamera->NearPlane : 0.1f;
                            farPlane = sceneCamera.RenderCamera != null ? sceneCamera.RenderCamera->FarPlane : 10000f;
                            
                            var proj = System.Runtime.CompilerServices.Unsafe.As<
                              FFXIVClientStructs.FFXIV.Common.Math.Matrix4x4,
                              System.Numerics.Matrix4x4>(ref rawProj);
                            
                            viewProjMatrix = view * proj;
                        }
                        catch { }
                    }

                    System.Numerics.Vector2? hoverUV = null;
                    float progress = 0f;
                    bool isPlaying = false;
                    float playbackState = 0.0f; // 0 = Stop, 1 = Play, 2 = Paused

                    var activeStream = _mediaManager?.GetActiveStream();
                    if (activeStream != null)
                    {
                        if (activeStream.Length > 0)
                            progress = activeStream.Time / (float)activeStream.Length;

                        isPlaying = activeStream.PlaybackState == NAudio.Wave.PlaybackState.Playing;
                        if (isPlaying) playbackState = 1.0f;
                        else if (activeStream.PlaybackState == NAudio.Wave.PlaybackState.Paused) playbackState = 2.0f;
                    }
                    if (_mediaManager != null && _mediaManager.IsFFmpegPlaying)
                    {
                        isPlaying = true;
                        playbackState = 1.0f;
                    }

                    if (_isResolvingMedia) {
                        playbackState = 1.0f;
                    }

                    if (isPlaying || _isResolvingMedia) {
                        _screensaverTimer.Stop();
                        _screensaverTimer.Reset();
                    } else {
                        if (!_screensaverTimer.IsRunning) _screensaverTimer.Start();
                    }

                    float showScreensaver = (_screensaverTimer.ElapsedMilliseconds > 5000 || (_isResolvingMedia && !isPlaying)) ? 1.0f : 0.0f;
                    float timeSeconds = (float)(((DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + _serverTimeOffsetMs) / 1000.0) % 864000.0);

                    var mousePos = ImGui.GetIO().MousePos;
                    var (tl, tr, br, bl) = _worldRenderer.Transform.Corners;

                    bool vTL = _gameGui.WorldToScreen(tl, out var sTL);
                    bool vTR = _gameGui.WorldToScreen(tr, out var sTR);
                    bool vBR = _gameGui.WorldToScreen(br, out var sBR);
                    bool vBL = _gameGui.WorldToScreen(bl, out var sBL);

                    System.Numerics.Vector2 uv = new System.Numerics.Vector2(-1, -1);
                    if (cameraPos.HasValue && cameraForward.HasValue)
                    {
                        var viewport = ImGui.GetMainViewport();
                        float ndcX = ((mousePos.X - viewport.Pos.X) / viewport.Size.X) * 2f - 1f;
                        float ndcY = -(((mousePos.Y - viewport.Pos.Y) / viewport.Size.Y) * 2f - 1f);

                        float fovDist = 1.0f / (float)Math.Tan(fovY * 0.5f);
                        var rayOrigin = cameraPos.Value;
                        var rayDir = System.Numerics.Vector3.Normalize(ndcX * aspectRatio * cameraRight + ndcY * cameraUp - fovDist * cameraForward.Value);

                        var tvRight = tr - tl;
                        var tvDown = bl - tl;
                        var tvNormal = System.Numerics.Vector3.Normalize(System.Numerics.Vector3.Cross(tvRight, tvDown));

                        float denom = System.Numerics.Vector3.Dot(tvNormal, rayDir);
                        if (Math.Abs(denom) > 1e-6f)
                        {
                            float t = System.Numerics.Vector3.Dot(tl - rayOrigin, tvNormal) / denom;
                            if (t > 0f)
                            {
                                var hitPoint = rayOrigin + rayDir * t;
                                var d = hitPoint - tl;
                                float u = System.Numerics.Vector3.Dot(d, tvRight) / tvRight.LengthSquared();
                                float v = System.Numerics.Vector3.Dot(d, tvDown) / tvDown.LengthSquared();
                                uv = new System.Numerics.Vector2(u, v);
                            }
                        }
                    }
                    else if (vTL || vTR || vBR || vBL)
                    {
                        uv = MathUtils.InverseBilinear(mousePos, sTL, sTR, sBR, sBL);
                    }

                    // UI Alpha Mask Check
                    if (!_config.DisableUIBlockDetection && _uiCapture != null && uv.X >= 0 && uv.Y >= 0)
                    {
                        var io = ImGui.GetIO();
                        float scaleX = io.DisplaySize.X > 0 ? _uiCapture.Width / io.DisplaySize.X : 1.0f;
                        float scaleY = io.DisplaySize.Y > 0 ? _uiCapture.Height / io.DisplaySize.Y : 1.0f;
                        int physX = (int)(mousePos.X * scaleX);
                        int physY = (int)(mousePos.Y * scaleY);

                        IntPtr unk68Ptr = SceneColorProbe.GetToneAdjustSourceSrvPtr();
                        
                        bool isOccluding = _uiCapture.IsPixelOccluding(physX, physY, unk68Ptr, _depthCapture, useDifferenceFallback);
                        if (isOccluding)
                        {
                            uv = new System.Numerics.Vector2(-1, -1);
                        }
                    }

                    // We must calculate mouse state unconditionally every frame so that holding the mouse
                    // and dragging it OVER the window doesn't falsely trigger a "Click" event!
                    bool hasFocus = GetForegroundWindow() == _mainWindowHandle;
                    bool isLeftMousePressed = hasFocus && (GetAsyncKeyState(0x01) & 0x8000) != 0; // VK_LBUTTON
                    bool isRightMousePressed = hasFocus && (GetAsyncKeyState(0x02) & 0x8000) != 0; // VK_RBUTTON
                    bool isMouseClicked = isLeftMousePressed && !_wasLeftMousePressed;
                    bool isMouseReleased = !isLeftMousePressed && _wasLeftMousePressed;
                    _wasLeftMousePressed = isLeftMousePressed;

                    bool isOnTv = uv.X >= 0 && uv.X <= 1 && uv.Y >= 0 && uv.Y <= 1;
                    if (isMouseClicked)
                    {
                        _clickStartedOnTv = isOnTv;
                    }

                    if (_clickStartedOnTv && isLeftMousePressed)
                    {
                        ImGui.GetIO().WantCaptureMouse = true;
                    }

                    if (isOnTv)
                    {
                        hoverUV = uv;

                        // Pass native mouse state to Emulation Server if active
                        SendEmulationMouseState(uv.X, uv.Y, 0, isLeftMousePressed, isRightMousePressed);

                        if (_currentStreamer != "Emulation" && _currentStreamer != "Camera")
                        {
                            if (isMouseReleased && _clickStartedOnTv)
                            {
                            // Handle Volume Slider Drag
                            if (uv.Y > 0.95f && uv.Y < 0.97f && uv.X > 0.32f && uv.X < 0.60f)
                            {
                                if (_mediaManager != null)
                                {
                                    float volProgress = (uv.X - 0.32f) / 0.28f;
                                    _mediaManager.LiveStreamVolume = Math.Clamp(volProgress * 3f, 0f, 3f);
                                    _config.LivestreamVolume = _mediaManager.LiveStreamVolume;
                                }
                            }
                            
                            // Seek Bar Drag (0.32 - 0.60)
                            if (uv.Y > 0.88f && uv.Y < 0.95f && uv.X >= 0.32f && uv.X <= 0.60f)
                            {
                                if (activeStream != null)
                                {
                                    float seekProgress = (uv.X - 0.32f) / 0.28f;
                                    activeStream.Time = (long)(seekProgress * activeStream.Length);
                                }
                            }
                        }

                        if (isMouseReleased)
                        {
                            _config.Save(); // Save volume if it changed
                        }

                        if (isMouseReleased && _clickStartedOnTv)
                        {
                            _pluginLog.Information($"Media Control Clicked at UV: {uv.X:F2}, {uv.Y:F2}");

                            if (_isQueueMenuOpen)
                            {
                                var action = _queueMenuTextureManager?.GetActionAtUV(uv.X, uv.Y);
                                if (action == "close") {
                                    _isQueueMenuOpen = false;
                                } else if (action == "clear") {
                                    _mediaQueue.Clear();
            // Deprecated: v2 heartbeat handles pushing state
                                    _queueMenuTextureManager?.UpdateQueue(_mediaQueue, _currentMediaTitle ?? "Nothing Playing");
                                } else if (action == "paste") {
                                    Thread thread = new Thread(() =>
                                    {
                                        string clip = "";
                                        for (int i = 0; i < 5; i++)
                                        {
                                            try { clip = System.Windows.Forms.Clipboard.GetText(); if (!string.IsNullOrEmpty(clip)) break; } catch { }
                                            Thread.Sleep(50);
                                        }
                                        if (!string.IsNullOrEmpty(clip))
                                        {
                                            EnqueueFrameworkAction(() =>
                                            {
                                                _mediaQueue.Enqueue(clip);
                                                PrintChat($"[媒体播放器] 已添加到队列 ({_mediaQueue.Count}): {clip}", ChatSeverity.Info);
                                                if (_mediaManager?.GetActiveStream() == null || _mediaManager.GetActiveStream().PlaybackState == NAudio.Wave.PlaybackState.Stopped)
                                                {
                                                    if (_playerObject != null) PlayRouted(_mediaQueue.Dequeue(), CurrentAudioSource);
                                                }
                                                // Deprecated: v2 heartbeat handles pushing state
                                                _queueMenuTextureManager?.UpdateQueue(_mediaQueue, _currentMediaTitle ?? "Nothing Playing");
                                            });
                                        }
                                        else
                                        {
                                            EnqueueFrameworkAction(() => PrintChatError("[媒体播放器] 无法读取剪贴板或剪贴板为空"));
                                        }
                                    });
                                    thread.SetApartmentState(ApartmentState.STA);
                                    thread.Start();
                                } else if (action != null && action.StartsWith("remove:")) {
                                    if (int.TryParse(action.Split(':')[1], out int idx)) {
                                        var list = _mediaQueue.ToList();
                                        if (idx >= 0 && idx < list.Count) {
                                            list.RemoveAt(idx);
                                            _mediaQueue = new Queue<string>(list);
            // Deprecated: v2 heartbeat handles pushing state
                                            _queueMenuTextureManager?.UpdateQueue(_mediaQueue, _currentMediaTitle ?? "Nothing Playing");
                                        }
                                    }
                                }
                                return; // Handled
                            }

                            // Handle Transport Controls (Y between 0.85 and 0.95)
                            if (uv.Y > 0.85f && uv.Y < 0.95f)
                            {
                                // Prev (0.02 - 0.06)
                                if (uv.X >= 0.02f && uv.X <= 0.06f)
                                {
                                    PlayPrevious();
                                }
                                // Rewind (0.07 - 0.11)
                                else if (uv.X >= 0.07f && uv.X <= 0.11f)
                                {
                                    SeekRelative(-_config.SeekIncrementSeconds);
                                }
                                // Play/Pause (0.12 - 0.16)
                                else if (uv.X >= 0.12f && uv.X <= 0.16f)
                                {
                                    TogglePlayPause();
                                }
                                // Fast Forward (0.17 - 0.21)
                                else if (uv.X >= 0.17f && uv.X <= 0.21f)
                                {
                                    SeekRelative(_config.SeekIncrementSeconds);
                                }
                                // Next (0.22 - 0.26)
                                else if (uv.X >= 0.22f && uv.X <= 0.26f)
                                {
                                    PlayNext();
                                }
                                // Stop (0.27 - 0.31)
                                else if (uv.X >= 0.27f && uv.X <= 0.31f)
                                {
                                    Stop();
                                }

                                // Loop (0.62 - 0.66)
                                else if (uv.X >= 0.62f && uv.X <= 0.66f)
                                {
                                    _config.LoopEnabled = !_config.LoopEnabled;
                                    _config.Save();
                                    PrintChat($"[媒体播放器] 循环播放: {(_config.LoopEnabled ? "开" : "关")}");
                                }
                                // Shuffle (0.68 - 0.72)
                                else if (uv.X >= 0.68f && uv.X <= 0.72f)
                                {
                                    _config.ShuffleEnabled = !_config.ShuffleEnabled;
                                    _config.Save();
                                    PrintChat($"[媒体播放器] 随机播放: {(_config.ShuffleEnabled ? "开" : "关")}");
                                }
                                // Refresh (0.74 - 0.78)
                                else if (uv.X >= 0.74f && uv.X <= 0.78f)
                                {
                                    RequestRefreshCurrentMedia();
                                }
                                // Lock (0.80 - 0.84)
                                else if (uv.X >= 0.80f && uv.X <= 0.84f)
                                {
                                    if (CurrentTvPlacement != null && CurrentTvPlacement.OwnerId == _config.OwnerId)
                                    {
                                        CurrentTvPlacement.IsLocked = !CurrentTvPlacement.IsLocked;
                                        if (!string.IsNullOrEmpty(LocationKey))
                                        {
                                            _screenSettingsWindow.RegisterTvAsync(LocationKey);
                                            PrintChat($"[媒体播放器] 电视已{(CurrentTvPlacement.IsLocked ? "锁定" : "解锁")}");
                                        }
                                    }
                                    else if (CurrentTvPlacement == null)
                                    {
                                        CurrentTvPlacement = new TvPlacement { OwnerId = _config.OwnerId, IsLocked = false };
                                        if (!string.IsNullOrEmpty(LocationKey))
                                        {
                                            _screenSettingsWindow.RegisterTvAsync(LocationKey);
                                        }
                                        PrintChat("[媒体播放器] 电视已注册并解锁");
                                    }
                                    else { PrintChat("[媒体播放器] 你不是这台电视的所有者"); }
                                }
                                // Paste (0.85 - 0.89)
                                else if (uv.X >= 0.85f && uv.X <= 0.89f)
                                {
                                    if (_playerObject != null)
                                    {
                                        PrintChat("[媒体播放器] 正在读取剪贴板...", ChatSeverity.Info);
                                        Thread thread = new Thread(() =>
                                        {
                                            string clip = "";
                                            for (int i = 0; i < 5; i++)
                                            {
                                                try { clip = System.Windows.Forms.Clipboard.GetText(); if (!string.IsNullOrEmpty(clip)) break; } catch { }
                                                Thread.Sleep(50);
                                            }
                                            if (!string.IsNullOrEmpty(clip))
                                            {
                                                EnqueueFrameworkAction(() =>
                                                {
                                                    PrintChat("[媒体播放器] 正在从剪贴板加载链接...", ChatSeverity.Info);
                                                    PlayRouted(clip, CurrentAudioSource);
                                                });
                                            }
                                            else
                                            {
                                                EnqueueFrameworkAction(() => PrintChatError("[媒体播放器] 无法读取剪贴板或剪贴板为空"));
                                            }
                                        });
                                        thread.SetApartmentState(ApartmentState.STA);
                                        thread.Start();
                                    }
                                }
                                // Queue (0.90 - 0.94)
                                else if (uv.X >= 0.90f && uv.X <= 0.94f)
                                {
                                    EnqueueFrameworkAction(() =>
                                    {
                                        _isQueueMenuOpen = !_isQueueMenuOpen;
                                        if (_isQueueMenuOpen)
                                        {
                                            _queueMenuTextureManager?.UpdateQueue(_mediaQueue, _currentMediaTitle ?? "Nothing Playing");
                                        }
                                    });
                                }
                                // Kill/Stop (0.95 - 0.99)
                                else if (uv.X >= 0.95f && uv.X <= 0.99f)
                                {
                                    Stop();
                                }
                            }
                            // History Top Left (0.02 - 0.08, 0.04 - 0.12)
                            else if (uv.Y >= 0.04f && uv.Y <= 0.12f && uv.X >= 0.02f && uv.X <= 0.08f)
                            {
                                EnqueueFrameworkAction(() =>
                                {
                                    _isHistoryMenuOpen = !_isHistoryMenuOpen;
                                    if (_isHistoryMenuOpen)
                                    {
                                        _historyMenuTextureManager?.UpdateHistory(_config.WatchHistory);
                                    }
                                });
                            }
                            // DMCA Top Right (0.92 - 0.98, 0.04 - 0.12)
                            else if (uv.Y >= 0.04f && uv.Y <= 0.12f && uv.X >= 0.92f && uv.X <= 0.98f)
                            {
                                string url = _lastStreamURL;
                                if (!string.IsNullOrEmpty(url)) {
                                    string domain = "the site administrator";
                                    try {
                                        Uri uri = new Uri(url);
                                        domain = uri.Host;
                                        PrintChat($"[媒体播放器] 正在打开 DMCA 信息...", ChatSeverity.Info);
                                    } catch { }
                                    
                                    string dmcaText = $"Content URL: {url}\n\nPlease contact {domain} to report this content.";
                                    ImGui.SetClipboardText(dmcaText);
                                    PrintChat("[媒体播放器] DMCA 联系信息和链接已复制到剪贴板", ChatSeverity.Info);
                                } else {
                                    PrintChatError("[媒体播放器] 没有可复制的媒体链接");
                                }
                            }
                            else if (_isHistoryMenuOpen)
                            {
                                // We clicked inside the TV bounds while the history menu was open.
                                var clickedEntry = _historyMenuTextureManager?.GetItemAtUV(uv.X, uv.Y);
                                if (clickedEntry != null)
                                {
                                    // Clicked a history item! Close menu and play it.
                                    _isHistoryMenuOpen = false;
                                    
                                    // Same routing logic as MediaBrowserWindow
                                    if (YtDlpManager.IsUrlSupported(clickedEntry.Url) && _ytDlpManager.IsAvailable())
                                    {
                                        PlayRouted(clickedEntry.Url, CurrentAudioSource, (int)clickedEntry.TimecodeMs);
                                    }
                                    else
                                    {
                                        TuneIntoStream(clickedEntry.Url, CurrentAudioSource, (int)clickedEntry.TimecodeMs);
                                    }
                                }
                                else
                                {
                                    // Clicked outside any items, close the menu
                                    _isHistoryMenuOpen = false;
                                }
                            }
                        }
                        }
                    }

                    // Update dynamic 3D text texture
                    if (_titleTextureManager != null)
                    {
                        _titleTextureManager.UpdateText(_currentMediaTitle, _currentStreamer);
                    }

                    bool isLocked = CurrentTvPlacement?.IsLocked ?? true;
                    float lockState = isLocked ? 1.0f : 0.0f;
                    if (_currentStreamer == "Emulation") {
                        lockState = -1.0f;
                    }
                    float volume = _mediaManager != null ? _mediaManager.LiveStreamVolume : 1f;
                    
                    IntPtr srvPtr = _isQueueMenuOpen 
                        ? (_queueMenuTextureManager?.TextureHandle ?? IntPtr.Zero) 
                        : _isHistoryMenuOpen 
                        ? (_historyMenuTextureManager?.TextureHandle ?? IntPtr.Zero) 
                        : (_titleTextureManager?.TextureHandle ?? IntPtr.Zero);

                    if (_currentStreamer == "Emulation") {
                        srvPtr = IntPtr.Zero;
                    }

                    _worldRenderer.EnableGlow = _config.DepthOcclusionEnabled && _config.TvGlowEnabled;
                    
                    // useDifferenceFallback is already calculated above when checking UI occlusion,
                    // but we re-calculate it here in case the logic above was skipped.
                    // (Actually we calculated it at the top of OnDraw, so we don't need to do it again here.)
                    
                    var mainViewport = ImGui.GetMainViewport();
                    
                    _worldRenderer.Render(videoSrv, videoWidth, videoHeight, videoTrueWidth, videoTrueHeight, _depthCapture,
                        _prevCameraPos ?? cameraPos, _prevCameraForward ?? cameraForward, _prevCameraRight ?? cameraRight, _prevCameraUp ?? cameraUp,
                        fovY, aspectRatio, _uiCapture, nearPlane, farPlane, hoverUV, progress, playbackState, lockState, volume, srvPtr, _config.LoopEnabled, _config.ShuffleEnabled, timeSeconds, showScreensaver, useDifferenceFallback: useDifferenceFallback,
                        viewProjMatrix: _prevViewProjMatrix ?? viewProjMatrix, viewportPos: mainViewport.Pos, viewportSize: mainViewport.Size, uiBlendThreshold: _config.UIBlendThreshold);
                        
                    _prevCameraPos = cameraPos;
                    _prevCameraForward = cameraForward;
                    _prevCameraRight = cameraRight;
                    _prevCameraUp = cameraUp;
                    _prevViewProjMatrix = viewProjMatrix;
                }
                
                // Draw floating Emulation Controller UI
                if (_currentStreamer == "Emulation" && _worldRenderer.Transform != null) {
                    var (tl, tr, br, bl) = _worldRenderer.Transform.Corners;
                    if (_gameGui.WorldToScreen(tr, out var sTR)) {
                        ImGui.SetNextWindowPos(new System.Numerics.Vector2(sTR.X + 20, sTR.Y));
                        ImGui.SetNextWindowBgAlpha(0.8f);
                        if (ImGui.Begin("Emulation Controllers", ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoMove)) {
                            ImGui.Text("Controller Slot");
                            ImGui.Separator();
                            for (byte i = 0; i < 4; i++) {
                                if (ImGui.Selectable($"Player {i+1}", _controllerService?.PlayerSlot == i)) {
                                    if (_controllerService != null) _controllerService.PlayerSlot = i;
                                }
                            }
                            ImGui.End();
                        }
                    }
                }
            }

            DrawOutdoorGridDebug();
        }

        private unsafe void DrawOutdoorGridDebug()
        {
            if (!_config.ShowOutdoorGridDebug) return;

            var playerPos = GetLocalPlayer()?.Position;
            if (playerPos == null) return;

            var housingMgr = FFXIVClientStructs.FFXIV.Client.Game.HousingManager.Instance();
            if (housingMgr != null && housingMgr->IsInside()) return;

            var drawList = ImGui.GetBackgroundDrawList();
            uint color = ImGui.ColorConvertFloat4ToU32(new System.Numerics.Vector4(0, 1, 0, 0.5f));
            float thickness = 2.0f;

            int currentGridX = (int)Math.Floor(playerPos.Value.X / 50.0f);
            int currentGridZ = (int)Math.Floor(playerPos.Value.Z / 50.0f);

            void DrawLineSegmented(System.Numerics.Vector3 pStart, System.Numerics.Vector3 pEnd)
            {
                int segments = 10;
                for (int i = 0; i < segments; i++)
                {
                    float t1 = i / (float)segments;
                    float t2 = (i + 1) / (float)segments;
                    var pA = System.Numerics.Vector3.Lerp(pStart, pEnd, t1);
                    var pB = System.Numerics.Vector3.Lerp(pStart, pEnd, t2);
                    if (_gameGui.WorldToScreen(pA, out var spA) && _gameGui.WorldToScreen(pB, out var spB))
                    {
                        drawList.AddLine(spA, spB, color, thickness);
                    }
                }
            }

            for (int dx = -2; dx <= 2; dx++)
            {
                for (int dz = -2; dz <= 2; dz++)
                {
                    float startX = (currentGridX + dx) * 50.0f;
                    float startZ = (currentGridZ + dz) * 50.0f;
                    float y = playerPos.Value.Y;

                    var p1 = new System.Numerics.Vector3(startX, y, startZ);
                    var p2 = new System.Numerics.Vector3(startX + 50f, y, startZ);
                    var p3 = new System.Numerics.Vector3(startX + 50f, y, startZ + 50f);
                    var p4 = new System.Numerics.Vector3(startX, y, startZ + 50f);

                    DrawLineSegmented(p1, p2);
                    DrawLineSegmented(p2, p3);
                    DrawLineSegmented(p3, p4);
                    DrawLineSegmented(p4, p1);

                    var center = new System.Numerics.Vector3(startX + 25f, y, startZ + 25f);
                    if (_gameGui.WorldToScreen(center, out var sCenter))
                    {
                        string text = $"Grid {currentGridX + dx}, {currentGridZ + dz}";
                        var textSize = ImGui.CalcTextSize(text);
                        sCenter.X -= textSize.X / 2;
                        drawList.AddText(sCenter, color, text);
                    }
                }
            }
        }

        private System.Numerics.Matrix4x4? _lastStabilizedVP;

        /// <summary>
        /// Computes the game's combined View * Projection matrix from the active camera.
        /// Reads both matrices directly from FFXIV to guarantee perfect sync.
        /// </summary>
        private unsafe System.Numerics.Matrix4x4? GetViewProjectionMatrix()
        {
            if (_camera == null) return null;

            try
            {
                var sceneCamera = _camera->CameraBase.SceneCamera;

                var rawView = sceneCamera.RenderCamera != null ? sceneCamera.RenderCamera->ViewMatrix : sceneCamera.ViewMatrix;
                var view = System.Runtime.CompilerServices.Unsafe.As<
                  FFXIVClientStructs.FFXIV.Common.Math.Matrix4x4,
                  System.Numerics.Matrix4x4>(ref rawView);

                if (sceneCamera.RenderCamera == null) return null;

                var rawProj = sceneCamera.RenderCamera->ProjectionMatrix;
                var proj = System.Runtime.CompilerServices.Unsafe.As<
                  FFXIVClientStructs.FFXIV.Common.Math.Matrix4x4,
                  System.Numerics.Matrix4x4>(ref rawProj);

                var vp = System.Numerics.Matrix4x4.Multiply(view, proj);

                if (_lastStabilizedVP.HasValue)
                {
                    float diff = 0;
                    diff += Math.Abs(vp.M11 - _lastStabilizedVP.Value.M11);
                    diff += Math.Abs(vp.M12 - _lastStabilizedVP.Value.M12);
                    diff += Math.Abs(vp.M13 - _lastStabilizedVP.Value.M13);
                    diff += Math.Abs(vp.M21 - _lastStabilizedVP.Value.M21);
                    diff += Math.Abs(vp.M22 - _lastStabilizedVP.Value.M22);
                    diff += Math.Abs(vp.M23 - _lastStabilizedVP.Value.M23);
                    diff += Math.Abs(vp.M31 - _lastStabilizedVP.Value.M31);
                    diff += Math.Abs(vp.M32 - _lastStabilizedVP.Value.M32);
                    diff += Math.Abs(vp.M33 - _lastStabilizedVP.Value.M33);
                    diff += Math.Abs(vp.M41 - _lastStabilizedVP.Value.M41);
                    diff += Math.Abs(vp.M42 - _lastStabilizedVP.Value.M42);
                    diff += Math.Abs(vp.M43 - _lastStabilizedVP.Value.M43);

                    // Stabilize the combined ViewProjection matrix to filter out both
                    // camera float drift AND TAA/DLSS/FSR projection sub-pixel jitter.
                    if (diff < 0.002f)
                    {
                        vp = _lastStabilizedVP.Value;
                    }
                }
                _lastStabilizedVP = vp;

                return vp;
            }
            catch
            {
                return null;
            }
        }

        private void OnOpenConfig()
        {
            _settingsWindow.Toggle();
        }

        public void ToggleConfigUi()
        {
            _settingsWindow.Toggle();
        }

        public void HandleOutdoorSettingToggled()
        {
            var key = GetLocationKey();
            if (string.IsNullOrEmpty(key)) return;

            if (key.StartsWith("zone_"))
            {
                if (!_config.EnableOutdoorPublicScreens)
                {
                    _worldRenderer.Transform.Enabled = false;
                    _mediaManager?.StopStream();
                    _lastStreamURL = "";
                    _currentMediaOwnerId = "";
                    _isLocalDj = false;
                    _lastStreamObject = null;
                }
                else
                {
                    RestoreScreenForCurrentLocation();
                    RestoreMediaForCurrentLocation();
                    _ = FetchServerDataForCurrentLocationAsync();
                }
            }
        }

    }
}
