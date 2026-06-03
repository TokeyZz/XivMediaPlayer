using Dalamud.Configuration;
using Dalamud.Plugin;
using System;

namespace XivMediaPlayer {
  [Serializable]
  public class Configuration : IPluginConfiguration {
    public event EventHandler OnConfigurationChanged;

    private float _livestreamVolume = 1;
    private bool _tuneIntoTwitchStreams = true;
    private bool _tuneIntoTwitchStreamPrompt = true;
    private int _defaultVideoOpen = 1; // 0 = open, 1 = closed
    private int _audioOutputDeviceIndex = -1;

    int IPluginConfiguration.Version { get; set; }

    #region Saved configuration values

    public float LivestreamVolume { get => _livestreamVolume; set => _livestreamVolume = value; }
    public bool TuneIntoTwitchStreams { get => _tuneIntoTwitchStreams; set => _tuneIntoTwitchStreams = value; }
    public bool TuneIntoTwitchStreamPrompt { get => _tuneIntoTwitchStreamPrompt; set => _tuneIntoTwitchStreamPrompt = value; }
    public int DefaultVideoOpen { get => _defaultVideoOpen; set => _defaultVideoOpen = value; }
    public int AudioOutputDeviceIndex { get => _audioOutputDeviceIndex; set => _audioOutputDeviceIndex = value; }

    // yt-dlp settings
    public string YtDlpPath { get; set; } = "";
    public int PreferredQuality { get; set; } = 720;
    public bool AutoUpdateYtDlp { get; set; } = false;

    // World screen compositing settings
    public MediaPlayerCore.Compositing.WorldScreenTransform WorldScreen { get; set; } = new MediaPlayerCore.Compositing.WorldScreenTransform();

    #endregion

    private readonly IDalamudPluginInterface pluginInterface;

    public Configuration(IDalamudPluginInterface pi) {
      this.pluginInterface = pi;
    }

    public void Save() {
      if (this.pluginInterface != null) {
        this.pluginInterface.SavePluginConfig(this);
      }
      OnConfigurationChanged?.Invoke(this, EventArgs.Empty);
    }
  }
}
