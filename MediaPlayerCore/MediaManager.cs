using NAudio.Wave;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;

namespace MediaPlayerCore {
  public class MediaManager : IDisposable {
    byte[] _lastFrame;
    private bool _invalidated = false;
    ConcurrentDictionary<string, MediaObject> _playbackStreams = new ConcurrentDictionary<string, MediaObject>();

    public event EventHandler<MediaError> OnErrorReceived;
    public event EventHandler OnCleanupTime;
    private IMediaGameObject _mainPlayer = null;
    private IMediaGameObject _camera;
    private string _libVLCPath;
    private Task _updateLoop;
    private bool notDisposed = true;
    private float _livestreamVolume = 1;
    private float _cameraAndPlayerPositionSlider;

    public float LiveStreamVolume { get => _livestreamVolume; set => _livestreamVolume = value; }
    public byte[] LastFrame { get => _lastFrame; set => _lastFrame = value; }
    public bool Invalidated { get => _invalidated; set => _invalidated = value; }

    public event EventHandler OnNewMediaTriggered;

    public MediaManager(IMediaGameObject playerObject, IMediaGameObject camera, string libVLCPath) {
      _mainPlayer = playerObject;
      _camera = camera;
      _libVLCPath = libVLCPath;
      _updateLoop = Task.Run(() => Update());
    }

    public async void PlayStream(IMediaGameObject playerObject, string audioPath, int delay = 0) {
      Task.Run(() => {
        try {
          OnNewMediaTriggered?.Invoke(this, EventArgs.Empty);
          if (!string.IsNullOrEmpty(audioPath)) {
            if (audioPath.StartsWith("http") || audioPath.StartsWith("rtmp")) {
              foreach (var sound in _playbackStreams) {
                sound.Value.Invalidated = true;
                sound.Value?.Stop();
              }
              _playbackStreams.Clear();
              ConfigureStream(playerObject, audioPath, delay);
            }
          }
        } catch (Exception e) {
          OnErrorReceived?.Invoke(this, new MediaError() { Exception = e });
        }
      });
    }

    public async void ChangeStream(IMediaGameObject playerObject, string audioPath, float width) {
      Task.Run(() => {
        try {
          OnNewMediaTriggered?.Invoke(this, EventArgs.Empty);
          if (!string.IsNullOrEmpty(audioPath)) {
            if (audioPath.StartsWith("http") || audioPath.StartsWith("rtmp")) {
              if (_playbackStreams.ContainsKey(playerObject.Name)) {
                _playbackStreams[playerObject.Name].ChangeVideoStream(audioPath, width);
              }
            }
          }
        } catch (Exception e) {
          OnErrorReceived?.Invoke(this, new MediaError() { Exception = e });
        }
      });
    }

    public async void StopStream() {
      foreach (var sound in _playbackStreams) {
        sound.Value?.Stop();
      }
      _playbackStreams.Clear();
    }

    public bool IsAllowedToStartStream(IMediaGameObject playerObject) {
      if (_playbackStreams.ContainsKey(playerObject.Name)) {
        return true;
      } else {
        if (_playbackStreams.Count == 0) {
          return true;
        } else {
          foreach (string key in _playbackStreams.Keys) {
            bool noStream = _playbackStreams[key].PlaybackState == PlaybackState.Stopped;
            return noStream;
          }
        }
      }
      return false;
    }

    private void ConfigureStream(IMediaGameObject playerObject, string audioPath, int delay = 0) {
      if (playerObject != null) {
        try {
          if (_playbackStreams.ContainsKey(playerObject.Name)) {
            try {
              _playbackStreams[playerObject.Name]?.Dispose();
            } catch (Exception e) {
              OnErrorReceived?.Invoke(this, new MediaError() { Exception = e });
            }
          }

          _playbackStreams[playerObject.Name] = new MediaObject(
            this, playerObject, _camera,
            SoundType.Livestream, audioPath, _libVLCPath, false);

          lock (_playbackStreams[playerObject.Name]) {
            float volume = _livestreamVolume;
            _playbackStreams[playerObject.Name].OnErrorReceived += MediaManager_OnErrorReceived;
            _playbackStreams[playerObject.Name].Play(audioPath, volume, delay);
          }
        } catch (Exception e) {
          OnErrorReceived?.Invoke(this, new MediaError() { Exception = e });
        }
      }
    }

    private void Update() {
      while (notDisposed) {
        try {
          UpdateVolumes(_playbackStreams);
        } catch { }
        Thread.Sleep(100);
      }
    }

    public void UpdateVolumes(ConcurrentDictionary<string, MediaObject> sounds) {
      for (int i = 0; i < sounds.Count; i++) {
        lock (sounds) {
          try {
            string characterObjectName = sounds.Keys.ElementAt<string>(i);
            if (sounds.ContainsKey(characterObjectName)) {
              try {
                lock (sounds[characterObjectName]) {
                  if (sounds[characterObjectName].SpatialAllowed) {
                    if (sounds[characterObjectName].CharacterObject != null) {
                      Vector3 dir = new Vector3();
                      if (sounds[characterObjectName].CharacterObject.Position.Length() > 0) {
                        dir = sounds[characterObjectName].CharacterObject.Position - GetListeningPosition();
                      } else {
                        dir = _mainPlayer.Position - GetListeningPosition();
                      }
                      float direction = AngleDir(_camera.Forward, dir, _camera.Top);
                      try {
                        sounds[characterObjectName].Volume = CalculateObjectVolume(characterObjectName, sounds[characterObjectName]);
                      } catch (Exception e) { OnErrorReceived?.Invoke(this, new MediaError() { Exception = e }); }
                    }
                  } else {
                    sounds[characterObjectName].Volume = _livestreamVolume;
                  }
                }
              } catch (Exception e) { OnErrorReceived?.Invoke(this, new MediaError() { Exception = e }); }
            }
          } catch (Exception e) { OnErrorReceived?.Invoke(this, new MediaError() { Exception = e }); }
        }
      }
    }

    Vector3 GetListeningPosition() {
      return Vector3.Lerp(new Vector3(_camera.Position.X, _mainPlayer.Position.Y, _camera.Position.Z), _mainPlayer.Position, _cameraAndPlayerPositionSlider);
    }

    public float CalculateObjectVolume(string playerName, MediaObject mediaObject) {
      float maxDistance = 100;
      float volume = _livestreamVolume;
      float distance = Vector3.Distance(GetListeningPosition(), mediaObject.CharacterObject.Position);
      return Math.Clamp(volume * ((maxDistance - distance) / maxDistance), 0f, 1f);
    }

    public float AngleDir(Vector3 fwd, Vector3 targetDir, Vector3 up) {
      Vector3 perp = Vector3.Cross(fwd, targetDir);
      float dir = Vector3.Dot(perp, up);
      return dir;
    }

    private void MediaManager_OnErrorReceived(object? sender, MediaError e) {
      OnErrorReceived?.Invoke(this, new MediaError() { Exception = e.Exception });
    }

    public void CleanSounds() {
      try {
        foreach (var sound in _playbackStreams) {
          if (sound.Value != null) {
            sound.Value.Invalidated = true;
            sound.Value?.Dispose();
            sound.Value.OnErrorReceived -= MediaManager_OnErrorReceived;
          }
        }
        _lastFrame = null;
        _playbackStreams?.Clear();
        OnCleanupTime?.Invoke(this, EventArgs.Empty);
      } catch (Exception e) { OnErrorReceived?.Invoke(this, new MediaError() { Exception = e }); }
    }

    public void Dispose() {
      Task.Run(async () => {
        notDisposed = false;
        CleanSounds();
        try {
          if (_updateLoop != null) {
            _updateLoop?.Dispose();
          }
        } catch (Exception e) { OnErrorReceived?.Invoke(this, new MediaError() { Exception = e }); }
      });
    }
  }
}
