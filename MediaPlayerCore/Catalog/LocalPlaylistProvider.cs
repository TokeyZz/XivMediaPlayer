using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace MediaPlayerCore.Catalog {
  /// <summary>
  /// Provides catalogs from local JSON playlist files.
  /// Users can create, share, and import .json playlist files.
  /// 
  /// Expected JSON format:
  /// {
  ///  "name": "My Playlist",
  ///  "description": "A collection of streams",
  ///  "author": "Username",
  ///  "items": [
  ///   { "title": "Stream 1", "url": "https://...", "category": "Music", "duration": 300 },
  ///   { "title": "Stream 2", "url": "https://...", "category": "Gaming" }
  ///  ]
  /// }
  /// </summary>
  public class LocalPlaylistProvider : IMediaCatalogProvider {
    private readonly string _playlistDirectory;
    private MediaCatalog? _mergedCatalog;

    public string Name => "Local Playlists";
    public string Description => "Media catalogs from local JSON files";
    public bool IsAvailable => Directory.Exists(_playlistDirectory);

    /// <summary>
    /// Creates a provider that reads .json playlist files from the given directory.
    /// </summary>
    public LocalPlaylistProvider(string playlistDirectory) {
      _playlistDirectory = playlistDirectory;
      if (!Directory.Exists(_playlistDirectory)) {
        try { Directory.CreateDirectory(_playlistDirectory); } catch { }
      }
    }

    public async Task<MediaCatalog?> FetchCatalog() {
      if (_mergedCatalog != null) return _mergedCatalog;
      return await LoadAllPlaylists();
    }

    public Task<string?> ResolveStreamUrl(MediaCatalogItem item) {
      // Local playlists contain direct URLs — no additional resolution needed
      return Task.FromResult<string?>(item.Url);
    }

    public async Task Refresh() {
      _mergedCatalog = null;
      await LoadAllPlaylists();
    }

    /// <summary>
    /// Returns a list of all playlist files found in the directory.
    /// </summary>
    public string[] GetPlaylistFiles() {
      if (!Directory.Exists(_playlistDirectory)) return Array.Empty<string>();
      return Directory.GetFiles(_playlistDirectory, "*.json", SearchOption.TopDirectoryOnly);
    }

    /// <summary>
    /// Loads a single playlist file.
    /// </summary>
    public static MediaCatalog? LoadPlaylist(string filePath) {
      try {
        string json = File.ReadAllText(filePath);
        return JsonConvert.DeserializeObject<MediaCatalog>(json);
      } catch {
        return null;
      }
    }

    /// <summary>
    /// Saves a catalog to a JSON file.
    /// </summary>
    public static void SavePlaylist(string filePath, MediaCatalog catalog) {
      string json = JsonConvert.SerializeObject(catalog, Formatting.Indented);
      File.WriteAllText(filePath, json);
    }

    /// <summary>
    /// Creates a sample playlist file for reference.
    /// </summary>
    public void CreateSamplePlaylist() {
      var sample = new MediaCatalog {
        Name = "Sample Playlist",
        Description = "Example playlist — edit or replace this file!",
        Author = "XivMediaPlayer",
        Items = new List<MediaCatalogItem> {
          new MediaCatalogItem {
            Title = "Big Buck Bunny",
            Url = "https://www.youtube.com/watch?v=aqz-KE-bpKQ",
            Category = "Animation",
            Description = "A short animated film by the Blender Foundation.",
            DurationSeconds = 596,
          },
          new MediaCatalogItem {
            Title = "Sintel",
            Url = "https://www.youtube.com/watch?v=eRsGyueVLvQ",
            Category = "Animation",
            Description = "An open-source animated short film by the Blender Foundation.",
            DurationSeconds = 888,
          },
          new MediaCatalogItem {
            Title = "Lofi Girl Radio",
            Url = "https://www.youtube.com/watch?v=jfKfPfyJRdk",
            Category = "Music",
            Description = "Lofi hip hop radio — beats to relax/study to",
            IsLive = true,
          },
        }
      };
      string path = Path.Combine(_playlistDirectory, "sample_playlist.json");
      if (!File.Exists(path)) {
        SavePlaylist(path, sample);
      }
    }

    private Task<MediaCatalog> LoadAllPlaylists() {
      return Task.Run(() => {
        var merged = new MediaCatalog {
          Name = "Local Playlists",
          Description = "All local playlist files merged",
        };

        string[] files = GetPlaylistFiles();
        foreach (string file in files) {
          var playlist = LoadPlaylist(file);
          if (playlist?.Items != null) {
            // Tag items with their source playlist as a category prefix
            foreach (var item in playlist.Items) {
              if (string.IsNullOrEmpty(item.Category)) {
                item.Category = playlist.Name;
              }
              merged.Items.Add(item);
            }
          }
        }

        _mergedCatalog = merged;
        return merged;
      });
    }
  }
}
