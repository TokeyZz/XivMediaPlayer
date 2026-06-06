using Dalamud.Configuration;
using Dalamud.Plugin;
using System;
using System.Collections.Generic;

namespace XivMediaPlayer {
  [Serializable]
  public class RoomMediaState {
      public string CurrentUrl { get; set; } = "";
      public long TimecodeMs { get; set; } = 0;
      public List<string> Playlist { get; set; } = new List<string>();
  }

  [Serializable]
  public class Configuration : IPluginConfiguration {
    public event EventHandler OnConfigurationChanged;

    private float _livestreamVolume = 0.5f;
    private bool _tuneIntoTwitchStreams = true;
    private bool _tuneIntoTwitchStreamPrompt = true;
    private int _defaultVideoOpen = 1; // 0 = open, 1 = closed

    int IPluginConfiguration.Version { get; set; }

    #region Saved configuration values

    public float LivestreamVolume { get => _livestreamVolume; set => _livestreamVolume = value; }
    public bool TuneIntoTwitchStreams { get => _tuneIntoTwitchStreams; set => _tuneIntoTwitchStreams = value; }
    public bool TuneIntoTwitchStreamPrompt { get => _tuneIntoTwitchStreamPrompt; set => _tuneIntoTwitchStreamPrompt = value; }
    public int DefaultVideoOpen { get => _defaultVideoOpen; set => _defaultVideoOpen = value; }

    // yt-dlp settings
    public int PreferredQuality { get; set; } = 720;

    // World screen compositing settings (legacy single placement)
    public MediaPlayerCore.Compositing.WorldScreenTransform WorldScreen { get; set; } = new MediaPlayerCore.Compositing.WorldScreenTransform();

    // Per-location screen placements: key = location string, value = transform
    public Dictionary<string, MediaPlayerCore.Compositing.WorldScreenTransform> ScreenPlacements { get; set; }
      = new Dictionary<string, MediaPlayerCore.Compositing.WorldScreenTransform>();

    public Dictionary<string, RoomMediaState> RoomMediaStates { get; set; } = new Dictionary<string, RoomMediaState>();

    public string ServerUrl { get; set; } = "http://24.77.70.65:5000";

    // Unique identity for the local user to establish TV ownership
    public string OwnerId { get; set; } = Guid.NewGuid().ToString();

    #endregion

    [NonSerialized]
    private IDalamudPluginInterface pluginInterface;

    /// <summary>
    /// Parameterless constructor required for Dalamud deserialization.
    /// </summary>
    public Configuration() { }

    /// <summary>
    /// Call after construction or deserialization to wire up the save interface.
    /// </summary>
    public void Initialize(IDalamudPluginInterface pi) {
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
