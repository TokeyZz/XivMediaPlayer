using LibVLCSharp.Shared;
using System.ComponentModel;
using System.Diagnostics;

namespace MediaPlayerCore {
  internal class VideoView : IVideoView, IDisposable, ISupportInitialize {
    public VideoView() {
    }

    MediaPlayer? _mp;

    /// <summary>
    /// The MediaPlayer attached to this view (or null)
    /// </summary>
    public MediaPlayer? MediaPlayer {
      get => _mp;
      set {
        if (ReferenceEquals(_mp, value)) {
          return;
        }

        Detach();
        _mp = value;
        Attach();
      }
    }

    /// <summary>
    /// This currently does not do anything
    /// </summary>
    void ISupportInitialize.BeginInit() {
    }

    /// <summary>
    /// This attaches the mediaplayer to the view (if any)
    /// </summary>
    void ISupportInitialize.EndInit() {
      if (IsInDesignMode)
        return;

      Attach();
    }

    bool IsInDesignMode {
      get {
        return false;
      }
    }

    void Detach() {
      if (_mp == null || _mp.NativeReference == IntPtr.Zero)
        return;

      _mp.Hwnd = IntPtr.Zero;
    }

    void Attach() {
      if (_mp == null || _mp.NativeReference == IntPtr.Zero)
        return;

      _mp.Hwnd = Process.GetCurrentProcess().Handle;
    }

    bool disposedValue;

    public void Dispose(bool disposing) {
      if (!disposedValue) {
        if (disposing) {
          if (MediaPlayer != null && MediaPlayer.NativeReference != IntPtr.Zero) {
            MediaPlayer.Hwnd = IntPtr.Zero;
          }
        }
        disposedValue = true;
      }
    }

    public void Dispose() {
      Dispose(true);
    }
  }
}
