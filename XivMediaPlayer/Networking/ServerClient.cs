using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using XivMediaPlayer.Shared;
using XivMediaPlayer.Shared.Models;
using Dalamud.Plugin.Services;

namespace XivMediaPlayer.Networking
{
    public class ServerClient : IDisposable
    {
        private readonly HttpClient _httpClient;
        private readonly string _baseUrl;
        public string BaseUrl => _baseUrl;
        private readonly IPluginLog _log;

        public ServerClient(string baseUrl, IPluginLog log)
        {
            _baseUrl = baseUrl;
            _log = log;
            _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        }

        public async Task<long?> GetServerTimeAsync()
        {
            try
            {
                var response = await _httpClient.GetAsync($"{_baseUrl}/api/rooms/time");
                if (response.IsSuccessStatusCode)
                    return await response.Content.ReadFromJsonAsync<long>();
            }
            catch (Exception ex) { _log.Error(ex, "[Net] Failed to fetch server time"); }
            return null;
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

        public async Task<bool> DeleteTvAsync(string locationKey, string tvId, string ownerId, bool bypassLock)
        {
            try
            {
                var response = await _httpClient.DeleteAsync($"{_baseUrl}/api/rooms/{Uri.EscapeDataString(locationKey)}/tvs/{Uri.EscapeDataString(tvId)}?ownerId={Uri.EscapeDataString(ownerId)}");
                if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                {
                    throw new UnauthorizedAccessException("Cannot delete TV: It is locked by its owner.");
                }
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                _log.Error(ex, $"Failed to delete TV for room {locationKey}");
                throw;
            }
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

                if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
                {
                    throw new InvalidOperationException("You are no longer the media owner.");
                }

                if (response.StatusCode == System.Net.HttpStatusCode.BadRequest)
                {
                    var errorMsg = await response.Content.ReadAsStringAsync();
                    throw new ArgumentException(errorMsg);
                }

                if (response.IsSuccessStatusCode)
                {
                    return await response.Content.ReadFromJsonAsync<RoomMediaStateSync>();
                }
            }
            catch (InvalidOperationException) { throw; }
            catch (UnauthorizedAccessException) { throw; }
            catch (Exception ex)
            {
                _log.Error(ex, $"Failed to update media state for room {locationKey}");
                throw;
            }
            return null;
        }

        public async Task<List<TvPlacement>> GetTvsBatchAsync(List<string> locationKeys)
        {
            try
            {
                var response = await _httpClient.PostAsJsonAsync($"{_baseUrl}/api/rooms/batch/tvs", locationKeys);
                if (response.IsSuccessStatusCode)
                {
                    var tvs = await response.Content.ReadFromJsonAsync<List<TvPlacement>>();
                    return tvs ?? new List<TvPlacement>();
                }
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Failed to get TVs in batch");
            }
            return new List<TvPlacement>();
        }

        public async Task<List<RoomMediaStateSync>> GetMediaStatesBatchAsync(List<string> locationKeys)
        {
            try
            {
                var response = await _httpClient.PostAsJsonAsync($"{_baseUrl}/api/rooms/batch/media", locationKeys);
                if (response.IsSuccessStatusCode)
                {
                    var states = await response.Content.ReadFromJsonAsync<List<RoomMediaStateSync>>();
                    return states ?? new List<RoomMediaStateSync>();
                }
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Failed to get media states in batch");
            }
            return new List<RoomMediaStateSync>();
        }

        /// <summary>
        /// POST with retry. Takes a factory function so each attempt creates a fresh HttpRequestMessage.
        /// </summary>
        private async Task<HttpResponseMessage?> SendPostWithRetryAsync(string url, object body, int maxRetries = 2)
        {
            for (int attempt = 0; attempt <= maxRetries; attempt++)
            {
                try
                {
                    if (attempt > 0)
                        _log.Warning($"[Net] Retry: attempt={attempt}, url={url}");
                    var request = new HttpRequestMessage(HttpMethod.Post, url)
                    {
                        Content = JsonContent.Create(body)
                    };
                    var response = await _httpClient.SendAsync(request);
                    if ((int)response.StatusCode == 429 || (int)response.StatusCode >= 500)
                    {
                        if (attempt < maxRetries) { await Task.Delay(500 * (attempt + 1)); continue; }
                        return response;
                    }
                    return response;
                }
                catch (TaskCanceledException) { if (attempt >= maxRetries) return null; await Task.Delay(500 * (attempt + 1)); }
                catch (HttpRequestException) { if (attempt >= maxRetries) return null; await Task.Delay(500 * (attempt + 1)); }
            }
            return null;
        }

        public async Task<ApiResult<ClaimDjResponse>> ClaimDjAsync(string locationKey, ClaimDjRequest request)
        {
            try
            {
                var url = $"{_baseUrl}/api/rooms/{Uri.EscapeDataString(locationKey)}/claim-dj";
                var response = await SendPostWithRetryAsync(url, request);
                if (response == null)
                    return ApiResult<ClaimDjResponse>.Fail("[Net] ClaimDj: no response after retries");
                if (response.IsSuccessStatusCode)
                    return await response.Content.ReadFromJsonAsync<ApiResult<ClaimDjResponse>>()
                        ?? ApiResult<ClaimDjResponse>.Fail("Empty response");
                if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
                    return await response.Content.ReadFromJsonAsync<ApiResult<ClaimDjResponse>>()
                        ?? ApiResult<ClaimDjResponse>.Fail("DJ conflict");
                return ApiResult<ClaimDjResponse>.Fail($"Unexpected status: {response.StatusCode}");
            }
            catch (Exception ex) { _log.Error(ex, $"[Net] ClaimDj failed for {locationKey}"); return ApiResult<ClaimDjResponse>.Fail(ex.Message); }
        }

        public async Task<ApiResult<HeartbeatResponse>> HeartbeatAsync(string locationKey, HeartbeatRequest request)
        {
            try
            {
                var url = $"{_baseUrl}/api/rooms/{Uri.EscapeDataString(locationKey)}/heartbeat";
                var response = await SendPostWithRetryAsync(url, request);
                if (response == null)
                    return ApiResult<HeartbeatResponse>.Fail("[Net] Heartbeat: no response after retries");
                if (response.IsSuccessStatusCode)
                    return await response.Content.ReadFromJsonAsync<ApiResult<HeartbeatResponse>>()
                        ?? ApiResult<HeartbeatResponse>.Fail("Empty response");
                return ApiResult<HeartbeatResponse>.Fail($"Unexpected status: {response.StatusCode}");
            }
            catch (Exception ex) { _log.Error(ex, $"[Net] Heartbeat failed for {locationKey}"); return ApiResult<HeartbeatResponse>.Fail(ex.Message); }
        }

        public async Task<ApiResult<ClaimDjResponse>> ReleaseDjAsync(string locationKey, ReleaseDjRequest request)
        {
            try
            {
                var url = $"{_baseUrl}/api/rooms/{Uri.EscapeDataString(locationKey)}/release-dj";
                var response = await SendPostWithRetryAsync(url, request);
                if (response == null)
                    return ApiResult<ClaimDjResponse>.Fail("[Net] ReleaseDj: no response after retries");
                if (response.IsSuccessStatusCode)
                    return await response.Content.ReadFromJsonAsync<ApiResult<ClaimDjResponse>>()
                        ?? ApiResult<ClaimDjResponse>.Fail("Empty response");
                return ApiResult<ClaimDjResponse>.Fail($"Unexpected status: {response.StatusCode}");
            }
            catch (Exception ex) { _log.Error(ex, $"[Net] ReleaseDj failed for {locationKey}"); return ApiResult<ClaimDjResponse>.Fail(ex.Message); }
        }

        public async Task<RoomStateResponse?> GetStateAsync(string locationKey)
        {
            try
            {
                var response = await _httpClient.GetAsync($"{_baseUrl}/api/rooms/{Uri.EscapeDataString(locationKey)}/state");
                if (response.IsSuccessStatusCode)
                    return await response.Content.ReadFromJsonAsync<RoomStateResponse>();
            }
            catch (Exception ex) { _log.Error(ex, $"[Net] GetState failed for {locationKey}"); }
            return null;
        }

        public void Dispose()
        {
            _httpClient.Dispose();
        }
    }
}
