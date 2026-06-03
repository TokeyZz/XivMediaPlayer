using Newtonsoft.Json;
using System.Diagnostics;
using System.Text;

namespace MediaPlayerCore.YtDlp {
  /// <summary>
  /// Metadata returned by yt-dlp --dump-json for a given URL.
  /// </summary>
  public class YtDlpMetadata {
    [JsonProperty("title")]
    public string? Title { get; set; }

    [JsonProperty("duration")]
    public double? Duration { get; set; }

    [JsonProperty("thumbnail")]
    public string? Thumbnail { get; set; }

    [JsonProperty("uploader")]
    public string? Uploader { get; set; }

    [JsonProperty("url")]
    public string? Url { get; set; }

    [JsonProperty("webpage_url")]
    public string? WebpageUrl { get; set; }

    [JsonProperty("is_live")]
    public bool? IsLive { get; set; }

    [JsonProperty("ext")]
    public string? Extension { get; set; }
  }

  /// <summary>
  /// Manages yt-dlp binary execution for resolving stream URLs and fetching metadata.
  /// Supports YouTube, Twitch, and 1000+ other sites.
  /// </summary>
  public class YtDlpManager {
    private string _ytDlpPath;
    private int _preferredMaxHeight;
    private readonly object _lock = new object();

    public event EventHandler<string>? OnStatusUpdate;
    public event EventHandler<Exception>? OnError;

    /// <summary>
    /// Path to the yt-dlp executable.
    /// </summary>
    public string YtDlpPath {
      get => _ytDlpPath;
      set => _ytDlpPath = value;
    }

    /// <summary>
    /// Preferred max video height for quality selection (e.g. 360, 480, 720, 1080).
    /// 0 = best available.
    /// </summary>
    public int PreferredMaxHeight {
      get => _preferredMaxHeight;
      set => _preferredMaxHeight = value;
    }

    public YtDlpManager(string? ytDlpPath = null, int preferredMaxHeight = 720) {
      _ytDlpPath = ytDlpPath ?? FindYtDlp();
      _preferredMaxHeight = preferredMaxHeight;
    }

    /// <summary>
    /// Returns true if the yt-dlp binary exists at the configured path.
    /// </summary>
    public bool IsAvailable() {
      return !string.IsNullOrEmpty(_ytDlpPath) && File.Exists(_ytDlpPath);
    }

    /// <summary>
    /// Resolves a URL to a direct stream URL suitable for VLC playback.
    /// Returns null if resolution fails.
    /// </summary>
    public async Task<string?> ResolveStreamUrl(string url) {
      if (!IsAvailable()) {
        OnError?.Invoke(this, new FileNotFoundException("yt-dlp binary not found at: " + _ytDlpPath));
        return null;
      }

      try {
        OnStatusUpdate?.Invoke(this, "Resolving stream URL...");

        string formatArg = _preferredMaxHeight > 0
          ? $"best[height<={_preferredMaxHeight}]/best"
          : "best";

        string result = await RunYtDlp($"--get-url -f \"{formatArg}\" \"{url}\"");
        string? streamUrl = result?.Trim().Split('\n').FirstOrDefault()?.Trim();

        if (!string.IsNullOrEmpty(streamUrl)) {
          OnStatusUpdate?.Invoke(this, "Stream URL resolved.");
          return streamUrl;
        }

        OnError?.Invoke(this, new Exception("yt-dlp returned empty URL for: " + url));
        return null;
      } catch (Exception e) {
        OnError?.Invoke(this, e);
        return null;
      }
    }

    /// <summary>
    /// Fetches metadata (title, duration, uploader, thumbnail, etc.) for a URL.
    /// Returns null if fetching fails.
    /// </summary>
    public async Task<YtDlpMetadata?> GetMetadata(string url) {
      if (!IsAvailable()) {
        return null;
      }

      try {
        string result = await RunYtDlp($"--dump-json --no-download \"{url}\"");
        if (!string.IsNullOrEmpty(result)) {
          return JsonConvert.DeserializeObject<YtDlpMetadata>(result);
        }
      } catch (Exception e) {
        OnError?.Invoke(this, e);
      }
      return null;
    }

    /// <summary>
    /// Resolves a URL to multiple quality stream URLs.
    /// Returns an array where index 0 = audio-only, then ascending quality.
    /// Falls back to single URL if format listing fails.
    /// </summary>
    public async Task<string[]> ResolveMultiQualityUrls(string url) {
      if (!IsAvailable()) {
        return Array.Empty<string>();
      }

      try {
        // Try to get URLs at specific quality levels
        var qualities = new[] { 360, 480, 720, 1080 };
        var urls = new List<string>();

        // Audio only
        string? audioUrl = await ResolveUrlWithFormat(url, "bestaudio");
        urls.Add(audioUrl ?? "");

        // Video at each quality level
        foreach (int height in qualities) {
          string? qualityUrl = await ResolveUrlWithFormat(url, $"best[height<={height}]/best");
          urls.Add(qualityUrl ?? "");
        }

        // If we got at least one valid URL, return the array
        if (urls.Any(u => !string.IsNullOrEmpty(u))) {
          return urls.ToArray();
        }

        // Fallback: just get best URL
        string? bestUrl = await ResolveStreamUrl(url);
        if (!string.IsNullOrEmpty(bestUrl)) {
          return new string[] { bestUrl, bestUrl, bestUrl, bestUrl, bestUrl };
        }
      } catch (Exception e) {
        OnError?.Invoke(this, e);
      }
      return Array.Empty<string>();
    }

    /// <summary>
    /// Checks if the given URL is likely supported by yt-dlp
    /// (not a raw stream or local file).
    /// </summary>
    public static bool IsUrlSupported(string url) {
      if (string.IsNullOrWhiteSpace(url)) return false;
      // Don't try yt-dlp on raw streams or local files
      if (url.StartsWith("rtmp://", StringComparison.OrdinalIgnoreCase)) return false;
      if (url.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase)) return false;
      if (File.Exists(url)) return false;
      // Must be an HTTP URL
      return url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Attempts to self-update yt-dlp via yt-dlp -U.
    /// </summary>
    public async Task<bool> SelfUpdate() {
      if (!IsAvailable()) return false;

      try {
        OnStatusUpdate?.Invoke(this, "Updating yt-dlp...");
        string result = await RunYtDlp("-U");
        OnStatusUpdate?.Invoke(this, "yt-dlp update complete.");
        return true;
      } catch (Exception e) {
        OnError?.Invoke(this, e);
        return false;
      }
    }

    #region Private Helpers

    private async Task<string?> ResolveUrlWithFormat(string url, string format) {
      try {
        string result = await RunYtDlp($"--get-url -f \"{format}\" \"{url}\"");
        return result?.Trim().Split('\n').FirstOrDefault()?.Trim();
      } catch {
        return null;
      }
    }

    private async Task<string> RunYtDlp(string arguments) {
      return await Task.Run(() => {
        lock (_lock) {
          var psi = new ProcessStartInfo {
            FileName = _ytDlpPath,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
          };

          using var process = Process.Start(psi);
          if (process == null) {
            throw new Exception("Failed to start yt-dlp process");
          }

          string output = process.StandardOutput.ReadToEnd();
          string error = process.StandardError.ReadToEnd();

          // Timeout after 30 seconds
          if (!process.WaitForExit(30000)) {
            try { process.Kill(); } catch { }
            throw new TimeoutException("yt-dlp timed out after 30 seconds");
          }

          if (process.ExitCode != 0 && string.IsNullOrEmpty(output)) {
            throw new Exception($"yt-dlp exited with code {process.ExitCode}: {error}");
          }

          return output;
        }
      });
    }

    /// <summary>
    /// Attempts to locate yt-dlp on the system PATH or in common locations.
    /// </summary>
    private static string FindYtDlp() {
      // Check PATH
      string? pathResult = FindOnPath("yt-dlp.exe");
      if (pathResult != null) return pathResult;

      pathResult = FindOnPath("yt-dlp");
      if (pathResult != null) return pathResult;

      // Common install locations on Windows
      string[] commonPaths = new[] {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "yt-dlp", "yt-dlp.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "yt-dlp", "yt-dlp.exe"),
        @"C:\yt-dlp\yt-dlp.exe",
      };

      foreach (string path in commonPaths) {
        if (File.Exists(path)) return path;
      }

      return "yt-dlp.exe"; // Fallback — will fail IsAvailable() if not on PATH
    }

    private static string? FindOnPath(string fileName) {
      string? pathVar = Environment.GetEnvironmentVariable("PATH");
      if (pathVar == null) return null;

      foreach (string dir in pathVar.Split(Path.PathSeparator)) {
        string fullPath = Path.Combine(dir.Trim(), fileName);
        if (File.Exists(fullPath)) return fullPath;
      }
      return null;
    }

    #endregion
  }
}
