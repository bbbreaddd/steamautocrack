using System.Text.Json;
using System.Text.Json.Serialization;
using FuzzySharp;
using FuzzySharp.SimilarityRatio;
using FuzzySharp.SimilarityRatio.Scorer.Composite;
using NinjaNye.SearchExtensions;
using Serilog;
using SQLite;

namespace SteamAutoCrack.Core.Utils;

[Table("steamapp")]
public class SteamApp
{
    [JsonPropertyName("appid")]
    [Column("appid")]
    [PrimaryKey]
    public uint? AppId { get; set; }

    [JsonPropertyName("name")]
    [Column("name")]
    public string? Name { get; set; }

    public override string ToString()
    {
        return $"{AppId}={Name}";
    }
}

public class AppList
{
    [JsonPropertyName("apps")] public List<SteamApp>? Apps { get; set; }
    [JsonPropertyName("have_more_results")] public bool HaveMoreResults { get; set; }

    [JsonPropertyName("last_appid")] public uint LastAppId { get; set; }
}

public class StoreSteamAppsV1
{
    [JsonPropertyName("response")] public AppList? AppList { get; set; }
}

public class SteamAppList
{
    private const int FuzzySearchScore = 80;

    private static string steamapplisturl => 
        (Config.Config.EMUGameInfoConfigs.GameInfoAPI == EMUGameInfoConfig.GeneratorGameInfoAPI.GeneratorProxyServer)
            ? $"{(string.IsNullOrEmpty(Config.Config.EMUGameInfoConfigs.CustomAPIEndpoint) ? EMUGameInfoConfig.DefaultProxyURL : Config.Config.EMUGameInfoConfigs.CustomAPIEndpoint).TrimEnd('/')}/IStoreService/GetAppList/v1/" 
            : "https://api.steampowered.com/IStoreService/GetAppList/v1/";

    private static readonly ILogger _log = Log.ForContext<SteamAppList>();

    private static bool bInited;

    private static bool bDisposed;

    private static readonly string Database = Path.Combine(Config.Config.TempPath, "SteamAppList.db");

    public static SQLiteAsyncConnection? db;

    private static TaskCompletionSource<bool> _initializationTcs = new();

    public static async Task Initialize(bool forceupdate = false)
    {
        try
        {
            bDisposed = false;
            _log.Debug("Initializing Steam App list...");
            if (!Directory.Exists(Config.Config.TempPath))
                Directory.CreateDirectory(Config.Config.TempPath);

            _initializationTcs = new TaskCompletionSource<bool>();

            db = new SQLiteAsyncConnection(Database);
            await db.CreateTableAsync<SteamApp>().ConfigureAwait(false);
            var count = await db.Table<SteamApp>().CountAsync().ConfigureAwait(false);

            bool dbExistsWithData = File.Exists(Database) && count > 0;
            bool needsUpdate = DateTime.Now.Subtract(File.GetLastWriteTimeUtc(Database)).TotalDays >= 7 || count == 0 || forceupdate;
            if (bInited && !needsUpdate && !forceupdate)
            {
                _log.Debug("Already initialized Steam App list.");
                return;
            }

            // Make db firstly inited if there's data
            if (dbExistsWithData)
            {
                _log.Debug("Database exists with {count} apps. Marking as initialized.", count);
                bInited = true;
                _initializationTcs.TrySetResult(true);
            }

            if (needsUpdate)
            {
                try
                {
                    _log.Information("Updating Steam App list...");
                    if (string.IsNullOrEmpty(Config.Config.EMUGameInfoConfigs.SteamWebAPIKey))
                    {
                        var currentApi = Config.Config.EMUGameInfoConfigs.GameInfoAPI;
                        _log.Debug("Steam Web API Key is empty. Current API Mode: {mode}", currentApi);

                        if (currentApi == EMUGameInfoConfig.GeneratorGameInfoAPI.GeneratorProxyServer)
                        {
                            _log.Debug("Using Proxy Server mode, proceeding without local API key.");
                        }
                        else if (currentApi == EMUGameInfoConfig.GeneratorGameInfoAPI.GeneratorCommunityScraper)
                        {
                            _log.Debug("Using Community Scraper mode, skipping app list update.");
                            if (!dbExistsWithData) bDisposed = true;
                            return;
                        }
                        else
                        {
                            _log.Warning("Steam Web API Key not set. Please set it to update Steam App List.");
                            if (!dbExistsWithData) bDisposed = true;
                            return;
                        }
                    }

                    using var client = new HttpClient();
                    uint lastAppId = 0;
                    bool haveMore = false;
                    var allApps = new List<SteamApp>();
                    var requestKey = Config.Config.EMUGameInfoConfigs.SteamWebAPIKey;
                    var useProxy = Config.Config.EMUGameInfoConfigs.GameInfoAPI == EMUGameInfoConfig.GeneratorGameInfoAPI.GeneratorProxyServer;
                    var hasProxyUrl = !string.IsNullOrEmpty(Config.Config.EMUGameInfoConfigs.CustomAPIEndpoint);

                    var maxRetries = 3;

                    do
                    {
                        string url;
                        if (useProxy)
                        {
                            url = $"{steamapplisturl}?max_results=50000&last_appid={lastAppId}";
                        }
                        else
                        {
                            url = $"{steamapplisturl}?key={requestKey}&max_results=50000&last_appid={lastAppId}";
                        }
                        _log.Debug("Requesting Steam App list batch with last_appid={lastAppId}", lastAppId);

                        var attempt = 0;
                        bool batchSuccess = false;
                        while (attempt < maxRetries && !batchSuccess)
                        {
                            attempt++;
                            try
                            {
                                var response = await client.GetAsync(url).ConfigureAwait(false);
                                response.EnsureSuccessStatusCode();
                                var responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                                var steamApps = DeserializeSteamApps(responseBody);
                                if (steamApps?.AppList?.Apps != null && steamApps.AppList.Apps.Count > 0)
                                {
                                    allApps.AddRange(steamApps.AppList.Apps);
                                    _log.Debug("Fetched {count} apps in this batch.", steamApps.AppList.Apps.Count);
                                }

                                haveMore = steamApps?.AppList?.HaveMoreResults ?? false;
                                lastAppId = steamApps?.AppList?.LastAppId ?? 0;

                                batchSuccess = true;
                            }
                            catch (Exception ex)
                            {
                                _log.Warning(ex, "Failed to fetch Steam App batch (attempt {attempt}/{maxRetries}).", attempt, maxRetries);
                                if (attempt < maxRetries)
                                {
                                    var delayMs = (int)(1000 * Math.Pow(2, attempt - 1));
                                    await Task.Delay(delayMs).ConfigureAwait(false);
                                }
                                else
                                {
                                    _log.Error(ex, "Exhausted retries fetching Steam App list. Aborting update.");
                                    if (!dbExistsWithData) bDisposed = true;
                                    return;
                                }
                            }
                        }

                    } while (haveMore);

                    if (allApps.Count > 0)
                    {
                        await db.InsertAllAsync(allApps, "OR IGNORE").ConfigureAwait(false);
                        _log.Information("Updated Steam App list. Total fetched apps: {count}", allApps.Count);
                    }
                    else
                    {
                        _log.Information("No apps fetched from Steam App list update.");
                    }
                }
                catch (Exception ex)
                {
                    _log.Error(ex, "Failed to initialize Steam App list, Retrying...");
                }
            }
            else
            {
                _log.Information("Applist already updated to latest version.");
            }

            if (!dbExistsWithData)
            {
                bInited = true;
                _initializationTcs.TrySetResult(true);
            }

            var updatedCount = await db.Table<SteamApp>().CountAsync().ConfigureAwait(false);
            _log.Information("Initialized Steam App list, App Count: {count}", updatedCount);
            return;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to initialize Steam App list.");
            bDisposed = true;
            return;
        }
    }

    public static async Task WaitForReady()
    {
        if (bDisposed == true)
        {
            if (Config.Config.EMUGameInfoConfigs.GameInfoAPI == EMUGameInfoConfig.GeneratorGameInfoAPI.GeneratorCommunityScraper)
            {
                _log.Debug("Steam App list is not initialized. Proceeding without app names (expected for Community Scraper).");
            }
            else
            {
                _log.Error("Steam App list is not initialized (likely due to missing API key). Proceeding without app names.");
            }
            return;
        }

        _log.Debug("Waiting for Steam App list initialized...");
        await _initializationTcs.Task.ConfigureAwait(false);
    }

    private static StoreSteamAppsV1? DeserializeSteamApps(string json)
    {
        try
        {
            var opts = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            };
            return JsonSerializer.Deserialize<StoreSteamAppsV1>(json, opts);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to deserialize Steam App list JSON.");
            return new StoreSteamAppsV1 { AppList = new AppList { Apps = new List<SteamApp>(), HaveMoreResults = false, LastAppId = 0 } };
        }
    }

    private static async Task<IEnumerable<SteamApp>> SearchSteamStoreAPI(string name)
    {
        var apps = new List<SteamApp>();
        try
        {
            if (string.IsNullOrWhiteSpace(name)) return apps;
            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(15);
            var url = $"https://store.steampowered.com/api/storesearch/?term={Uri.EscapeDataString(name)}&l=english&cc=US";
            var response = await client.GetAsync(url).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                using var json = JsonDocument.Parse(content);
                if (json.RootElement.TryGetProperty("items", out var items))
                {
                    foreach (var item in items.EnumerateArray())
                    {
                        if (item.TryGetProperty("id", out var idProp) && item.TryGetProperty("name", out var nameProp))
                        {
                            apps.Add(new SteamApp
                            {
                                AppId = idProp.GetUInt32(),
                                Name = nameProp.GetString()
                            });
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to search Steam Store API fallback.");
        }
        return apps;
    }

    public static async Task<IEnumerable<SteamApp>> GetListOfAppsByName(string name)
    {
        if (db == null || await db.Table<SteamApp>().CountAsync().ConfigureAwait(false) == 0)
        {
            return await SearchSteamStoreAPI(name).ConfigureAwait(false);
        }

        var query = await db.Table<SteamApp>().ToListAsync().ConfigureAwait(false);
        var SearchOfAppsByName = query.Search(x => x.Name)
            .SetCulture(StringComparison.OrdinalIgnoreCase)
            .ContainingAll(name.Split(' '));
        var listOfAppsByName = SearchOfAppsByName.ToList();
        if (uint.TryParse(name, out var appid))
        {
            var app = await GetAppById(appid).ConfigureAwait(false);
            var appToRemove = listOfAppsByName.Find(d => d.AppId == appid);
            if (appToRemove != null) listOfAppsByName.Remove(appToRemove);
            if (app != null) listOfAppsByName.Insert(0, app);
        }

        return listOfAppsByName;
    }

    public static async Task<IEnumerable<SteamApp>> GetListOfAppsByNameFuzzy(string name)
    {
        if (db == null || await db.Table<SteamApp>().CountAsync().ConfigureAwait(false) == 0)
        {
            return await SearchSteamStoreAPI(name).ConfigureAwait(false);
        }

        var query = await db.Table<SteamApp>().ToListAsync().ConfigureAwait(false);
        var listOfAppsByName = new List<SteamApp>();
        var results = Process.ExtractTop(new SteamApp { Name = name }, query, x => x.Name?.ToLower(),
            ScorerCache.Get<WeightedRatioScorer>(), FuzzySearchScore);
        foreach (var item in results) listOfAppsByName.Add(item.Value);

        if (uint.TryParse(name, out var appid))
        {
            var app = await GetAppById(appid).ConfigureAwait(false);
            var appToRemove = listOfAppsByName.Find(d => d.AppId == appid);
            if (appToRemove != null) listOfAppsByName.Remove(appToRemove);
            if (app != null) listOfAppsByName.Insert(0, app);
        }

        return listOfAppsByName;
    }

    public static async Task<SteamApp?> GetAppByName(string name)
    {
        _log?.Debug($"Trying to get app name for app: {name}");
        var app = await db!.Table<SteamApp>()
            .FirstOrDefaultAsync(x => x.Name != null && x.Name.Equals(name))
            .ConfigureAwait(false);
        if (app != null) _log?.Debug($"Successfully got app name for app: {app}");
        return app;
    }

    public static async Task<SteamApp> GetAppById(uint appid)
    {
        _log?.Debug($"Trying to get app with ID {appid}");
        SteamApp? app = null;
        if (db != null)
        {
            app = await db.Table<SteamApp>().FirstOrDefaultAsync(x => x.AppId.Equals(appid)).ConfigureAwait(false);
        }
        if (app != null) _log?.Debug($"Successfully got app {app}");
        else
        {
            return new SteamApp()
            {
                AppId = appid,
                Name = null,
            };
        }
        return app;
    }
}