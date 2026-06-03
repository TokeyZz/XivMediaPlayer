using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;
using NAudio.Wave;
using System;
using System.Numerics;

namespace XivMediaPlayer.Windows {
  internal class SettingsWindow : Window {
    private Configuration _config;

    public SettingsWindow(Configuration config) :
      base("Media Player Settings", ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.AlwaysAutoResize, false) {
      _config = config;
      Size = new Vector2(420, 0);
      SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void Draw() {
      // Volume 
      ImGui.TextColored(new Vector4(0.7f, 0.9f, 1.0f, 1.0f), "Audio");
      ImGui.Separator();

      float volume = _config.LivestreamVolume;
      if (ImGui.SliderFloat("Stream Volume", ref volume, 0f, 1f)) {
        _config.LivestreamVolume = volume;
        _config.Save();
      }

      // Audio output device
      string[] deviceNames = GetAudioDeviceNames();
      int selectedDevice = _config.AudioOutputDeviceIndex + 1; // -1 = default, shift to 0-based
      if (ImGui.Combo("Audio Output", ref selectedDevice, deviceNames, deviceNames.Length)) {
        _config.AudioOutputDeviceIndex = selectedDevice - 1;
        _config.Save();
      }

      ImGui.Spacing();
      ImGui.Spacing();

      // Twitch 
      ImGui.TextColored(new Vector4(0.7f, 0.9f, 1.0f, 1.0f), "Twitch");
      ImGui.Separator();

      bool tuneInto = _config.TuneIntoTwitchStreams;
      if (ImGui.Checkbox("Auto-tune into Twitch streams (in residential areas)", ref tuneInto)) {
        _config.TuneIntoTwitchStreams = tuneInto;
        _config.Save();
      }

      bool streamPrompt = _config.TuneIntoTwitchStreamPrompt;
      if (ImGui.Checkbox("Show stream prompts in chat", ref streamPrompt)) {
        _config.TuneIntoTwitchStreamPrompt = streamPrompt;
        _config.Save();
      }

      ImGui.Spacing();
      ImGui.Spacing();

      // Video 
      ImGui.TextColored(new Vector4(0.7f, 0.9f, 1.0f, 1.0f), "Video");
      ImGui.Separator();

      bool defaultOpen = _config.DefaultVideoOpen == 0;
      if (ImGui.Checkbox("Open video window by default when stream starts", ref defaultOpen)) {
        _config.DefaultVideoOpen = defaultOpen ? 0 : 1;
        _config.Save();
      }

      ImGui.Spacing();
      ImGui.Spacing();

      // yt-dlp 
      ImGui.TextColored(new Vector4(0.7f, 0.9f, 1.0f, 1.0f), "yt-dlp");
      ImGui.Separator();

      string ytDlpPath = _config.YtDlpPath ?? "";
      if (ImGui.InputText("yt-dlp Path", ref ytDlpPath, 512)) {
        _config.YtDlpPath = ytDlpPath;
        _config.Save();
      }
      ImGui.SameLine();
      ImGui.TextDisabled("(?)");
      if (ImGui.IsItemHovered()) {
        ImGui.SetTooltip("Leave empty to auto-detect from PATH.\nDownload yt-dlp from https://github.com/yt-dlp/yt-dlp/releases");
      }

      string[] qualityLabels = new string[] { "360p", "480p", "720p", "1080p", "Best" };
      int[] qualityValues = new int[] { 360, 480, 720, 1080, 0 };
      int currentQualityIdx = Array.IndexOf(qualityValues, _config.PreferredQuality);
      if (currentQualityIdx < 0) currentQualityIdx = 2; // default 720p
      if (ImGui.Combo("Preferred Quality", ref currentQualityIdx, qualityLabels, qualityLabels.Length)) {
        _config.PreferredQuality = qualityValues[currentQualityIdx];
        _config.Save();
      }

      bool autoUpdate = _config.AutoUpdateYtDlp;
      if (ImGui.Checkbox("Auto-update yt-dlp on plugin load", ref autoUpdate)) {
        _config.AutoUpdateYtDlp = autoUpdate;
        _config.Save();
      }
    }

    private string[] GetAudioDeviceNames() {
      int deviceCount = WaveOut.DeviceCount;
      string[] names = new string[deviceCount + 1];
      names[0] = "Default";
      for (int i = 0; i < deviceCount; i++) {
        var caps = WaveOut.GetCapabilities(i);
        names[i + 1] = caps.ProductName;
      }
      return names;
    }
  }
}
