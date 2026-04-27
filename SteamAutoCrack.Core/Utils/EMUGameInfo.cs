#pragma warning disable CS4014

using System.ComponentModel;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using IniFile;
using Serilog;
using SteamKit2;
using ValveKeyValue;
using static SteamAutoCrack.Core.Utils.EMUGameInfoConfig;
using Section = IniFile.Section;

namespace SteamAutoCrack.Core.Utils;

public class EMUGameInfoConfig
{
    public const string DefaultProxyURL = "https://api.breadcloud.net/";

    public enum GeneratorGameInfoAPI
    {
        [Description("Community Scraper")] GeneratorCommunityScraper,
        [Description("Proxy Server")] GeneratorProxyServer,
        [Description("SteamKit2 Client")] GeneratorSteamClient,
        [Description("Steam Web API")] GeneratorSteamWeb,
        [Description("Offline")] GeneratorOffline
    }

    private readonly ILogger _log;

    public EMUGameInfoConfig()
    {
        _log = Log.ForContext<EMUGameInfoConfig>();
    }

    public GeneratorGameInfoAPI GameInfoAPI { get; set; } = GeneratorGameInfoAPI.GeneratorCommunityScraper;

    /// <summary>
    ///     Required when using Steam official Web API.
    /// </summary>
    public string SteamWebAPIKey { get; set; } = string.Empty;

    public string ConfigPath { get; set; } = Path.Combine(Config.Config.TempPath, "steam_settings");

    /// <summary>
    ///     Enable generate game achievement images.
    /// </summary>
    public bool GenerateImages { get; set; } = true;

    public uint AppID { get; set; }

    /// <summary>
    ///     Use Steam Web App List when generating DLCs.
    /// </summary>
    public bool UseSteamWebAppList { get; set; } = false;
    
    /// <summary>
    ///     Custom Steam Web API endpoint URL.
    /// </summary>
    public string CustomAPIEndpoint { get; set; } = string.Empty;

    public void SetAppIDFromString(string str)
    {
        if (!uint.TryParse(str, out var appID))
        {
            _log.Error("Invaild Steam AppID.");
            throw new Exception("Invaild Steam AppID.");
        }

        AppID = appID;
    }

    public static class DefaultConfig
    {
        public static GeneratorGameInfoAPI GameInfoAPI = GeneratorGameInfoAPI.GeneratorCommunityScraper;
        public static readonly string ConfigPath = Path.Combine(Config.Config.TempPath, "steam_settings");

        /// <summary>
        ///     Enable generate game achievement images.
        /// </summary>
        public static readonly bool GenerateImages = true;

        /// <summary>
        ///     Use Steam Web App List when generating DLCs.
        /// </summary>
        public static readonly bool UseSteamWebAppList = false;

        /// <summary>
        ///     Custom Steam Web API endpoint URL.
        /// </summary>
        public static string CustomAPIEndpoint { get; set; } = string.Empty;

        /// <summary>
        ///     Required when using Steam official Web API.
        /// </summary>
        public static string SteamWebAPIKey { get; set; } = string.Empty;
    }
}

public interface IEMUGameInfo
{
    public Task<bool> Generate(EMUGameInfoConfig GameInfoConfig, CancellationToken cancellationToken = default);
}

public class EMUGameInfo : IEMUGameInfo
{
    private readonly ILogger _log;

    public EMUGameInfo()
    {
        _log = Log.ForContext<EMUGameInfo>();
    }

    public async Task<bool> Generate(EMUGameInfoConfig GameInfoConfig, CancellationToken cancellationToken = default)
    {
        Generator Generator;
        _log.Information("Generating game info...");
        try
        {
            Generator = GameInfoConfig.GameInfoAPI switch
            {
                GeneratorGameInfoAPI.GeneratorSteamClient => new GeneratorSteamClient(GameInfoConfig),
                GeneratorGameInfoAPI.GeneratorSteamWeb => new GeneratorSteamWeb(GameInfoConfig),
                GeneratorGameInfoAPI.GeneratorProxyServer => new GeneratorProxyServer(GameInfoConfig),
                GeneratorGameInfoAPI.GeneratorCommunityScraper => new GeneratorCommunityScraper(GameInfoConfig),
                GeneratorGameInfoAPI.GeneratorOffline => new GeneratorOffline(GameInfoConfig),
                _ => throw new Exception("Invalid game info API.")
            };
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Error: ");
            return false;
        }

        try
        {
            await Generator.InfoGenerator(cancellationToken).ConfigureAwait(false);
            _log.Information("Generated game info.");
            return true;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to generate game info.");
            return false;
        }
    }
}

internal abstract class Generator
{
    protected const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) " +
        "Chrome/87.0.4280.88 Safari/537.36";

    protected readonly ILogger _log;
    protected readonly uint AppID;
    protected readonly string ConfigPath;
    protected readonly bool GenerateImages;
    protected readonly string SteamWebAPIKey;
    protected readonly bool UseSteamWebAppList;
    protected readonly string CustomAPIEndpoint;
    public Ini config_app = new();
    protected List<string> DownloadedFile = new();
    protected JsonDocument? GameSchema;
    private DateTime LastWebRequestTime;

    public Generator(EMUGameInfoConfig GameInfoConfig)
    {
        _log = Log.ForContext<EMUGameInfo>();
        Ini.Config.AllowHashForComments(true);
        SteamWebAPIKey = GameInfoConfig.SteamWebAPIKey;
        ConfigPath = GameInfoConfig.ConfigPath;
        AppID = GameInfoConfig.AppID;
        GenerateImages = GameInfoConfig.GenerateImages;
        UseSteamWebAppList = GameInfoConfig.UseSteamWebAppList;
        CustomAPIEndpoint = GameInfoConfig.CustomAPIEndpoint;
    }

    protected string GetSteamWebUrl(string officialUrl, string queryParams = "")
    {
        bool useProxy = this is GeneratorProxyServer;

        if (useProxy)
        {
            var endpoint = string.IsNullOrWhiteSpace(CustomAPIEndpoint) ? DefaultProxyURL : CustomAPIEndpoint;
            _log.Debug("Proxy mode active. Using endpoint: {Endpoint}", endpoint);
            
            // Use custom endpoint as proxy
            var officialUri = new Uri(officialUrl);
            var customBase = endpoint.TrimEnd('/');
            var separatorProxy = string.IsNullOrEmpty(queryParams) ? "" : "?";
            var finalUrl = $"{customBase}{officialUri.AbsolutePath}{separatorProxy}{queryParams}";
            _log.Debug("Generated Proxy URL: {Url}", finalUrl);
            return finalUrl;
        }

        // Standard logic (Official Steam API)
        if (officialUrl.Contains("key=")) return officialUrl; // Key already present
        var separator = officialUrl.Contains("?") ? "&" : "?";
        return $"{officialUrl}{separator}key={SteamWebAPIKey}{(string.IsNullOrEmpty(queryParams) ? "" : "&" + queryParams)}";
    }

    public abstract Task InfoGenerator(CancellationToken cancellationToken = default);

    protected Task GenerateBasic()
    {
        return Task.Run(() =>
        {
            try
            {
                _log.Debug("Generating basic infos...");
                if (Directory.Exists(ConfigPath))
                {
                    Directory.Delete(ConfigPath, true);
                    _log.Debug("Deleted previous steam_settings folder.");
                }

                Directory.CreateDirectory(ConfigPath);
                _log.Debug("Created steam_settings folder.");
            }
            catch (UnauthorizedAccessException ex)
            {
                _log.Error(ex,
                    "Failed to access steam_settings path. (Try run SteamAutoCrack with administrative rights)");
                throw new Exception("Failed to access steam_settings path.");
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Failed to access steam_settings path.");
                throw new Exception("Failed to access steam_settings path.");
            }

            _log.Debug("Outputting game info to {0}", Path.GetFullPath(ConfigPath));
            try
            {
                var appidPath = Path.Combine(ConfigPath, "steam_appid.txt");
                File.WriteAllText(appidPath, AppID.ToString());
                _log.Debug("Generated steam_appid.txt at: {Path}", Path.GetFullPath(appidPath));
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Failed to write steam_appid.txt.");
                throw new Exception("Failed to write steam_appid.txt.");
            }

            _log.Debug("Generated basic infos.");
        });
    }

    protected virtual async Task<bool> GetGameSchema(CancellationToken cancellationToken = default)
    {
        try
        {
            _log.Information("Getting game schema...");
            var useCustomEndpoint = !string.IsNullOrWhiteSpace(CustomAPIEndpoint);
            var GameSchemaUrl = "https://api.steampowered.com/ISteamUserStats/GetSchemaForGame/v2/";

            var useProxy = this is GeneratorProxyServer;

            if (!useProxy && !useCustomEndpoint && (SteamWebAPIKey == string.Empty || SteamWebAPIKey == null))
            {
                _log.Warning("Empty Steam Web API Key, skipping getting game schema...");
                return false;
            }

            _log.Debug($"Getting schema for App {AppID}");

            var language = Config.Config.EMUConfigs.Language.ToString();

            var client = new HttpClient();
            client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
            
            string apiUrl = GetSteamWebUrl("https://api.steampowered.com/ISteamUserStats/GetSchemaForGame/v2/", $"l={language}&appid={AppID}");

            client.Timeout = TimeSpan.FromSeconds(30);
            var response = await LimitSteamWebApiGET(client,
                new HttpRequestMessage(HttpMethod.Get, apiUrl), cancellationToken);
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var responseCode = response.StatusCode;
            if (responseCode == HttpStatusCode.OK)
            {
                _log.Debug("Got game schema.");
                GameSchema = JsonDocument.Parse(responseBody);
            }
            else if (responseCode == HttpStatusCode.Forbidden)
            {
                _log.Error("Error 403 in getting game schema, please check your Steam Web API key. Skipping...");
                throw new Exception("Error 403 in getting game schema.");
            }
            else
            {
                _log.Error("Error {Code} in getting game schema. Skipping...", responseCode);
                throw new Exception($"Error {responseCode} in getting game schema. Skipping...");
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            _log.Debug("Operation was canceled.");
            return false;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to get game schema.");
            return false;
        }
    }

    protected async Task<bool> GetGameSchemaScrapeFallback(CancellationToken cancellationToken = default)
    {
        string[] fallbackUsers = {
            "76561198028121353", "76561197979911851", "76561198017975643", "76561197993544755", "76561198355953202",
            "76561198001237877", "76561198237402290", "76561198152618007", "76561198355625888", "76561198213148949",
            "76561197969050296", "76561198217186687", "76561198037867621", "76561198094227663", "76561198019712127",
            "76561197963550511", "76561198134044398", "76561198001678750", "76561197973009892", "76561198044596404",
            "76561197976597747", "76561197969810632", "76561198095049646", "76561198085065107", "76561198864213876",
            "76561197962473290", "76561198388522904", "76561198033715344", "76561197995070100", "76561198313790296",
            "76561198063574735", "76561197996432822", "76561197976968076", "76561198281128349", "76561198154462478",
            "76561198027233260", "76561198842864763", "76561198010615256", "76561198035900006", "76561198122859224",
            "76561198235911884", "76561198027214426", "76561197970825215", "76561197968410781", "76561198104323854",
            "76561198001221571", "76561198256917957", "76561198008181611", "76561198407953371", "76561198062901118",
            "76561197979667190", "76561197974742349", "76561198077213101", "76561198121398682", "76561198019009765",
            "76561198119667710", "76561197990233857", "76561199130977924", "76561198096081579", "76561198139084236",
            "76561197971011821", "76561198063728345", "76561198082995144", "76561197963534359", "76561198118726910",
            "76561198097945516", "76561198124872187", "76561198077248235", "76561198326510209", "76561198109083829",
            "76561198808371265", "76561198048373585", "76561198005337430", "76561198045455280", "76561197981111953",
            "76561197992133229", "76561198152760885", "76561198037809069", "76561198382166453", "76561198093753361",
            "76561198396723427", "76561199168919006", "76561198006391846", "76561198040421250", "76561197994616562",
            "76561198017902347", "76561198044387084", "76561198172367910", "76561199353305847", "76561198121336040",
            "76561197972951657", "76561198251835488", "76561198102767019", "76561198021180815", "76561197976796589",
            "76561197992548975", "76561198367471798", "76561197965978376", "76561197993312863", "76561198128158703",
            "76561198015685843", "76561198047438206", "76561197971026489", "76561198252374474", "76561198061393233",
            "76561199173688191", "76561198008797636", "76561197995008105", "76561197984235967", "76561198417144062",
            "76561197978640923", "76561198219343843", "76561197982718230", "76561198031837797", "76561198039492467",
            "76561198020125851", "76561198192399786", "76561198028011423", "76561198318111105", "76561198155124847",
            "76561198168877244", "76561198105279930", "76561197988664525", "76561198996604130", "76561197969148931",
            "76561198035552258", "76561198015992850", "76561198050474710", "76561198029503957", "76561198026221141",
            "76561198025653291", "76561198034213886", "76561198096632451", "76561197972378106", "76561197997477460",
            "76561198054210948", "76561198111433283", "76561198004332929", "76561198045540632", "76561198043532513",
            "76561199080934614", "76561197970246998", "76561197986240493", "76561198029532782", "76561198018254158",
            "76561197973230221", "76561198020746864", "76561198158932704", "76561198086250077", "76561198269242105",
            "76561198294806446", "76561198031164839", "76561198019555404", "76561198048151962", "76561198003041763",
            "76561198025391492", "76561197962630138", "76561198072936438", "76561198120120943", "76561197984010356",
            "76561198042965266", "76561198046642155", "76561198015856631", "76561198124865933", "76561198042781427",
            "76561198443388781", "76561198426000196", "76561198051725954", "76561197992105918", "76561198172925593",
            "76561198071709714", "76561197981228012", "76561197981027062", "76561198122276418", "76561198019841907",
            "76561197985091630", "76561199492215670", "76561198106206019", "76561198090111762", "76561198104561325",
            "76561197991699268", "76561198072361453", "76561198027066612", "76561198032614383", "76561198844130640",
            "76561198106145311", "76561198079227501", "76561198093579202", "76561198315929726", "76561198171791210",
            "76561198264362271", "76561198846208086", "76561197991613008", "76561198026306582", "76561197973701057",
            "76561198028428529", "76561198427572372", "76561197983517848", "76561198085238363", "76561198070220549",
            "76561198101049562", "76561197969365800", "76561198413266831", "76561198015514779", "76561198811114019",
            "76561198165450871", "76561197994575642", "76561198034906703", "76561198119915053", "76561198079896896",
            "76561198008549198", "76561197988052802", "76561198004532679", "76561198002535276", "76561197970545939",
            "76561197977920776", "76561198007200913", "76561197984605215", "76561198831075066", "76561197970970678",
            "76561197982273259", "76561197970307937", "76561198413088851", "76561197970360549", "76561198051740093",
            "76561197966617426", "76561198356842617", "76561198025111129", "76561197996825541", "76561197967716198",
            "76561197975329196", "76561197998058239", "76561198027668357", "76561197962850521", "76561198258304011",
            "76561198098314980", "76561198127957838", "76561198060520130", "76561198035612474", "76561198318547224",
            "76561198020810038", "76561198080773680", "76561198033967307", "76561198034503074", "76561198150467988",
            "76561197994153029", "76561198026278913", "76561198217979953", "76561197988445370", "76561198083977059"
        };
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        client.Timeout = TimeSpan.FromSeconds(30);

        foreach (var userId in fallbackUsers)
        {
            try
            {
                var apiUrl = $"https://steamcommunity.com/profiles/{userId}/stats/{AppID}/?xml=1";
                var response = await LimitSteamWebApiGET(client, new HttpRequestMessage(HttpMethod.Get, apiUrl), cancellationToken);
                var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
                
                if (response.StatusCode != HttpStatusCode.OK || !responseBody.Contains("</playerstats>") || responseBody.Contains("<error>"))
                    continue;

                var xmlDoc = new System.Xml.XmlDocument();
                xmlDoc.LoadXml(responseBody);
                
                var achievementsList = new List<Achievement>();
                var achievementsNode = xmlDoc.SelectSingleNode("//achievements");
                if (achievementsNode != null)
                {
                    foreach (System.Xml.XmlNode ach in achievementsNode.SelectNodes("achievement"))
                    {
                        var a = new Achievement
                        {
                            Name = ach.SelectSingleNode("apiname")?.InnerText ?? "",
                            DisplayName = ach.SelectSingleNode("name")?.InnerText ?? "",
                            Description = ach.SelectSingleNode("description")?.InnerText ?? "",
                            Icon = ach.SelectSingleNode("iconClosed")?.InnerText ?? "",
                            IconGray = ach.SelectSingleNode("iconOpen")?.InnerText ?? "",
                            Hidden = 0
                        };
                        achievementsList.Add(a);
                    }
                }

                var statsList = new List<object>();
                var statsNode = xmlDoc.SelectSingleNode("//stats");
                if (statsNode != null)
                {
                    foreach (System.Xml.XmlNode stat in statsNode.SelectNodes("stat"))
                    {
                        var s = new
                        {
                            name = stat.SelectSingleNode("name")?.InnerText ?? "",
                            defaultvalue = 0
                        };
                        statsList.Add(s);
                    }
                }

                var gameObj = new
                {
                    game = new
                    {
                        availableGameStats = new
                        {
                            achievements = achievementsList,
                            stats = statsList
                        }
                    }
                };

                var jsonStr = JsonSerializer.Serialize(gameObj);
                GameSchema = JsonDocument.Parse(jsonStr);
                _log.Information($"Successfully got game schema using fallback scraper from user {userId}!");
                return true;
            }
            catch (Exception ex)
            {
                _log.Debug(ex, $"Fallback user {userId} failed.");
            }
        }

        _log.Error("All fallback users failed to retrieve achievement schema. The game might not have achievements, or the fallback users have private profiles.");
        return false;
    }

    protected async Task GenerateInventory(CancellationToken cancellationToken = default)
    {
        try
        {
            var useProxy = this is GeneratorProxyServer;

            if (!useProxy && string.IsNullOrWhiteSpace(CustomAPIEndpoint) && (SteamWebAPIKey == string.Empty || SteamWebAPIKey == null))
            {
                _log.Debug("Empty Steam Web API Key, skipping generate inventory...");
                return;
            }

            _log.Debug("Generating inventory info...");
            var digest = string.Empty;
            using (var client = new HttpClient())
            {
                _log.Debug("Getting inventory digest...");
                JsonDocument digestJson;
                client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
                var apiUrl = GetSteamWebUrl("https://api.steampowered.com/IInventoryService/GetItemDefMeta/v1", $"appid={AppID}");

                client.Timeout = TimeSpan.FromSeconds(30);
                var response = await LimitSteamWebApiGET(client,
                    new HttpRequestMessage(HttpMethod.Get, apiUrl), cancellationToken);
                var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var responseCode = response.StatusCode;
                if (responseCode == HttpStatusCode.OK)
                {
                    _log.Debug("Got inventory digest.");
                    digestJson = JsonDocument.Parse(responseBody);
                }
                else if (responseCode == HttpStatusCode.Forbidden)
                {
                    _log.Error(
                        "Error 403 in getting game inventory digest, please check your Steam Web API key. Skipping...");
                    throw new Exception("Error 403 in getting game inventory digest.");
                }
                else
                {
                    _log.Error("Error {Code} in getting game inventory digest. Skipping...", responseCode);
                    throw new Exception($"Error {responseCode} in getting game inventory digest. Skipping...");
                }

                if (response.Content != null)
                {
                    var responsejson =
                        JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken)
                            .ConfigureAwait(false));
                    if (responsejson.RootElement.TryGetProperty("response", out var responsedata))
                        digest = responsedata.GetProperty("digest").ToString();
                }

                if (digest == null)
                {
                    _log.Debug("No inventory digest, skipping...");
                    return;
                }
            }

            using (var client = new HttpClient())
            {
                _log.Debug("Getting inventory items...");
                client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
                client.Timeout = TimeSpan.FromSeconds(30);
                var apiUrl2 = GetSteamWebUrl("https://api.steampowered.com/IGameInventory/GetItemDefArchive/v0001", $"appid={AppID}&digest={digest.Trim(new[] { '\"' })}");

                var response = await LimitSteamWebApiGET(client,
                    new HttpRequestMessage(HttpMethod.Get, apiUrl2), cancellationToken);

                if (response.StatusCode == HttpStatusCode.OK)
                {
                    if (response.Content != null)
                    {
                        var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                        var items = JsonNode.Parse(content.Trim(new[] { '\0' }))?.Root.AsArray();
                        if (items?.Count > 0)
                        {
                            _log.Debug("Found items, generating...");
                            var inventory = new JsonObject();
                            var inventorydefault = new JsonObject();
                            foreach (var item in items)
                            {
                                var x = new JsonObject();
                                var index = item?["itemdefid"]?.ToString();

                                if (item != null)
                                    foreach (var t in item.AsObject())
                                        if (t.Key != null && t.Value != null)
                                            x.Add(t.Key, t.Value.ToString());

                                inventory.Add(index!, x);
                                inventorydefault.Add(index!, 1);
                            }

                            File.WriteAllText(Path.Combine(ConfigPath, "items.json"), inventory.ToString());
                            File.WriteAllText(Path.Combine(ConfigPath, "default_items.json"),
                                inventorydefault.ToString());
                            return;
                        }
                    }
                    else
                    {
                        _log.Information("No inventory items. Skipping...");
                        return;
                    }
                }
                else
                {
                    throw new Exception($"Error {response.StatusCode} in getting game inventory.");
                }
            }

            _log.Debug("Generated inventory info.");
        }
        catch (KeyNotFoundException)
        {
            _log.Information("No inventory, skipping...");
        }
        catch (OperationCanceledException)
        {
            _log.Debug("Operation was canceled.");
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to generate inventory info. Skipping...");
        }
    }

    protected async Task DownloadImageAsync(string imageFolder, Achievement achievement,
        CancellationToken cancellationToken = default)
    {
        using var client = new HttpClient();
        try
        {
            var fileName = Path.GetFileName(achievement.Icon);
            var targetPath = Path.Combine(imageFolder, fileName);
            if (!DownloadedFile.Exists(x => x == targetPath))
            {
                DownloadedFile.Add(targetPath);
                var response = await client.GetAsync(new Uri(achievement.Icon, UriKind.Absolute), cancellationToken);
                response.EnsureSuccessStatusCode();
                await using var fs = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None);
                await response.Content.CopyToAsync(fs, cancellationToken);
            }
            else
            {
                _log.Debug("Image {targetPath} already downloaded. Skipping...", targetPath);
            }
        }
        catch (OperationCanceledException)
        {
            _log.Debug("Operation was canceled.");
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to download image of achievement \"{name}\", skipping...", achievement.Name);
        }

        try
        {
            var fileNameGray = Path.GetFileName(achievement.IconGray);
            var targetPathGray = Path.Combine(imageFolder, fileNameGray);
            if (!DownloadedFile.Exists(x => x == targetPathGray))
            {
                DownloadedFile.Add(targetPathGray);
                var response =
                    await client.GetAsync(new Uri(achievement.IconGray, UriKind.Absolute), cancellationToken);
                response.EnsureSuccessStatusCode();
                await using var fs = new FileStream(targetPathGray, FileMode.Create, FileAccess.Write, FileShare.None);
                await response.Content.CopyToAsync(fs, cancellationToken);
            }
            else
            {
                _log.Debug("Gray image {targetPath} already downloaded. Skipping...", targetPathGray);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to download gray image of achievement \"{name}\", skipping...", achievement.Name);
        }
    }

    protected async Task GenerateAchievements(CancellationToken cancellationToken = default)
    {
        try
        {
            _log.Debug("Generating achievements...");
            var achievementList = new List<Achievement>();
            var achievementData = GameSchema!.RootElement.GetProperty("game")
                    .GetProperty("availableGameStats")
                    .GetProperty("achievements");

            achievementList = JsonSerializer.Deserialize<List<Achievement>>(achievementData.GetRawText());
            if (achievementList?.Count > 0)
            {
                var empty = achievementList.Count == 1 ? "" : "s";
                _log.Debug($"Successfully got {achievementList.Count} achievement{empty}.");
            }
            else
            {
                _log.Debug("No achievements found.");
                return;
            }

            if (GenerateImages)
            {
                _log.Debug("Downloading achievement images...");
                var imagePath = Path.Combine(ConfigPath, "achievement_images");
                Directory.CreateDirectory(imagePath);

                IEnumerable<Task> downloadTasksQuery =
                    from achievement in achievementList
                    select DownloadImageAsync(imagePath, achievement, cancellationToken);

                var downloadTasks = downloadTasksQuery.ToList();
                while (downloadTasks.Any())
                {
                    var finishedTask = await Task.WhenAny(downloadTasks);
                    downloadTasks.Remove(finishedTask);
                    cancellationToken.ThrowIfCancellationRequested();
                }

                _log.Debug("Downloaded achievement images.");
            }

            _log.Debug("Saving achievements...");
            foreach (var achievement in achievementList)
            {
                // Update achievement list to point to local images instead
                achievement.Icon = $"achievement_images/{Path.GetFileName(achievement.Icon)}";
                achievement.IconGray = $"achievement_images/{Path.GetFileName(achievement.IconGray)}";
            }

            var achievementJson = JsonSerializer.Serialize(
                achievementList,
                new JsonSerializerOptions
                {
                    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                    WriteIndented = true
                });
            await File.WriteAllTextAsync(Path.Combine(ConfigPath, "achievements.json"), achievementJson,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (KeyNotFoundException)
        {
            _log.Information("No achievements, skipping...");
        }
        catch (OperationCanceledException)
        {
            _log.Debug("Operation was canceled.");
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to generate achievements. Skipping...");
        }

        _log.Debug("Generated achievements.");
    }

    protected async Task GenerateStats(CancellationToken cancellationToken = default)
    {
        await Task.Run(() =>
        {
            try
            {
                _log.Debug("Generating stats...");
                var statData = GameSchema!.RootElement.GetProperty("game")
                    .GetProperty("availableGameStats")
                    .GetProperty("stats");

                _log.Debug("Saving stats...");
                var statsList = new List<JsonObject>();
                
                foreach (var stat in statData.EnumerateArray())
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var name = "";
                    var defaultValue = "";

                    if (stat.TryGetProperty("name", out var _name)) name = _name.GetString() ?? "";
                    if (stat.TryGetProperty("defaultvalue", out var _defaultvalue))
                        defaultValue = _defaultvalue.ToString();

                    var statObject = new JsonObject
                    {
                        ["name"] = name,
                        ["type"] = "int",
                        ["default"] = defaultValue,
                        ["global"] = "0"
                    };
                    
                    statsList.Add(statObject);
                }

                if (statsList.Count > 0)
                {
                    var statsJson = JsonSerializer.Serialize(
                        statsList,
                        new JsonSerializerOptions
                        {
                            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                            WriteIndented = true
                        });
                    File.WriteAllText(Path.Combine(ConfigPath, "stats.json"), statsJson);
                    
                    var empty = statsList.Count == 1 ? "" : "s";
                    _log.Debug($"Successfully got {statsList.Count} stat{empty}.");
                }
                else
                {
                    _log.Debug("No stat found.");
                    return;
                }
            }
            catch (OperationCanceledException)
            {
                _log.Debug("Operation was canceled.");
            }
            catch (KeyNotFoundException)
            {
                _log.Information("No stats, skipping...");
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Failed to generate stats. Skipping...");
            }

            _log.Debug("Generated stats.");
        }, cancellationToken);
    }

    protected async Task<HttpResponseMessage> LimitSteamWebApiGET(HttpClient http_client,
        HttpRequestMessage http_request, CancellationToken cancellationToken = default)
    {
        // Steam has a limit of 300 requests every 5 minutes (1 request per second).
        if (DateTime.Now - LastWebRequestTime < TimeSpan.FromSeconds(1))
            Thread.Sleep(TimeSpan.FromSeconds(1));

        LastWebRequestTime = DateTime.Now;

        return await http_client.SendAsync(http_request, HttpCompletionOption.ResponseContentRead, cancellationToken)
            .ConfigureAwait(false);
    }

    protected void WriteIni()
    {
        try
        {
            _log.Debug("Writing configs.app.ini...");
            config_app.SaveTo(Path.Combine(ConfigPath, "configs.app.ini"));
        }
        catch (Exception ex)
        {
            _log.Information(ex, "Failed to Write configs.app.ini");
        }
    }

    protected class DLC
    {
        public uint DLCId { get; set; } = 0;
        public KeyValue? Info { get; set; }
    }

    public class Achievement
    {
        /// <summary>
        ///     Achievement description.
        /// </summary>
        [JsonPropertyName("description")]
        public string Description { get; set; } = string.Empty;

        /// <summary>
        ///     Human readable name, as shown on webpage, game library, overlay, etc.
        /// </summary>
        [JsonPropertyName("displayName")]
        public string DisplayName { get; set; } = string.Empty;

        /// <summary>
        ///     Is achievement hidden? 0 = false, else true.
        /// </summary>
        [JsonPropertyName("hidden")]
        public int Hidden { get; set; } = 0;

        /// <summary>
        ///     Path to icon when unlocked (colored).
        /// </summary>
        [JsonPropertyName("icon")]
        public string Icon { get; set; } = string.Empty;

        /// <summary>
        ///     Path to icon when locked (grayed out).
        /// </summary>
        // ReSharper disable once StringLiteralTypo
        [JsonPropertyName("icongray")]
        public string IconGray { get; set; } = string.Empty;

        /// <summary>
        ///     Internal name.
        /// </summary>
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;
    }
}

internal class GeneratorSteamClient : Generator
{
    private static Steam3Session? steam3;

    public GeneratorSteamClient(EMUGameInfoConfig GameInfoConfig) : base(GameInfoConfig)
    {
    }

    private async Task<byte[]> DownloadPubfileAsync(ulong publishedFileId,
        CancellationToken cancellationToken = default)
    {
        var details = await steam3!.GetPublishedFileDetails(publishedFileId);

        cancellationToken.ThrowIfCancellationRequested();

        if (!string.IsNullOrEmpty(details?.file_url))
            return await DownloadWebFile(details.filename, details.file_url, cancellationToken);

        _log.Warning("Publish File {id} doesn't contain file_url.", publishedFileId);
        throw new Exception("Unable to download publish file.");
    }

    private async Task<byte[]> DownloadWebFile(string fileName, string url,
        CancellationToken cancellationToken = default)
    {
        using (var client = HttpClientFactory.CreateHttpClient())
        {
            _log.Debug("Downloading {0}", fileName);
            using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task Steam3Start(CancellationToken cancellationToken = default)
    {
        await Task.Run(() =>
        {
            try
            {
                _log.Information("Starting Steam3 Session...");

                steam3 = new Steam3Session(
                    new SteamUser.LogOnDetails
                    {
                        Username = null,
                        Password = null
                    }
                    , cancellationToken
                );
            }
            catch (OperationCanceledException)
            {
                _log.Debug("Operation was canceled.");
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Failed to start Steam3 Session.");
                throw new Exception("Failed to start Steam3 Session.");
            }

            _log.Debug("Started Steam3 Session...");
        });
    }

    private async Task<KeyValue> GetSteam3AppSection(uint appId, EAppInfoSection section)
    {
        return await Task.Run(() =>
        {
            try
            {
                if (steam3 == null || steam3.AppInfo == null) return null;

                if (!steam3.AppInfo.TryGetValue(appId, out var app) || app == null) return null;

                var appinfo = app.KeyValues;
                var section_key = section switch
                {
                    EAppInfoSection.Common => "common",
                    EAppInfoSection.Extended => "extended",
                    EAppInfoSection.Config => "config",
                    EAppInfoSection.Depots => "depots",
                    _ => throw new NotImplementedException()
                };
                var section_kv = appinfo.Children.Where(c => c.Name == section_key).FirstOrDefault();
                return section_kv;
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Failed to get Steam3 App Section.");
                throw new Exception("Failed to get Steam3 App Section.");
            }
        }) ?? KeyValue.Invalid;
    }

    private async Task GenerateControllerInfo(CancellationToken cancellationToken = default)
    {
        var controllerFolder = Path.Combine(ConfigPath, "controller");
        string[] supported_controllers_types =
        [ // in order of preference
            "controller_xbox360",
            "controller_xboxone",
            "controller_steamcontroller_gordon",

            // TODO not sure about these
            "controller_ps4",
            "controller_ps5",
            "controller_switch_pro",
            "controller_neptune"
        ];

        Dictionary<string, string> keymap_digital = new()
        {
            { "button_a", "A" },
            { "button_b", "B" },
            { "button_x", "X" },
            { "button_y", "Y" },
            { "dpad_north", "DUP" },
            { "dpad_south", "DDOWN" },
            { "dpad_east", "DRIGHT" },
            { "dpad_west", "DLEFT" },
            { "button_escape", "START" },
            { "button_menu", "BACK" },
            { "left_bumper", "LBUMPER" },
            { "right_bumper", "RBUMPER" },
            { "button_back_left", "Y" },
            { "button_back_right", "A" },
            { "button_back_left_upper", "X" },
            { "button_back_right_upper", "B" }
        };
        Dictionary<string, string> keymap_left_joystick = new()
        {
            { "dpad_north", "DLJOYUP" },
            { "dpad_south", "DLJOYDOWN" },
            { "dpad_west", "DLJOYLEFT" },
            { "dpad_east", "DLJOYRIGHT" },
            { "click", "LSTICK" }
        };
        Dictionary<string, string> keymap_right_joystick = new()
        {
            { "dpad_north", "DRJOYUP" },
            { "dpad_south", "DRJOYDOWN" },
            { "dpad_west", "DRJOYLEFT" },
            { "dpad_east", "DRJOYRIGHT" },
            { "click", "RSTICK" }
        };

        // these are found in "group_source_bindings"
        HashSet<string> supported_keys_digital =
        [
            "switch",
            "button_diamond",
            "dpad"
        ];
        HashSet<string> supported_keys_triggers =
        [
            "left_trigger",
            "right_trigger"
        ];
        HashSet<string> supported_keys_joystick =
        [
            "joystick",
            "right_joystick",
            "dpad"
        ];

        JsonObject LoadTextVdf(Stream inStream)
        {
            ArgumentNullException.ThrowIfNull(inStream);
            var format = KVSerializationFormat.KeyValues1Text;
            var kv = KVSerializer.Create(format);
            var vdfDataDoc = kv.Deserialize(inStream, new KVSerializerOptions
            {
                EnableValveNullByteBugBehavior = true
            });

            return ToJsonObj(vdfDataDoc);
        }

        JsonObject ToJsonObj(KVObject? vdfKeyValue)
        {
            if (vdfKeyValue is null) return new JsonObject();

            JsonObject rootJobj = new();
            Queue<(KVObject kvPair, JsonObject myJobj)> pending = new([
                (vdfKeyValue, rootJobj)
            ]);

            JsonNode? SingleVdfKvToJobj(KVValue val)
            {
                switch (val.ValueType)
                {
                    case KVValueType.Null:
                        return null;
                    case KVValueType.Collection:
                        return JsonNode.Parse("{}");
                    case KVValueType.Array:
                        return JsonNode.Parse("[]");
                    case KVValueType.BinaryBlob:
                        return JsonNode.Parse("[]");
                    case KVValueType.String:
                        return (string?)val ?? string.Empty;
                    case KVValueType.Int32:
                        return (int)val;
                    case KVValueType.UInt64:
                        return (ulong)val;
                    case KVValueType.FloatingPoint:
                        return (double)val;
                    case KVValueType.Pointer:
                        return (ulong)val;
                    case KVValueType.Int64:
                        return (long)val;
                    case KVValueType.Boolean:
                        return (bool)val;
                    default:
                        return val.ToString();
                }
            }

            while (pending.Count > 0)
            {
                var (kv, currentObj) = pending.Dequeue();
                var nameSafe = kv.Name is null ? string.Empty : kv.Name;
                if (!kv.Children.Any()) // regular "key" : "value"
                {
                    if (currentObj.TryGetPropertyValue(nameSafe, out var oldVal)) // name exists
                    {
                        if (oldVal is null) // convert it to array
                        {
                            /* "some_prop": null
                             *
                             * >>>
                             *
                             * "some_prop": [
                             *  null,
                             *  <new value here>
                             * ]
                             */
                            currentObj.Remove(nameSafe);
                            currentObj[nameSafe] = new JsonArray(null, SingleVdfKvToJobj(kv.Value));
                        }
                        else if (oldVal.GetValueKind() == JsonValueKind.Array) // previously converted
                        {
                            oldVal.AsArray().Add(kv.Value);
                        }
                        else // convert it to array
                        {
                            /* "some_prop": "old value"
                             *
                             * >>>
                             *
                             * "some_prop": [
                             *  "old value",
                             *  <new value here>
                             * ]
                             */
                            currentObj.Remove(nameSafe);
                            currentObj[nameSafe] = new JsonArray(oldVal, SingleVdfKvToJobj(kv.Value));
                        }
                    }
                    else // new name
                    {
                        currentObj[nameSafe] = SingleVdfKvToJobj(kv.Value);
                    }
                }
                else // nested object "key" : { ... }
                {
                    JsonObject newObj = new(); // new container for the key/value pairs

                    if (currentObj.TryGetPropertyValue(nameSafe, out var oldNode) && oldNode is not null)
                    {
                        // if key already exists then convert the parent container to array of objects
                        /*
                         * "controller_mappings": {
                         *  "group": {},                // ===== 1
                         * }
                         *
                         * >>>
                         *
                         * "controller_mappings": {
                         *  "group": [                  // ==== 2
                         *    {},
                         *    {},
                         *    {},
                         *  ]
                         * }
                         *
                         */
                        if (oldNode.GetValueKind() == JsonValueKind.Object) // ===== 1
                        {
                            // convert it to array
                            currentObj.Remove(nameSafe);
                            currentObj[nameSafe] = new JsonArray(oldNode, newObj); // ==== 2
                        }
                        else // already converted to array
                        {
                            oldNode.AsArray().Add(newObj);
                        }
                    }
                    else // entirely new key, start as an object/dictionary
                    {
                        currentObj[nameSafe] = newObj;
                    }

                    // add all nested elements for the next iterations
                    foreach (var item in kv.Children)
                        // the owner of element will be this new json object
                        pending.Enqueue((item, newObj));
                }
            }

            return rootJobj;
        }

        JsonObject ToObjSafe(JsonNode? obj)
        {
            if (obj is null) return new JsonObject();

            switch (obj.GetValueKind())
            {
                case JsonValueKind.Object: return obj.AsObject();
            }

            return new JsonObject();
        }

        string ToStringSafe(JsonNode? obj)
        {
            if (obj is null) return string.Empty;

            switch (obj.GetValueKind())
            {
                case JsonValueKind.String: return obj.ToString() ?? string.Empty;
            }

            return string.Empty;
        }

        double ToNumSafe(JsonNode? obj)
        {
            if (obj is null) return 0;

            switch (obj.GetValueKind())
            {
                case JsonValueKind.String:
                case JsonValueKind.Number:
                {
                    if (double.TryParse(obj.ToString() ?? string.Empty, CultureInfo.InvariantCulture, out var num) &&
                        !double.IsNaN(num)) return num;
                }
                    break;
                case JsonValueKind.True: return 1;
            }

            return 0;
        }

        JsonArray ToVdfArraySafe(JsonNode? node)
        {
            if (node is null) return [];

            switch (node.GetValueKind())
            {
                case JsonValueKind.Array:
                    return node.AsArray();
            }

            return [node.DeepClone()];
        }


        JsonNode? GetKeyIgnoreCase(JsonNode? obj, params string[] keys)
        {
            if (keys is null || keys.Length == 0) return null;

            var idx = 0;
            while (idx < keys.Length)
            {
                if (obj is null || obj.GetValueKind() != JsonValueKind.Object) return null;

                var currentObj = obj.AsObject();
                var objDict = currentObj
                    .GroupBy(kv => kv.Key.ToUpperInvariant(),
                        kv => (ActualKey: kv.Key, ActualObj: kv.Value)) // upper key <> [list of actual values]
                    .ToDictionary(g => g.Key, g => g.ToList());
                var currentKey = keys[idx];
                if (objDict.Count == 0 || !objDict.TryGetValue(currentKey.ToUpperInvariant(), out var objList) ||
                    objList.Count == 0) return null;

                obj = null;
                foreach (var (actualKey, actualObj) in objList)
                    if (string.Equals(currentKey, actualKey, StringComparison.Ordinal))
                    {
                        obj = actualObj;
                        break;
                    }

                idx++;
            }

            return obj;
        }

        void AddInputBindings(Dictionary<string, HashSet<string>> actions_bindings, JsonObject group,
            IReadOnlyDictionary<string, string> keymap, string? forced_btn_mapping = null)
        {
            var inputs = ToVdfArraySafe(GetKeyIgnoreCase(group, "inputs"));
            foreach (var inputObj in inputs)
            foreach (var btnKv in ToObjSafe(inputObj)) // "left_bumper", "button_back_left", ...
            foreach (var btnObj in ToVdfArraySafe(btnKv.Value))
            foreach (var btnPropKv in ToObjSafe(btnObj)) // "activators", ...
            foreach (var btnPropObj in ToVdfArraySafe(btnPropKv.Value))
            foreach (var btnPressTypeKv in ToObjSafe(btnPropObj)) // "Full_Press", ...
            foreach (var btnPressTypeObj in ToVdfArraySafe(btnPressTypeKv.Value))
            foreach (var pressTypePropsKv in ToObjSafe(btnPressTypeObj)) // "bindings", ...
            foreach (var pressTypePropsObj in ToVdfArraySafe(pressTypePropsKv.Value))
            foreach (var bindingKv in ToObjSafe(pressTypePropsObj)) // "binding", ...
            {
                if (!bindingKv.Key.Equals("binding", StringComparison.OrdinalIgnoreCase)) continue;

                /*
                 * ex1:
                 * "binding": [
                 *   "game_action ui ui_advpage0, Route Advisor Navigation Page",
                 *   "game_action ui ui_mapzoom_out, Map Zoom Out"
                 * ]   ^          ^       ^
                 *     ^       category   ^
                 *     type               action name
                 *
                 * ex2:
                 * "binding": [
                 *   "xinput_button TRIGGER_LEFT, Brake/Reverse"
                 * ]   ^              ^
                 *     ^              ^
                 *     type           action name
                 *
                 * 1. split and trim each string => string[]
                 * 2. save each string[]         => List<string[]>
                 */
                // each string is composed of:
                //   1. binding type, ex: "game_action", "xinput_button", ...
                //   2. (optional) action category, ex: "ui", should be from one of the previously parsed action list
                //   3. action name, ex: "ui_mapzoom_out" or "TRIGGER_LEFT"
                var current_btn_name = btnKv.Key; // "left_bumper", "button_back_left", ...

                var binding_instructions_lists = ToVdfArraySafe(bindingKv.Value)
                    .Select(obj => ToStringSafe(obj))
                    .Select(str =>
                        str.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                            .Select(element => element.Trim())
                            .ToArray()
                    )
                    .Where(list => list.Length > 1); // we need at least the instruction

                //Log.Instance.Write(Log.Kind.Debug, $"button '{current_btn_name}' has [{binding_instructions_lists.Count()}] binding instructions (group/.../activators/Full_Press/bindings/binding/[ <INSTRUCTIONS_LIST> ])");
                foreach (var binding_instructions in binding_instructions_lists)
                {
                    var binding_type = binding_instructions[0];
                    string? action_name = null;
                    if (binding_type.Equals("game_action", StringComparison.OrdinalIgnoreCase) &&
                        binding_instructions.Length >= 3)
                        action_name = binding_instructions[2]; // ex: "ui_mapzoom_out,"
                    else if (binding_type.Equals("xinput_button", StringComparison.OrdinalIgnoreCase) &&
                             binding_instructions.Length >= 2)
                        action_name = binding_instructions[1]; // ex: "TRIGGER_LEFT,"

                    if (action_name is null)
                    {
                        _log.Debug(
                            $"unsupported binding type '{binding_type}' in button '{current_btn_name}' (group/.../activators/Full_Press/bindings/binding/['<BINDING_TYPE> ...'])");
                        continue;
                    }

                    if (action_name.Last() == ',') action_name = action_name.Substring(0, action_name.Length - 1);

                    string? btn_binding = null;
                    if (forced_btn_mapping is null)
                    {
                        if (keymap.TryGetValue(current_btn_name.ToLowerInvariant(), out var mapped_btn_binding))
                        {
                            btn_binding = mapped_btn_binding;
                        }
                        else
                        {
                            _log.Debug($"keymap is missing button '{current_btn_name}'");
                            continue;
                        }
                    }
                    else
                    {
                        btn_binding = forced_btn_mapping;
                    }

                    if (btn_binding is not null)
                    {
                        HashSet<string>? action_bindings_set;
                        if (!actions_bindings.TryGetValue(action_name, out action_bindings_set))
                        {
                            action_bindings_set = [];
                            actions_bindings[action_name] = action_bindings_set;
                        }

                        action_bindings_set.Add(btn_binding);
                    }
                    else
                    {
                        _log.Debug($"missing keymap for btn '{current_btn_name}' (group/inputs/<BTN_NAME>)");
                    }
                }
            }
        }

        void SaveControllerVdfObj(JsonObject con)
        {
            var controller_mappings = ToObjSafe(GetKeyIgnoreCase(con, "controller_mappings"));

            var groups = ToVdfArraySafe(GetKeyIgnoreCase(controller_mappings, "group"));
            var actions = ToVdfArraySafe(GetKeyIgnoreCase(controller_mappings, "actions"));
            var presets = ToVdfArraySafe(GetKeyIgnoreCase(controller_mappings, "preset"));

            // each group defines the controller key and its binding
            var groups_by_id = groups
                .Select(g => new KeyValuePair<uint, JsonObject>(
                    (uint)ToNumSafe(GetKeyIgnoreCase(g, "id")), ToObjSafe(g)
                ))
                .ToDictionary(kv => kv.Key, kv => kv.Value)
                .AsReadOnly();

            // list of supported actions
            /*
             * ex:
             * "actions": {
             *   "ui": { ... },
             *   "driving": { ... }
             * },
             */
            var supported_actions_list = actions
                .SelectMany(a => ToObjSafe(a))
                .Select(kv => kv.Key.ToUpperInvariant())
                .ToHashSet();

            /*
             * ex:
             * {   preset name
             *     /
             *   "ui": {
             *     "ui_advpage0": ["LJOY=joystick_move"],
             *        ^                 ^
             *       action name        ^
             *                         action bindings set
             *     ...
             *     ...
             *   },
             *
             *   "driving": {
             *     "driving_abackward": ["LBUMPER"],
             *     ...
             *     ...
             *   },
             *
             *   ...
             *   ...
             * }
             */
            var presets_actions_bindings = new Dictionary<string, Dictionary<string, HashSet<string>>>();

            /*
             * "id": 0,
             * "name": "ui",
             * "group_source_bindings": {
             *   "13": "switch active",
             *   "16": "right_trigger active",
             *   "65": "joystick active",
             *   "15": "left_trigger active",
             *   "60": "right_joystick inactive",
             *   "66": "right_joystick active"
             * }
             *
             * each preset has:
             *   1. action name, could be in the previous list or a standalone/new one
             *   2. the key bindings (groups) of this preset
             *      * each key binding entry is a key-value pair:
             *        <group ID> - <button_name SPACE active/inactive>
             *  also notice how the last 2 key-value pairs define the same "right_joystick",
             *  but one is active (ID=66) and the other is inactive (ID=60)
             */
            foreach (var presetObj in presets)
            {
                var preset_name = ToStringSafe(GetKeyIgnoreCase(presetObj, "name"));
                // find this preset in the parsed actions list
                if (!supported_actions_list.Contains(preset_name.ToUpperInvariant()) &&
                    !preset_name.Equals("default", StringComparison.OrdinalIgnoreCase)) continue;

                var group_source_bindings = ToObjSafe(GetKeyIgnoreCase(presetObj, "group_source_bindings"));
                var bindings_map = new Dictionary<string, HashSet<string>>();
                foreach (var group_source_binding_kv in group_source_bindings)
                {
                    uint group_number = 0;
                    if (!uint.TryParse(group_source_binding_kv.Key, CultureInfo.InvariantCulture, out group_number)
                        || !groups_by_id.ContainsKey(group_number))
                    {
                        _log.Debug(
                            $"group_source_bindings with ID '{group_source_binding_kv.Key}' has bad number");
                        continue;
                    }

                    var group_source_binding_elements = ToStringSafe(group_source_binding_kv.Value)
                        .Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(str => str.Trim())
                        .ToArray();
                    /*
                     * "group_source_bindings": {
                     *   "10": "switch active",
                     *   "11": "button_diamond active",
                     *   "12": "left_trigger inactive",
                     *   "18": "left_trigger active",
                     *   "13": "right_trigger inactive",
                     *   "19": "right_trigger active",
                     *   "14": "right_joystick active",
                     *   "15": "dpad inactive",
                     *   "16": "dpad active",
                     *   "17": "joystick active",
                     *   "21": "left_trackpad active",
                     *   "20": "right_trackpad active"
                     * }
                     */
                    if (group_source_binding_elements.Length < 2 || !group_source_binding_elements[1]
                            .Equals("active", StringComparison.OrdinalIgnoreCase)) continue;

                    // ex: "button_diamond", "right_trigger", "dpad" ...
                    var btn_name_lower = group_source_binding_elements[0].ToLowerInvariant();
                    if (supported_keys_digital.Contains(btn_name_lower))
                    {
                        var group = groups_by_id[group_number];
                        AddInputBindings(bindings_map, group, keymap_digital);
                    }

                    if (supported_keys_triggers.Contains(btn_name_lower))
                    {
                        var group = groups_by_id[group_number];
                        var group_mode = ToStringSafe(GetKeyIgnoreCase(group, "mode"));
                        if (group_mode.Equals("trigger", StringComparison.OrdinalIgnoreCase))
                        {
                            foreach (var groupProp in group)
                                /*
                                 * "group": [
                                 *   {
                                 *     "id": 36,
                                 *     "mode": "trigger",
                                 *     "inputs": {
                                 *       ...
                                 *     }
                                 *     ...
                                 *     "gameactions": {
                                 *       "driving": "driving_abackward"
                                 *     }               ^
                                 *                     ^
                                 *     ...           action name
                                 *   }
                                 */
                                if (groupProp.Key.Equals("gameactions", StringComparison.OrdinalIgnoreCase))
                                {
                                    // ex: action_name = "driving_abackward"
                                    var action_name = ToStringSafe(GetKeyIgnoreCase(groupProp.Value, preset_name));
                                    string binding;
                                    if (string.Equals(btn_name_lower, "left_trigger",
                                            StringComparison.OrdinalIgnoreCase))
                                        binding = "LTRIGGER";
                                    else
                                        binding = "RTRIGGER";

                                    var binding_with_trigger = $"{binding}=trigger";
                                    if (bindings_map.TryGetValue(action_name, out var action_set))
                                    {
                                        if (!action_set.Contains(binding) && !action_set.Contains(binding_with_trigger))
                                            action_set.Add(binding);
                                    }
                                    else
                                    {
                                        bindings_map[action_name] = [binding_with_trigger];
                                    }
                                }
                                else if (groupProp.Key.Equals("inputs", StringComparison.OrdinalIgnoreCase))
                                {
                                    string binding;
                                    if (string.Equals(btn_name_lower, "left_trigger",
                                            StringComparison.OrdinalIgnoreCase))
                                        binding = "DLTRIGGER";
                                    else
                                        binding = "DRTRIGGER";
                                    AddInputBindings(bindings_map, group, keymap_digital, binding);
                                }
                        }
                        else
                        {
                            _log.Debug(
                                $"group with ID [{group_number}] has unknown trigger mode '{group_mode}'");
                        }
                    }

                    if (supported_keys_joystick.Contains(btn_name_lower))
                    {
                        var group = groups_by_id[group_number];
                        var group_mode = ToStringSafe(GetKeyIgnoreCase(group, "mode"));
                        if (group_mode.Equals("joystick_move", StringComparison.OrdinalIgnoreCase))
                        {
                            foreach (var groupProp in group)
                                /*
                                 * "group": [
                                 *   {
                                 *     "id": 36,
                                 *     "mode": "trigger",
                                 *     "inputs": {
                                 *       ...
                                 *     }
                                 *     ...
                                 *     "gameactions": {
                                 *       "driving": "driving_abackward"
                                 *     }
                                 *     ...
                                 *   }
                                 */
                                if (groupProp.Key.Equals("gameactions", StringComparison.OrdinalIgnoreCase))
                                {
                                    var action_name = ToStringSafe(GetKeyIgnoreCase(groupProp.Value, preset_name));
                                    string binding;
                                    if (string.Equals(btn_name_lower, "joystick", StringComparison.OrdinalIgnoreCase))
                                        binding = "LJOY";
                                    else if (string.Equals(btn_name_lower, "right_joystick",
                                                 StringComparison.OrdinalIgnoreCase))
                                        binding = "RJOY";
                                    else
                                        binding = "DPAD";

                                    var binding_with_joystick = $"{binding}=joystick_move";
                                    if (bindings_map.TryGetValue(action_name, out var action_set))
                                    {
                                        if (!action_set.Contains(binding) &&
                                            !action_set.Contains(binding_with_joystick)) action_set.Add(binding);
                                    }
                                    else
                                    {
                                        bindings_map[action_name] = [binding_with_joystick];
                                    }
                                }
                                else if (groupProp.Key.Equals("inputs", StringComparison.OrdinalIgnoreCase))
                                {
                                    string binding;
                                    if (string.Equals(btn_name_lower, "joystick", StringComparison.OrdinalIgnoreCase))
                                        binding = "LSTICK";
                                    else
                                        binding = "RSTICK";
                                    AddInputBindings(bindings_map, group, keymap_digital, binding);
                                }
                        }
                        else if (group_mode.Equals("dpad", StringComparison.OrdinalIgnoreCase))
                        {
                            if (string.Equals(btn_name_lower, "joystick", StringComparison.OrdinalIgnoreCase))
                                AddInputBindings(bindings_map, group, keymap_left_joystick);
                            else if (string.Equals(btn_name_lower, "right_joystick",
                                         StringComparison.OrdinalIgnoreCase))
                                AddInputBindings(bindings_map, group, keymap_right_joystick);
                            // dpad 
                        }
                        else
                        {
                            _log.Debug($"group with ID [{group_number}] has unknown joystick mode '{group_mode}'");
                        }
                    }
                }

                presets_actions_bindings[preset_name] = bindings_map;
            }

            if (presets_actions_bindings.Count > 0)
            {
                Directory.CreateDirectory(controllerFolder);
                foreach (var (presetName, presetObj) in presets_actions_bindings)
                {
                    List<string> filecontent = [];
                    foreach (var (actionName, actionBindingsSet) in presetObj)
                        filecontent.Add($"{actionName}={string.Join(',', actionBindingsSet)}");

                    var filepath = Path.Combine(controllerFolder, $"{presetName}.txt");
                    File.WriteAllLines(filepath, filecontent, new UTF8Encoding(false));
                }
            }
            else
            {
                _log.Warning("No supported controller presets were found");
            }
        }

        try
        {
            _log.Debug("Generating Controller Info...");
            var GameInfoConfig = await GetSteam3AppSection(AppID, EAppInfoSection.Config).ConfigureAwait(false);
            if (GameInfoConfig == null)
            {
                _log.Warning("Failed to get controller info, skipping...(AppID: {appid})", AppID);
                return;
            }

            if (GameInfoConfig["steamcontrollerconfigdetails"] == KeyValue.Invalid)
            {
                _log.Debug("No Controller Info, Skipping...");
                return;
            }


            var supportedCons = GameInfoConfig["steamcontrollerconfigdetails"].Children.Where(
                c => supported_controllers_types.Contains(c["controller_type"].Value)
                     && c["enabled_branches"].Value!.Split(",")
                         .Any(br => br.Equals("default", StringComparison.OrdinalIgnoreCase)));

            cancellationToken.ThrowIfCancellationRequested();

            KeyValue? con = null;
            foreach (var item in supported_controllers_types)
            {
                foreach (var supportedCon in supportedCons)
                    if (supportedCon["controller_type"].Value!.Equals(item, StringComparison.OrdinalIgnoreCase))
                    {
                        con = supportedCon;
                        break;
                    }

                if (con != null) break;
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (con == null)
            {
                _log.Warning("Failed to get supported controller info, skipping...(AppID: {appid})", AppID);
                return;
            }

            _log.Debug("Downloading controller vdf file {id} (Type: {type})...", con.Name,
                con["controller_type"].Value);
            var controller_vdf = await DownloadPubfileAsync(Convert.ToUInt64(con.Name), cancellationToken);
            using (var vdfStream = new MemoryStream(controller_vdf, false))
            {
                var controller_vdf_json = LoadTextVdf(vdfStream);
                SaveControllerVdfObj(controller_vdf_json);
            }

            _log.Debug("Generated Controller Info.");
        }
        catch (OperationCanceledException)
        {
            _log.Debug("Operation was canceled.");
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Failed to generate controller info.");
        }
    }

    private async Task GenerateSupportedLang(CancellationToken cancellationToken = default)
    {
        try
        {
            _log.Debug("Generating supported_languages.txt...");
            var GameInfoCommon = await GetSteam3AppSection(AppID, EAppInfoSection.Common).ConfigureAwait(false);
            if (GameInfoCommon == null)
            {
                _log.Error("Failed to get game info, skipping generate supported languages...(AppID: {appid})", AppID);
                return;
            }

            if (GameInfoCommon["supported_languages"] != KeyValue.Invalid)
            {
                _log.Debug("Writing supported_languages.txt...");
                var sw = new StreamWriter(Path.Combine(ConfigPath, "supported_languages.txt"));
                var newline = "";
                GameInfoCommon["supported_languages"].Children.ForEach(delegate(KeyValue language)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (language.Children.Exists(x => x.Name == "supported" && x.Value == "true"))
                    {
                        sw.Write(newline + language.Name);
                        newline = Environment.NewLine;
                    }
                });
                sw.Close();
            }
        }
        catch (OperationCanceledException)
        {
            _log.Debug("Operation was canceled.");
        }
        catch (Exception ex)
        {
            _log.Information(ex, "Failed to generate supported_languages.txt. Skipping...");
        }

        _log.Debug("Generated supported_languages.txt.");
    }

    private async Task GenerateDepots(CancellationToken cancellationToken = default)
    {
        try
        {
            _log.Debug("Generating depot infos...");
            var GameInfoDepots = await GetSteam3AppSection(AppID, EAppInfoSection.Depots).ConfigureAwait(false);
            if (GameInfoDepots == null)
            {
                _log.Error("Failed to get game info, skipping generate depots...(AppID: {appid})", AppID);
                return;
            }

            if (GameInfoDepots != KeyValue.Invalid)
            {
                _log.Debug("Writing depots.txt...");
                var swdepots = new StreamWriter(Path.Combine(ConfigPath, "depots.txt"));
                var newline = "";
                GameInfoDepots.Children.ForEach(delegate(KeyValue DepotIDs)
                {
                    uint DepotID = 0;
                    if (uint.TryParse(DepotIDs.Name, out DepotID))
                    {
                        swdepots.Write(newline + DepotID);
                        newline = Environment.NewLine;
                    }
                });
                swdepots.Close();

                _log.Debug("Writing branches.json...");
                if (GameInfoDepots.Children.Exists(x => x.Name == "branches"))
                {
                    var branches = new JsonArray();

                    foreach (var branch in GameInfoDepots["branches"].Children)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        var branchObject = new JsonObject();

                        var description = "";
                        var prot = false;
                        uint buildid = 0;
                        uint timeupdated = 0;

                        branchObject.Add("name", branch.Name);

                        if (branch.Children.Exists(x => x.Name == "description"))
                            description = branch["description"].Value;
                        if (branch.Children.Exists(x => x.Name == "pwdrequired"))
                            if (branch["pwdrequired"].Value == "1" || branch["pwdrequired"].Value == "true")
                                prot = true;

                        branch.Children.Exists(x => x.Name == "buildid" && uint.TryParse(x.Value, out buildid));
                        branch.Children.Exists(x => x.Name == "timeupdated" && uint.TryParse(x.Value, out timeupdated));

                        branchObject.Add("description", description);
                        branchObject.Add("protected", prot);
                        branchObject.Add("build_id", buildid);
                        branchObject.Add("time_updated", timeupdated);

                        branches.Add(branchObject);
                    }

                    File.WriteAllText(Path.Combine(ConfigPath, "branches.json"), branches.ToString());
                }
            }
        }
        catch (OperationCanceledException)
        {
            _log.Debug("Operation was canceled.");
        }
        catch (Exception ex)
        {
            _log.Information(ex, "Failed to generate depot infos. Skipping...");
        }

        _log.Debug("Generated depot infos.");
    }

    private async Task GenerateDLCs(CancellationToken cancellationToken = default)
    {
        try
        {
            _log.Debug("Generating DLCs...");
            var GameInfoDLCs = await GetSteam3AppSection(AppID, EAppInfoSection.Extended).ConfigureAwait(false);
            if (GameInfoDLCs == null)
            {
                _log.Error("Failed to get game info, skipping generate DLCs...(AppID: {appid})", AppID);
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();

            var DLCIds = new List<uint>();
            if (GameInfoDLCs["listofdlc"] != KeyValue.Invalid)
            {
                _log.Debug("Getting DLCs from Extended section...");
                if (GameInfoDLCs["listofdlc"].Value != null)
                {
                    DLCIds.AddRange(
                        new List<string>(GameInfoDLCs["listofdlc"].Value?.Split(',') ?? Array.Empty<string>()).ConvertAll(x =>
                            Convert.ToUInt32(x)));
                }
                else
                {
                    _log.Information("No DLC in Extended section, skipping...(AppID: {appid})", AppID);
                    return;
                }
            }

            cancellationToken.ThrowIfCancellationRequested();

            var GameInfoDepots = await GetSteam3AppSection(AppID, EAppInfoSection.Depots).ConfigureAwait(false);

            GameInfoDepots.Children.ForEach(delegate(KeyValue DepotIDs)
            {
                uint dlcid = 0;
                if (DepotIDs.Children.Exists(x => x.Name == "dlcappid" && uint.TryParse(x.Value, out dlcid)))
                    if (!DLCIds.Contains(dlcid))
                        DLCIds.Add(dlcid);
            });

            if (DLCIds.Count == 0)
            {
                _log.Debug("No DLCs. Skipping...");
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (UseSteamWebAppList)
            {
                await SteamAppList.WaitForReady().ConfigureAwait(false);
                _log.Debug("Using Steam Web App list.");
                var DLCInfos = new List<SteamApp>();
                foreach (var DLCId in DLCIds)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    DLCInfos.Add(await SteamAppList.GetAppById(DLCId).ConfigureAwait(false));
                }

                var dlcsection = new Section("app::dlcs")
                {
                    new Property("unlock_all", "0", " 1=report all DLCs as unlocked",
                        " 0=report only the DLCs mentioned",
                        " some games check for \"hidden\" DLCs, hence this should be set to 1 in that case",
                        " but other games detect emus by querying for a fake/bad DLC, hence this should be set to 0 in that case",
                        " default=1")
                };

                dlcsection.AddComment(" format: ID=name");
                foreach (var DLC in DLCInfos)
                {
                    string? name;
                    string id;
                    name = DLC.Name;
                    if (DLC.AppId.HasValue)
                    {
                        id = DLC.AppId.Value.ToString();
                        if (name == null || name == string.Empty) name = "Unknown Steam app " + id;
                        dlcsection.Add(new Property(id, name));
                    }
                }

                dlcsection.Items.Add(new BlankLine());
                config_app.Add(dlcsection);
            }
            else
            {
                _log.Debug("Using Steam3 App list.");
                var DLCs = new Dictionary<uint, KeyValue>();
                IEnumerable<Task> getInfoTasksQuery =
                    from DLCId in DLCIds
                    select GetAppInfo(DLCId);

                var getInfoTasks = getInfoTasksQuery.ToList();
                while (getInfoTasks.Any())
                {
                    var finishedTask = await Task.WhenAny(getInfoTasks);
                    getInfoTasks.Remove(finishedTask);
                    cancellationToken.ThrowIfCancellationRequested();
                }

                foreach (var DLCId in DLCIds)
                    if (!DLCs.ContainsKey(DLCId))
                        DLCs.Add(DLCId, await GetSteam3AppSection(DLCId, EAppInfoSection.Common).ConfigureAwait(false));

                var dlcsection = new Section("app::dlcs")
                {
                    new Property("unlock_all", "0", " 1=report all DLCs as unlocked",
                        " 0=report only the DLCs mentioned",
                        " some games check for \"hidden\" DLCs, hence this should be set to 1 in that case",
                        " but other games detect emus by querying for a fake/bad DLC, hence this should be set to 0 in that case",
                        " default=1")
                };
                dlcsection.AddComment(" format: ID=name");

                foreach (var DLC in DLCs)
                {
                    string? name;
                    string id;
                    if (DLC.Value != null)
                    {
                        name = DLC.Value.Children.Find(x => x.Name == "name")?.Value;
                        id = DLC.Key.ToString();
                        if (name == null || name == string.Empty) name = "Unknown Steam app " + id;
                        dlcsection.Add(new Property(id, name));
                    }
                }

                dlcsection.Items.Add(new BlankLine());
                config_app.Add(dlcsection);
            }
        }
        catch (OperationCanceledException)
        {
            _log.Debug("Operation was canceled.");
        }
        catch (Exception ex)
        {
            _log.Information(ex, "Failed to generate DLCs. Skipping...");
        }

        _log.Debug("Generated DLCs.");
    }

    private async Task GetAppInfo(uint appID)
    {
        await steam3!.RequestAppInfo(appID, true).ConfigureAwait(false);
    }

    private bool WaitForConnected(CancellationToken cancellationToken = default)
    {
        if (steam3!.IsLoggedOn || steam3.bAborted)
            return steam3.IsLoggedOn;

        steam3.WaitUntilCallback(() => { }, () => steam3.IsLoggedOn, cancellationToken);

        return steam3.IsLoggedOn;
    }

    public override async Task InfoGenerator(CancellationToken cancellationToken = default)
    {
        try
        {
            await GenerateBasic().ConfigureAwait(false);
            await Steam3Start(cancellationToken).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();

            var TaskA = Task.Run(async () =>
            {
                if (await GetGameSchema(cancellationToken).ConfigureAwait(false))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var Tasks2 = new List<Task>
                        { GenerateAchievements(cancellationToken), GenerateStats(cancellationToken) };
                    while (Tasks2.Count > 0)
                    {
                        var finishedTask2 = await Task.WhenAny(Tasks2);
                        Tasks2.Remove(finishedTask2);

                        cancellationToken.ThrowIfCancellationRequested();
                    }
                }
                else
                {
                    _log.Error("Could not retrieve game schema (achievements/stats).");
                }
            });

            if (WaitForConnected(cancellationToken))
            {
                await GetAppInfo(AppID).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                var Tasks1 = new List<Task>
                {
                    GenerateSupportedLang(cancellationToken), GenerateDepots(cancellationToken),
                    GenerateDLCs(cancellationToken), GenerateInventory(cancellationToken),
                    GenerateControllerInfo()
                };
                while (Tasks1.Count > 0)
                {
                    var finishedTask1 = await Task.WhenAny(Tasks1);
                    Tasks1.Remove(finishedTask1);

                    cancellationToken.ThrowIfCancellationRequested();
                }
            }

            Task.WaitAll(TaskA);
            cancellationToken.ThrowIfCancellationRequested();
            WriteIni();
            steam3?.Disconnect();
        }
        catch (OperationCanceledException)
        {
            _log.Information("Operation was canceled.");
        }
        catch (Exception e)
        {
            if (steam3 != null) steam3?.Disconnect();
            throw new Exception(e.ToString());
        }
    }
}

internal class GeneratorSteamWeb : Generator
{
    public GeneratorSteamWeb(EMUGameInfoConfig GameInfoConfig) : base(GameInfoConfig)
    {
    }

    private async Task GenerateDLCs(CancellationToken cancellationToken = default)
    {
        try
        {
            _log.Debug("Generating DLCs...");
            var DLCIds = new List<uint>();

            var client = new HttpClient();
            client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
            client.Timeout = TimeSpan.FromSeconds(30);
            var response = await LimitSteamWebApiGET(client,
                    new HttpRequestMessage(HttpMethod.Get,
                        $"https://store.steampowered.com/api/appdetails/?appids={AppID}&l=english"), cancellationToken)
                .ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();

            if (response.StatusCode == HttpStatusCode.OK && response.Content != null)
            {
                var responsejson =
                    JsonDocument.Parse(
                        await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
                if (responsejson.RootElement.GetProperty(AppID.ToString()).GetProperty("success").GetBoolean())
                    if (responsejson.RootElement.GetProperty(AppID.ToString()).GetProperty("data")
                        .TryGetProperty("dlc", out var dlcid))
                        foreach (var dlc in dlcid.EnumerateArray())
                            DLCIds.Add(dlc.GetUInt32());
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (DLCIds.Count == 0)
            {
                _log.Debug("No DLCs. Skipping...");
                return;
            }

            await SteamAppList.WaitForReady().ConfigureAwait(false);
            var DLCInfos = new List<SteamApp>();
            foreach (var DLCId in DLCIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                DLCInfos.Add(await SteamAppList.GetAppById(DLCId).ConfigureAwait(false));
            }

            var dlcsection = new Section("app::dlcs")
            {
                new Property("unlock_all", "0", " 1=report all DLCs as unlocked", " 0=report only the DLCs mentioned",
                    " some games check for \"hidden\" DLCs, hence this should be set to 1 in that case",
                    " but other games detect emus by querying for a fake/bad DLC, hence this should be set to 0 in that case",
                    " default=1")
            };

            foreach (var DLC in DLCInfos)
            {
                string? name;
                string id;
                name = DLC.Name;
                if (DLC.AppId.HasValue)
                {
                    id = DLC.AppId.Value.ToString();
                    if (name == null || name == string.Empty) name = "Unknown Steam app " + id;
                    dlcsection.Add(new Property(id, name));
                }
            }

            dlcsection.Items.Add(new BlankLine());
            config_app.Add(dlcsection);
        }
        catch (OperationCanceledException)
        {
            _log.Debug("Operation was canceled.");
        }
        catch (Exception ex)
        {
            _log.Information(ex, "Failed to generate DLCs. Skipping...");
        }

        _log.Debug("Generated DLCs.");
    }

    public override async Task InfoGenerator(CancellationToken cancellationToken = default)
    {
        try
        {
            await GenerateBasic().ConfigureAwait(false);
            if (await GetGameSchema(cancellationToken).ConfigureAwait(false))
            {
                var Tasks2 = new List<Task>
                {
                    GenerateAchievements(cancellationToken), GenerateStats(cancellationToken),
                    GenerateDLCs(cancellationToken), GenerateInventory(cancellationToken)
                };
                while (Tasks2.Count > 0)
                {
                    var finishedTask2 = await Task.WhenAny(Tasks2);
                    Tasks2.Remove(finishedTask2);

                    cancellationToken.ThrowIfCancellationRequested();
                }
            }
            else
            {
                _log.Error("Could not retrieve game schema. Achievements, stats, and inventory will not be generated.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            WriteIni();
        }
        catch (OperationCanceledException)
        {
            _log.Information("Operation was canceled.");
        }
        catch (Exception e)
        {
            throw new Exception(e.ToString());
        }
    }
}

internal class GeneratorOffline : Generator
{
    public GeneratorOffline(EMUGameInfoConfig GameInfoConfig) : base(GameInfoConfig)
    {
    }

    public override async Task InfoGenerator(CancellationToken cancellationToken = default)
    {
        _log.Debug("Generator Offline, skip generating other files...");
        await GenerateBasic().ConfigureAwait(false);
    }
}

internal class GeneratorCommunityScraper : GeneratorSteamWeb
{
    public GeneratorCommunityScraper(EMUGameInfoConfig GameInfoConfig) : base(GameInfoConfig)
    {
    }

    protected override async Task<bool> GetGameSchema(CancellationToken cancellationToken = default)
    {
        return await GetGameSchemaScrapeFallback(cancellationToken);
    }
}

internal class GeneratorProxyServer : GeneratorSteamWeb
{
    public GeneratorProxyServer(EMUGameInfoConfig GameInfoConfig) : base(GameInfoConfig)
    {
    }
}