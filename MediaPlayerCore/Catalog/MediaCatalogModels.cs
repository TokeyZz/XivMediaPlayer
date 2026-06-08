using Newtonsoft.Json;
using System;
using System.Collections.Generic;

namespace MediaPlayerCore.Catalog {
  /// <summary>
  /// A single playable media item in a catalog.
  /// </summary>
  public class MediaCatalogItem {
    [JsonProperty("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [JsonProperty("title")]
    public string Title { get; set; } = "";

    [JsonProperty("url")]
    public string Url { get; set; } = "";

    [JsonProperty("thumbnail")]
    public string? Thumbnail { get; set; }

    [JsonProperty("description")]
    public string? Description { get; set; }

    [JsonProperty("duration")]
    public double? DurationSeconds { get; set; }

    [JsonProperty("uploader")]
    public string? Uploader { get; set; }

    [JsonProperty("category")]
    public string? Category { get; set; }

    [JsonProperty("tags")]
    public List<string>? Tags { get; set; }

    [JsonProperty("isLive")]
    public bool IsLive { get; set; }

    [JsonProperty("startTimeMs")]
    public long StartTimeMs { get; set; }

    /// <summary>
    /// Formatted start time string (e.g. "1:23:45").
    /// </summary>
    [JsonIgnore]
    public string ProgressFormatted {
      get {
        if (StartTimeMs <= 0) return "";
        var ts = TimeSpan.FromMilliseconds(StartTimeMs);
        return ts.TotalHours >= 1
          ? ts.ToString(@"h\:mm\:ss")
          : ts.ToString(@"mm\:ss");
      }
    }

    /// <summary>
    /// Formatted duration string (e.g. "1:23:45").
    /// </summary>
    [JsonIgnore]
    public string DurationFormatted {
      get {
        if (DurationSeconds == null || DurationSeconds <= 0) return IsLive ? "LIVE" : "";
        var ts = TimeSpan.FromSeconds(DurationSeconds.Value);
        return ts.TotalHours >= 1
          ? ts.ToString(@"h\:mm\:ss")
          : ts.ToString(@"mm\:ss");
      }
    }
  }

  /// <summary>
  /// A named collection of media items, optionally organized into categories.
  /// </summary>
  public class MediaCatalog {
    [JsonProperty("name")]
    public string Name { get; set; } = "Untitled Catalog";

    [JsonProperty("description")]
    public string? Description { get; set; }

    [JsonProperty("author")]
    public string? Author { get; set; }

    [JsonProperty("version")]
    public int Version { get; set; } = 1;

    [JsonProperty("items")]
    public List<MediaCatalogItem> Items { get; set; } = new List<MediaCatalogItem>();

    /// <summary>
    /// Returns all unique categories found in items.
    /// </summary>
    [JsonIgnore]
    public IEnumerable<string> Categories {
      get {
        var seen = new HashSet<string>();
        foreach (var item in Items) {
          string cat = item.Category ?? "Uncategorized";
          if (seen.Add(cat)) yield return cat;
        }
      }
    }

    /// <summary>
    /// Returns items filtered by category.
    /// </summary>
    public IEnumerable<MediaCatalogItem> GetByCategory(string category) {
      foreach (var item in Items) {
        string cat = item.Category ?? "Uncategorized";
        if (string.Equals(cat, category, StringComparison.OrdinalIgnoreCase)) {
          yield return item;
        }
      }
    }

    /// <summary>
    /// Search items by title or description.
    /// </summary>
    public IEnumerable<MediaCatalogItem> Search(string query) {
      if (string.IsNullOrWhiteSpace(query)) {
        foreach (var item in Items) yield return item;
        yield break;
      }
      string q = query.ToLowerInvariant();
      foreach (var item in Items) {
        if ((item.Title?.ToLowerInvariant().Contains(q) == true) ||
          (item.Description?.ToLowerInvariant().Contains(q) == true) ||
          (item.Uploader?.ToLowerInvariant().Contains(q) == true)) {
          yield return item;
        }
      }
    }
  }
}
