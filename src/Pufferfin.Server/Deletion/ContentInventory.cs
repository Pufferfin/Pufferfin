using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Pufferfin.Server.Arr;
using Pufferfin.Server.Arr.Models;
using Pufferfin.Server.Configuration;
using Pufferfin.Server.Requests.Integration;
using Pufferfin.Server.Seeding;
using Pufferfin.Server.Storage;

namespace Pufferfin.Server.Deletion;

/// <summary>
/// Lists every whole title on disk with the torrents it came from, for the content manager: their age, ratio and
/// trackers, and the deletion scheduled for it.
/// </summary>
/// <remarks>
/// Reads only. The torrents are linked the way the deletion links them: through the history of Radarr and Sonarr for
/// films and series, by inode for audiobooks, each service read once for the whole list. Without qBittorrent the
/// titles are still listed, without their torrents.
/// </remarks>
public sealed class ContentInventory
{
    private readonly ILibraryContent _library;
    private readonly ArrClient _arr;
    private readonly QBittorrentClient _qBittorrent;
    private readonly IFileLinks _links;
    private readonly ScheduledDeletionStore _schedules;
    private readonly IPufferfinConfigurationStore _configuration;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ContentInventory> _logger;

    // The privacy of a torrent never changes: asked once per torrent qBittorrent does not tell it for.
    private readonly ConcurrentDictionary<string, bool?> _privacy = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Initializes a new instance of the <see cref="ContentInventory"/> class.
    /// </summary>
    /// <param name="library">The library content.</param>
    /// <param name="arr">The Radarr and Sonarr client.</param>
    /// <param name="qBittorrent">The qBittorrent client.</param>
    /// <param name="links">The inodes and hard links.</param>
    /// <param name="schedules">The scheduled deletions.</param>
    /// <param name="configuration">The configuration store.</param>
    /// <param name="timeProvider">The time provider.</param>
    /// <param name="logger">The logger.</param>
    public ContentInventory(
        ILibraryContent library,
        ArrClient arr,
        QBittorrentClient qBittorrent,
        IFileLinks links,
        ScheduledDeletionStore schedules,
        IPufferfinConfigurationStore configuration,
        TimeProvider timeProvider,
        ILogger<ContentInventory> logger)
    {
        _library = library;
        _arr = arr;
        _qBittorrent = qBittorrent;
        _links = links;
        _schedules = schedules;
        _configuration = configuration;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>
    /// Lists the titles.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The list.</returns>
    public async Task<ContentInventoryResult> ListAsync(CancellationToken cancellationToken)
    {
        var settings = _configuration.Current.Settings;
        var policy = SeedPolicy.From(settings);
        var now = _timeProvider.GetUtcNow();
        var entries = _library.ListTitles();
        var due = _schedules.GetAll()
            .Where(d => d.Outcome is null)
            .GroupBy(d => d.ItemId)
            .ToDictionary(g => g.Key, g => g.Min(d => d.DueAt));

        var torrentsOf = new Dictionary<Guid, IReadOnlyList<ContentTorrent>>();
        var seasonTorrentsOf = new Dictionary<Guid, IReadOnlyList<ContentTorrent>>();
        var available = false;
        if (settings.GetService(PufferfinService.QBittorrent) is { } qBittorrent)
        {
            try
            {
                (torrentsOf, seasonTorrentsOf) = await LinkTorrentsAsync(entries, settings, qBittorrent, policy, now, cancellationToken).ConfigureAwait(false);
                available = true;
            }
            catch (ArrApiException ex)
            {
                _logger.LogWarning("The content manager lists the titles without their torrents: {Failure}", ex.Message);
            }
        }

        var rows = entries
            .Select(entry => ContentRows.Build(
                entry,
                torrentsOf.TryGetValue(entry.ItemId, out var torrents) ? torrents : [],
                due.TryGetValue(entry.ItemId, out var dueAt) ? dueAt : null) with
            {
                Seasons = (entry.Seasons ?? [])
                    .OrderBy(season => season.Number == 0 ? int.MaxValue : season.Number)
                    .Select(season => ContentRows.BuildSeason(
                        season,
                        seasonTorrentsOf.TryGetValue(season.ItemId, out var seasonTorrents) ? seasonTorrents : [],
                        due.TryGetValue(season.ItemId, out var seasonDueAt) ? seasonDueAt : null))
                    .ToList()
            })
            .ToList();
        return new ContentInventoryResult(rows, available, policy.MinSeedingTime.TotalDays);
    }

    private static Dictionary<int, List<string>> HashesBy(IEnumerable<ArrHistoryRecord> history, Func<ArrHistoryRecord, int?> key)
    {
        var map = new Dictionary<int, List<string>>();
        foreach (var record in history.Where(r => !string.IsNullOrEmpty(r.DownloadId)))
        {
            if (key(record) is not int id || id <= 0)
            {
                continue;
            }

            if (!map.TryGetValue(id, out var hashes))
            {
                hashes = [];
                map[id] = hashes;
            }

            var hash = record.DownloadId!.ToLowerInvariant();
            if (!hashes.Contains(hash, StringComparer.Ordinal))
            {
                hashes.Add(hash);
            }
        }

        return map;
    }

    private async Task<(Dictionary<Guid, IReadOnlyList<ContentTorrent>> Titles, Dictionary<Guid, IReadOnlyList<ContentTorrent>> Seasons)> LinkTorrentsAsync(
        IReadOnlyList<LibraryEntry> entries,
        PufferfinSettings settings,
        ServiceSettings qBittorrent,
        SeedPolicy policy,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var all = (await _qBittorrent.GetAllTorrentsAsync(qBittorrent, cancellationToken).ConfigureAwait(false))
            .Where(t => !string.IsNullOrEmpty(t.Hash))
            .GroupBy(t => t.Hash!.ToLowerInvariant(), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var byTmdb = new Dictionary<int, List<string>>();
        if (settings.IsEnabled(PufferfinService.Radarr) && entries.Any(e => e.Kind == LibraryTitleKind.Movie))
        {
            // By the Radarr id of each record, as the deletion does: the film attached to a record is not always there
            var radarr = settings.GetService(PufferfinService.Radarr)!;
            var tmdbOf = (await _arr.GetMoviesAsync(radarr, cancellationToken).ConfigureAwait(false))
                .GroupBy(m => m.Id)
                .ToDictionary(g => g.Key, g => g.First().TmdbId);
            var history = await _arr.GetHistorySinceAsync(radarr, DateTimeOffset.UnixEpoch, cancellationToken).ConfigureAwait(false);
            byTmdb = HashesBy(history, r => (r.MovieId ?? r.Movie?.Id) is int id && tmdbOf.TryGetValue(id, out var tmdb) ? tmdb : r.Movie?.TmdbId);
        }

        var byTvdb = new Dictionary<int, List<string>>();
        var byTvdbSeason = new Dictionary<(int Tvdb, int Season), List<string>>();
        if (settings.IsEnabled(PufferfinService.Sonarr) && entries.Any(e => e.Kind == LibraryTitleKind.Series))
        {
            var sonarr = settings.GetService(PufferfinService.Sonarr)!;
            var tvdbOf = (await _arr.GetAllSeriesAsync(sonarr, cancellationToken).ConfigureAwait(false))
                .GroupBy(m => m.Id)
                .ToDictionary(g => g.Key, g => g.First().TvdbId);
            var history = await _arr.GetHistorySinceAsync(sonarr, DateTimeOffset.UnixEpoch, cancellationToken).ConfigureAwait(false);
            int? TvdbOf(ArrHistoryRecord r) => (r.SeriesId ?? r.Series?.Id) is int id && tvdbOf.TryGetValue(id, out var tvdb) ? tvdb : r.Series?.TvdbId;
            byTvdb = HashesBy(history, TvdbOf);

            // Each season its own torrents, from the episodes each download brought: a pack of several seasons is under each
            foreach (var record in history.Where(r => !string.IsNullOrEmpty(r.DownloadId)))
            {
                if (TvdbOf(record) is not int tvdb || tvdb <= 0 || record.Episode?.SeasonNumber is not int season)
                {
                    continue;
                }

                if (!byTvdbSeason.TryGetValue((tvdb, season), out var hashes))
                {
                    hashes = [];
                    byTvdbSeason[(tvdb, season)] = hashes;
                }

                var hash = record.DownloadId!.ToLowerInvariant();
                if (!hashes.Contains(hash, StringComparer.Ordinal))
                {
                    hashes.Add(hash);
                }
            }
        }

        _logger.LogInformation(
            "Content manager: {Torrents} torrents, history of {Films} films and {Series} series",
            all.Count,
            byTmdb.Count,
            byTvdb.Count);

        var books = entries.Where(e => e.Kind == LibraryTitleKind.Audiobook).ToList();
        var bookCandidates = books.Count > 0
            ? await GetAudiobookCandidatesAsync(all.Values, settings, qBittorrent, cancellationToken).ConfigureAwait(false)
            : [];

        async Task<IReadOnlyList<ContentTorrent>> DescribeAsync(IEnumerable<string> hashes)
        {
            var torrents = new List<ContentTorrent>();
            foreach (var hash in hashes)
            {
                if (!all.TryGetValue(hash, out var torrent))
                {
                    continue;
                }

                var isPrivate = torrent.IsPrivate ?? await GetPrivacyAsync(qBittorrent, hash, cancellationToken).ConfigureAwait(false);
                torrents.Add(ContentRows.Describe(torrent, isPrivate, policy, now));
            }

            return torrents;
        }

        var result = new Dictionary<Guid, IReadOnlyList<ContentTorrent>>();
        var seasons = new Dictionary<Guid, IReadOnlyList<ContentTorrent>>();
        foreach (var entry in entries)
        {
            IEnumerable<string> hashes = entry.Kind switch
            {
                LibraryTitleKind.Movie when entry.TmdbId is int tmdb && byTmdb.TryGetValue(tmdb, out var found) => found,
                LibraryTitleKind.Series when entry.TvdbId is int tvdb && byTvdb.TryGetValue(tvdb, out var found) => found,
                LibraryTitleKind.Audiobook => MatchAudiobook(entry, bookCandidates),
                _ => []
            };

            result[entry.ItemId] = await DescribeAsync(hashes).ConfigureAwait(false);
            if (entry.TvdbId is int seriesTvdb)
            {
                foreach (var season in entry.Seasons ?? [])
                {
                    seasons[season.ItemId] = await DescribeAsync(byTvdbSeason.TryGetValue((seriesTvdb, season.Number), out var found) ? found : []).ConfigureAwait(false);
                }
            }
        }

        return (result, seasons);
    }

    private IEnumerable<string> MatchAudiobook(LibraryEntry entry, IReadOnlyList<(string Hash, IReadOnlyList<FileIdentity?> Files)> candidates)
    {
        var files = entry.FilePaths.Select(_links.GetIdentity).OfType<FileIdentity>().ToList();
        return files.Count == 0 || candidates.Count == 0
            ? []
            : InodeTorrentMatcher.Match(files, candidates).Select(m => m.Hash);
    }

    private async Task<IReadOnlyList<(string Hash, IReadOnlyList<FileIdentity?> Files)>> GetAudiobookCandidatesAsync(
        IEnumerable<QBittorrentTorrent> torrents,
        PufferfinSettings settings,
        ServiceSettings qBittorrent,
        CancellationToken cancellationToken)
    {
        // The same torrents the deletion looks at for an audiobook
        var paths = StoragePaths.From(settings);
        var policy = SeedPolicy.From(settings);
        var candidates = new List<(string Hash, IReadOnlyList<FileIdentity?> Files)>();
        foreach (var torrent in torrents.Where(t => !string.IsNullOrEmpty(t.SavePath))
            .Where(t => string.Equals(t.Category, MediaLayout.AudiobooksCategory, StringComparison.OrdinalIgnoreCase)
                || (policy.Manages(t.Category)
                    && !string.Equals(t.Category, MediaLayout.RadarrCategory, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(t.Category, MediaLayout.SonarrCategory, StringComparison.OrdinalIgnoreCase))))
        {
            var files = await _qBittorrent.GetFilesAsync(qBittorrent, torrent.Hash!, cancellationToken).ConfigureAwait(false);
            candidates.Add((
                torrent.Hash!.ToLowerInvariant(),
                files.Where(f => f.Priority > 0 && !string.IsNullOrEmpty(f.Name))
                    .Select(f => _links.GetIdentity(paths.ToLocalPath(Path.Join(torrent.SavePath, f.Name))))
                    .ToList()));
        }

        return candidates;
    }

    private async Task<bool?> GetPrivacyAsync(ServiceSettings qBittorrent, string hash, CancellationToken cancellationToken)
    {
        if (_privacy.TryGetValue(hash, out var known))
        {
            return known;
        }

        bool? isPrivate;
        try
        {
            isPrivate = await _qBittorrent.GetIsPrivateAsync(qBittorrent, hash, cancellationToken).ConfigureAwait(false);
        }
        catch (ArrApiException)
        {
            // Unknown, and asked again next time
            return null;
        }

        if (isPrivate is not null)
        {
            _privacy[hash] = isPrivate;
        }

        return isPrivate;
    }
}
