using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Pufferfin.Catalog.Models;
using Pufferfin.Catalog.Tmdb;

namespace Pufferfin.Server.Requests.Integration;

/// <summary>
/// <see cref="ITitleMetadata"/> read from the TMDB catalog, which caches and rate limits TMDB.
/// </summary>
public sealed class TmdbTitleMetadata : ITitleMetadata
{
    private readonly TmdbCatalogService _catalog;

    /// <summary>
    /// Initializes a new instance of the <see cref="TmdbTitleMetadata"/> class.
    /// </summary>
    /// <param name="catalog">The TMDB catalog.</param>
    public TmdbTitleMetadata(TmdbCatalogService catalog)
    {
        _catalog = catalog;
    }

    /// <inheritdoc />
    public async Task<TitleMetadata?> GetMovieAsync(int tmdbId, CancellationToken cancellationToken)
        => Map(await _catalog.GetMovieAsync(tmdbId, Guid.Empty, cancellationToken).ConfigureAwait(false));

    /// <inheritdoc />
    public async Task<TitleMetadata?> GetSeriesAsync(int tmdbId, CancellationToken cancellationToken)
        => Map(await _catalog.GetSeriesAsync(tmdbId, Guid.Empty, cancellationToken).ConfigureAwait(false));

    private static TitleMetadata? Map(CatalogTitleDetails? details)
        => details is null
            ? null
            : new TitleMetadata(
                details.TmdbId,
                details.Title,
                details.OriginalLanguage,
                details.Genres.Select(g => g.Id).ToList(),
                details.Countries.Select(c => c.Code).ToList(),
                details.TvdbId,
                details.OriginalTitle);
}
