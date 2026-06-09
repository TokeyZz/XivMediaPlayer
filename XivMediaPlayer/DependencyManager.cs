using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;

namespace XivMediaPlayer
{
    public class DependencyManager
    {
        private readonly IPluginLog _pluginLog;
        private readonly string _version;
        
        public bool IsReady { get; private set; }
        public bool IsDownloading { get; private set; }
        public float DownloadProgress { get; private set; }
        public string Status { get; private set; } = "Initializing...";
        public bool HasError { get; private set; }
        public string ErrorMessage { get; private set; } = string.Empty;

        public string DependenciesDir { get; private set; }

        public DependencyManager(string configDir, string pluginDir, string version, IPluginLog pluginLog)
        {
            _version = version;
            _pluginLog = pluginLog;

            // Check if dependencies exist in the plugin folder (e.g. for local developer compiles)
            if (Directory.Exists(Path.Combine(pluginDir, "cef")) && Directory.Exists(Path.Combine(pluginDir, "libvlc")))
            {
                DependenciesDir = pluginDir;
                CheckDependencies();
                if (IsReady) Status = "Ready (Local Build)";
            }
            else
            {
                DependenciesDir = Path.Combine(configDir, "Dependencies");
                CheckDependencies();
            }
        }

        private void CheckDependencies()
        {
            string cefPath = Path.Combine(DependenciesDir, "cef", "libcef.dll");
            string vlcPath = Path.Combine(DependenciesDir, "libvlc", "win-x64", "libvlc.dll");
            string ffmpegPath = Path.Combine(DependenciesDir, "ffmpeg.exe");

            if (File.Exists(cefPath) && File.Exists(vlcPath))
            {
                IsReady = true;
                Status = "Ready";
            }
            else
            {
                IsReady = false;
                Status = "Missing media dependencies. Click to download.";
            }

            if (!File.Exists(ffmpegPath) && !IsDownloading)
            {
                _pluginLog.Information("ffmpeg.exe not found! Auto-downloading...");
                _ = Task.Run(async () => await DownloadFFmpegAsync());
            }
        }

        public async Task DownloadDependenciesAsync()
        {
            if (IsDownloading || IsReady) return;

            IsDownloading = true;
            HasError = false;
            DownloadProgress = 0f;
            Status = "Starting download...";

            try
            {
                Directory.CreateDirectory(DependenciesDir);
                string zipPath = Path.Combine(DependenciesDir, "Dependencies.zip");

                // Download URL for the dependencies zip from the GitHub release
                string url = $"https://github.com/Sebane1/XivMediaPlayer/releases/download/{_version}/XivMediaPlayer-Dependencies.zip";
                
                _pluginLog.Information($"Downloading dependencies from: {url}");

                using (var client = new HttpClient())
                {
                    client.DefaultRequestHeaders.UserAgent.ParseAdd("XivMediaPlayer-Plugin");
                    
                    using (var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
                    {
                        response.EnsureSuccessStatusCode();

                        var totalBytes = response.Content.Headers.ContentLength ?? -1L;
                        var canReportProgress = totalBytes != -1;

                        using (var stream = await response.Content.ReadAsStreamAsync())
                        using (var fileStream = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true))
                        {
                            var buffer = new byte[8192];
                            var totalRead = 0L;
                            var isMoreToRead = true;

                            do
                            {
                                var read = await stream.ReadAsync(buffer, 0, buffer.Length);
                                if (read == 0)
                                {
                                    isMoreToRead = false;
                                }
                                else
                                {
                                    await fileStream.WriteAsync(buffer, 0, read);
                                    totalRead += read;

                                    if (canReportProgress)
                                    {
                                        DownloadProgress = (float)totalRead / totalBytes;
                                        Status = $"Downloading: {(totalRead / 1024 / 1024)}MB / {(totalBytes / 1024 / 1024)}MB";
                                    }
                                    else
                                    {
                                        Status = $"Downloading: {(totalRead / 1024 / 1024)}MB";
                                    }
                                }
                            } while (isMoreToRead);
                        }
                    }
                }

                Status = "Extracting dependencies... (This may take a minute)";
                _pluginLog.Information("Extracting Dependencies.zip...");

                await Task.Run(() =>
                {
                    // Clean up old folders if they exist
                    string cefDir = Path.Combine(DependenciesDir, "cef");
                    string vlcDir = Path.Combine(DependenciesDir, "libvlc");
                    
                    try {
                        if (Directory.Exists(cefDir)) Directory.Delete(cefDir, true);
                        if (Directory.Exists(vlcDir)) Directory.Delete(vlcDir, true);

                        ZipFile.ExtractToDirectory(zipPath, DependenciesDir, true);
                    } catch (Exception e) when (e is UnauthorizedAccessException || e is IOException) {
                        // DLLs are locked because they are already loaded into the game process memory.
                        // (Usually happens if the user reloaded the plugin via /xlplugins)
                        // We can safely silently ignore this because it means the files are already successfully installed!
                        _pluginLog.Warning("Dependencies are currently locked by the process. Skipping extraction and assuming existing files are valid.");
                    }
                });

                _pluginLog.Information("Extraction complete.");
                
                // Cleanup zip
                if (File.Exists(zipPath))
                {
                    File.Delete(zipPath);
                }

                IsReady = true;
                Status = "Dependencies installed successfully!";
            }
            catch (Exception ex)
            {
                HasError = true;
                ErrorMessage = ex.Message;
                Status = "Download failed.";
                _pluginLog.Error(ex, "Failed to download media dependencies.");
            }
            finally
            {
                IsDownloading = false;
            }

            if (!File.Exists(Path.Combine(DependenciesDir, "ffmpeg.exe")))
            {
                await DownloadFFmpegAsync();
            }
        }

        private async Task DownloadFFmpegAsync()
        {
            try
            {
                Status = "Downloading FFmpeg...";
                _pluginLog.Information("Downloading FFmpeg...");
                string zipPath = Path.Combine(DependenciesDir, "ffmpeg.zip");
                string url = "https://github.com/ffbinaries/ffbinaries-prebuilt/releases/download/v4.4.1/ffmpeg-4.4.1-win-64.zip";

                using (var client = new HttpClient())
                using (var response = await client.GetAsync(url))
                using (var stream = await response.Content.ReadAsStreamAsync())
                using (var fileStream = new FileStream(zipPath, FileMode.Create, FileAccess.Write))
                {
                    await stream.CopyToAsync(fileStream);
                }

                Status = "Extracting FFmpeg...";
                await Task.Run(() =>
                {
                    ZipFile.ExtractToDirectory(zipPath, DependenciesDir, true);
                    File.Delete(zipPath);
                });

                CheckDependencies();
            }
            catch (Exception ex)
            {
                _pluginLog.Error(ex, "Failed to download FFmpeg.");
            }
        }
    }
}
