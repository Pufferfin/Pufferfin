using System.Collections.Generic;

namespace Pufferfin.Server.Requests.Integration;

/// <summary>
/// A film or a series, reduced to what the Requests module needs.
/// </summary>
/// <param name="TmdbId">The TMDB id.</param>
/// <param name="Title">The title.</param>
/// <param name="OriginalLanguage">The ISO 639-1 original language.</param>
/// <param name="GenreIds">The TMDB genre ids.</param>
/// <param name="CountryCodes">The ISO 3166-1 countries of origin.</param>
/// <param name="TvdbId">The TVDB id of a series.</param>
/// <param name="OriginalTitle">The title in its original language, searched as well.</param>
public sealed record TitleMetadata(
    int TmdbId,
    string Title,
    string? OriginalLanguage,
    IReadOnlyList<int> GenreIds,
    IReadOnlyList<string> CountryCodes,
    int? TvdbId = null,
    string? OriginalTitle = null);
