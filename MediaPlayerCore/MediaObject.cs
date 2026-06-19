using LibVLCSharp.Shared;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using NAudio.CoreAudioApi;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Advanced;
using SixLabors.ImageSharp.PixelFormats;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Numerics;
using System.Runtime.InteropServices;

namespace MediaPlayerCore {
  public class MediaObject : IDisposable {
    private IMediaGameObject _playerObject;
    private IMediaGameObject _camera;
    private SoundType _soundType;
    private bool _spatialAllowed;
    private MediaPlayer _vlcPlayer;
    private MediaManager _parent;

    private MemoryMappedFile _vlcMappedFile;
    private MemoryMappedViewAccessor _vlcMappedViewAccessor;
    private IntPtr _vlcBuffer = IntPtr.Zero;

    private IWavePlayer _waveOut;
    private WaveFormat _waveFormat;
    private BufferedWaveProvider _bufferedWaveProvider;
    private PanningSampleProvider _panningProvider;
    private VolumeSampleProvider _volumeProvider;
    private bool _isPlayingAudio = false;

    private LibVLC? _ownedLibVLC;
    private EventHandler<LogEventArgs>? _vlcLogHandler;
    private byte[] _audioBuffer = Array.Empty<byte>();

    public float Pan {
      get => _panningProvider?.Pan ?? 0;
      set {
        if (_panningProvider != null) {
          _panningProvider.Pan = Math.Clamp(value, -1f, 1f);
        }
      }
    }

    public event EventHandler<MediaError> OnErrorReceived;
    public event EventHandler<string> PlaybackStopped;
    public event EventHandler<string> PlaybackFinished;

    private string _soundPath;

    private uint _width = 1280;
    private uint _height = 720;

    private const uint _bytePerPixel = 4;

    private uint _pitch;

    private uint _lines;
    private float volumePercentage = 1;
    private float _baseVolume = 1;
    private bool _vlcWasAbleToStart;
    private bool _disposed;
    private bool _isDisposing;
    private readonly object _disposeLock = new object();

    private bool _audioOnly;

    public MediaObject(MediaManager parent, IMediaGameObject playerObject, IMediaGameObject camera,
      SoundType soundType, string soundPath, bool spatialAllowed, bool audioOnly = false) {
      _playerObject = playerObject;
      _audioOnly = audioOnly;
      _soundPath = soundPath;
      _camera = camera;
      _parent = parent;
      this._soundType = soundType;
      _spatialAllowed = spatialAllowed;
      _pitch = Align(_width * _bytePerPixel);
      _lines = Align(_height);
      _vlcMappedFile = MemoryMappedFile.CreateNew(null, _pitch * _lines);
      _vlcMappedViewAccessor = _vlcMappedFile.CreateViewAccessor();
      _vlcBuffer = _vlcMappedViewAccessor.SafeMemoryMappedViewHandle.DangerousGetHandle();
      _parent.OnCleanupTime += _parent_OnCleanupTime;
    }

    public bool IsDisposed => _disposed;

    private void _parent_OnCleanupTime(object? sender, EventArgs e) {
      Invalidated = true;
    }

    private static uint Align(uint size) {
      if (size % 32 == 0) {
        return size;
      }
      return ((size / 32) + 1) * 32;
    }

    public IMediaGameObject CharacterObject { get => _playerObject; set => _playerObject = value; }
    public float Volume {
      get {
        try {
          if (_vlcPlayer != null) {
            return _vlcPlayer.Volume;
          }
        } catch { }
        return 0;
      }
      set {
        if (_vlcPlayer != null) {
          try {
            float clampedValue = Math.Max(0f, value);
            float scale = clampedValue;
            if (clampedValue <= 1.0f) {
                scale = (float)Math.Pow(clampedValue, 3);
            } else {
                scale = 1.0f + (clampedValue - 1.0f);
            }

            int newValue = (int)(scale * 100f);
            if (newValue != _vlcPlayer.Volume) {
              _baseVolume = newValue;
              _vlcPlayer.Volume = (int)((float)newValue * volumePercentage);
              if (_volumeProvider != null) {
                  _volumeProvider.Volume = ((float)newValue / 100f * volumePercentage) * 2.0f;
              }
            }
          } catch (Exception e) { OnErrorReceived?.Invoke(this, new MediaError() { Exception = e }); }
        }
      }
    }
    public PlaybackState PlaybackState {
      get {
        if (_vlcPlayer != null) {
          try {
            if (_vlcPlayer.State == LibVLCSharp.Shared.VLCState.Playing) return PlaybackState.Playing;
            if (_vlcPlayer.State == LibVLCSharp.Shared.VLCState.Paused) return PlaybackState.Paused;
            return PlaybackState.Stopped;
          } catch {
            return PlaybackState.Stopped;
          }
        } else {
          return PlaybackState.Stopped;
        }
      }
    }
    
    public LibVLCSharp.Shared.VLCState VlcState => _vlcPlayer?.State ?? LibVLCSharp.Shared.VLCState.Stopped;
    
    public long Time {
      get => _vlcPlayer?.Time ?? 0;
      set {
        if (_vlcPlayer != null) {
          try {
            if (!_vlcPlayer.IsSeekable && _vlcPlayer.State == LibVLCSharp.Shared.VLCState.Playing) {
                return;
            }
          } catch {}

          if (_vlcPlayer.State == LibVLCSharp.Shared.VLCState.Ended || _vlcPlayer.State == LibVLCSharp.Shared.VLCState.Stopped) {
            ChangeVideoStream(_soundPath, _width, (int)value);
          } else {
            _vlcPlayer.Time = value;
            _bufferedWaveProvider?.ClearBuffer();
          }
        }
      }
    }

    public long Length => _vlcPlayer?.Length ?? 0;

    public void Pause() {
      _vlcPlayer?.SetPause(true);
    }
    public void Resume() {
      _vlcPlayer?.SetPause(false);
    }
    public SoundType SoundType { get => _soundType; set => _soundType = value; }
    public string SoundPath { get => _soundPath; set => _soundPath = value; }
    public IMediaGameObject Camera { get => _camera; set => _camera = value; }
    public bool Invalidated { get; internal set; }
    public bool SpatialAllowed { get => _spatialAllowed; set => _spatialAllowed = value; }
    public MediaManager Parent { get => _parent; set => _parent = value; }

    public void Stop() {
      PlaybackStopped?.Invoke(this, "OK");
      if (_vlcPlayer != null) {
        try {
          _vlcPlayer?.Stop();
        } catch (Exception e) { OnErrorReceived?.Invoke(this, new MediaError() { Exception = e }); }
      }
      Invalidated = true;
    }

    public void Seek(long timeMs) {
      if (_vlcPlayer == null || _disposed) return;
      try {
        if (_vlcPlayer.State == VLCState.Ended || _vlcPlayer.State == VLCState.Stopped)
          ChangeVideoStream(_soundPath, _width, (int)timeMs);
        else {
          _vlcPlayer.Time = timeMs;
          _bufferedWaveProvider?.ClearBuffer();
        }
      } catch (Exception e) { OnErrorReceived?.Invoke(this, new MediaError { Exception = e }); }
    }
    public void Play(string mediaPath, float volume, int startTimeMs, Dictionary<string, string>? httpHeaders) {
      Task.Run(async delegate {
        try {
          if (string.IsNullOrEmpty(mediaPath)) {
            Debug.WriteLine("[MediaObject] Play SKIPPED: mediaPath is empty");
            return;
          }
          if (PlaybackState != PlaybackState.Stopped) {
            Debug.WriteLine($"[MediaObject] Play SKIPPED: state={PlaybackState}, not Stopped");
            return;
          }
          try {
            var shortPath = mediaPath.Length > 120 ? mediaPath[..120] + "..." : mediaPath;
            Debug.WriteLine($"[MediaObject] Play START: path={shortPath}, vol={volume}, startMs={startTimeMs}");
            var sw = System.Diagnostics.Stopwatch.StartNew();

            lock (_parent.FrameLock) {
              _parent.LastFrame = Array.Empty<byte>();
              _parent.LastFrameWidth = 0;
              _parent.LastFrameHeight = 0;
              _parent.LastFrameCount++;
            }

            _ownedLibVLC = _parent.CreateLibVLC();
            var libVLC = _ownedLibVLC;
            Debug.WriteLine($"[MediaObject] Created LibVLC: {libVLC.GetHashCode()}, took {sw.ElapsedMilliseconds}ms");
              string userAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

              _vlcLogHandler = (s, e) => {
                if (e.Level >= LogLevel.Error) {
                  Debug.WriteLine($"[VLC-Error] {e.Module}: {e.Message}");
                  OnErrorReceived?.Invoke(this, new MediaError() { Exception = new Exception($"VLC [{e.Module}]: {e.Message}") });
                } else if (e.Level >= LogLevel.Warning) {
                  Debug.WriteLine($"[VLC-Warn] {e.Module}: {e.Message}");
                }
              };
              libVLC.Log += _vlcLogHandler;

              var media = new Media(libVLC, mediaPath, mediaPath.StartsWith("http") || mediaPath.StartsWith("rtmp") || mediaPath.StartsWith("rtsp")
                ? FromType.FromLocation : FromType.FromPath);
              
              if (_audioOnly) {
                  media.AddOption(":no-video");
              }

              if (mediaPath.StartsWith("rtsp")) {
                  if (_audioOnly) {
                      media.AddOption(":network-caching=300");
                  } else {
                      media.AddOption(":network-caching=30");
                      media.AddOption(":clock-jitter=0");
                      media.AddOption(":drop-late-frames");
                      media.AddOption(":skip-frames");
                  }
              } else {
                  media.AddOption(":network-caching=2000");
              }
              
              if (startTimeMs > 0) {
                  media.AddOption($":start-time={(startTimeMs / 1000.0).ToString(System.Globalization.CultureInfo.InvariantCulture)}");
              }

              if (httpHeaders != null && httpHeaders.TryGetValue("User-Agent", out string mediaUserAgent)) {
                  media.AddOption($":http-user-agent={mediaUserAgent}");
              } else {
                  media.AddOption($":http-user-agent={userAgent}");
              }

              if (httpHeaders != null && httpHeaders.TryGetValue("Referer", out string mediaReferer)) {
                  media.AddOption($":http-referrer={mediaReferer}");
              }
              if (httpHeaders != null && httpHeaders.TryGetValue("Cookie", out string mediaCookie)) {
                  media.AddOption($":http-cookie={mediaCookie}");
              }

              Debug.WriteLine("[MediaObject] Creating Media, about to Parse...");
              var parseSw = System.Diagnostics.Stopwatch.StartNew();
              var parseTask = media.Parse(mediaPath.StartsWith("http") || mediaPath.StartsWith("rtmp") || mediaPath.StartsWith("rtsp")
                ? MediaParseOptions.ParseNetwork : MediaParseOptions.ParseLocal);
              if (await Task.WhenAny(parseTask, Task.Delay(15000)) != parseTask)
              {
                  parseSw.Stop();
                  Debug.WriteLine("[MediaObject] Media.Parse TIMEOUT after 15s");
                  throw new TimeoutException("Media.Parse timed out after 15s");
              }
              await parseTask; // throw if the actual task faulted
              parseSw.Stop();
              Debug.WriteLine($"[MediaObject] Media parsed OK: duration={media.Duration}ms, tracks={media.Tracks?.Length ?? 0}, parsed in {parseSw.ElapsedMilliseconds}ms");

              lock (_disposeLock) {
                if (_disposed) {
                   media.Dispose();
                   return;
                }

                _vlcPlayer = new MediaPlayer(media);
                
                if (_spatialAllowed) {
                    _vlcPlayer.SetAudioFormat("s16l", 48000, 1);
                    _vlcPlayer.SetAudioCallbacks(PlayAudio, PauseAudio, ResumeAudio, FlushAudio, DrainAudio);
                    
                    _waveFormat = new WaveFormat(48000, 16, 1);
                    _bufferedWaveProvider = new BufferedWaveProvider(_waveFormat);
                    _bufferedWaveProvider.BufferDuration = TimeSpan.FromSeconds(10);
                    _bufferedWaveProvider.DiscardOnBufferOverflow = true;
                    
                    _panningProvider = new PanningSampleProvider(_bufferedWaveProvider.ToSampleProvider());
                    _panningProvider.Pan = 0;
                    
                    _volumeProvider = new VolumeSampleProvider(_panningProvider);
                    _volumeProvider.Volume = (_baseVolume / 100f) * 2.0f;
                    
                    _waveOut = new WasapiOut(AudioClientShareMode.Shared, 150);
                    _waveOut.Init(_volumeProvider);
                }

                _vlcPlayer.Stopped += delegate {
                  Debug.WriteLine("[VLC] State: Stopped");
                  lock (_parent.FrameLock) {
                    _parent.LastFrame = Array.Empty<byte>();
                    _parent.LastFrameWidth = 0;
                    _parent.LastFrameHeight = 0;
                    _parent.LastFrameCount++;
                  }
                };
                _vlcPlayer.Paused += delegate {
                  Debug.WriteLine("[VLC] State: Paused");
                };
                long exactSeekMs = startTimeMs;
                _vlcPlayer.Playing += (s, e) => {
                  Debug.WriteLine("[VLC] State: Playing");
                  if (exactSeekMs > 0) {
                      Task.Run(async () => {
                        await Task.Delay(2000);
                            if (_vlcPlayer != null) {
                                _vlcPlayer.Time = exactSeekMs;
                                _bufferedWaveProvider?.ClearBuffer();
                            }
                        exactSeekMs = 0;
                    });
                  }
                };
                _vlcPlayer.EndReached += delegate {
                  Debug.WriteLine("[VLC] State: EndReached");
                  PlaybackFinished?.Invoke(this, "OK");
                };
                _vlcPlayer.EncounteredError += (s, e) => {
                  Debug.WriteLine("[VLC] EncounteredError event fired!");
                  OnErrorReceived?.Invoke(this, new MediaError() { Exception = new Exception("VLC player encountered an error during playback.") });
                };
                if (!_audioOnly && (mediaPath.StartsWith("http") || mediaPath.StartsWith("rtmp") || mediaPath.StartsWith("rtsp") || mediaPath.EndsWith(".mp4") || mediaPath.EndsWith(".avi"))) {
                    _vlcPlayer.SetVideoFormatCallbacks(VideoFormatSetup, null);
                    _vlcPlayer.SetVideoCallbacks(Lock, null, Display);
                }
}

              _baseVolume = volume;
              Volume = volume;

              sw.Stop();
              Debug.WriteLine($"[MediaObject] About to call VLC Play(), setup took {sw.ElapsedMilliseconds}ms");
              bool playResult = _vlcPlayer.Play();
              Debug.WriteLine($"[MediaObject] VLC Play() returned: {playResult}, totalPlaySetup={sw.ElapsedMilliseconds}ms");
              _vlcWasAbleToStart = playResult;

              if (!playResult) {
                Debug.WriteLine("[MediaObject] VLC Play() FAILED — returning false!");
                OnErrorReceived?.Invoke(this, new MediaError() { Exception = new Exception("VLC Play() returned false — playback failed to start.") });
              }
            } catch (Exception e) {
              Debug.WriteLine($"[MediaObject] Play exception: {e}");
              OnErrorReceived?.Invoke(this, new MediaError() { Exception = e });
              PlaybackStopped?.Invoke(this, "OK");
            }
          } catch (Exception e) {
          Debug.WriteLine($"[MediaObject] Outer play exception: {e}");
          OnErrorReceived?.Invoke(this, new MediaError() { Exception = e });
          PlaybackStopped?.Invoke(this, "ERR");
        }
      });
    }
    public void ChangeVideoStream(string soundPath, float width, int startTimeMs = 0, Dictionary<string, string>? httpHeaders = null) {
      var shortPath = soundPath?.Length > 120 ? soundPath[..120] + "..." : soundPath;
      Debug.WriteLine($"[MediaObject] ChangeVideoStream START: path={shortPath}");
      Task.Run(async delegate {
        try {
          if (_vlcPlayer != null) {
            var libVLC = _ownedLibVLC ?? _parent.CreateLibVLC();
            _ownedLibVLC = libVLC;
            var media = new Media(libVLC, soundPath, soundPath.StartsWith("http") || soundPath.StartsWith("rtmp") || soundPath.StartsWith("rtsp")
                     ? FromType.FromLocation : FromType.FromPath);
            
            if (_audioOnly) {
                media.AddOption(":no-video");
            }

            if (soundPath.StartsWith("rtsp")) {
                media.AddOption(":network-caching=30");
                media.AddOption(":clock-jitter=0");
                media.AddOption(":drop-late-frames");
                media.AddOption(":skip-frames");
            } else {
                media.AddOption(":network-caching=2000");
            }
            
            if (startTimeMs > 0) {
                media.AddOption($":start-time={(startTimeMs / 1000.0).ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            }

            string userAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";
            if (httpHeaders != null && httpHeaders.TryGetValue("User-Agent", out string mediaUserAgent)) {
                media.AddOption($":http-user-agent={mediaUserAgent}");
            } else {
                media.AddOption($":http-user-agent={userAgent}");
            }

            if (httpHeaders != null && httpHeaders.TryGetValue("Referer", out string mediaReferer)) {
                media.AddOption($":http-referrer={mediaReferer}");
            }
            if (httpHeaders != null && httpHeaders.TryGetValue("Cookie", out string mediaCookie)) {
                media.AddOption($":http-cookie={mediaCookie}");
            }

            await media.Parse(soundPath.StartsWith("http") || soundPath.StartsWith("rtmp") || soundPath.StartsWith("rtsp")
              ? MediaParseOptions.ParseNetwork : MediaParseOptions.ParseLocal);
            
            MediaPlayer playerToStop = null;
            lock (_disposeLock) {
                if (_disposed) {
                    media.Dispose();
                    return;
                }
                playerToStop = _vlcPlayer;
            }

            if (playerToStop != null) {
                try { playerToStop.Stop(); } catch { }
                _bufferedWaveProvider?.ClearBuffer();
                await Task.Delay(250);
            }

            lock (_disposeLock) {
                if (_disposed) {
                    media.Dispose();
                    return;
                }
                if (_vlcPlayer != null) {
                    _vlcPlayer.Media = media;
                }
            }

            long exactSeekMs = startTimeMs;
            EventHandler<EventArgs> playingHandler = null;
            playingHandler = (s, e) => {
                if (exactSeekMs > 0) {
                    Task.Run(async () => {
                        await Task.Delay(2000);
                          if (_vlcPlayer != null && !_disposed) {
                              _vlcPlayer.Time = exactSeekMs;
                              _bufferedWaveProvider?.ClearBuffer();
                          }
                        exactSeekMs = 0;
                    });
                }
                if (_vlcPlayer != null) {
                    _vlcPlayer.Playing -= playingHandler;
                }
            };
            _vlcPlayer.Playing += playingHandler;

            bool playResult = _vlcPlayer.Play();
            if (!playResult)
                Debug.WriteLine("[MediaObject] ChangeVideoStream Play() returned FALSE - stream switch may have failed");
          }
        } catch (Exception e) { OnErrorReceived?.Invoke(this, new MediaError() { Exception = e }); }
      });
    }

    public static float AngleDir(Vector3 fwd, Vector3 targetDir, Vector3 up) {
      Vector3 perp = Vector3.Cross(fwd, targetDir);
      float dir = Vector3.Dot(perp, up);
      return dir;
    }

    private IntPtr Lock(IntPtr opaque, IntPtr planes) {
      try {
        if (_vlcBuffer != IntPtr.Zero) {
            Marshal.WriteIntPtr(planes, _vlcBuffer);
        }
        return IntPtr.Zero;
      } catch {
        return IntPtr.Zero;
      }
    }

    public void ResetVolume() {
    }

      private void DrainAudio(IntPtr data) {
      }

      private void FlushAudio(IntPtr data, long pts) {
          _bufferedWaveProvider?.ClearBuffer();
      }

      private void ResumeAudio(IntPtr data, long pts) {
          _waveOut?.Play();
      }

      private void PauseAudio(IntPtr data, long pts) {
          _waveOut?.Pause();
      }

        private void PlayAudio(IntPtr data, IntPtr samples, uint count, long pts) {
            if (_bufferedWaveProvider != null && _waveFormat != null) {
                int bytes = (int)count * _waveFormat.BlockAlign;
                if (_audioBuffer.Length < bytes) _audioBuffer = new byte[bytes];
                Marshal.Copy(samples, _audioBuffer, 0, bytes);
                _bufferedWaveProvider.AddSamples(_audioBuffer, 0, bytes);

                if (_waveOut != null) {
                    if (_waveOut.PlaybackState != PlaybackState.Playing) {
                        if (_bufferedWaveProvider.BufferedDuration.TotalMilliseconds > 300) {
                            _waveOut.Play();
                        }
                    }
                }
            }
        }
      private void Display(IntPtr opaque, IntPtr picture) {
        lock (_disposeLock) {
            if (_disposed) return;
            try {
              lock (_parent.FrameLock) {
                int totalBytes = (int)(_pitch * _lines);
                if (_parent.LastFrame.Length != totalBytes) {
                    _parent.LastFrame = new byte[totalBytes];
                }
                Marshal.Copy(_vlcBuffer, _parent.LastFrame, 0, totalBytes);
                
                unsafe {
                    fixed (byte* ptr = _parent.LastFrame) {
                        for (int i = 3; i < totalBytes; i += 4) {
                            ptr[i] = 255;
                        }
                    }
                }
                
                _parent.LastFrameWidth = (int)(_pitch / _bytePerPixel);
                _parent.LastFrameHeight = (int)_lines;
                _parent.LastFrameCount++;
              }
            } catch (Exception ex) {
                Debug.WriteLine("[MediaObject] Display error: {ex}");
            }
        }
      }

      private uint VideoFormatSetup(ref IntPtr opaque, IntPtr chroma, ref uint width, ref uint height, ref uint pitches, ref uint lines) {
        byte[] rv32 = System.Text.Encoding.ASCII.GetBytes("RV32");
        Marshal.Copy(rv32, 0, chroma, 4);
        
        _width = width;
        _height = height;
        _pitch = Align(_width * _bytePerPixel);
        _lines = Align(_height);
        
        pitches = _pitch;
        lines = _lines;
        
        lock (_disposeLock) {
          if (!_disposed) {
            if (_vlcMappedViewAccessor != null) {
              _vlcMappedViewAccessor.Dispose();
            }
            if (_vlcMappedFile != null) {
              _vlcMappedFile.Dispose();
            }
            _vlcMappedFile = MemoryMappedFile.CreateNew(null, _pitch * _lines);
            _vlcMappedViewAccessor = _vlcMappedFile.CreateViewAccessor();
            _vlcBuffer = _vlcMappedViewAccessor.SafeMemoryMappedViewHandle.DangerousGetHandle();
          }
        }
        return 1;
      }

    public void Dispose() {
      MediaPlayer playerToStop = null;

      lock (_disposeLock) {
          if (_disposed || _isDisposing) {
            return;
          }
          _isDisposing = true;
          
          playerToStop = _vlcPlayer;
      }

      if (_vlcLogHandler != null && _ownedLibVLC != null) {
          try { _ownedLibVLC.Log -= _vlcLogHandler; } catch { }
      }

      if (playerToStop != null) {
          PlaybackStopped?.Invoke(this, "OK");
          try { playerToStop.Stop(); } catch { }
          try { playerToStop.Dispose(); } catch { }
      }
      if (_ownedLibVLC != null) {
          try { _ownedLibVLC.Dispose(); } catch { }
      }

      lock (_disposeLock) {
          if (_disposed) return;
          _disposed = true;
          _parent.OnCleanupTime -= _parent_OnCleanupTime;

          _vlcPlayer = null;
          _ownedLibVLC = null;

          if (_vlcMappedViewAccessor != null) {
              _vlcMappedViewAccessor.Dispose();
              _vlcMappedViewAccessor = null;
          }
          if (_vlcMappedFile != null) {
              _vlcMappedFile.Dispose();
              _vlcMappedFile = null;
          }
          _vlcBuffer = IntPtr.Zero;

          if (_waveOut != null) {
              _waveOut.Stop();
              _waveOut.Dispose();
              _waveOut = null;
          }
      }
    }
  }
}
