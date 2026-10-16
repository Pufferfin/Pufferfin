using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Pufferfin.Server.Arr;
using Pufferfin.Server.Arr.Models;
using Pufferfin.Server.Configuration;

namespace Pufferfin.Server.Requests.Search;

/// <summary>
/// Searches the indexers of Prowlarr for a film under each of its titles, the way the interactive search of Radarr
/// does not: Radarr searches one title with TMDB's year, and misses the releases named after another title or dated
/// by another year.
/// </summary>
/// <remarks>
/// Each release found is checked against the film's titles and year, rewritten with the title and year Radarr knows
/// the film by, and parsed by Radarr, which tells its languages and quality and must map it to the same film. A
/// release of an indexer Radarr does not have is only kept from a public tracker: a private one is always handed
/// through the indexer Radarr has, so its seeding rules apply.
/// </remarks>
public sealed class VariantReleaseSearch
{
    // Searches run at once on Prowlarr, and parses at once on Radarr.
    private const int Parallelism = 3;

    // The releases parsed at most, the best seeded first: the rest would not be taken anyway.
    private const int MaxParsed = 30;

    // The Newznab category of films.
    private const int MovieCategory = 2000;

    private readonly ArrClient _arr;
    private readonly ProwlarrClient _prowlarr;
    private readonly ILogger<VariantReleaseSearch> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="VariantReleaseSearch"/> class.
    /// </summary>
    /// <param name="arr">The Radarr and Sonarr client.</param>
    /// <param name="prowlarr">The Prowlarr client.</param>
    /// <param name="logger">The logger.</param>
    public VariantReleaseSearch(ArrClient arr, ProwlarrClient prowlarr, ILogger<VariantReleaseSearch> logger)
    {
        _arr = arr;
        _prowlarr = prowlarr;
        _logger = logger;
    }

    /// <summary>
    /// Finds the releases of a film under each of its titles. Nothing when Prowlarr is not set up, or does not answer.
    /// </summary>
    /// <param name="settings">The Pufferfin settings.</param>
    /// <param name="radarr">The Radarr settings.</param>
    /// <param name="movie">The film in Radarr.</param>
    /// <param name="titles">The titles: French, English, original, in this order.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The releases found, to push to Radarr, ranked as the selector expects.</returns>
    public async Task<IReadOnlyList<ArrRelease>> FindMovieReleasesAsync(
        PufferfinSettings settings,
        ServiceSettings radarr,
        ArrMovie movie,
        IReadOnlyList<string?> titles,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(radarr);
        ArgumentNullException.ThrowIfNull(movie);
        ArgumentNullException.ThrowIfNull(titles);
        if (settings.GetService(PufferfinService.Prowlarr) is not { } prowlarr || string.IsNullOrWhiteSpace(movie.Title))
        {
            return [];
        }

        int? year = movie.Year > 0 ? movie.Year : null;
        var queries = ReleaseTitles.BuildQueries(titles, year);
        try
        {
            var indexers = await GetIndexersAsync(prowlarr, radarr, cancellationToken).ConfigureAwait(false);
            var counts = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
            var found = new ConcurrentDictionary<string, (ProwlarrRelease Release, string Query)>(StringComparer.Ordinal);
            await Parallel.ForEachAsync(
                queries,
                new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = cancellationToken },
                async (query, token) =>
                {
                    var results = await SearchAsync(prowlarr, query, token).ConfigureAwait(false);
                    counts[query] = results.Count;
                    foreach (var result in results.Where(r => !string.IsNullOrWhiteSpace(r.Title)))
                    {
                        found.TryAdd(Key(result), (result, query));
                    }
                }).ConfigureAwait(false);

            var candidates = new List<(ProwlarrRelease Release, string Query, TitleMatch Match, IndexerLink Indexer)>();
            foreach (var (release, query) in found.Values)
            {
                if (!indexers.TryGetValue(release.IndexerId, out var indexer) || !indexer.Usable)
                {
                    continue;
                }

                if (ReleaseTitles.MatchMovie(release.Title!, titles, year) is { } match)
                {
                    candidates.Add((release, query, match, indexer));
                }
            }

            var best = candidates.OrderByDescending(c => c.Release.Seeders ?? 0).Take(MaxParsed).ToList();
            var parsed = new ConcurrentBag<ArrRelease>();
            await Parallel.ForEachAsync(
                best,
                new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = cancellationToken },
                async (candidate, token) =>
                {
                    if (await ToReleaseAsync(radarr, movie, candidate.Release, candidate.Query, candidate.Match, candidate.Indexer, token).ConfigureAwait(false) is { } release)
                    {
                        parsed.Add(release);
                    }
                }).ConfigureAwait(false);

            // A quality the film's profile does not want would only be refused by Radarr when pushed
            var allowed = await GetAllowedQualitiesAsync(radarr, movie.QualityProfileId, cancellationToken).ConfigureAwait(false);
            var ranked = ReleaseRanking.Rank(parsed.Where(r => allowed is null || (r.Quality?.Quality?.Id is int id && allowed.Contains(id))));
            _logger.LogInformation(
                "Search of film {TmdbId} under its titles: {Queries}; {Found} releases, {Matching} of this film, {Usable} usable",
                movie.TmdbId,
                string.Join("; ", queries.Select(q => string.Create(CultureInfo.InvariantCulture, $"\"{q}\" {counts.GetValueOrDefault(q)}"))),
                found.Count,
                candidates.Count,
                ranked.Count);
            return ranked;
        }
        catch (Exception ex) when ((ex is ArrApiException or JsonException or TaskCanceledException) && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("The search of film {TmdbId} under its titles failed, Radarr's own search goes on alone: {Failure}", movie.TmdbId, ex.Message);
            return [];
        }
    }

    /// <summary>
    /// Pushes a release found by this search to Radarr, which grabs it when it approves it for the same film.
    /// </summary>
    /// <param name="radarr">The Radarr settings.</param>
    /// <param name="release">The release, found by this search.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Radarr's decision, or <c>null</c> when it gave none.</returns>
    public async Task<ArrRelease?> PushAsync(ServiceSettings radarr, ArrRelease release, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(radarr);
        ArgumentNullException.ThrowIfNull(release);
        var push = release.Push ?? throw new ArgumentException("The release was not found by this search.", nameof(release));
        var body = new JsonObject
        {
            ["title"] = push.Title,
            ["downloadUrl"] = push.DownloadUrl,
            ["magnetUrl"] = push.MagnetUrl,
            ["protocol"] = release.Protocol,
            ["size"] = release.Size,
            ["seeders"] = release.Seeders,
            ["indexer"] = push.Indexer,
            ["publishDate"] = (push.PublishDate ?? DateTimeOffset.UtcNow).ToString("O", CultureInfo.InvariantCulture)
        };
        if (push.IndexerId > 0)
        {
            body["indexerId"] = push.IndexerId;
        }

        if (!string.IsNullOrWhiteSpace(release.InfoHash))
        {
            body["infoHash"] = release.InfoHash;
        }

        return await _arr.PushReleaseAsync(radarr, body, cancellationToken).ConfigureAwait(false);
    }

    private static string Key(ProwlarrRelease release)
        => !string.IsNullOrWhiteSpace(release.InfoHash)
            ? "h:" + release.InfoHash.ToUpperInvariant()
            : "g:" + release.IndexerId.ToString(CultureInfo.InvariantCulture) + ":" + (release.Guid ?? release.Title);

    private async Task<List<ProwlarrRelease>> SearchAsync(ServiceSettings prowlarr, string query, CancellationToken cancellationToken)
    {
        try
        {
            return await _prowlarr.SearchAsync(prowlarr, query, MovieCategory, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when ((ex is ArrApiException or JsonException or TaskCanceledException) && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("The search \"{Query}\" on Prowlarr failed: {Failure}", query, ex.Message);
            return [];
        }
    }

    // The indexers of Prowlarr, tied to the same indexers in Radarr, which Prowlarr names "<name> (Prowlarr)".
    private async Task<Dictionary<int, IndexerLink>> GetIndexersAsync(ServiceSettings prowlarr, ServiceSettings radarr, CancellationToken cancellationToken)
    {
        var radarrIndexers = await _arr.GetIndexersAsync(radarr, cancellationToken).ConfigureAwait(false);
        var byName = radarrIndexers
            .Select(i => (Id: i["id"]?.GetValue<int>() ?? 0, Name: i["name"]?.GetValue<string>()))
            .Where(i => i.Id > 0 && !string.IsNullOrWhiteSpace(i.Name))
            .GroupBy(i => i.Name!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var links = new Dictionary<int, IndexerLink>();
        foreach (var indexer in await _prowlarr.GetItemsAsync(prowlarr, "indexer", cancellationToken).ConfigureAwait(false))
        {
            var id = indexer["id"]?.GetValue<int>() ?? 0;
            var name = indexer["name"]?.GetValue<string>();
            if (id == 0 || string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var isPublic = string.Equals(indexer["privacy"]?.GetValue<string>(), "public", StringComparison.OrdinalIgnoreCase);
            links[id] = byName.TryGetValue(name + " (Prowlarr)", out var inRadarr)
                ? new IndexerLink(inRadarr.Id, inRadarr.Name!, true)
                : new IndexerLink(0, name, isPublic);
        }

        return links;
    }

    // The qualities a profile allows, its groups included; null when it cannot be read, so nothing is held back
    private async Task<HashSet<int>?> GetAllowedQualitiesAsync(ServiceSettings radarr, int profileId, CancellationToken cancellationToken)
    {
        var profiles = await _arr.GetQualityProfilesAsync(radarr, cancellationToken).ConfigureAwait(false);
        var profile = profiles.FirstOrDefault(p => p["id"]?.GetValue<int>() == profileId);
        if (profile?["items"] is not JsonArray items)
        {
            return null;
        }

        var allowed = new HashSet<int>();
        Collect(items, true);
        return allowed;

        void Collect(JsonArray nodes, bool parentAllowed)
        {
            foreach (var node in nodes.OfType<JsonObject>())
            {
                var isAllowed = parentAllowed && (node["allowed"]?.GetValue<bool>() ?? false);
                if (node["items"] is JsonArray { Count: > 0 } children)
                {
                    Collect(children, isAllowed);
                }
                else if (isAllowed && node["quality"]?["id"]?.GetValue<int>() is int id)
                {
                    allowed.Add(id);
                }
            }
        }
    }

    private async Task<ArrRelease?> ToReleaseAsync(
        ServiceSettings radarr,
        ArrMovie movie,
        ProwlarrRelease release,
        string query,
        TitleMatch match,
        IndexerLink indexer,
        CancellationToken cancellationToken)
    {
        var title = ReleaseTitles.Rewrite(release.Title!, match, movie.Title!, movie.Year);
        JsonObject parse;
        try
        {
            parse = await _arr.ParseReleaseAsync(radarr, title, cancellationToken).ConfigureAwait(false);
        }
        catch (ArrApiException ex)
        {
            _logger.LogDebug("Radarr could not parse {Title}: {Failure}", title, ex.Message);
            return null;
        }

        // Radarr must take it for the same film: a release it would file under another one is never pushed
        if (parse["movie"]?["id"]?.GetValue<int>() != movie.Id)
        {
            return null;
        }

        var parsed = parse["parsedMovieInfo"];
        return new ArrRelease
        {
            Guid = "pufferfin:" + (release.Guid ?? title),
            Title = release.Title,
            IndexerId = indexer.RadarrId,
            Languages = parse["languages"]?.Deserialize<List<ArrLanguage>>(ArrHttpClient.JsonOptions) ?? [],
            Quality = parsed?["quality"]?.Deserialize<ArrQualityModel>(ArrHttpClient.JsonOptions),
            Seeders = release.Seeders,
            Size = release.Size,
            Protocol = release.Protocol,
            InfoHash = release.InfoHash,
            MappedMovieId = movie.Id,
            Push = new ReleasePush(title, release.Title!, release.DownloadUrl, release.MagnetUrl, indexer.RadarrId, indexer.Name, release.PublishDate, query)
        };
    }

    private sealed record IndexerLink(int RadarrId, string Name, bool Usable);
}
