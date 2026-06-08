using MediaPlayerCore.Catalog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace XivMediaPlayer.Providers
{
    public class HistoryMediaProvider : IMediaCatalogProvider
    {
        private readonly Configuration _config;

        public HistoryMediaProvider(Configuration config)
        {
            _config = config;
        }

        public string Name => "History";

        public string Description => "Pick up where you left off.";

        public bool IsAvailable => true;

        public Task<MediaCatalog?> FetchCatalog()
        {
            var catalog = new MediaCatalog
            {
                Name = "Watch History",
                Description = "Media you have watched recently.",
                Author = "XivMediaPlayer"
            };

            var sortedHistory = _config.WatchHistory.Values
                .OrderByDescending(x => x.LastPlayed)
                .ToList();

            foreach (var entry in sortedHistory)
            {
                // Only show entries that have a valid timecode > 5 seconds
                if (entry.TimecodeMs > 5000)
                {
                    var item = new MediaCatalogItem
                    {
                        Url = entry.Url,
                        Title = entry.Title,
                        StartTimeMs = entry.TimecodeMs,
                        Uploader = "History"
                    };
                    catalog.Items.Add(item);
                }
            }

            return Task.FromResult<MediaCatalog?>(catalog);
        }

        public Task Refresh()
        {
            return Task.CompletedTask;
        }

        public Task<string?> ResolveStreamUrl(MediaCatalogItem item)
        {
            return Task.FromResult<string?>(item.Url);
        }
    }
}
