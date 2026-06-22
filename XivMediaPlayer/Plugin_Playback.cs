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

        /// <summary>
        /// Seeks the current stream forward or backward by the given number of seconds.
        /// </summary>
        public void SeekRelative(int seconds)
        {
            var activeStream = _mediaManager?.GetActiveStream();
            if (activeStream == null || activeStream.Length <= 0) return;

            long newTime = activeStream.Time + (seconds * 1000L);
            newTime = Math.Clamp(newTime, 0, activeStream.Length);
            activeStream.Time = newTime;

            // v2 heartbeat carries new timecode
        }

        /// <summary>
        /// Completely stops playback, clears the queue, and clears the saved room resume state.
        /// </summary>
        public void Stop()
        {
            PrintChat("[媒体播放器] 正在停止播放并清除队列...");
            _mediaManager?.StopStream();
            _mediaQueue.Clear();
            ResetStreamValues(true);

            // Clear the saved room state so it doesn't auto-resume next time we enter
            var key = CurrentTvPlacement?.LocationKey ?? GetLocationKey();
            if (!string.IsNullOrEmpty(key) && _config.RoomMediaStates.ContainsKey(key))
            {
                _config.RoomMediaStates.Remove(key);
                _config.Save();
            }
        }

        /// <summary>
        /// Toggles play/pause on the current stream.
        /// </summary>
        public void TogglePlayPause()
        {
            var activeStream = _mediaManager?.GetActiveStream();
            if (activeStream == null)
            {
                return;
            }

            if (activeStream.PlaybackState == NAudio.Wave.PlaybackState.Playing)
            {
                activeStream.Pause();
                _isIntentionallyPaused = true;
            }
            else
            {
                if (activeStream.PlaybackState == NAudio.Wave.PlaybackState.Stopped && !string.IsNullOrEmpty(_lastStreamURL))
                {
                    _mediaManager?.StopStream();
                    if (YtDlpManager.IsUrlSupported(_lastStreamURL) && _ytDlpManager.IsAvailable())
                    {
                        PlayRouted(_lastStreamURL, CurrentAudioSource, 0);
                    }
                    else
                    {
                        TuneIntoStream(_lastStreamURL, CurrentAudioSource, 0);
                    }
                    _isIntentionallyPaused = false;
                    return;
                }

                activeStream.Resume();
                _isIntentionallyPaused = false;
            }

        }

        /// <summary>
        /// Returns true if the stream is currently intentionally paused by the user.
        /// </summary>
        public bool IsIntentionallyPaused => _isIntentionallyPaused;

        /// <summary>
        /// Plays the next track from the media queue.
        /// If shuffle is enabled, picks a random track from the queue.
        /// </summary>
        public void PlayNext()
        {
            if (_mediaQueue.Count == 0 || _playerObject == null) return;

            // Record history
            if (!string.IsNullOrEmpty(_lastStreamURL))
            {
                _mediaHistory.Push(_lastStreamURL);
            }

            string nextUrl;
            if (_config.ShuffleEnabled && _mediaQueue.Count > 1)
            {
                // Shuffle queue logic
                var list = _mediaQueue.ToList();
                int idx = _shuffleRandom.Next(list.Count);
                nextUrl = list[idx];
                list.RemoveAt(idx);
                _mediaQueue = new Queue<string>(list);
            }
            else
            {
                nextUrl = _mediaQueue.Dequeue();
            }

            PrintChat($"[媒体播放器] 下一首: {nextUrl}");
            PlayRouted(nextUrl, CurrentAudioSource);
        }

        /// <summary>
        /// Plays the previous track from the media history stack.
        /// Pushes the current track back onto the front of the queue.
        /// </summary>
        public void PlayPrevious()
        {
            if (_mediaHistory.Count == 0 || _playerObject == null) return;

            // Requeue current media
            if (!string.IsNullOrEmpty(_lastStreamURL))
            {
                var list = _mediaQueue.ToList();
                list.Insert(0, _lastStreamURL);
                _mediaQueue = new Queue<string>(list);
            }

            string prevUrl = _mediaHistory.Pop();
            PrintChat($"[媒体播放器] 上一首: {prevUrl}");
            PlayRouted(prevUrl, CurrentAudioSource);
        }

        /// <summary>
        /// Toggles mute on/off. Stores the pre-mute volume and restores it when unmuting.
        /// </summary>
        public void ToggleMute()
        {
            if (_mediaManager == null) return;

            if (_isMuted)
            {
                _mediaManager.LiveStreamVolume = _preMuteVolume;
                _config.LivestreamVolume = _preMuteVolume;
                _isMuted = false;
            }
            else
            {
                _preMuteVolume = _mediaManager.LiveStreamVolume;
                _mediaManager.LiveStreamVolume = 0;
                _isMuted = true;
            }
        }

        /// <summary>
        /// Whether the media player is currently muted.
        /// </summary>
        public bool IsMuted => _isMuted;

        /// <summary>
        /// Re-resolves and replays the current media URL at the current timecode.
        /// Useful when the 2D/3D screen fails to load.
        /// </summary>
        public void RefreshCurrentMedia()
        {
            RequestRefreshCurrentMedia();
        }

        public void RequestRefreshCurrentMedia()
        {
            if (_refreshQueued) return;
            _refreshQueued = true;
            EnqueueFrameworkAction(() =>
            {
                _refreshQueued = false;
                DoRefreshCurrentMedia();
            });
        }

        internal void DoRefreshCurrentMedia()
        {
            if (string.IsNullOrEmpty(_lastStreamURL) || _playerObject == null) return;

            var activeStream = _mediaManager?.GetActiveStream();
            int currentTimeMs = activeStream != null ? (int)activeStream.Time : 0;

            PrintChat("[媒体播放器] 正在刷新媒体...");
            _mediaManager?.StopStream();
            
            if (YtDlpManager.IsUrlSupported(_lastStreamURL) && _ytDlpManager.IsAvailable())
            {
                // Follower mode: don't try to claim DJ on error retry
                PlayRouted(_lastStreamURL, CurrentAudioSource, currentTimeMs, !_isLocalDj);
            }
            else
            {
                TuneIntoStream(_lastStreamURL, CurrentAudioSource, currentTimeMs);
            }
        }

        /// <summary>
        /// Kills the media manager and restarts it, then resumes the current media.
        /// Recovers from locked-up VLC states.
        /// </summary>
        public void KillAndRestart()
        {
            RequestKillAndRestart();
        }

        public void RequestKillAndRestart()
        {
            UpdateWatchHistory();
            _killRestartQueued = true;
            EnqueueFrameworkAction(() =>
            {
                _killRestartQueued = false;
                DoKillAndRestart();
            });
        }

        private void DoKillAndRestart()
        {
            PrintChat("[媒体播放器] 正在重置媒体管线...");

            // Save what we were playing
            string savedUrl = _lastStreamURL;
            var activeStream = _mediaManager?.GetActiveStream();
            int savedTimeMs = activeStream != null ? (int)activeStream.Time : 0;

            // Tear down
            _mediaManager?.Dispose();
            _mediaManager = null;
            _cefBrowserHandle?.Dispose();
            _cefBrowserHandle = null;
            _videoWindow.MediaManager = null;

            // Reinitialize
            try
            {
                InitializeMediaManager();
            }
            catch (Exception e)
            {
                _pluginLog.Warning(e, "[Media Player] Failed to reinitialize MediaManager during kill.");
                PrintChatError("[媒体播放器] 无法重启媒体管线");
                return;
            }

            // Resume playback
            if (!string.IsNullOrEmpty(savedUrl) && _playerObject != null)
            {
                PrintChat("[媒体播放器] 正在恢复播放...");
                PlayRouted(savedUrl, CurrentAudioSource, savedTimeMs);
            }
            else
            {
                PrintChat("[媒体播放器] 媒体管线已重启");
            }
        }

    }
}
