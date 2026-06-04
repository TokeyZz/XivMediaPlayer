using LibVLCSharp.Shared;
using NAudio.Wave;
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
    private LibVLC libVLC;
    private MediaPlayer _vlcPlayer;
    private MediaManager _parent;

    private static MemoryMappedFile _currentMappedFile;
    private static MemoryMappedViewAccessor _currentMappedViewAccessor;
    public event EventHandler<MediaError> OnErrorReceived;
    public event EventHandler<string> PlaybackStopped;
    public event EventHandler<string> PlaybackFinished;

    private string _soundPath;
    private string _libVLCPath;

    private const uint _width = 1280;
    private const uint _height = 720;

    /// <summary>
    /// RGBA is used, so 4 byte per pixel, or 32 bits.
    /// </summary>
    private const uint _bytePerPixel = 4;

    /// <summary>
    /// the number of bytes per "line"
    /// For performance reasons inside the core of VLC, it must be aligned to multiples of 32.
    /// </summary>
    private uint _pitch;

    /// <summary>
    /// The number of lines in the buffer.
    /// For performance reasons inside the core of VLC, it must be aligned to multiples of 32.
    /// </summary>
    private uint _lines;
    private float volumePercentage = 1;
    private float _baseVolume = 1;
    private bool _vlcWasAbleToStart;
    private bool _disposed;

    public MediaObject(MediaManager parent, IMediaGameObject playerObject, IMediaGameObject camera,
      SoundType soundType, string soundPath, string libVLCPath, bool spatialAllowed) {
      _playerObject = playerObject;
      _soundPath = soundPath;
      _camera = camera;
      _libVLCPath = libVLCPath;
      _parent = parent;
      this._soundType = soundType;
      _spatialAllowed = spatialAllowed;
      _pitch = Align(_width * _bytePerPixel);
      _lines = Align(_height);
      _parent.OnCleanupTime += _parent_OnCleanupTime;
    }

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
            int newValue = (int)(value * 100f);
            if (newValue != _vlcPlayer.Volume) {
              _baseVolume = newValue;
              _vlcPlayer.Volume = (int)((float)newValue * volumePercentage);
            }
          } catch (Exception e) { OnErrorReceived?.Invoke(this, new MediaError() { Exception = e }); }
        }
      }
    }
    public PlaybackState PlaybackState {
      get {
        if (_vlcPlayer != null) {
          try {
            return _vlcPlayer.IsPlaying ? PlaybackState.Playing : PlaybackState.Stopped;
          } catch {
            return PlaybackState.Stopped;
          }
        } else {
          return PlaybackState.Stopped;
        }
      }
    }
    
    public long Time {
      get => _vlcPlayer?.Time ?? 0;
      set { if (_vlcPlayer != null) _vlcPlayer.Time = value; }
    }

    public long Length => _vlcPlayer?.Length ?? 0;

    public void Pause() {
      _vlcPlayer?.Pause();
    }
    public SoundType SoundType { get => _soundType; set => _soundType = value; }
    public string SoundPath { get => _soundPath; set => _soundPath = value; }
    public IMediaGameObject Camera { get => _camera; set => _camera = value; }
    public bool Invalidated { get; internal set; }
    public bool SpatialAllowed { get => _spatialAllowed; set => _spatialAllowed = value; }
    public MediaManager Parent { get => _parent; set => _parent = value; }

    public void Stop() {
      Volume = 0;
      PlaybackStopped?.Invoke(this, "OK");
      if (_vlcPlayer != null) {
        try {
          _vlcPlayer?.Stop();
        } catch (Exception e) { OnErrorReceived?.Invoke(this, new MediaError() { Exception = e }); }
      }
      Volume = 0;
      Invalidated = true;
    }

    public void Play(string mediaPath, float volume, int startTimeMs, Dictionary<string, string>? httpHeaders) {
      Task.Run(async delegate {
        try {
          if (!string.IsNullOrEmpty(mediaPath) && PlaybackState == PlaybackState.Stopped) {
            try {
              _parent.LastFrame = new byte[0];
              string location = _libVLCPath + @"\libvlc\win-x64";
              Debug.WriteLine($"[MediaObject] Initializing VLC from: {location}");
              Debug.WriteLine($"[MediaObject] Media path: {mediaPath.Substring(0, Math.Min(100, mediaPath.Length))}...");

              Core.Initialize(location);
              var vlcArgs = new List<string> {
                "--vout=none", 
                "--http-user-agent=Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
                "--http-reconnect",
                "--network-caching=2000"
              };

              if (httpHeaders != null && httpHeaders.TryGetValue("Referer", out string referer)) {
                vlcArgs.Add($"--http-referrer={referer}");
              }

              libVLC = new LibVLC(vlcArgs.ToArray());

              // Hook VLC's internal log to catch errors
              libVLC.Log += (s, e) => {
                if (e.Level >= LogLevel.Warning) {
                  Debug.WriteLine($"[VLC-{e.Level}] {e.Module}: {e.Message}");
                  OnErrorReceived?.Invoke(this, new MediaError() { Exception = new Exception($"VLC [{e.Level}] {e.Module}: {e.Message}") });
                }
              };

              var media = new Media(libVLC, mediaPath, mediaPath.StartsWith("http") || mediaPath.StartsWith("rtmp")
                ? FromType.FromLocation : FromType.FromPath);
              
              if (startTimeMs > 0) {
                  media.AddOption($":start-time={startTimeMs / 1000.0}");
              }

              Debug.WriteLine("[MediaObject] Parsing media...");
              await media.Parse(mediaPath.StartsWith("http") || mediaPath.StartsWith("rtmp")
                ? MediaParseOptions.ParseNetwork : MediaParseOptions.ParseLocal);
              Debug.WriteLine($"[MediaObject] Media parsed. Duration: {media.Duration}ms");

              _vlcPlayer = new MediaPlayer(media);
              _vlcPlayer.SetAudioOutput("waveout");
              _vlcPlayer.Stopped += delegate { _parent.LastFrame = new byte[0]; };
              _vlcPlayer.EndReached += delegate {
                PlaybackFinished?.Invoke(this, "OK");
              };
              _vlcPlayer.EncounteredError += (s, e) => {
                Debug.WriteLine("[MediaObject] VLC EncounteredError event fired!");
                OnErrorReceived?.Invoke(this, new MediaError() { Exception = new Exception("VLC player encountered an error during playback.") });
              };
              if (mediaPath.StartsWith("http") || mediaPath.StartsWith("rtmp") || mediaPath.EndsWith(".mp4") || mediaPath.EndsWith(".avi")) {
                _vlcPlayer.SetVideoFormat("RV32", _width, _height, _pitch);
                _vlcPlayer.SetVideoCallbacks(Lock, null, Display);
              }
              _baseVolume = volume;
              Volume = volume;
              
              long exactSeekMs = startTimeMs;
              _vlcPlayer.Playing += (s, e) => {
                  if (exactSeekMs > 0) {
                      // Fire exact seek to correct keyframe snapping margin of error
                      Task.Run(async () => {
                          await Task.Delay(100);
                          if (_vlcPlayer != null) {
                              _vlcPlayer.Time = exactSeekMs;
                          }
                          exactSeekMs = 0;
                      });
                  }
              };

              bool playResult = _vlcPlayer.Play();
              Debug.WriteLine($"[MediaObject] VLC Play() returned: {playResult}");
              _vlcWasAbleToStart = playResult;

              if (!playResult) {
                OnErrorReceived?.Invoke(this, new MediaError() { Exception = new Exception("VLC Play() returned false — playback failed to start.") });
              }
            } catch (Exception e) {
              Debug.WriteLine($"[MediaObject] Play exception: {e}");
              OnErrorReceived?.Invoke(this, new MediaError() { Exception = e });
              PlaybackStopped?.Invoke(this, "OK");
            }
          } else {
            Debug.WriteLine($"[MediaObject] Play skipped. mediaPath empty={string.IsNullOrEmpty(mediaPath)}, state={PlaybackState}");
          }
        } catch (Exception e) {
          Debug.WriteLine($"[MediaObject] Outer play exception: {e}");
          OnErrorReceived?.Invoke(this, new MediaError() { Exception = e });
          PlaybackStopped?.Invoke(this, "ERR");
        }
      });
    }

    public void ChangeVideoStream(string soundPath, float width) {
      Task.Run(async delegate {
        try {
          if (_vlcWasAbleToStart) {
            var media = new Media(libVLC, soundPath, soundPath.StartsWith("http") || soundPath.StartsWith("rtmp")
                     ? FromType.FromLocation : FromType.FromPath);
            await media.Parse(soundPath.StartsWith("http") || soundPath.StartsWith("rtmp")
              ? MediaParseOptions.ParseNetwork : MediaParseOptions.ParseLocal);
            _vlcPlayer.Media = media;
            _vlcPlayer.Play();
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
        _currentMappedFile = MemoryMappedFile.CreateNew(null, _pitch * _lines);
        _currentMappedViewAccessor = _currentMappedFile.CreateViewAccessor();
        Marshal.WriteIntPtr(planes, _currentMappedViewAccessor.SafeMemoryMappedViewHandle.DangerousGetHandle());
        return IntPtr.Zero;
      } catch {
        return IntPtr.Zero;
      }
    }

    public void ResetVolume() {
      // No-op for VLC-only path; volume is managed through the VLC player directly.
    }

    private void Display(IntPtr opaque, IntPtr picture) {
      if (!Invalidated) {
        try {
          using (var image = new Image<Bgra32>((int)(_pitch / _bytePerPixel), (int)_lines))
          using (var sourceStream = _currentMappedFile.CreateViewStream()) {
            var mg = image.GetPixelMemoryGroup();
            for (int i = 0; i < mg.Count; i++) {
              sourceStream.Read(MemoryMarshal.AsBytes(mg[i].Span));
            }
            lock (_parent.LastFrame) {
              MemoryStream stream = new MemoryStream();
              image.SaveAsJpeg(stream);
              stream.Flush();
              stream.Position = 0;
              _parent.LastFrame = stream.ToArray();
            }
          }
          _currentMappedViewAccessor.Dispose();
          _currentMappedFile.Dispose();
          _currentMappedFile = null;
          _currentMappedViewAccessor = null;
        } catch (Exception e) { OnErrorReceived?.Invoke(this, new MediaError() { Exception = e }); }
      }
    }

    public void Dispose() {
      if (_disposed) {
        return;
      }
      _disposed = true;
      _parent.OnCleanupTime -= _parent_OnCleanupTime;
      Stop();
      Volume = 0;
      try { _vlcPlayer?.Dispose(); } catch { }
      _vlcPlayer = null;
      try { libVLC?.Dispose(); } catch { }
      libVLC = null;
    }
  }
}
