using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Pufferfin.Server.Arr;
using Pufferfin.Server.Deletion;
using Pufferfin.Server.Requests;
using Pufferfin.Server.Seeding;
using Pufferfin.Server.Tests.Requests;
using Xunit;

namespace Pufferfin.Server.Tests.Seeding;

/// <summary>
/// The content manager: titles listed with their torrents, deleted seven days after they are scheduled, only the
/// title scheduled, and a private torrent kept seeding until its minimum.
/// </summary>
public sealed class ContentManagerTests : IDisposable
{
    private const int DuneTmdbId = 438631;
    private const int BonesTvdbId = 75682;
    private static readonly Guid _movieItem = Guid.NewGuid();
    private static readonly Guid _admin = Guid.NewGuid();

    private readonly SeedingTestKit _kit = new();
    private readonly ListedLibrary _library = new();
    private readonly string _schedulesPath = Path.Combine(Path.GetTempPath(), "pufferfin-tests", Guid.NewGuid().ToString("N"), "scheduled.json");
    private readonly ScheduledDeletionStore _schedules;
    private readonly ScheduledDeletionRunner _runner;
    private readonly ContentDeletionService _deletion;

    public ContentManagerTests()
    {
        _schedules = new ScheduledDeletionStore(_schedulesPath, _kit.Logs.CreateLogger<ScheduledDeletionStore>());
        _library.Titles[_movieItem] = new LibraryTitle(_movieItem, LibraryTitleKind.Movie, "Dune", DuneTmdbId, null, null, null, []);
        _kit.Server.Add("movie", new JsonObject { ["id"] = 50, ["title"] = "Dune", ["tmdbId"] = DuneTmdbId, ["hasFile"] = true });

        var requests = new RequestStore(Path.Combine(Path.GetTempPath(), "pufferfin-tests", Guid.NewGuid().ToString("N"), "requests.json"), _kit.Logs.CreateLogger<RequestStore>());
        var resolver = new TitleTorrentResolver(_kit.Arr, _kit.QBittorrent, new RequestTestKit.FakeMetadata(), _kit.Links, _kit.Logs.CreateLogger<TitleTorrentResolver>());
        var deletion = new ContentDeletionService(
            _library,
            new RequestTestKit.FakeLibrary(),
            resolver,
            _kit.Arr,
            _kit.QBittorrent,
            _kit.Store,
            requests,
            _kit.Configuration,
            _kit.Clock,
            _kit.Logs.CreateLogger<ContentDeletionService>());
        _deletion = deletion;
        _runner = new ScheduledDeletionRunner(_schedules, deletion, _kit.Clock, _kit.Logs.CreateLogger<ScheduledDeletionRunner>());
    }

    private static DateTimeOffset Now => SeedingTestKit.Now;

    private bool RadarrDeletedTheFilm
        => _kit.Server.Calls.Any(c => c.Method == HttpMethod.Delete && c.Path.StartsWith("movie/50", StringComparison.Ordinal));

    public void Dispose()
    {
        _runner.Dispose();
        _kit.Dispose();
    }

    [Fact]
    public void Schedule_IsDueSevenDaysLater_AndDoneOnceHandled()
    {
        var scheduled = ScheduledDeletions.Schedule(_library.Titles[_movieItem], _admin, Now);

        Assert.Equal(Now + TimeSpan.FromDays(7), scheduled.DueAt);
        Assert.Equal(ScheduledDeletionDecision.NotDue, ScheduledDeletions.Decide(scheduled, Now.AddDays(6.9)));
        Assert.Equal(ScheduledDeletionDecision.Delete, ScheduledDeletions.Decide(scheduled, Now.AddDays(7)));
        Assert.Equal(ScheduledDeletionDecision.Done, ScheduledDeletions.Decide(scheduled with { Outcome = ScheduledDeletionOutcome.Removed }, Now.AddDays(8)));
    }

    [Fact]
    public void Mismatch_RefusesAnotherKind_OrAnotherTitle()
    {
        var scheduled = ScheduledDeletions.Schedule(_library.Titles[_movieItem], _admin, Now);
        var same = _library.Titles[_movieItem];

        Assert.Null(ScheduledDeletions.Mismatch(scheduled, same));
        Assert.NotNull(ScheduledDeletions.Mismatch(scheduled, same with { TmdbId = 1 }));
        Assert.NotNull(ScheduledDeletions.Mismatch(scheduled, same with { Kind = LibraryTitleKind.Series }));
        Assert.NotNull(ScheduledDeletions.Mismatch(scheduled, same with { ItemId = Guid.NewGuid() }));
    }

    [Fact]
    public void Store_KeepsOneScheduleATitle_CancelsIt_AndSurvivesARestart()
    {
        var first = _schedules.Add(ScheduledDeletions.Schedule(_library.Titles[_movieItem], _admin, Now));
        var again = _schedules.Add(ScheduledDeletions.Schedule(_library.Titles[_movieItem], _admin, Now.AddDays(3)));

        Assert.Equal(first.DueAt, again.DueAt);
        Assert.Single(new ScheduledDeletionStore(_schedulesPath, _kit.Logs.CreateLogger<ScheduledDeletionStore>()).GetAll());

        Assert.True(_schedules.Cancel(_movieItem));
        Assert.False(_schedules.Cancel(_movieItem));
        Assert.Empty(new ScheduledDeletionStore(_schedulesPath, _kit.Logs.CreateLogger<ScheduledDeletionStore>()).GetAll());
    }

    [Fact]
    public async Task NothingIsDeleted_BeforeTheDay()
    {
        Grab("own1");
        _kit.AddTorrent("own1", isPrivate: false, seededDays: 40);
        _schedules.Add(ScheduledDeletions.Schedule(_library.Titles[_movieItem], _admin, Now));
        _kit.Clock.Advance(TimeSpan.FromDays(6));

        Assert.Equal(0, await _runner.RunAsync(CancellationToken.None));
        Assert.DoesNotContain(_kit.Server.Calls, c => c.Method == HttpMethod.Delete);
        Assert.Empty(_kit.Deletions);
    }

    [Fact]
    public async Task DueTitle_GoesWithItsPublicTorrent_Once()
    {
        Grab("own1");
        _kit.AddTorrent("own1", isPrivate: false, seededDays: 7);
        _kit.AddTorrent("unrelated", isPrivate: false, seededDays: 7);
        _schedules.Add(ScheduledDeletions.Schedule(_library.Titles[_movieItem], _admin, Now));
        _kit.Clock.Advance(TimeSpan.FromDays(7));

        Assert.Equal(1, await _runner.RunAsync(CancellationToken.None));

        Assert.True(RadarrDeletedTheFilm);
        Assert.Equal(new[] { "own1" }, Assert.Single(_kit.Deletions));
        Assert.Equal(ScheduledDeletionOutcome.Removed, Assert.Single(_schedules.GetAll()).Outcome);

        // A second run finds nothing to do
        var calls = _kit.Server.Calls.Count;
        Assert.Equal(0, await _runner.RunAsync(CancellationToken.None));
        Assert.Equal(calls, _kit.Server.Calls.Count);
    }

    [Fact]
    public async Task DueTitle_KeepsItsPrivateTorrentSeeding_UntilItsMinimum()
    {
        Grab("own1");
        _kit.AddTorrent("own1", isPrivate: true, seededDays: 2);
        _schedules.Add(ScheduledDeletions.Schedule(_library.Titles[_movieItem], _admin, Now));
        _kit.Clock.Advance(TimeSpan.FromDays(7));

        await _runner.RunAsync(CancellationToken.None);

        Assert.True(RadarrDeletedTheFilm);
        Assert.Empty(_kit.Deletions);
        Assert.Equal("own1", Assert.Single(_kit.Store.GetPendingReleases()).Hash);
    }

    [Fact]
    public async Task AnotherTitleUnderTheSameId_IsNeverDeleted()
    {
        Grab("own1");
        _kit.AddTorrent("own1", isPrivate: false, seededDays: 40);
        _schedules.Add(ScheduledDeletions.Schedule(_library.Titles[_movieItem], _admin, Now));
        _library.Titles[_movieItem] = _library.Titles[_movieItem] with { TmdbId = 1 };
        _kit.Server.Add("movie", new JsonObject { ["id"] = 51, ["title"] = "Other", ["tmdbId"] = 1, ["hasFile"] = true });
        _kit.Clock.Advance(TimeSpan.FromDays(7));

        await _runner.RunAsync(CancellationToken.None);

        Assert.DoesNotContain(_kit.Server.Calls, c => c.Method == HttpMethod.Delete);
        Assert.Empty(_kit.Deletions);
        Assert.Equal(ScheduledDeletionOutcome.NeedsAttention, Assert.Single(_schedules.GetAll()).Outcome);
    }

    [Fact]
    public async Task TitleAlreadyGone_IsRecorded_WithoutDeleting()
    {
        _schedules.Add(ScheduledDeletions.Schedule(_library.Titles[_movieItem], _admin, Now));
        _library.Titles.Remove(_movieItem);
        _kit.Clock.Advance(TimeSpan.FromDays(7));

        Assert.Equal(1, await _runner.RunAsync(CancellationToken.None));

        Assert.DoesNotContain(_kit.Server.Calls, c => c.Method == HttpMethod.Delete);
        Assert.Equal(ScheduledDeletionOutcome.AlreadyGone, Assert.Single(_schedules.GetAll()).Outcome);
    }

    [Fact]
    public void Rows_WeighTheRatioBySize_AndTellTheTrackers()
    {
        var big = new ContentTorrent("a", "A", Now.AddDays(-10), 2.0, true, 3_000, null);
        var small = new ContentTorrent("b", "B", Now.AddDays(-2), 0.0, false, 1_000, null);

        Assert.Equal(1.5, ContentRows.Ratio([big, small])!.Value, 3);
        Assert.Null(ContentRows.Ratio([]));
        Assert.Equal(ContentTracker.Mixed, ContentRows.Tracker([big, small]));
        Assert.Equal(ContentTracker.Private, ContentRows.Tracker([big]));
        Assert.Equal(ContentTracker.Public, ContentRows.Tracker([small]));
        Assert.Equal(ContentTracker.Unknown, ContentRows.Tracker([big with { IsPrivate = null }]));
        Assert.Equal(ContentTracker.None, ContentRows.Tracker([]));

        var entry = new LibraryEntry(_movieItem, LibraryTitleKind.Series, ContentCategory.Series, "Bones", 2005, null, 75682, 4_000, []);
        var row = ContentRows.Build(entry, [small, big], Now.AddDays(7));
        Assert.Equal(Now.AddDays(-10), row.AddedAt);
        Assert.Equal("a", row.Torrents[0].Hash);
        Assert.Equal(Now.AddDays(7), row.DeletionDueAt);
    }

    [Fact]
    public void Describe_ProtectsAPrivateTorrentBeforeItsMinimum_NeverAPublicOne()
    {
        var policy = SeedPolicy.From(_kit.Configuration.Current.Settings);
        var torrent = new QBittorrentTorrent { Hash = "ABC", Name = "Release", Progress = 1, Ratio = 0.4, SeedingTime = (long)TimeSpan.FromDays(2).TotalSeconds, CompletionOn = Now.AddDays(-2).ToUnixTimeSeconds(), AddedOn = Now.AddDays(-11).ToUnixTimeSeconds(), Size = 10 };

        var isPrivate = ContentRows.Describe(torrent, true, policy, Now);
        var unknown = ContentRows.Describe(torrent, null, policy, Now);
        var isPublic = ContentRows.Describe(torrent, false, policy, Now);

        Assert.Equal("abc", isPrivate.Hash);
        Assert.Equal(Now.AddDays(-11), isPrivate.AddedAt);
        Assert.Equal(Now + (policy.MinSeedingTime - TimeSpan.FromDays(2)), isPrivate.ProtectedUntil);
        Assert.NotNull(unknown.ProtectedUntil);
        Assert.Null(isPublic.ProtectedUntil);
    }

    [Fact]
    public async Task Inventory_ListsEachTitle_WithItsOwnTorrents_AndItsSchedule()
    {
        _library.Entries.Add(new LibraryEntry(_movieItem, LibraryTitleKind.Movie, ContentCategory.Movie, "Dune", 2021, DuneTmdbId, null, 8_000, []));
        _library.Entries.Add(new LibraryEntry(Guid.NewGuid(), LibraryTitleKind.Movie, ContentCategory.Anime, "Paprika", 2006, 4977, null, 2_000, []));
        _kit.Server.Resource("history/since").Add(new JsonObject
        {
            ["id"] = 1,
            ["eventType"] = "grabbed",
            ["movieId"] = 50,
            ["movie"] = new JsonObject { ["id"] = 50, ["tmdbId"] = DuneTmdbId },
            ["downloadId"] = "OWN1",
            ["date"] = Now.AddDays(-5)
        });
        _kit.AddTorrent("own1", isPrivate: true, ratio: 0.8, seededDays: 4);
        _kit.AddTorrent("unrelated", isPrivate: false, seededDays: 4);
        _schedules.Add(ScheduledDeletions.Schedule(_library.Titles[_movieItem], _admin, Now));

        var inventory = new ContentInventory(_library, _kit.Arr, _kit.QBittorrent, _kit.Links, _schedules, _kit.Configuration, _kit.Clock, _kit.Logs.CreateLogger<ContentInventory>());
        var result = await inventory.ListAsync(CancellationToken.None);

        Assert.True(result.TorrentsAvailable);
        var dune = result.Rows.Single(r => r.Title == "Dune");
        var torrent = Assert.Single(dune.Torrents);
        Assert.Equal("own1", torrent.Hash);
        Assert.Equal(0.8, dune.Ratio!.Value, 3);
        Assert.Equal(ContentTracker.Private, dune.Tracker);
        Assert.NotNull(dune.ProtectedUntil);
        Assert.Equal(Now.AddDays(7), dune.DeletionDueAt);
        var paprika = result.Rows.Single(r => r.Title == "Paprika");
        Assert.Empty(paprika.Torrents);
        Assert.Equal(ContentTracker.None, paprika.Tracker);
        Assert.Equal(ContentCategory.Anime, paprika.Category);

        // Reading never changes anything
        Assert.DoesNotContain(_kit.Server.Calls, c => c.Method != HttpMethod.Get && c.Path != "auth/login");
    }

    [Fact]
    public async Task Inventory_ListsTheSeasonsOfASeries_EachWithItsOwnTorrents()
    {
        var season1 = Guid.NewGuid();
        var season2 = Guid.NewGuid();
        _library.Entries.Add(new LibraryEntry(
            Guid.NewGuid(),
            LibraryTitleKind.Series,
            ContentCategory.Series,
            "Bones",
            2005,
            null,
            BonesTvdbId,
            9_000,
            [],
            [new LibrarySeason(season2, 2, "Saison 2", 1, 3_000), new LibrarySeason(season1, 1, "Saison 1", 2, 6_000)]));
        _kit.Server.Add("series", new JsonObject { ["id"] = 7, ["title"] = "Bones", ["tvdbId"] = BonesTvdbId });
        SeriesGrab("history/since", "PACK1", 1, 1);
        SeriesGrab("history/since", "COMPLETE", 1, 1);
        SeriesGrab("history/since", "COMPLETE", 3, 2);
        _kit.AddTorrent("pack1", category: MediaLayout.SonarrCategory, isPrivate: true, ratio: 0.5, seededDays: 1);
        _kit.AddTorrent("complete", category: MediaLayout.SonarrCategory, isPrivate: false, ratio: 2, seededDays: 30);

        var inventory = new ContentInventory(_library, _kit.Arr, _kit.QBittorrent, _kit.Links, _schedules, _kit.Configuration, _kit.Clock, _kit.Logs.CreateLogger<ContentInventory>());
        var bones = Assert.Single((await inventory.ListAsync(CancellationToken.None)).Rows);

        Assert.Equal([1, 2], bones.Seasons.Select(s => s.Number));
        var first = bones.Seasons[0];
        Assert.Equal(season1, first.ItemId);
        Assert.Equal(["complete", "pack1"], first.Torrents.Select(t => t.Hash).Order());
        Assert.Equal(ContentTracker.Mixed, first.Tracker);
        Assert.NotNull(first.ProtectedUntil);
        var second = bones.Seasons[1];
        Assert.Equal("complete", Assert.Single(second.Torrents).Hash);
        Assert.Null(second.ProtectedUntil);
    }

    [Fact]
    public async Task DeletingASeason_DeletesOnlyItsFiles_AndKeepsEveryTorrent()
    {
        var season1 = Guid.NewGuid();
        _library.Titles[season1] = new LibraryTitle(season1, LibraryTitleKind.Season, "Bones S01", null, BonesTvdbId, 1, null, []);
        _kit.Server.Add("series", new JsonObject { ["id"] = 7, ["title"] = "Bones", ["tvdbId"] = BonesTvdbId });
        Episode(1, 1, 1, 11);
        Episode(2, 1, 2, 12);
        Episode(3, 2, 1, 13);
        SeriesGrab("history/series", "PACK1", 1, 1);
        SeriesGrab("history/series", "PACK1", 2, 1);
        SeriesGrab("history/series", "COMPLETE", 1, 1);
        SeriesGrab("history/series", "COMPLETE", 3, 2);
        _kit.AddTorrent("pack1", category: MediaLayout.SonarrCategory, isPrivate: true, seededDays: 1);
        _kit.AddTorrent("complete", category: MediaLayout.SonarrCategory, isPrivate: true, seededDays: 1);

        var outcome = await _deletion.DeleteAsync(season1, DeletionMode.Safe, false, _admin, true, CancellationToken.None);

        Assert.Equal(DeletionStatus.Deleted, outcome.Status);
        var deletedFiles = _kit.Server.Calls.Where(c => c.Method == HttpMethod.Delete).Select(c => c.Path).ToList();
        Assert.Equal(["episodefile/11", "episodefile/12"], deletedFiles.Order());
        Assert.Contains(_kit.Server.Calls, c => c.Method == HttpMethod.Put && c.Path.StartsWith("episode/monitor", StringComparison.Ordinal));

        // The season pack keeps seeding until its minimum; the complete pack still holds season 2, so it stays
        Assert.Empty(_kit.Deletions);
        Assert.Equal("pack1", Assert.Single(_kit.Store.GetPendingReleases()).Hash);
        Assert.Contains(outcome.Plan!.Torrents, t => t.Hash == "complete" && t.Action == TorrentAction.KeepShared);
    }

    [Fact]
    public void ASeason_CanBeScheduled_AndOnlyThatSeasonMatches()
    {
        var season1 = Guid.NewGuid();
        var title = new LibraryTitle(season1, LibraryTitleKind.Season, "Bones S01", null, BonesTvdbId, 1, null, []);
        var deletion = ScheduledDeletions.Schedule(title, _admin, Now);

        Assert.Equal(LibraryTitleKind.Season, deletion.Kind);
        Assert.Null(ScheduledDeletions.Mismatch(deletion, title));
        Assert.NotNull(ScheduledDeletions.Mismatch(deletion, title with { Kind = LibraryTitleKind.Series }));
    }

    private void Episode(int id, int season, int number, int fileId)
        => _kit.Server.Add("episode", new JsonObject
        {
            ["id"] = id,
            ["seriesId"] = 7,
            ["seasonNumber"] = season,
            ["episodeNumber"] = number,
            ["hasFile"] = true,
            ["episodeFileId"] = fileId
        });

    private void SeriesGrab(string resource, string downloadId, int episodeId, int season)
        => _kit.Server.Resource(resource).Add(new JsonObject
        {
            ["id"] = Random.Shared.Next(1, 100000),
            ["eventType"] = "grabbed",
            ["seriesId"] = 7,
            ["episodeId"] = episodeId,
            ["series"] = new JsonObject { ["id"] = 7, ["tvdbId"] = BonesTvdbId },
            ["episode"] = new JsonObject { ["id"] = episodeId, ["seriesId"] = 7, ["seasonNumber"] = season },
            ["downloadId"] = downloadId,
            ["date"] = Now.AddDays(-2)
        });

    private void Grab(string downloadId)
        => _kit.Server.Resource("history/movie").Add(new JsonObject
        {
            ["id"] = Random.Shared.Next(1, 100000),
            ["eventType"] = "grabbed",
            ["movieId"] = 50,
            ["downloadId"] = downloadId,
            ["sourceTitle"] = "Release." + downloadId,
            ["date"] = Now.AddDays(-1)
        });

    private sealed class ListedLibrary : ILibraryContent
    {
        public Dictionary<Guid, LibraryTitle> Titles { get; } = [];

        public List<LibraryEntry> Entries { get; } = [];

        public LibraryTitle? Find(Guid itemId, Guid? visibleTo) => Titles.GetValueOrDefault(itemId);

        public IReadOnlyList<Guid> FindMovies(int tmdbId)
            => Titles.Values.Where(t => t.Kind == LibraryTitleKind.Movie && t.TmdbId == tmdbId).Select(t => t.ItemId).ToList();

        public void DeleteFiles(Guid itemId)
        {
        }

        public IReadOnlyList<LibraryEntry> ListTitles() => Entries;
    }
}
