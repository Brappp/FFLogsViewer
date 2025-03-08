using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FFLogsViewer.Manager;
using FFLogsViewer.Model;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FFLogsViewer;

public class FFLogsClient : IDisposable
{
    public volatile bool IsTokenValid;
    public int LimitPerHour;
    public bool HasLimitPerHourFailed => this.rateLimitDataFetchAttempts >= 3;

    private readonly HttpClient httpClient;
    private readonly object lastCacheRefreshLock = new();
    private readonly ConcurrentDictionary<string, dynamic?> cache = new();
    private volatile bool isRateLimitDataLoading;
    private volatile int rateLimitDataFetchAttempts = 5;
    private DateTime? lastCacheRefresh;
    private bool disposedValue;

    public class Token
    {
        [JsonProperty("access_token")]
        public string? AccessToken { get; set; }

        [JsonProperty("token_type")]
        public string? TokenType { get; set; }

        [JsonProperty("expires_in")]
        public int ExpiresIn { get; set; }

        [JsonProperty("error")]
        public string? Error { get; set; }
    }

    public FFLogsClient()
    {
        this.httpClient = new HttpClient();
        this.SetToken();
    }

    public static bool IsConfigSet()
    {
        return !string.IsNullOrEmpty(Service.Configuration.ClientId)
               && !string.IsNullOrEmpty(Service.Configuration.ClientSecret);
    }

    public static int EstimateCurrentLayoutPoints()
    {
        var zoneCount = GetZoneInfo().Count();
        if (zoneCount == 0)
        {
            return 1;
        }

        return zoneCount * 5;
    }

    public void ClearCache()
    {
        this.cache.Clear();
    }

    public void SetToken()
    {
        this.IsTokenValid = false;
        this.rateLimitDataFetchAttempts = 0;

        if (!IsConfigSet())
        {
            return;
        }

        Task.Run(async () =>
        {
            var token = await FetchToken().ConfigureAwait(false);

            if (token is { Error: null })
            {
                this.httpClient.DefaultRequestHeaders.Authorization
                    = new AuthenticationHeaderValue("Bearer", token.AccessToken);

                this.IsTokenValid = true;
            }
            else
            {
                Service.PluginLog.Error($"FF Logs token couldn't be set: {(token == null ? "return was null" : token.Error)}");
            }
        });
    }

    /// <summary>
    /// Refresh the FFLogs API access token using OAuth2 client credentials.
    /// </summary>
    /// <param name="clientId">The FFLogs Client ID from configuration.</param>
    /// <param name="clientSecret">The FFLogs Client Secret from configuration.</param>
    /// <returns>True if a valid token is obtained; otherwise false.</returns>
    public async Task<bool> RefreshAccessTokenAsync(string clientId, string clientSecret)
    {
        try
        {
            string tokenUrl = "https://www.fflogs.com/oauth/token";

            // Prepare the form data for the client credentials grant.
            var form = new Dictionary<string, string>
            {
                { "grant_type", "client_credentials" },
                { "client_id", clientId },
                { "client_secret", clientSecret }
            };

            HttpResponseMessage tokenResponse = await httpClient.PostAsync(tokenUrl, new FormUrlEncodedContent(form));
            if (!tokenResponse.IsSuccessStatusCode)
            {
                Service.PluginLog.Error($"FFLogs token request failed with status code: {tokenResponse.StatusCode}");
                IsTokenValid = false;
                return false;
            }

            string responseContent = await tokenResponse.Content.ReadAsStringAsync();
            Token? tokenData = JsonConvert.DeserializeObject<Token>(responseContent);
            if (tokenData != null && !string.IsNullOrEmpty(tokenData.AccessToken))
            {
                // Set the Bearer token in the default request headers.
                httpClient.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", tokenData.AccessToken);
                IsTokenValid = true;
                Service.PluginLog.Debug("FFLogs API access token obtained successfully.");
                return true;
            }
            else
            {
                Service.PluginLog.Error("Failed to parse FFLogs access token.");
                IsTokenValid = false;
                return false;
            }
        }
        catch (Exception ex)
        {
            Service.PluginLog.Error(ex, "Exception while refreshing FFLogs access token.");
            IsTokenValid = false;
            return false;
        }
    }

    public async Task FetchGameData()
    {
        if (!this.IsTokenValid)
        {
            Service.PluginLog.Error("FFLogs token not set.");
            return;
        }

        const string baseAddress = "https://www.fflogs.com/api/v2/client";
        const string query = """{"query":"{worldData {expansions {name id zones {name id difficulties {name id} encounters {name id}}}}}"}""";

        var content = new StringContent(query, Encoding.UTF8, "application/json");

        try
        {
            var dataResponse = await this.httpClient.PostAsync(baseAddress, content);
            var jsonContent = await dataResponse.Content.ReadAsStringAsync();
            Service.GameDataManager.SetDataFromJson(jsonContent);
        }
        catch (Exception ex)
        {
            Service.PluginLog.Error(ex, "Error while fetching game data.");
        }
    }

    public async Task<dynamic?> FetchLogs(CharData charData)
    {
        if (!this.IsTokenValid)
        {
            Service.PluginLog.Error("FFLogs token not valid.");
            return null;
        }

        Service.HistoryManager.AddHistoryEntry(charData);

        const string baseAddress = "https://www.fflogs.com/api/v2/client";

        var query = BuildQuery(charData);

        try
        {
            var isCaching = Service.Configuration.IsCachingEnabled;
            if (isCaching)
            {
                this.CheckCache();
            }

            dynamic? deserializeJson = null;
            var isCached = isCaching && this.cache.TryGetValue(query, out deserializeJson);

            if (!isCached)
            {
                var content = new StringContent(query, Encoding.UTF8, "application/json");
                var dataResponse = await this.httpClient.PostAsync(baseAddress, content);
                var jsonContent = await dataResponse.Content.ReadAsStringAsync();
                deserializeJson = JsonConvert.DeserializeObject(jsonContent);

                if (isCaching)
                {
                    this.cache.TryAdd(query, deserializeJson);
                }
            }

            return deserializeJson;
        }
        catch (Exception ex)
        {
            Service.PluginLog.Error(ex, "Error while fetching data.");
            return null;
        }
    }

    /// <summary>
    /// Fetch character data from the FFLogs API using a GraphQL query.
    /// The query retrieves the encounter rankings (including best parse percent and kill count)
    /// for a given fight (encounter).
    /// </summary>
    /// <param name="playerName">Character name.</param>
    /// <param name="serverSlug">Server slug (e.g. "Midgardsormr").</param>
    /// <param name="serverRegion">Region (e.g. "NA", "EU", "JP").</param>
    /// <param name="encounterId">The numeric encounter ID for the fight.</param>
    /// <returns>A JSON string containing the API response, or null on failure.</returns>
    public async Task<string?> FetchCharacterDataAsync(string playerName, string serverSlug, string serverRegion, int encounterId)
    {
        if (!IsTokenValid)
        {
            Service.PluginLog.Error("Access token is not valid. Cannot fetch character data.");
            return null;
        }

        try
        {
            // Build the GraphQL query.
            string graphqlQuery = $@"
{{
  characterData {{
    character(name: ""{playerName}"", serverSlug: ""{serverSlug}"", serverRegion: ""{serverRegion}"") {{
      encounterRankings(encounterID: {encounterId}) {{
        rankings {{
          rankPercent
        }}
        total
      }}
    }}
  }}
}}";

            // Wrap the query into a JSON payload.
            var payload = new { query = graphqlQuery };
            string jsonPayload = JsonConvert.SerializeObject(payload);

            // Send the POST request to the FFLogs GraphQL endpoint.
            HttpResponseMessage response = await httpClient.PostAsync("https://www.fflogs.com/api/v2/client", new StringContent(jsonPayload, Encoding.UTF8, "application/json"));
            if (!response.IsSuccessStatusCode)
            {
                Service.PluginLog.Error($"FFLogs API query failed with status code: {response.StatusCode}");
                return null;
            }

            string responseJson = await response.Content.ReadAsStringAsync();
            return responseJson;
        }
        catch (Exception ex)
        {
            Service.PluginLog.Error(ex, "Exception while fetching character data from FFLogs API.");
            return null;
        }
    }

    /// <summary>
    /// Fetch a player's best parse and kill count for a specific encounter.
    /// </summary>
    /// <param name="firstName">Character first name.</param>
    /// <param name="lastName">Character last name.</param>
    /// <param name="worldName">Character world/server name.</param>
    /// <param name="encounterId">The encounter ID to query.</param>
    /// <param name="difficultyId">The difficulty ID (default: 100 for normal).</param>
    /// <param name="metric">The metric to use (e.g. "rdps", "dps", etc.).</param>
    /// <returns>A tuple containing the best parse percentage and kill count, or null values if data couldn't be retrieved.</returns>
    public async Task<(float? BestParse, int? Kills)> FetchEncounterParseAsync(
        string firstName, string lastName, string worldName, int encounterId, int difficultyId = 100, string metric = "rdps")
    {
        if (!this.IsTokenValid)
        {
            Service.PluginLog.Error("FFLogs token is not valid.");
            return (null, null);
        }

        try
        {
            var regionName = CharDataManager.GetRegionCode(worldName);
            if (string.IsNullOrEmpty(regionName))
            {
                Service.PluginLog.Error("Invalid world name or region not found.");
                return (null, null);
            }

            string graphqlQuery = $@"
            {{
              characterData {{
                character(name: ""{firstName} {lastName}"", serverSlug: ""{worldName}"", serverRegion: ""{regionName}"") {{
                  encounterRankings(encounterID: {encounterId}, metric: {metric}, difficulty: {difficultyId}) {{
                    totalKills
                    rankings {{
                      rankPercent
                    }}
                  }}
                }}
              }}
            }}";

            // Wrap the query into a JSON payload
            var payload = new { query = graphqlQuery };
            string jsonPayload = JsonConvert.SerializeObject(payload);

            // Send the POST request to the FFLogs GraphQL endpoint
            HttpResponseMessage response = await this.httpClient.PostAsync(
                "https://www.fflogs.com/api/v2/client",
                new StringContent(jsonPayload, Encoding.UTF8, "application/json"));

            if (!response.IsSuccessStatusCode)
            {
                Service.PluginLog.Error($"FFLogs API query failed with status code: {response.StatusCode}");
                return (null, null);
            }

            string responseJson = await response.Content.ReadAsStringAsync();
            JObject responseObj = JObject.Parse(responseJson);

            var parseData = responseObj["data"]?["characterData"]?["character"]?["encounterRankings"];
            if (parseData == null || parseData["rankings"] == null)
            {
                Service.PluginLog.Error("No parse data found for this character and encounter.");
                return (null, null);
            }

            float? bestParse = null;
            if (parseData["rankings"].HasValues)
            {
                bestParse = parseData["rankings"].First["rankPercent"]?.Value<float>();
            }

            int? totalKills = parseData["totalKills"]?.Value<int>();

            return (bestParse, totalKills);
        }
        catch (Exception ex)
        {
            Service.PluginLog.Error(ex, "Error fetching parse data from FFLogs.");
            return (null, null);
        }
    }

    public void RefreshRateLimitData(bool resetFetchAttempts = false)
    {
        if (resetFetchAttempts)
        {
            this.rateLimitDataFetchAttempts = 0;
        }

        if (this.isRateLimitDataLoading || (this.LimitPerHour <= 0 && this.HasLimitPerHourFailed))
        {
            return;
        }

        this.isRateLimitDataLoading = true;

        Interlocked.Increment(ref this.rateLimitDataFetchAttempts);

        this.LimitPerHour = 0;

        Task.Run(async () =>
        {
            var rateLimitData = await this.FetchRateLimitData().ConfigureAwait(false);

            if (rateLimitData != null && rateLimitData["error"] == null)
            {
                var limitPerHour = rateLimitData["data"]?["rateLimitData"]?["limitPerHour"]?.ToObject<int>();
                if (limitPerHour is null or <= 0)
                {
                    Service.PluginLog.Error($"Couldn't find proper limit per hour: {rateLimitData}");
                }
                else
                {
                    this.LimitPerHour = limitPerHour.Value;
                    this.rateLimitDataFetchAttempts = 0;
                }
            }
            else
            {
                Service.PluginLog.Error($"FF Logs rate limit data couldn't be fetched: {(rateLimitData == null ? "return was null" : rateLimitData["error"])}");
            }

            this.isRateLimitDataLoading = false;
        });
    }

    public void InvalidateCache(CharData charData)
    {
        if (Service.Configuration.IsCachingEnabled)
        {
            var query = BuildQuery(charData);
            this.cache.Remove(query, out _);
        }
    }

    private static string BuildQuery(CharData charData)
    {
        var query = new StringBuilder();
        query.Append(
            $"{{\"query\":\"query {{characterData{{character(name: \\\"{charData.FirstName} {charData.LastName}\\\"serverSlug: \\\"{charData.WorldName}\\\"serverRegion: \\\"{charData.RegionName}\\\"){{");
        query.Append("hidden ");

        var metric = Service.MainWindow.GetCurrentMetric();
        charData.LoadedMetric = metric;
        foreach (var (id, difficulty, isForcingADPS) in GetZoneInfo())
        {
            query.Append($"Zone{id}diff{difficulty}: zoneRankings(zoneID: {id}, difficulty: {difficulty}, metric: ");
            if (isForcingADPS && (Service.MainWindow.OverriddenMetric == null
                                  || Service.MainWindow.OverriddenMetric.InternalName == Service.Configuration.Metric.InternalName))
            {
                query.Append("dps");
            }
            else
            {
                query.Append($"{metric.InternalName}");
            }

            // do not add if standard, avoid issues with alliance raids that do not support any partition
            if (Service.MainWindow.Partition.Id != -1)
            {
                query.Append($", partition: {Service.MainWindow.Partition.Id}");
            }

            if (Service.MainWindow.Job.Name != "All jobs")
            {
                var specName = Service.MainWindow.Job.Name == "Current job"
                                       ? GameDataManager.Jobs.FirstOrDefault(job => job.Id == charData.JobId)?.GetSpecName()
                                       : Service.MainWindow.Job.GetSpecName();

                if (specName != null)
                {
                    query.Append($", specName: \\\"{specName}\\\"");
                }
            }

            query.Append($", timeframe: {(Service.MainWindow.IsTimeframeHistorical() ? "Historical" : "Today")}");

            query.Append(')');
        }

        query.Append("}}}\"}");

        return query.ToString();
    }

    private static async Task<Token?> FetchToken()
    {
        var client = new HttpClient();

        const string baseAddress = "https://www.fflogs.com/oauth/token";
        const string grantType = "client_credentials";

        var form = new Dictionary<string, string>
        {
            { "grant_type", grantType },
            { "client_id", Service.Configuration.ClientId },
            { "client_secret", Service.Configuration.ClientSecret },
        };

        try
        {
            var tokenResponse = await client.PostAsync(baseAddress, new FormUrlEncodedContent(form));
            var jsonContent = await tokenResponse.Content.ReadAsStringAsync();
            return JsonConvert.DeserializeObject<Token>(jsonContent);
        }
        catch (Exception ex)
        {
            Service.PluginLog.Error(ex, "Error while fetching token.");
        }

        return null;
    }

    private static IEnumerable<(int ZoneId, int DifficultyId, bool IsForcingADPS)> GetZoneInfo()
    {
        return Service.Configuration.Layout
                .Where(entry => entry.Type == LayoutEntryType.Encounter)
                .GroupBy(entry => new { entry.ZoneId, entry.DifficultyId })
                .Select(group => (group.Key.ZoneId, group.Key.DifficultyId, IsForcingADPS: group.Any(entry => entry.IsForcingADPS)));
    }

    private void CheckCache()
    {
        lock (this.lastCacheRefreshLock)
        {
            if (this.lastCacheRefresh == null)
            {
                this.lastCacheRefresh = DateTime.Now;
                return;
            }

            // clear cache after an hour
            if ((DateTime.Now - this.lastCacheRefresh.Value).TotalHours > 1)
            {
                this.ClearCache();
                this.lastCacheRefresh = DateTime.Now;
            }
        }
    }

    private async Task<JObject?> FetchRateLimitData()
    {
        if (!this.IsTokenValid)
        {
            Service.PluginLog.Error("FFLogs token not valid.");
            return null;
        }

        const string baseAddress = "https://www.fflogs.com/api/v2/client";
        const string query = """{"query":"{rateLimitData {limitPerHour}}"}""";

        var content = new StringContent(query, Encoding.UTF8, "application/json");

        try
        {
            var dataResponse = await this.httpClient.PostAsync(baseAddress, content);
            var jsonContent = await dataResponse.Content.ReadAsStringAsync();
            return JsonConvert.DeserializeObject<JObject>(jsonContent);
        }
        catch (Exception ex)
        {
            Service.PluginLog.Error(ex, "Error while fetching rate limit data.");
        }

        return null;
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!disposedValue)
        {
            if (disposing)
            {
                httpClient.Dispose();
            }
            disposedValue = true;
        }
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
}
