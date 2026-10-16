using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Pufferfin.Server.Arr;
using Pufferfin.Server.Arr.Models;
using Pufferfin.Server.Configuration;
using Pufferfin.Server.Requests.Integration;
using Pufferfin.Server.Requests.Search;
using Pufferfin.Server.Requests.Selection;

namespace Pufferfin.Server.Requests.Workflow;

/// <summary>
/// Films, through Radarr.
/// </summary>
/// <remarks>
/// The search: add the film to Radarr when missing (profile of the first language tried, root folder of its
/// category), check the queue and the grab history against a double download, run an interactive search, take
/// the first release in the first language that has one (original version last), check the queue and the
/// history again (the search is slow, and Radarr may have grabbed meanwhile), then grab.
/// </remarks>
public sealed class MovieWorkflow : ArrWorkflowBase
{
    // The releases pushed at most for one search when Radarr refuses the first ones.
    private const int MaxPushes = 3;

    private readonly ITitleMetadata _metadata;
    private readonly IMediaLibrary _library;
    private readonly VariantReleaseSearch? _variantSearch;

    /// <summary>
    /// Initializes a new instance of the <see cref="MovieWorkflow"/> class.
    /// </summary>
    /// <param name="arr">The Radarr and Sonarr client.</param>
    /// <param name="qBittorrent">The qBittorrent client.</param>
    /// <param name="metadata">The TMDB metadata.</param>
    /// <param name="library">The Jellyfin library.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="variantSearch">The search of the film under each of its titles, through Prowlarr.</param>
    public MovieWorkflow(ArrClient arr, QBittorrentClient qBittorrent, ITitleMetadata metadata, IMediaLibrary library, ILogger<MovieWorkflow> logger, VariantReleaseSearch? variantSearch = null)
        : base(PufferfinService.Radarr, arr, qBittorrent, logger)
    {
        _metadata = metadata;
        _library = library;
        _variantSearch = variantSearch;
    }

    /// <inheritdoc />
    public override bool Handles(RequestKind kind) => kind == RequestKind.Movie;

    /// <inheritdoc />
    protected override async Task<MediaRequest> SearchCoreAsync(MediaRequest request, ServiceSettings service, RequestContext context, CancellationToken cancellationToken)
    {
        if (_library.HasMovie(request.TmdbId))
        {
            return request.To(RequestState.Available, context.Now);
        }

        var metadata = await _metadata.GetMovieAsync(request.TmdbId, cancellationToken).ConfigureAwait(false);
        if (metadata is null)
        {
            return request.Fail(context.Now, RequestProblemCode.TitleUnknown, "TMDB does not know this film.");
        }

        var plan = LanguagePlan.Build(request.Language, context.PreferredLanguages);
        var movie = await Arr.FindMovieAsync(service, request.TmdbId, cancellationToken).ConfigureAwait(false);
        if (movie is null)
        {
            var (added, failed) = await AddAsync(request, service, metadata, plan[0], context, cancellationToken).ConfigureAwait(false);
            if (added is null)
            {
                return failed!;
            }

            movie = added;
        }

        request = request with { ArrId = movie.Id };
        if (movie.HasFile)
        {
            return request.To(RequestState.Importing, context.Now);
        }

        if ((await CoverageAsync(service, movie.Id, cancellationToken).ConfigureAwait(false)).Covered)
        {
            return request.To(RequestState.Downloading, context.Now);
        }

        // Radarr searches one title with TMDB's year; Pufferfin also searches the French, English and original titles
        // through Prowlarr, and every release found competes, ranked well seeded first
        var own = await Arr.SearchMovieReleasesAsync(service, movie.Id, cancellationToken).ConfigureAwait(false);
        var found = _variantSearch is null
            ? []
            : await _variantSearch.FindMovieReleasesAsync(context.Settings, service, movie, [metadata.Title, movie.Title, metadata.OriginalTitle], cancellationToken).ConfigureAwait(false);
        var releases = ReleaseRanking.Merge(own, found);

        var pushes = 0;
        var checkedAgain = false;
        while (true)
        {
            var selection = ReleaseSelector.Select(releases, plan, metadata.OriginalLanguage, context.RejectSubtitledOnly, SelectionScope.Movie);
            if (selection.Release is not { } release)
            {
                return await NoReleaseAsync(request, service, selection.Problem!, context, cancellationToken).ConfigureAwait(false);
            }

            if (!checkedAgain)
            {
                // The search took a while: Radarr may have grabbed the film from its RSS feed meanwhile.
                checkedAgain = true;
                var refreshed = await Arr.FindMovieAsync(service, request.TmdbId, cancellationToken).ConfigureAwait(false) ?? movie;
                if (refreshed.HasFile || (await CoverageAsync(service, movie.Id, cancellationToken).ConfigureAwait(false)).Covered)
                {
                    return request.To(refreshed.HasFile ? RequestState.Importing : RequestState.Downloading, context.Now);
                }

                movie = refreshed;
            }

            await AlignProfileAsync(service, movie, selection.Language!, cancellationToken).ConfigureAwait(false);
            if (release.Push is null)
            {
                await Arr.GrabReleaseAsync(service, release, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                // A release found under another title is pushed: Radarr grabs it only when it approves it for this film
                var decision = await _variantSearch!.PushAsync(service, release, cancellationToken).ConfigureAwait(false);
                if (decision is not { Approved: true } || (decision.MappedMovieId is not null && decision.MappedMovieId != movie.Id))
                {
                    Logger.LogInformation(
                        "Request {RequestId}: Radarr refused {Title} found under \"{Query}\": {Reasons}",
                        request.Id,
                        release.Push.OriginalTitle,
                        release.Push.Query,
                        decision is null ? "no answer" : string.Join("; ", decision.Rejections));
                    releases = releases.Where(r => !ReferenceEquals(r, release)).ToList();
                    if (++pushes >= MaxPushes)
                    {
                        return await NoReleaseAsync(request, service, new RequestProblem(RequestProblemCode.NoRelease, "Releases were found, but Radarr refused them."), context, cancellationToken).ConfigureAwait(false);
                    }

                    continue;
                }
            }

            Logger.LogInformation(
                "Request {RequestId}: grabbed {Title} for film {TmdbId} in {Language} ({Seeders} seeders{Source})",
                request.Id,
                release.Push?.OriginalTitle ?? release.Title,
                request.TmdbId,
                selection.Language!.Code,
                release.Seeders,
                release.Push is null ? string.Empty : ", found under \"" + release.Push.Query + "\"");
            return request.To(RequestState.Downloading, context.Now) with
            {
                ChosenLanguage = selection.Language.Code,
                ReleaseTitle = release.Push?.OriginalTitle ?? release.Title,
                GrabbedAt = context.Now,
                Attempts = request.Attempts + 1,
                Progress = new RequestProgress(0, null, null)
            };
        }
    }

    /// <inheritdoc />
    protected override async Task<MediaRequest> TrackCoreAsync(MediaRequest request, ServiceSettings service, RequestContext context, CancellationToken cancellationToken)
    {
        if (_library.HasMovie(request.TmdbId))
        {
            // A temporary film only expires once its own download is known: looked up before it becomes available.
            request = await WithTemporaryDownloadAsync(request, service, null, cancellationToken).ConfigureAwait(false);
            return request.To(RequestState.Available, context.Now);
        }

        var movie = await Arr.FindMovieAsync(service, request.TmdbId, cancellationToken).ConfigureAwait(false);
        if (movie is null)
        {
            return request.Fail(context.Now, RequestProblemCode.TitleUnknown, "The film was removed from Radarr.");
        }

        var queue = await Arr.GetMovieQueueAsync(service, movie.Id, cancellationToken).ConfigureAwait(false);
        if (queue.Count > 0)
        {
            request = await WithTemporaryDownloadAsync(request, service, queue, cancellationToken).ConfigureAwait(false);
            return await FollowQueueAsync(request, queue, context, cancellationToken).ConfigureAwait(false);
        }

        if (movie.HasFile)
        {
            // In the library under the folder Radarr imported to, though identified as another film: it is there,
            // and it gets its TMDB id back
            if (_library.ClaimMovie(request.TmdbId, movie.Path))
            {
                request = await WithTemporaryDownloadAsync(request, service, null, cancellationToken).ConfigureAwait(false);
                return request.To(RequestState.Available, context.Now);
            }

            _library.Refresh(movie.Path);
            return request.To(RequestState.Importing, context.Now);
        }

        var history = await Arr.GetMovieHistoryAsync(service, movie.Id, cancellationToken).ConfigureAwait(false);
        if (DuplicateGuard.Check(false, [], history).Covered)
        {
            // Grabbed, not in the queue yet: the queue fills a while after the grab.
            return request.State == RequestState.WaitingForSources ? request.To(RequestState.Downloading, context.Now) : request;
        }

        return NothingInFlight(request, LastFailure(request, history), context);
    }

    private async Task<MediaRequest> WithTemporaryDownloadAsync(MediaRequest request, ServiceSettings service, IReadOnlyList<ArrQueueItem>? queue, CancellationToken cancellationToken)
    {
        if (!request.Temporary || !string.IsNullOrEmpty(request.TorrentHash) || request.ArrId is not int movieId)
        {
            return request;
        }

        // The queue names the download while it runs; once imported, only the grab history does.
        var history = queue is { Count: > 0 } ? null : await Arr.GetMovieHistoryAsync(service, movieId, cancellationToken).ConfigureAwait(false);
        if (TemporaryDownloads.FindDownload(request, queue ?? [], history) is not { } hash)
        {
            return request;
        }

        Logger.LogInformation("Request {RequestId}: temporary film {TmdbId} downloads as torrent {Hash}", request.Id, request.TmdbId, hash);
        return request with { TorrentHash = hash };
    }

    private async Task<Coverage> CoverageAsync(ServiceSettings service, int movieId, CancellationToken cancellationToken)
    {
        var queue = await Arr.GetMovieQueueAsync(service, movieId, cancellationToken).ConfigureAwait(false);
        var history = await Arr.GetMovieHistoryAsync(service, movieId, cancellationToken).ConfigureAwait(false);
        return DuplicateGuard.Check(false, queue, history);
    }

    private async Task<(ArrMovie? Movie, MediaRequest? Failed)> AddAsync(
        MediaRequest request,
        ServiceSettings service,
        TitleMetadata metadata,
        LanguageCandidate firstLanguage,
        RequestContext context,
        CancellationToken cancellationToken)
    {
        var folders = await Arr.GetRootFoldersAsync(service, cancellationToken).ConfigureAwait(false);
        var category = MediaLayout.Classify(metadata.GenreIds, metadata.CountryCodes, metadata.OriginalLanguage);
        if (MediaLayout.PickRootFolder(category, MediaLayout.RadarrRootFolders, folders) is not { } rootFolder)
        {
            return (null, request.Fail(context.Now, RequestProblemCode.NoRootFolder, "Radarr has no root folder to file the film in."));
        }

        if (await ProfileIdAsync(service, firstLanguage, cancellationToken).ConfigureAwait(false) is not int profileId)
        {
            return (null, request.Fail(context.Now, RequestProblemCode.ServiceUnavailable, "Radarr has no quality profile."));
        }

        JsonObject movie;
        try
        {
            movie = await Arr.LookupMovieAsync(service, request.TmdbId, cancellationToken).ConfigureAwait(false);
        }
        catch (ArrApiException ex) when (ex.Failure == ArrFailure.NotFound)
        {
            return (null, request.Fail(context.Now, RequestProblemCode.TitleUnknown, "Radarr does not find this film."));
        }

        movie["qualityProfileId"] = profileId;
        movie["rootFolderPath"] = rootFolder;
        movie["monitored"] = true;
        movie["minimumAvailability"] = "released";
        movie["addOptions"] = new JsonObject { ["searchForMovie"] = false, ["monitor"] = "movieOnly" };
        movie["tags"] ??= new JsonArray();
        try
        {
            return (await Arr.AddMovieAsync(service, movie, cancellationToken).ConfigureAwait(false), null);
        }
        catch (ArrApiException ex) when (ex.Failure == ArrFailure.Rejected)
        {
            // Added by someone else in the meantime.
            if (await Arr.FindMovieAsync(service, request.TmdbId, cancellationToken).ConfigureAwait(false) is { } existing)
            {
                return (existing, null);
            }

            throw;
        }
    }

    private async Task AlignProfileAsync(ServiceSettings service, ArrMovie movie, LanguageCandidate language, CancellationToken cancellationToken)
    {
        // Monitored, so Radarr keeps watching its feeds for it; in the profile of the language taken, when it has a Pufferfin profile.
        var profileId = await AlignedProfileIdAsync(service, movie.QualityProfileId, language, cancellationToken).ConfigureAwait(false);
        if (profileId is null && movie.Monitored)
        {
            return;
        }

        var raw = await Arr.GetMovieObjectAsync(service, movie.Id, cancellationToken).ConfigureAwait(false);
        raw["monitored"] = true;
        if (profileId is int id)
        {
            raw["qualityProfileId"] = id;
        }

        await Arr.UpdateMovieAsync(service, movie.Id, raw, cancellationToken).ConfigureAwait(false);
    }
}
