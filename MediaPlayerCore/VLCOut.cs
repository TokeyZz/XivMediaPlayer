using LibVLCSharp.Shared;
using NAudio.Wave;

namespace MediaPlayerCore {
  internal class VLCOut : IWavePlayer {
    private LibVLC libVLC;
    private MediaPlayer _vlcPlayer;
    event EventHandler<StoppedEventArgs> _stopped;

    float IWavePlayer.Volume { get => (float)_vlcPlayer.Volume / 100f; set => _vlcPlayer.Volume = (int)(value * 100f); }

    PlaybackState IWavePlayer.PlaybackState => _vlcPlayer.IsPlaying ? PlaybackState.Playing : PlaybackState.Stopped;

    WaveFormat IWavePlayer.OutputWaveFormat => WaveFormat.CreateIeeeFloatWaveFormat(0, 0);

    event EventHandler<StoppedEventArgs> IWavePlayer.PlaybackStopped {
      add {
        _stopped += value;
      }

      remove {
        _stopped -= value;
      }
    }

    void IDisposable.Dispose() {
      _vlcPlayer?.Dispose();
    }
    void IWavePlayer.Init(IWaveProvider waveProvider) {
    }
    public void Init(string soundPath, string libVLCPath) {
      Task.Run(async delegate {
        try {
          string location = libVLCPath + @"\libvlc\win-x64";
          Core.Initialize(location);
          libVLC = new LibVLC("--vout", "none");
          var media = new Media(libVLC, soundPath, FromType.FromPath);
          await media.Parse(MediaParseOptions.ParseLocal);
          _vlcPlayer = new MediaPlayer(media);
          var processingCancellationTokenSource = new CancellationTokenSource();
          _vlcPlayer.Stopped += (s, e) => processingCancellationTokenSource.CancelAfter(1);
          _vlcPlayer.Stopped += _vlcPlayer_Stopped;
        } catch { }
      });
    }

    private void _vlcPlayer_Stopped(object? sender, EventArgs e) {
      _stopped.Invoke(this, new StoppedEventArgs());
    }

    void IWavePlayer.Pause() {
      _vlcPlayer?.Pause();
    }

    void IWavePlayer.Play() {
      _vlcPlayer?.Play();
    }

    void IWavePlayer.Stop() {
      _vlcPlayer?.Stop();
    }
  }
}
