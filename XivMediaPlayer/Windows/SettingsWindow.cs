using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;
using System;
using System.Numerics;

namespace XivMediaPlayer.Windows {
  internal class SettingsWindow : Window {
    private Plugin _plugin;
    private Action _onVolumeFix;

    public SettingsWindow(Plugin plugin, Action onVolumeFix = null) :
      base("Media Player Settings", ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.AlwaysAutoResize, false) {
      _plugin = plugin;
      _onVolumeFix = onVolumeFix;
      Size = new Vector2(420, 0);
      SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void Draw() {
      // Volume 
      ImGui.TextColored(new Vector4(0.7f, 0.9f, 1.0f, 1.0f), "Audio");
      ImGui.Separator();

      float volume = _plugin.Config.LivestreamVolume;
      if (ImGui.SliderFloat("Stream Volume", ref volume, 0f, 3f)) {
        _plugin.Config.LivestreamVolume = volume;
        if (_plugin.MediaManager != null) {
            _plugin.MediaManager.LiveStreamVolume = volume;
        }
        _plugin.Config.Save();
      }

      if (_onVolumeFix != null && ImGui.Button("Fix Game Volume")) {
        _onVolumeFix.Invoke();
      }

      ImGui.Spacing();
      ImGui.Spacing();

      // Twitch 
      ImGui.TextColored(new Vector4(0.7f, 0.9f, 1.0f, 1.0f), "Twitch");
      ImGui.Separator();

      bool tuneInto = _plugin.Config.TuneIntoTwitchStreams;
      if (ImGui.Checkbox("Auto-tune into Twitch streams (in residential areas)", ref tuneInto)) {
        _plugin.Config.TuneIntoTwitchStreams = tuneInto;
        _plugin.Config.Save();
      }

      bool streamPrompt = _plugin.Config.TuneIntoTwitchStreamPrompt;
      if (ImGui.Checkbox("Show stream prompts in chat", ref streamPrompt)) {
        _plugin.Config.TuneIntoTwitchStreamPrompt = streamPrompt;
        _plugin.Config.Save();
      }

      ImGui.Spacing();
      ImGui.Spacing();

      // Video 
      ImGui.TextColored(new Vector4(0.7f, 0.9f, 1.0f, 1.0f), "Video");
      ImGui.Separator();

      bool defaultOpen = _plugin.Config.DefaultVideoOpen == 0;
      if (ImGui.Checkbox("Open video window by default when stream starts", ref defaultOpen)) {
        _plugin.Config.DefaultVideoOpen = defaultOpen ? 0 : 1;
        _plugin.Config.Save();
      }

      ImGui.Spacing();
      ImGui.Spacing();

      // Outdoor TVs
      ImGui.TextColored(new Vector4(0.7f, 0.9f, 1.0f, 1.0f), "Outdoor TVs");
      ImGui.Separator();

      bool enableOutdoor = _plugin.Config.EnableOutdoorPublicScreens;
      if (ImGui.Checkbox("Enable Public Outdoor Screens", ref enableOutdoor)) {
        _plugin.Config.EnableOutdoorPublicScreens = enableOutdoor;
        _plugin.Config.Save();
        _plugin.HandleOutdoorSettingToggled();
      }

      bool safeMode = _plugin.Config.OnlySafeDomainsPublicScreens;
      if (ImGui.Checkbox("Safe Mode (Only allow safe domains outside)", ref safeMode)) {
        if (!safeMode) {
            ImGui.OpenPopup("Disable Safe Mode Warning");
        } else {
            _plugin.Config.OnlySafeDomainsPublicScreens = true;
            _plugin.Config.Save();
        }
      }
      ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f),
        "Blocks unverified URLs on outdoor screens to prevent abuse.");

      var viewportCenter = ImGui.GetMainViewport().GetCenter();
      ImGui.SetNextWindowPos(viewportCenter, ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
      if (ImGui.BeginPopupModal("Disable Safe Mode Warning", ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings)) {
          ImGui.Text("WARNING: Disabling Safe Mode will allow almost any domain to play on outdoor screens (unless otherwise blacklisted by your current server).");
          ImGui.Text("You may be exposed to content that you may not wish to see from unmoderated domains.");
          ImGui.Spacing();
          ImGui.TextColored(new Vector4(1f, 0.3f, 0.3f, 1f), "By clicking 'I Agree', you accept full responsibility for your own screen,");
          ImGui.TextColored(new Vector4(1f, 0.3f, 0.3f, 1f), "and you explicitly agree that you WILL NOT play illegal content.");
          ImGui.Separator();
          ImGui.Spacing();
          
          if (ImGui.Button("I Agree, Disable Safe Mode", new Vector2(250, 0))) {
              _plugin.Config.OnlySafeDomainsPublicScreens = false;
              _plugin.Config.Save();
              ImGui.CloseCurrentPopup();
          }
          ImGui.SameLine();
          if (ImGui.Button("Cancel", new Vector2(120, 0))) {
              ImGui.CloseCurrentPopup();
          }
          ImGui.EndPopup();
      }

      ImGui.Separator();

      bool spatialAudio = _plugin.Config.SpatialAudioEnabled;
      if (ImGui.Checkbox("Enable 3D Spatial Audio", ref spatialAudio)) {
        _plugin.Config.SpatialAudioEnabled = spatialAudio;
        _plugin.Config.Save();
        _plugin.DoRefreshCurrentMedia();
      }
      ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f),
        "Dynamically pans audio to simulate physical TV locations. If you experience A/V sync issues, disable this.");

      ImGui.Separator();
      
      bool showGrid = _plugin.Config.ShowOutdoorGridDebug;
      if (ImGui.Checkbox("Show Outdoor Grid Overlay (Debug)", ref showGrid)) {
        _plugin.Config.ShowOutdoorGridDebug = showGrid;
        _plugin.Config.Save();
      }

      ImGui.Spacing();
      ImGui.Spacing();

      // Playback
      ImGui.TextColored(new Vector4(0.7f, 0.9f, 1.0f, 1.0f), "Playback");
      ImGui.Separator();

      int seekIncrement = _plugin.Config.SeekIncrementSeconds;
      if (ImGui.SliderInt("Seek Increment (seconds)", ref seekIncrement, 1, 60)) {
        _plugin.Config.SeekIncrementSeconds = seekIncrement;
        _plugin.Config.Save();
      }
      ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f),
        "How many seconds the << and >> buttons skip.");

      ImGui.Spacing();
      ImGui.Spacing();

      // yt-dlp quality
      ImGui.TextColored(new Vector4(0.7f, 0.9f, 1.0f, 1.0f), "yt-dlp");
      ImGui.Separator();

      string[] qualityLabels = new string[] { "360p", "480p", "720p", "1080p", "Best" };
      int[] qualityValues = new int[] { 360, 480, 720, 1080, 0 };
      int currentQualityIdx = Array.IndexOf(qualityValues, _plugin.Config.PreferredQuality);
      if (currentQualityIdx < 0) currentQualityIdx = 2; // default 720p
      if (ImGui.Combo("Preferred Quality", ref currentQualityIdx, qualityLabels, qualityLabels.Length)) {
        _plugin.Config.PreferredQuality = qualityValues[currentQualityIdx];
        _plugin.Config.Save();
      }

      ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f),
        "yt-dlp is automatically downloaded and updated.");

      ImGui.Spacing();
      ImGui.Spacing();

      // Server Sync
      ImGui.TextColored(new Vector4(0.7f, 0.9f, 1.0f, 1.0f), "Server Sync");
      ImGui.Separator();

      string serverUrl = _plugin.Config.ServerUrl;
      if (ImGui.InputText("Server URL", ref serverUrl, 256)) {
        _plugin.Config.ServerUrl = serverUrl;
        _plugin.Config.Save();
      }
      ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f),
        "URL of the backend server used to sync TVs.");

      ImGui.Spacing();
      ImGui.Spacing();

      // Support
      ImGui.TextColored(new Vector4(0.7f, 0.9f, 1.0f, 1.0f), "Support");
      ImGui.Separator();

      if (ImGui.Button("Support the Developer on Ko-fi")) {
          try {
              System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo {
                  FileName = "https://ko-fi.com/sebastina",
                  UseShellExecute = true
              });
          } catch { }
      }
    }
  }
}
