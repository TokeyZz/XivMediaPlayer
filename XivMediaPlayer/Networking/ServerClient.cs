using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using XivMediaPlayer.Networking.Models;
using Dalamud.Plugin.Services;

namespace XivMediaPlayer.Networking
{
    public class ServerClient : IDisposable
    {
        private readonly HttpClient _httpClient;
        private readonly string _baseUrl;
        private readonly IPluginLog _log;

        public ServerClient(string baseUrl, IPluginLog log)
        {
            _baseUrl = baseUrl;
            _log = log;
            _httpClient = new HttpClient();
        }

        public async Task<List<TvPlacement>> GetTvsForRoomAsync(string locationKey)
        {
            try
            {
                var response = await _httpClient.GetAsync($"{_baseUrl}/api/rooms/{Uri.EscapeDataString(locationKey)}/tvs");
                if (response.IsSuccessStatusCode)
                {
                    var tvs = await response.Content.ReadFromJsonAsync<List<TvPlacement>>();
                    return tvs ?? new List<TvPlacement>();
                }
            }
            catch (Exception ex)
            {
                _log.Error(ex, $"Failed to get TVs for room {locationKey}");
            }
            return new List<TvPlacement>();
        }

        public async Task<TvPlacement> RegisterTvAsync(string locationKey, TvPlacement placement)
        {
            try
            {
                var response = await _httpClient.PostAsJsonAsync($"{_baseUrl}/api/rooms/{Uri.EscapeDataString(locationKey)}/tvs", placement);
                if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                {
                    throw new UnauthorizedAccessException("This TV is locked by its owner and cannot be moved.");
                }

                if (response.IsSuccessStatusCode)
                {
                    return await response.Content.ReadFromJsonAsync<TvPlacement>();
                }
            }
            catch (Exception ex)
            {
                _log.Error(ex, $"Failed to register TV for room {locationKey}");
                throw;
            }
            return null;
        }

        public async Task<RoomMediaStateSync> GetMediaStateAsync(string locationKey)
        {
            try
            {
                var response = await _httpClient.GetAsync($"{_baseUrl}/api/rooms/{Uri.EscapeDataString(locationKey)}/media");
                if (response.IsSuccessStatusCode)
                {
                    return await response.Content.ReadFromJsonAsync<RoomMediaStateSync>();
                }
            }
            catch (Exception ex)
            {
                _log.Error(ex, $"Failed to get media state for room {locationKey}");
            }
            return null;
        }

        public async Task<RoomMediaStateSync> UpdateMediaStateAsync(string locationKey, RoomMediaStateSync state)
        {
            try
            {
                var response = await _httpClient.PostAsJsonAsync($"{_baseUrl}/api/rooms/{Uri.EscapeDataString(locationKey)}/media", state);
                if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                {
                    throw new UnauthorizedAccessException("The TV in this room is locked by its owner.");
                }

                if (response.IsSuccessStatusCode)
                {
                    return await response.Content.ReadFromJsonAsync<RoomMediaStateSync>();
                }
            }
            catch (Exception ex)
            {
                _log.Error(ex, $"Failed to update media state for room {locationKey}");
                throw; // Rethrow so the plugin can catch and handle it
            }
            return null;
        }

        public void Dispose()
        {
            _httpClient.Dispose();
        }
    }
}
