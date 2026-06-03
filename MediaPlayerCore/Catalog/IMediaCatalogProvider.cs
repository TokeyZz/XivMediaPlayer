using System;
using System.Threading.Tasks;

namespace MediaPlayerCore.Catalog {
  /// <summary>
  /// Interface for media catalog providers.
  /// Implement this to add support for new media sources (local playlists, YouTube, vr-m.net, etc.).
  /// </summary>
  public interface IMediaCatalogProvider {
    /// <summary>
    /// Display name for this provider (shown in the browser UI).
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Short description of what this provider offers.
    /// </summary>
    string Description { get; }

    /// <summary>
    /// Whether this provider is currently available/configured.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Fetches the catalog from this provider.
    /// May involve network requests, file reads, or process execution.
    /// </summary>
    Task<MediaCatalog?> FetchCatalog();

    /// <summary>
    /// Resolves a catalog item's URL to a direct stream URL playable by VLC.
    /// Some providers may need to do additional resolution (e.g. yt-dlp).
    /// If no resolution is needed, return the item's URL directly.
    /// </summary>
    Task<string?> ResolveStreamUrl(MediaCatalogItem item);

    /// <summary>
    /// Refreshes the catalog (e.g. re-fetches from remote source).
    /// </summary>
    Task Refresh();
  }
}
