using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Pufferfin.Server.Arr;
using Pufferfin.Server.Audiobooks;
using Pufferfin.Server.Configuration;
using Pufferfin.Server.Requests;
using Pufferfin.Server.Requests.Integration;
using Pufferfin.Server.Requests.Search;
using Pufferfin.Server.Requests.Workflow;
using Pufferfin.Server.Tests.AutoConfig;
using Pufferfin.Server.Tests.TestHelpers;

namespace Pufferfin.Server.Tests.Requests;

/// <summary>
/// A Radarr or Sonarr in memory, TMDB metadata, a library and the clients of the Requests module.
/// </summary>
public sealed class RequestTestKit : IDisposable
{
    public static readonly DateTimeOffset Now = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "pufferfin-tests", Guid.NewGuid().ToString("N"));

    public RequestTestKit()
    {
        Directory.CreateDirectory(_directory);
        Server = ArrFixtures.RadarrLike();
        Server.Resources["rootfolder"] = (JsonArray)JsonNode.Parse("[{\"id\":1,\"path\":\"/media/movies\"},{\"id\":2,\"path\":\"/media/anime-movies\"},{\"id\":3,\"path\":\"/media/series\"}]")!;
        Server.Resources["qualityprofile"] = (JsonArray)JsonNode.Parse("[{\"id\":1,\"name\":\"Any\"},{\"id\":7,\"name\":\"Pufferfin French\"},{\"id\":8,\"name\":\"Pufferfin Original\"}]")!;
        Server.Resources["indexer"] = (JsonArray)JsonNode.Parse("[{\"id\":1,\"enableInteractiveSearch\":true}]")!;
        Server.Resources["indexerstatus"] = [];
        Server.Resources["queue/details"] = [];
        Server.Resources["history/movie"] = [];
        Server.Resources["history/series"] = [];
        Server.Resources["release"] = [];
        Server.Resources["movie"] = [];
        Server.Resources["series"] = [];
        Server.Resources["episode"] = [];

        // A grab is recorded, not stored as a release.
        Server.Overrides.Add((method, path, body) =>
        {
            if (method == HttpMethod.Post && path == "release")
            {
                Grabs.Enqueue(JsonNode.Parse(body!)!["guid"]!.GetValue<string>());
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
            }

            if (method == HttpMethod.Get && path.StartsWith("movie/lookup/tmdb", StringComparison.Ordinal))
            {
                return FakeArrServer.Json(new JsonObject { ["title"] = "Dune", ["tmdbId"] = int.Parse(path.Split('=')[1], System.Globalization.CultureInfo.InvariantCulture), ["year"] = 2021 });
            }

            if (method == HttpMethod.Get && path.StartsWith("series/lookup", StringComparison.Ordinal))
            {
                var term = Uri.UnescapeDataString(path.Split("term=")[1]);
                var tvdb = int.Parse(term.Split(':')[1], System.Globalization.CultureInfo.InvariantCulture);
                return FakeArrServer.Json(new JsonArray(new JsonObject { ["title"] = "Show", ["tvdbId"] = tvdb, ["seasons"] = JsonNode.Parse("[{\"seasonNumber\":1,\"monitored\":true}]") }));
            }

            return null;
        });

        Store = new RequestStore(Path.Combine(_directory, "requests.json"), NullLogger<RequestStore>.Instance);
        Metadata = new FakeMetadata();
        Library = new FakeLibrary();
        Logs = new CapturingLoggerProvider();
        var http = FakeArrServer.CreateHttp(Server);
        Arr = new ArrClient(http);
        var qBittorrent = new QBittorrentClient(Server, NullLogger<QBittorrentClient>.Instance, TimeSpan.FromSeconds(5));
        Movies = new MovieWorkflow(
            Arr,
            qBittorrent,
            Metadata,
            Library,
            Logs.CreateLogger<MovieWorkflow>(),
            new VariantReleaseSearch(Arr, new ProwlarrClient(http), Logs.CreateLogger<VariantReleaseSearch>()));
        Series = new SeriesWorkflow(Arr, qBittorrent, Metadata, Library, Logs.CreateLogger<SeriesWorkflow>());
    }

    public FakeArrServer Server { get; }

    public ConcurrentQueue<string> Grabs { get; } = new();

    public RequestStore Store { get; }

    public FakeMetadata Metadata { get; }

    public FakeLibrary Library { get; }

    public CapturingLoggerProvider Logs { get; }

    public ArrClient Arr { get; }

    public MovieWorkflow Movies { get; }

    public SeriesWorkflow Series { get; }

    public IEnumerable<JsonObject> Commands => Server.Resource("command").OfType<JsonObject>();

    public static RequestContext Context(string preferred = "fr", bool? rejectSubtitledOnly = null, DateTimeOffset? now = null, bool withProwlarr = false)
    {
        var changes = new List<(string, object?)>
        {
            (SettingKeys.RadarrUrl, "http://radarr:7878"),
            (SettingKeys.RadarrApiKey, ArrFixtures.RadarrKey),
            (SettingKeys.SonarrUrl, "http://sonarr:8989"),
            (SettingKeys.SonarrApiKey, ArrFixtures.SonarrKey),
            (SettingKeys.PreferredAudioLanguages, preferred),
        };
        if (withProwlarr)
        {
            changes.Add((SettingKeys.ProwlarrUrl, "http://prowlarr:9696"));
            changes.Add((SettingKeys.ProwlarrApiKey, ArrFixtures.ProwlarrKey));
        }

        if (rejectSubtitledOnly is bool reject)
        {
            changes.Add((SettingKeys.RejectSubtitledOnly, reject));
        }

        var settings = ArrFixtures.Settings(changes.ToArray());
        return new RequestContext(settings, now ?? Now, settings.PreferredAudioLanguages);
    }

    public static MediaRequest Request(RequestKind kind = RequestKind.Movie, int tmdbId = 438631, string language = "fr", int? season = null, int? episode = null, DateOnly? airDate = null)
        => new()
        {
            Id = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            Kind = kind,
            TmdbId = tmdbId,
            SeasonNumber = season,
            EpisodeNumber = episode,
            AirDate = airDate,
            Title = "Title",
            Language = language,
            State = RequestState.Searching,
            CreatedAt = Now,
            UpdatedAt = Now
        };

    public static JsonObject ReleaseJson(string title, string[] languages, bool fullSeason = false, int season = 0, int[]? episodes = null, int seeders = 20)
        => new()
        {
            ["guid"] = title,
            ["title"] = title,
            ["indexerId"] = 1,
            ["protocol"] = "torrent",
            ["seeders"] = seeders,
            ["rejected"] = false,
            ["rejections"] = new JsonArray(),
            ["languages"] = new JsonArray(languages.Select(l => (JsonNode)new JsonObject { ["id"] = l == "French" ? 2 : 1, ["name"] = l }).ToArray()),
            ["fullSeason"] = fullSeason,
            ["seasonNumber"] = season,
            ["episodeNumbers"] = new JsonArray((episodes ?? []).Select(e => (JsonNode)e).ToArray())
        };

    public JsonObject AddMovie(int tmdbId = 438631, bool hasFile = false)
        => Server.Add("movie", new JsonObject { ["id"] = 50, ["title"] = "Dune", ["tmdbId"] = tmdbId, ["hasFile"] = hasFile, ["qualityProfileId"] = 7, ["monitored"] = true });

    public void Dispose()
    {
        Logs.Dispose();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    public sealed class FakeMetadata : ITitleMetadata
    {
        public Dictionary<int, TitleMetadata> Movies { get; } = new() { [438631] = new TitleMetadata(438631, "Dune", "en", [878, 12], ["US"]) };

        public Dictionary<int, TitleMetadata> Series { get; } = new() { [1399] = new TitleMetadata(1399, "Show", "en", [18], ["US"], 121361) };

        public HashSet<int> Unknown { get; } = [];

        // A slow TMDB widens the window between the admission checks and the creation.
        public TimeSpan Delay { get; set; }

        // Any other film exists, so tests can create many requests.
        public async Task<TitleMetadata?> GetMovieAsync(int tmdbId, CancellationToken cancellationToken)
        {
            await Task.Delay(Delay, cancellationToken);
            return Unknown.Contains(tmdbId) ? null : Movies.GetValueOrDefault(tmdbId) ?? new TitleMetadata(tmdbId, "Film " + tmdbId, "en", [], ["US"]);
        }

        public Task<TitleMetadata?> GetSeriesAsync(int tmdbId, CancellationToken cancellationToken) => Task.FromResult(Series.GetValueOrDefault(tmdbId));
    }

    public sealed class FakeAudiobooks : IAudiobookMetadata
    {
        public static readonly AudiobookMetadata Dune = new("B002V1A0WE", "Dune", ["Frank Herbert"], "Dune", "1", "fr", "https://m.media-amazon.com/images/I/dune.jpg", "fr");

        public Dictionary<string, AudiobookMetadata> Books { get; } = new(StringComparer.OrdinalIgnoreCase) { [Dune.Asin] = Dune };

        public bool Unreachable { get; set; }

        public Task<AudiobookMetadata?> GetAsync(string asin, string? market, CancellationToken cancellationToken)
            => Unreachable
                ? throw new Pufferfin.Catalog.Http.CatalogSourceException(Pufferfin.Catalog.Models.CatalogSource.Audible, Pufferfin.Catalog.Models.CatalogSourceStatus.Unavailable)
                : Task.FromResult(Books.GetValueOrDefault(asin));
    }

    public sealed class FakeLibrary : IMediaLibrary
    {
        public HashSet<int> Movies { get; } = [];

        public HashSet<(int Season, int Episode)> Episodes { get; } = [];

        public List<string?> Refreshed { get; } = [];

        /// <summary>Gets the folders the library holds a film in, whatever it identified it as.</summary>
        public HashSet<string> MovieFolders { get; } = new(StringComparer.OrdinalIgnoreCase);

        public bool HasMovie(int tmdbId) => Movies.Contains(tmdbId);

        public bool HasEpisodes(int tmdbId, int? tvdbId, IReadOnlyCollection<RequestEpisode> episodes) => episodes.All(e => Episodes.Contains((e.SeasonNumber, e.EpisodeNumber)));

        public void Refresh(string? path) => Refreshed.Add(path);

        public bool ClaimMovie(int tmdbId, string? arrFolder)
            => arrFolder is not null && MovieFolders.Contains(System.IO.Path.GetFileName(arrFolder.TrimEnd('/')));
    }
}
