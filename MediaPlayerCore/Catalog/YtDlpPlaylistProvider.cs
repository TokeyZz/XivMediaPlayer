using MediaPlayerCore.YtDlp;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;

namespace MediaPlayerCore.Catalog {
  /// <summary>
  /// Provides a catalog from a YouTube playlist (or any yt-dlp-supported playlist URL)
  /// by using yt-dlp --flat-playlist --dump-json to enumerate items.
  /// </summary>
  public class YtDlpPlaylistProvider : IMediaCatalogProvider {
    private readonly YtDlpManager _ytDlpManager;
    private string _playlistUrl;
    private MediaCatalog? _catalog;

    public string Name => "YouTube / yt-dlp Playlist";
    public string Description => "Browse playlists from YouTube and other yt-dlp-supported sites";
    public bool IsAvailable => _ytDlpManager.IsAvailable() && !string.IsNullOrEmpty(_playlistUrl);

    public string PlaylistUrl {
      get => _playlistUrl;
      set { _playlistUrl = value; _catalog = null; }
    }

    public YtDlpPlaylistProvider(YtDlpManager ytDlpManager, string playlistUrl = "") {
      _ytDlpManager = ytDlpManager;
      _playlistUrl = playlistUrl;
    }

    public async Task<MediaCatalog?> FetchCatalog() {
      if (_catalog != null) return _catalog;
      if (!IsAvailable) return null;

      return await LoadPlaylist();
    }

    public async Task<string?> ResolveStreamUrl(MediaCatalogItem item) {
      // Use yt-dlp to resolve the individual video URL to a direct stream
      return await _ytDlpManager.ResolveStreamUrl(item.Url);
    }

    public async Task Refresh() {
      _catalog = null;
      if (IsAvailable) {
        await LoadPlaylist();
      }
    }

    private async Task<MediaCatalog?> LoadPlaylist() {
      if (string.IsNullOrEmpty(_playlistUrl) || !_ytDlpManager.IsAvailable()) return null;

      return await Task.Run(() => {
        try {
          var catalog = new MediaCatalog {
            Name = "YouTube Playlist",
            Description = _playlistUrl,
          };

          // Use --flat-playlist to get metadata without downloading
          var psi = new ProcessStartInfo {
            FileName = _ytDlpManager.YtDlpPath,
            Arguments = $"--flat-playlist --dump-json \"{_playlistUrl}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
          };

          using var process = Process.Start(psi);
          if (process == null) return null;

          string output = process.StandardOutput.ReadToEnd();
          if (!process.WaitForExit(60000)) {
            try { process.Kill(); } catch { }
            return null;
          }

          // Each line is a JSON object for one playlist entry
          string[] lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
          foreach (string line in lines) {
            try {
              var obj = JObject.Parse(line.Trim());
              var item = new MediaCatalogItem {
                Title = obj["title"]?.ToString() ?? "Unknown",
                Url = obj["url"]?.ToString() ?? obj["webpage_url"]?.ToString() ?? "",
                DurationSeconds = obj["duration"]?.Value<double?>(),
                Uploader = obj["uploader"]?.ToString() ?? obj["channel"]?.ToString(),
                Thumbnail = obj["thumbnail"]?.ToString() ?? obj["thumbnails"]?.First?["url"]?.ToString(),
                Description = obj["description"]?.ToString(),
                IsLive = obj["is_live"]?.Value<bool>() ?? false,
                Category = catalog.Name,
              };

              // Fix relative URLs (yt-dlp flat-playlist sometimes returns video IDs)
              if (!string.IsNullOrEmpty(item.Url) && !item.Url.StartsWith("http")) {
                item.Url = "https://www.youtube.com/watch?v=" + item.Url;
              }

              if (!string.IsNullOrEmpty(item.Url)) {
                catalog.Items.Add(item);
              }
            } catch {
              // Skip unparseable lines
            }
          }

          // Try to get the playlist title from the first entry
          if (lines.Length > 0) {
            try {
              var first = JObject.Parse(lines[0].Trim());
              string? playlistTitle = first["playlist_title"]?.ToString()
                ?? first["playlist"]?.ToString();
              if (!string.IsNullOrEmpty(playlistTitle)) {
                catalog.Name = playlistTitle;
                // Update category on all items
                foreach (var item in catalog.Items) {
                  item.Category = playlistTitle;
                }
              }
            } catch { }
          }

          _catalog = catalog;
          return catalog;
        } catch {
          return null;
        }
      });
    }
  }
}
