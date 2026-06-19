using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;
using System;
using System.Net;
using System.Net.Http;
using System.Numerics;
using System.Threading.Tasks;

namespace XivMediaPlayer.Windows {
  internal class SettingsWindow : Window {
    private Plugin _plugin;
    private Action _onVolumeFix;
    private string _proxyTestResult = "";
    private Vector4 _proxyTestColor = new Vector4(1, 1, 1, 1);

    public SettingsWindow(Plugin plugin, Action onVolumeFix = null) :
      base("媒体播放器 设置", ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.AlwaysAutoResize, false) {
      _plugin = plugin;
      _onVolumeFix = onVolumeFix;
      Size = new Vector2(420, 0);
      SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void Draw() {
      // Audio
      ImGui.TextColored(new Vector4(0.7f, 0.9f, 1.0f, 1.0f), "音频");
      ImGui.Separator();

      float volume = _plugin.Config.LivestreamVolume;
      if (ImGui.SliderFloat("音量", ref volume, 0f, 3f)) {
        _plugin.Config.LivestreamVolume = volume;
        if (_plugin.MediaManager != null) {
            _plugin.MediaManager.LiveStreamVolume = volume;
        }
        _plugin.Config.Save();
      }

      if (_onVolumeFix != null && ImGui.Button("修复游戏音量")) {
        _onVolumeFix.Invoke();
      }

      ImGui.Spacing();
      ImGui.Spacing();

      // Messages
      ImGui.TextColored(new Vector4(0.7f, 0.9f, 1.0f, 1.0f), "消息提示");
      ImGui.Separator();

      string[] filterLabels = new string[] { "全部消息 (含调试)", "仅重要消息", "全部屏蔽" };
      int currentFilterIdx = (int)_plugin.Config.ChatMessageFilter;
      if (currentFilterIdx < 0 || currentFilterIdx > 2) currentFilterIdx = 0;
      if (ImGui.Combo("聊天消息过滤", ref currentFilterIdx, filterLabels, filterLabels.Length)) {
        _plugin.Config.ChatMessageFilter = (ChatMessageLevel)currentFilterIdx;
        _plugin.Config.Save();
      }
      ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f),
        "全部: 显示所有消息含调试 | 仅重要: 只显示错误和播放状态变更 | 全部屏蔽: 不显示任何聊天消息");

      ImGui.Spacing();
      ImGui.Spacing();

      // Twitch
      ImGui.TextColored(new Vector4(0.7f, 0.9f, 1.0f, 1.0f), "Twitch");
      ImGui.Separator();

      bool tuneInto = _plugin.Config.TuneIntoTwitchStreams;
      if (ImGui.Checkbox("自动加入 Twitch 直播 (住宅区)", ref tuneInto)) {
        _plugin.Config.TuneIntoTwitchStreams = tuneInto;
        _plugin.Config.Save();
      }

      bool streamPrompt = _plugin.Config.TuneIntoTwitchStreamPrompt;
      if (ImGui.Checkbox("在聊天中显示直播提示", ref streamPrompt)) {
        _plugin.Config.TuneIntoTwitchStreamPrompt = streamPrompt;
        _plugin.Config.Save();
      }

      ImGui.Spacing();
      ImGui.Spacing();

      // Video
      ImGui.TextColored(new Vector4(0.7f, 0.9f, 1.0f, 1.0f), "视频");
      ImGui.Separator();

      bool defaultOpen = _plugin.Config.DefaultVideoOpen == 0;
      if (ImGui.Checkbox("流开始时自动打开视频窗口", ref defaultOpen)) {
        _plugin.Config.DefaultVideoOpen = defaultOpen ? 0 : 1;
        _plugin.Config.Save();
      }

      bool autoResume = _plugin.Config.AutoResumeMedia;
      if (ImGui.Checkbox("进入房间时自动恢复播放", ref autoResume)) {
        _plugin.Config.AutoResumeMedia = autoResume;
        _plugin.Config.Save();
      }

      bool disableUiBlock = _plugin.Config.DisableUIBlockDetection;
      if (ImGui.Checkbox("禁用 UI 遮挡检测", ref disableUiBlock)) {
        _plugin.Config.DisableUIBlockDetection = disableUiBlock;
        _plugin.Config.Save();
      }
      if (ImGui.IsItemHovered()) {
        ImGui.SetTooltip("允许在游戏 UI 覆盖电视时仍可点击电视。如果视觉模组干扰 UI 检测, 可开启此选项。");
      }

      if (ImGui.Button("清除观看历史")) {
        _plugin.Config.WatchHistory.Clear();
        _plugin.Config.Save();
        _plugin.PrintChat("[媒体播放器] 观看历史已清除", Plugin.ChatSeverity.Info);
      }

      ImGui.Spacing();
      ImGui.Spacing();

      // Outdoor TVs
      ImGui.TextColored(new Vector4(0.7f, 0.9f, 1.0f, 1.0f), "室外电视");
      ImGui.Separator();

      bool enableOutdoor = _plugin.Config.EnableOutdoorPublicScreens;
      if (ImGui.Checkbox("启用公共室外屏幕", ref enableOutdoor)) {
        _plugin.Config.EnableOutdoorPublicScreens = enableOutdoor;
        _plugin.Config.Save();
        _plugin.HandleOutdoorSettingToggled();
      }

      bool safeMode = _plugin.Config.OnlySafeDomainsPublicScreens;
      if (ImGui.Checkbox("安全模式 (仅允许安全域名在室外播放)", ref safeMode)) {
        if (!safeMode) {
            ImGui.OpenPopup("安全模式警告");
        } else {
            _plugin.Config.OnlySafeDomainsPublicScreens = true;
            _plugin.Config.Save();
        }
      }
      ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f),
        "阻止未经验证的链接在室外屏幕上播放, 防止滥用。");

      var viewportCenter = ImGui.GetMainViewport().GetCenter();
      ImGui.SetNextWindowPos(viewportCenter, ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
      if (ImGui.BeginPopupModal("安全模式警告", ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings)) {
          ImGui.Text("警告: 关闭安全模式将允许几乎所有域名在室外屏幕上播放 (除非被服务器黑名单拦截)。");
          ImGui.Text("你可能会看到未经审核的内容。");
          ImGui.Spacing();
          ImGui.TextColored(new Vector4(1f, 0.3f, 0.3f, 1f), "点击\"我同意\"即表示你为屏幕内容承担全部责任,");
          ImGui.TextColored(new Vector4(1f, 0.3f, 0.3f, 1f), "并明确承诺不会播放非法内容。");
          ImGui.Separator();
          ImGui.Spacing();

          if (ImGui.Button("我同意, 关闭安全模式", new Vector2(250, 0))) {
              _plugin.Config.OnlySafeDomainsPublicScreens = false;
              _plugin.Config.Save();
              ImGui.CloseCurrentPopup();
          }
          ImGui.SameLine();
          if (ImGui.Button("取消", new Vector2(120, 0))) {
              ImGui.CloseCurrentPopup();
          }
          ImGui.EndPopup();
      }

      ImGui.Separator();

      bool spatialAudio = _plugin.Config.SpatialAudioEnabled;
      if (ImGui.Checkbox("启用 3D 空间音频", ref spatialAudio)) {
        _plugin.Config.SpatialAudioEnabled = spatialAudio;
        _plugin.Config.Save();
        _plugin.DoRefreshCurrentMedia();
      }
      ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f),
        "动态调整音频方向以模拟物理电视位置。如遇到音视频同步问题, 请关闭此选项。");

      ImGui.Separator();

      bool showGrid = _plugin.Config.ShowOutdoorGridDebug;
      if (ImGui.Checkbox("显示室外网格覆盖 (调试)", ref showGrid)) {
        _plugin.Config.ShowOutdoorGridDebug = showGrid;
        _plugin.Config.Save();
      }

      ImGui.Spacing();
      ImGui.Spacing();

      // Playback
      ImGui.TextColored(new Vector4(0.7f, 0.9f, 1.0f, 1.0f), "播放");
      ImGui.Separator();

      int seekIncrement = _plugin.Config.SeekIncrementSeconds;
      if (ImGui.SliderInt("跳跃步长 (秒)", ref seekIncrement, 1, 60)) {
        _plugin.Config.SeekIncrementSeconds = seekIncrement;
        _plugin.Config.Save();
      }
      ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f),
        "<< 和 >> 按钮跳跃的秒数。");

      ImGui.Spacing();
      ImGui.Spacing();

      // Debug
      ImGui.TextColored(new Vector4(0.7f, 0.9f, 1.0f, 1.0f), "Debug");
      ImGui.Separator();

      unsafe
      {
          var housingMgr = FFXIVClientStructs.FFXIV.Client.Game.HousingManager.Instance();
          if (housingMgr != null && !housingMgr->IsInside() && housingMgr->GetCurrentPlot() >= 0 && housingMgr->GetCurrentWard() >= 0)
          {
              ImGui.TextColored(new Vector4(0.4f, 1f, 0.4f, 1f), $"You are standing in Plot {housingMgr->GetCurrentPlot() + 1}");
          }
      }

      string locationKey = _plugin.LocationKey;
      ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1f), "Placement Key:");
      ImGui.SameLine();
      ImGui.Text(locationKey ?? "Unknown");

      if (_plugin.CurrentTvPlacement != null)
      {
          ImGui.TextColored(new Vector4(0.4f, 1f, 0.4f, 1f), "Synced TV Key:");
          ImGui.SameLine();
          ImGui.Text(_plugin.CurrentTvPlacement.LocationKey);
      }
      ImGui.Spacing();

      // yt-dlp quality
      ImGui.TextColored(new Vector4(0.7f, 0.9f, 1.0f, 1.0f), "yt-dlp");
      ImGui.Separator();

      string[] qualityLabels = new string[] { "360p", "480p", "720p", "1080p", "最佳" };
      int[] qualityValues = new int[] { 360, 480, 720, 1080, 0 };
      int currentQualityIdx = Array.IndexOf(qualityValues, _plugin.Config.PreferredQuality);
      if (currentQualityIdx < 0) currentQualityIdx = 2; // default 720p
      if (ImGui.Combo("首选画质", ref currentQualityIdx, qualityLabels, qualityLabels.Length)) {
        _plugin.Config.PreferredQuality = qualityValues[currentQualityIdx];
        _plugin.Config.Save();
      }

      ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f),
        "yt-dlp 会自动下载和更新。");

      if (_plugin.YtDlpManager != null && !_plugin.YtDlpManager.HasCookiesFile) {
        ImGui.Spacing();
        ImGui.TextColored(new Vector4(1f, 0.3f, 0.3f, 1f), "警告: 未找到 cookies.txt!");
        ImGui.TextWrapped("YouTube 现在会严格阻止无 Cookie 的播放器。请在浏览器中安装 VRCVideoCacher 扩展来本地同步 Cookie 数据。");

        if (ImGui.Button("Chrome/Edge/Brave 扩展")) {
            try {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo {
                    FileName = "https://chromewebstore.google.com/detail/vrcvideocacher-cookies-ex/kfgelknbegappcajiflgfbjbdpbpokge",
                    UseShellExecute = true
                });
            } catch { }
        }
        ImGui.SameLine();
        if (ImGui.Button("Firefox 扩展")) {
            try {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo {
                    FileName = "https://addons.mozilla.org/en-US/firefox/addon/vrcvideocachercookiesexporter/",
                    UseShellExecute = true
                });
            } catch { }
        }
      }

      ImGui.Spacing();
      ImGui.Spacing();

      // Server Sync
      ImGui.TextColored(new Vector4(0.7f, 0.9f, 1.0f, 1.0f), "服务器同步");
      ImGui.Separator();

      string serverUrl = _plugin.Config.ServerUrl;
      if (ImGui.InputText("服务器地址", ref serverUrl, 256)) {
        _plugin.Config.ServerUrl = serverUrl;
        _plugin.Config.Save();
      }
      ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f),
        "用于同步电视的后端服务器地址。");

      bool syncWithRoom = _plugin.Config.SyncWithRoom;
      if (ImGui.Checkbox("启用服务器同步模式", ref syncWithRoom)) {
        _plugin.Config.SyncWithRoom = syncWithRoom;
        _plugin.Config.Save();
      }
      if (ImGui.IsItemHovered()) {
        ImGui.SetTooltip("开启后自动同步房间 DJ 的播放。关闭后完全独立播放，不上传/不接收同步数据。");
      }

      if (syncWithRoom)
      {
          ImGui.Indent();
          bool forceSync = _plugin.Config.ForceSyncProgress;
          if (ImGui.Checkbox("强制同步视频进度", ref forceSync)) {
            _plugin.Config.ForceSyncProgress = forceSync;
            _plugin.Config.Save();
          }
          if (ImGui.IsItemHovered()) {
            ImGui.SetTooltip("跟随 DJ 的视频播放位置 (seek/pause/resume)。关闭后仅在 DJ 切换新 URL 时跟随，播放进度保持本地独立。");
          }
          ImGui.Unindent();
      }

      ImGui.Spacing();
      ImGui.Spacing();

      // Proxy
      ImGui.TextColored(new Vector4(0.7f, 0.9f, 1.0f, 1.0f), "代理设置");
      ImGui.Separator();

      string[] proxyTypes = new string[] { "关闭", "SOCKS5", "HTTP", "HTTPS" };
      string[] proxyValues = new string[] { "", "socks5", "http", "https" };
      int currentProxyIdx = Array.IndexOf(proxyValues, _plugin.Config.ProxyType);
      if (currentProxyIdx < 0) currentProxyIdx = 0;
      if (ImGui.Combo("代理类型", ref currentProxyIdx, proxyTypes, proxyTypes.Length)) {
        _plugin.Config.ProxyType = proxyValues[currentProxyIdx];
        _plugin.Config.Save();
      }

      bool proxyEnabled = currentProxyIdx > 0;

      string proxyHost = _plugin.Config.ProxyHost;
      if (ImGui.InputText("代理地址", ref proxyHost, 256)) {
        _plugin.Config.ProxyHost = proxyHost;
        _plugin.Config.Save();
      }

      int proxyPort = _plugin.Config.ProxyPort;
      if (ImGui.InputInt("端口", ref proxyPort)) {
        _plugin.Config.ProxyPort = Math.Clamp(proxyPort, 1, 65535);
        _plugin.Config.Save();
      }

      string proxyUser = _plugin.Config.ProxyUsername;
      if (ImGui.InputText("用户名 (选填)", ref proxyUser, 128)) {
        _plugin.Config.ProxyUsername = proxyUser;
        _plugin.Config.Save();
      }

      string proxyPass = _plugin.Config.ProxyPassword;
      if (ImGui.InputText("密码 (选填)", ref proxyPass, 128, ImGuiInputTextFlags.Password)) {
        _plugin.Config.ProxyPassword = proxyPass;
        _plugin.Config.Save();
      }

      if (ImGui.Button("测试代理")) {
        _proxyTestResult = "正在测试...";
        _proxyTestColor = new Vector4(1, 1, 1, 1);
        Task.Run(async () => {
          try {
            var handler = new HttpClientHandler();
            if (proxyEnabled)
            {
              handler.Proxy = new WebProxy($"{_plugin.Config.ProxyType}://{_plugin.Config.ProxyHost}:{_plugin.Config.ProxyPort}");
              if (!string.IsNullOrEmpty(_plugin.Config.ProxyUsername) && !string.IsNullOrEmpty(_plugin.Config.ProxyPassword))
              {
                handler.Proxy.Credentials = new NetworkCredential(_plugin.Config.ProxyUsername, _plugin.Config.ProxyPassword);
              }
            }
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
            var response = await client.GetAsync("https://x.com");
            if (response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.Redirect) {
              _proxyTestResult = "连接成功";
              _proxyTestColor = new Vector4(0.3f, 1f, 0.3f, 1);
            } else {
              _proxyTestResult = $"连接失败: HTTP {(int)response.StatusCode}";
              _proxyTestColor = new Vector4(1, 0.3f, 0.3f, 1);
            }
          } catch (Exception ex) {
            _proxyTestResult = $"连接失败: {ex.Message}";
            _proxyTestColor = new Vector4(1, 0.3f, 0.3f, 1);
          }
        });
      }

      if (!string.IsNullOrEmpty(_proxyTestResult)) {
        ImGui.SameLine();
        ImGui.TextColored(_proxyTestColor, _proxyTestResult);
      }

      ImGui.Spacing();
      ImGui.Spacing();

      // Help & Support
      ImGui.TextColored(new Vector4(0.7f, 0.9f, 1.0f, 1.0f), "帮助 & 支持");
      ImGui.Separator();

      if (ImGui.Button("教程视频 (如何放置电视)")) {
          try {
              System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo {
                  FileName = "https://www.youtube.com/watch?v=ZgLs2OJQ8ks",
                  UseShellExecute = true
              });
          } catch { }
      }

      ImGui.SameLine();

      if (ImGui.Button("加入 Discord 支持群")) {
          try {
              System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo {
                  FileName = "https://discord.gg/rtGXwMn7pX",
                  UseShellExecute = true
              });
          } catch { }
      }

      ImGui.Spacing();

      if (ImGui.Button("在 Ko-fi 支持开发者")) {
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
