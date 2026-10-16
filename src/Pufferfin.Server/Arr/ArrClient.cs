using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Pufferfin.Server.Arr.Models;
using Pufferfin.Server.Configuration;

namespace Pufferfin.Server.Arr;

/// <summary>
/// The v3 API shared by Radarr and Sonarr, plus the calls specific to each.
/// </summary>
/// <remarks>
/// Items that Pufferfin writes back (films, series, profiles) are read as <see cref="JsonObject"/>, so that a
/// field this client does not know survives the round trip.
/// </remarks>
public sealed class ArrClient
{
    private readonly ArrHttpClient _http;

    /// <summary>
    /// Initializes a new instance of the <see cref="ArrClient"/> class.
    /// </summary>
    /// <param name="http">The HTTP client.</param>
    public ArrClient(ArrHttpClient http)
    {
        _http = http;
    }

    // Shared configuration resources.

    /// <summary>Gets the root folders.</summary>
    /// <param name="service">The service settings.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The root folders.</returns>
    public Task<List<ArrRootFolder>> GetRootFoldersAsync(ServiceSettings service, CancellationToken cancellationToken)
        => _http.GetAsync<List<ArrRootFolder>>(service, "api/v3/rootfolder", cancellationToken);

    /// <summary>Adds a root folder.</summary>
    /// <param name="service">The service settings.</param>
    /// <param name="path">The path, as the service sees it.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    public Task AddRootFolderAsync(ServiceSettings service, string path, CancellationToken cancellationToken)
        => _http.SendAsync(service, HttpMethod.Post, "api/v3/rootfolder", new JsonObject { ["path"] = path }, cancellationToken);

    /// <summary>Gets the languages the service knows.</summary>
    /// <param name="service">The service settings.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The languages.</returns>
    public Task<List<ArrLanguage>> GetLanguagesAsync(ServiceSettings service, CancellationToken cancellationToken)
        => _http.GetAsync<List<ArrLanguage>>(service, "api/v3/language", cancellationToken);

    /// <summary>Gets the items of a configuration resource (<c>qualityprofile</c>, <c>customformat</c>, <c>downloadclient</c>, <c>notification</c>...).</summary>
    /// <param name="service">The service settings.</param>
    /// <param name="resource">The resource name.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The items.</returns>
    public Task<List<ArrNamedItem>> GetItemsAsync(ServiceSettings service, string resource, CancellationToken cancellationToken)
        => _http.GetAsync<List<ArrNamedItem>>(service, "api/v3/" + resource, cancellationToken);

    /// <summary>Gets the schema of a configuration resource: one template, or a list of templates for provider resources.</summary>
    /// <param name="service">The service settings.</param>
    /// <param name="resource">The resource name.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The schema.</returns>
    public Task<JsonNode> GetSchemaAsync(ServiceSettings service, string resource, CancellationToken cancellationToken)
        => _http.GetAsync<JsonNode>(service, "api/v3/" + resource + "/schema", cancellationToken);

    /// <summary>Creates an item of a configuration resource.</summary>
    /// <param name="service">The service settings.</param>
    /// <param name="resource">The resource name.</param>
    /// <param name="item">The item.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The created item.</returns>
    public Task<ArrNamedItem> CreateItemAsync(ServiceSettings service, string resource, JsonObject item, CancellationToken cancellationToken)
        => _http.SendAsync<ArrNamedItem>(service, HttpMethod.Post, "api/v3/" + resource, item, cancellationToken);

    /// <summary>Gets the tags.</summary>
    /// <param name="service">The service settings.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The tags.</returns>
    public Task<List<ArrTag>> GetTagsAsync(ServiceSettings service, CancellationToken cancellationToken)
        => _http.GetAsync<List<ArrTag>>(service, "api/v3/tag", cancellationToken);

    /// <summary>Starts a command (<c>MoviesSearch</c>, <c>SeasonSearch</c>, <c>EpisodeSearch</c>...).</summary>
    /// <param name="service">The service settings.</param>
    /// <param name="command">The command body, with its <c>name</c>.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    public Task SendCommandAsync(ServiceSettings service, JsonObject command, CancellationToken cancellationToken)
        => _http.SendAsync(service, HttpMethod.Post, "api/v3/command", command, cancellationToken);

    /// <summary>Gets the naming settings, as a raw object.</summary>
    /// <param name="service">The service settings.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The naming settings.</returns>
    public Task<JsonObject> GetNamingAsync(ServiceSettings service, CancellationToken cancellationToken)
        => _http.GetAsync<JsonObject>(service, "api/v3/config/naming", cancellationToken);

    /// <summary>Saves the naming settings.</summary>
    /// <param name="service">The service settings.</param>
    /// <param name="naming">The naming settings, as read and changed.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    public Task UpdateNamingAsync(ServiceSettings service, JsonObject naming, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(naming);
        return _http.SendAsync(service, HttpMethod.Put, Path("api/v3/config/naming/", naming["id"]?.GetValue<int>() ?? 1), naming, cancellationToken);
    }

    /// <summary>Gets the indexers, as raw objects.</summary>
    /// <param name="service">The service settings.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The indexers.</returns>
    public Task<List<JsonObject>> GetIndexersAsync(ServiceSettings service, CancellationToken cancellationToken)
        => _http.GetAsync<List<JsonObject>>(service, "api/v3/indexer", cancellationToken);

    /// <summary>Gets the status of the indexers that failed recently.</summary>
    /// <remarks>
    /// Radarr 6 and Sonarr 4 no longer serve <c>indexerstatus</c> (HTTP 404): then nothing is known about failing
    /// indexers, and an empty list is returned rather than a failure of the request.
    /// </remarks>
    /// <param name="service">The service settings.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The statuses, with <c>indexerId</c> and <c>disabledTill</c>; empty when the service does not tell.</returns>
    public async Task<List<JsonObject>> GetIndexerStatusesAsync(ServiceSettings service, CancellationToken cancellationToken)
    {
        try
        {
            return await _http.GetAsync<List<JsonObject>>(service, "api/v3/indexerstatus", cancellationToken).ConfigureAwait(false);
        }
        catch (ArrApiException ex) when (ex.Failure == ArrFailure.NotFound)
        {
            return [];
        }
    }

    /// <summary>Grabs a release found by an interactive search. Never retried.</summary>
    /// <param name="service">The service settings.</param>
    /// <param name="release">The release.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    public Task GrabReleaseAsync(ServiceSettings service, ArrRelease release, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(release);
        return _http.SendAsync(service, HttpMethod.Post, "api/v3/release", new JsonObject { ["guid"] = release.Guid, ["indexerId"] = release.IndexerId }, cancellationToken);
    }

    // Radarr.

    /// <summary>Finds a film of Radarr by TMDB id.</summary>
    /// <param name="service">The Radarr settings.</param>
    /// <param name="tmdbId">The TMDB id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The film, or <c>null</c> when Radarr does not have it.</returns>
    public async Task<ArrMovie?> FindMovieAsync(ServiceSettings service, int tmdbId, CancellationToken cancellationToken)
    {
        var movies = await _http.GetAsync<List<ArrMovie>>(service, Path("api/v3/movie?tmdbId=", tmdbId), cancellationToken).ConfigureAwait(false);
        return movies.FirstOrDefault(m => m.TmdbId == tmdbId);
    }

    /// <summary>Gets every film of Radarr.</summary>
    /// <param name="service">The Radarr settings.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The films.</returns>
    public Task<List<ArrMovie>> GetMoviesAsync(ServiceSettings service, CancellationToken cancellationToken)
        => _http.GetAsync<List<ArrMovie>>(service, "api/v3/movie", cancellationToken);

    /// <summary>Gets every series of Sonarr.</summary>
    /// <param name="service">The Sonarr settings.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The series.</returns>
    public Task<List<ArrSeries>> GetAllSeriesAsync(ServiceSettings service, CancellationToken cancellationToken)
        => _http.GetAsync<List<ArrSeries>>(service, "api/v3/series", cancellationToken);

    /// <summary>Gets a film of Radarr as a raw object, to write it back.</summary>
    /// <param name="service">The Radarr settings.</param>
    /// <param name="movieId">The Radarr id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The film.</returns>
    public Task<JsonObject> GetMovieObjectAsync(ServiceSettings service, int movieId, CancellationToken cancellationToken)
        => _http.GetAsync<JsonObject>(service, Path("api/v3/movie/", movieId), cancellationToken);

    /// <summary>Looks a film up by TMDB id, to add it.</summary>
    /// <param name="service">The Radarr settings.</param>
    /// <param name="tmdbId">The TMDB id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The film template.</returns>
    public Task<JsonObject> LookupMovieAsync(ServiceSettings service, int tmdbId, CancellationToken cancellationToken)
        => _http.GetAsync<JsonObject>(service, Path("api/v3/movie/lookup/tmdb?tmdbId=", tmdbId), cancellationToken);

    /// <summary>Adds a film.</summary>
    /// <param name="service">The Radarr settings.</param>
    /// <param name="movie">The film, from <see cref="LookupMovieAsync"/> with its profile and folder set.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The added film.</returns>
    public Task<ArrMovie> AddMovieAsync(ServiceSettings service, JsonObject movie, CancellationToken cancellationToken)
        => _http.SendAsync<ArrMovie>(service, HttpMethod.Post, "api/v3/movie", movie, cancellationToken);

    /// <summary>Writes a film back.</summary>
    /// <param name="service">The Radarr settings.</param>
    /// <param name="movieId">The Radarr id.</param>
    /// <param name="movie">The film.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    public Task UpdateMovieAsync(ServiceSettings service, int movieId, JsonObject movie, CancellationToken cancellationToken)
        => _http.SendAsync(service, HttpMethod.Put, Path("api/v3/movie/", movieId), movie, cancellationToken);

    /// <summary>Gets the queue items of a film.</summary>
    /// <param name="service">The Radarr settings.</param>
    /// <param name="movieId">The Radarr id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The queue items.</returns>
    public Task<List<ArrQueueItem>> GetMovieQueueAsync(ServiceSettings service, int movieId, CancellationToken cancellationToken)
        => _http.GetAsync<List<ArrQueueItem>>(service, Path("api/v3/queue/details?movieId=", movieId), cancellationToken);

    /// <summary>Gets the history of a film, newest first.</summary>
    /// <param name="service">The Radarr settings.</param>
    /// <param name="movieId">The Radarr id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The history records.</returns>
    public Task<List<ArrHistoryRecord>> GetMovieHistoryAsync(ServiceSettings service, int movieId, CancellationToken cancellationToken)
        => _http.GetAsync<List<ArrHistoryRecord>>(service, Path("api/v3/history/movie?movieId=", movieId), cancellationToken);

    /// <summary>Runs an interactive search for a film. Slow: it waits on every indexer.</summary>
    /// <param name="service">The Radarr settings.</param>
    /// <param name="movieId">The Radarr id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The releases, in the order Radarr ranks them.</returns>
    public Task<List<ArrRelease>> SearchMovieReleasesAsync(ServiceSettings service, int movieId, CancellationToken cancellationToken)
        => _http.GetAsync<List<ArrRelease>>(service, Path("api/v3/release?movieId=", movieId), cancellationToken, isSearch: true);

    /// <summary>Parses a release title as Radarr or Sonarr would: languages, quality, and the title it maps to. No side effect.</summary>
    /// <param name="service">The Radarr or Sonarr settings.</param>
    /// <param name="title">The release title.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The parse result.</returns>
    public Task<JsonObject> ParseReleaseAsync(ServiceSettings service, string title, CancellationToken cancellationToken)
        => _http.GetAsync<JsonObject>(service, "api/v3/parse?title=" + Uri.EscapeDataString(title), cancellationToken);

    /// <summary>
    /// Pushes a release found outside the service. The service maps it to a title and grabs it at once when it
    /// approves it, as it would a release of its own RSS feed. Never retried.
    /// </summary>
    /// <param name="service">The Radarr or Sonarr settings.</param>
    /// <param name="release">The release to push.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The decision: approved and grabbed, or the reasons it was not.</returns>
    public async Task<ArrRelease?> PushReleaseAsync(ServiceSettings service, JsonObject release, CancellationToken cancellationToken)
    {
        var answer = await _http.SendAsync(service, HttpMethod.Post, "api/v3/release/push", release, cancellationToken).ConfigureAwait(false);
        var node = JsonNode.Parse(answer);
        var first = node is JsonArray array ? array.FirstOrDefault() : node;
        return first?.Deserialize<ArrRelease>(ArrHttpClient.JsonOptions);
    }

    /// <summary>Gets the quality profiles, as written: their qualities and groups with what each allows.</summary>
    /// <param name="service">The Radarr or Sonarr settings.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The profiles.</returns>
    public Task<List<JsonObject>> GetQualityProfilesAsync(ServiceSettings service, CancellationToken cancellationToken)
        => _http.GetAsync<List<JsonObject>>(service, "api/v3/qualityprofile", cancellationToken);

    // Sonarr.

    /// <summary>Finds a series of Sonarr by TVDB id.</summary>
    /// <param name="service">The Sonarr settings.</param>
    /// <param name="tvdbId">The TVDB id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The series, or <c>null</c> when Sonarr does not have it.</returns>
    public async Task<ArrSeries?> FindSeriesAsync(ServiceSettings service, int tvdbId, CancellationToken cancellationToken)
    {
        var series = await _http.GetAsync<List<ArrSeries>>(service, Path("api/v3/series?tvdbId=", tvdbId), cancellationToken).ConfigureAwait(false);
        return series.FirstOrDefault(s => s.TvdbId == tvdbId);
    }

    /// <summary>Gets a series of Sonarr as a raw object, to write it back.</summary>
    /// <param name="service">The Sonarr settings.</param>
    /// <param name="seriesId">The Sonarr id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The series.</returns>
    public Task<JsonObject> GetSeriesObjectAsync(ServiceSettings service, int seriesId, CancellationToken cancellationToken)
        => _http.GetAsync<JsonObject>(service, Path("api/v3/series/", seriesId), cancellationToken);

    /// <summary>Looks a series up by TVDB id, to add it.</summary>
    /// <param name="service">The Sonarr settings.</param>
    /// <param name="tvdbId">The TVDB id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The series template, or <c>null</c> when Sonarr does not find it.</returns>
    public async Task<JsonObject?> LookupSeriesAsync(ServiceSettings service, int tvdbId, CancellationToken cancellationToken)
    {
        var results = await _http.GetAsync<List<JsonObject>>(service, Path("api/v3/series/lookup?term=tvdb%3A", tvdbId), cancellationToken).ConfigureAwait(false);
        return results.FirstOrDefault(r => r["tvdbId"]?.GetValue<int>() == tvdbId);
    }

    /// <summary>Adds a series.</summary>
    /// <param name="service">The Sonarr settings.</param>
    /// <param name="series">The series, from <see cref="LookupSeriesAsync"/> with its profile and folder set.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The added series.</returns>
    public Task<ArrSeries> AddSeriesAsync(ServiceSettings service, JsonObject series, CancellationToken cancellationToken)
        => _http.SendAsync<ArrSeries>(service, HttpMethod.Post, "api/v3/series", series, cancellationToken);

    /// <summary>Writes a series back.</summary>
    /// <param name="service">The Sonarr settings.</param>
    /// <param name="seriesId">The Sonarr id.</param>
    /// <param name="series">The series.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    public Task UpdateSeriesAsync(ServiceSettings service, int seriesId, JsonObject series, CancellationToken cancellationToken)
        => _http.SendAsync(service, HttpMethod.Put, Path("api/v3/series/", seriesId), series, cancellationToken);

    /// <summary>Gets the episodes of a series.</summary>
    /// <param name="service">The Sonarr settings.</param>
    /// <param name="seriesId">The Sonarr id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The episodes.</returns>
    public Task<List<ArrEpisode>> GetEpisodesAsync(ServiceSettings service, int seriesId, CancellationToken cancellationToken)
        => _http.GetAsync<List<ArrEpisode>>(service, Path("api/v3/episode?seriesId=", seriesId), cancellationToken);

    /// <summary>Monitors episodes.</summary>
    /// <param name="service">The Sonarr settings.</param>
    /// <param name="episodeIds">The episode ids.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    public Task MonitorEpisodesAsync(ServiceSettings service, IReadOnlyCollection<int> episodeIds, CancellationToken cancellationToken)
        => _http.SendAsync(
            service,
            HttpMethod.Put,
            "api/v3/episode/monitor",
            new JsonObject { ["episodeIds"] = new JsonArray(episodeIds.Select(id => (JsonNode)id).ToArray()), ["monitored"] = true },
            cancellationToken);

    /// <summary>Gets the queue items of a series, with their episode.</summary>
    /// <param name="service">The Sonarr settings.</param>
    /// <param name="seriesId">The Sonarr id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The queue items.</returns>
    public Task<List<ArrQueueItem>> GetSeriesQueueAsync(ServiceSettings service, int seriesId, CancellationToken cancellationToken)
        => _http.GetAsync<List<ArrQueueItem>>(service, Path("api/v3/queue/details?seriesId=", seriesId), cancellationToken);

    /// <summary>Gets the history of a season, newest first.</summary>
    /// <param name="service">The Sonarr settings.</param>
    /// <param name="seriesId">The Sonarr id.</param>
    /// <param name="seasonNumber">The season number.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The history records.</returns>
    public Task<List<ArrHistoryRecord>> GetSeasonHistoryAsync(ServiceSettings service, int seriesId, int seasonNumber, CancellationToken cancellationToken)
        => _http.GetAsync<List<ArrHistoryRecord>>(
            service,
            string.Create(CultureInfo.InvariantCulture, $"api/v3/history/series?seriesId={seriesId}&seasonNumber={seasonNumber}"),
            cancellationToken);

    /// <summary>Runs an interactive search for one episode. Slow: it waits on every indexer.</summary>
    /// <param name="service">The Sonarr settings.</param>
    /// <param name="episodeId">The episode id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The releases, in the order Sonarr ranks them.</returns>
    public Task<List<ArrRelease>> SearchEpisodeReleasesAsync(ServiceSettings service, int episodeId, CancellationToken cancellationToken)
        => _http.GetAsync<List<ArrRelease>>(service, Path("api/v3/release?episodeId=", episodeId), cancellationToken, isSearch: true);

    /// <summary>Runs an interactive search for a season. Slow: it waits on every indexer.</summary>
    /// <param name="service">The Sonarr settings.</param>
    /// <param name="seriesId">The Sonarr id.</param>
    /// <param name="seasonNumber">The season number.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The releases, in the order Sonarr ranks them.</returns>
    public Task<List<ArrRelease>> SearchSeasonReleasesAsync(ServiceSettings service, int seriesId, int seasonNumber, CancellationToken cancellationToken)
        => _http.GetAsync<List<ArrRelease>>(
            service,
            string.Create(CultureInfo.InvariantCulture, $"api/v3/release?seriesId={seriesId}&seasonNumber={seasonNumber}"),
            cancellationToken,
            isSearch: true);

    /// <summary>Gets the history of a whole series, newest first.</summary>
    /// <param name="service">The Sonarr settings.</param>
    /// <param name="seriesId">The Sonarr id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The history records.</returns>
    public Task<List<ArrHistoryRecord>> GetSeriesHistoryAsync(ServiceSettings service, int seriesId, CancellationToken cancellationToken)
        => _http.GetAsync<List<ArrHistoryRecord>>(service, Path("api/v3/history/series?seriesId=", seriesId), cancellationToken);

    /// <summary>Unmonitors episodes, so the service does not download them again once their files are deleted.</summary>
    /// <param name="service">The Sonarr settings.</param>
    /// <param name="episodeIds">The episode ids.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    public Task UnmonitorEpisodesAsync(ServiceSettings service, IReadOnlyCollection<int> episodeIds, CancellationToken cancellationToken)
        => _http.SendAsync(
            service,
            HttpMethod.Put,
            "api/v3/episode/monitor",
            new JsonObject { ["episodeIds"] = new JsonArray(episodeIds.Select(id => (JsonNode)id).ToArray()), ["monitored"] = false },
            cancellationToken);

    /// <summary>Deletes an episode file from the disk.</summary>
    /// <param name="service">The Sonarr settings.</param>
    /// <param name="episodeFileId">The episode file id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    public Task DeleteEpisodeFileAsync(ServiceSettings service, int episodeFileId, CancellationToken cancellationToken)
        => _http.SendAsync(service, HttpMethod.Delete, Path("api/v3/episodefile/", episodeFileId), null, cancellationToken);

    /// <summary>Removes a series and deletes its files from the disk.</summary>
    /// <param name="service">The Sonarr settings.</param>
    /// <param name="seriesId">The Sonarr id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    public Task DeleteSeriesAsync(ServiceSettings service, int seriesId, CancellationToken cancellationToken)
        => _http.SendAsync(service, HttpMethod.Delete, Path("api/v3/series/", seriesId) + "?deleteFiles=true&addImportListExclusion=false", null, cancellationToken);

    // Deletion and seeding policy.

    /// <summary>Removes a film and deletes its files from the disk.</summary>
    /// <param name="service">The Radarr settings.</param>
    /// <param name="movieId">The Radarr id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    public Task DeleteMovieAsync(ServiceSettings service, int movieId, CancellationToken cancellationToken)
        => _http.SendAsync(service, HttpMethod.Delete, Path("api/v3/movie/", movieId) + "?deleteFiles=true&addImportExclusion=false", null, cancellationToken);

    /// <summary>Gets the whole download queue: what is downloading or waiting to be imported.</summary>
    /// <param name="service">The Radarr or Sonarr settings.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The queue items.</returns>
    public Task<List<ArrQueueItem>> GetQueueAsync(ServiceSettings service, CancellationToken cancellationToken)
        => _http.GetAsync<List<ArrQueueItem>>(service, "api/v3/queue/details", cancellationToken);

    /// <summary>Gets the history since a date, oldest first, with the film or the series and episode of each record.</summary>
    /// <param name="service">The Radarr or Sonarr settings.</param>
    /// <param name="since">The date.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The history records.</returns>
    public Task<List<ArrHistoryRecord>> GetHistorySinceAsync(ServiceSettings service, DateTimeOffset since, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(service);
        var include = service.Service == PufferfinService.Radarr ? "includeMovie=true" : "includeSeries=true&includeEpisode=true";
        var date = Uri.EscapeDataString(since.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
        return _http.GetAsync<List<ArrHistoryRecord>>(service, "api/v3/history/since?date=" + date + "&" + include, cancellationToken);
    }

    private static string Path(string prefix, int id) => prefix + id.ToString(CultureInfo.InvariantCulture);
}
