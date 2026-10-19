using System;
using System.Collections.Generic;

namespace Pufferfin.Server.Deletion;

/// <summary>
/// A whole title on disk, as the content manager lists it: a film, a series or an audiobook.
/// </summary>
/// <param name="ItemId">The Jellyfin item id, the one a deletion takes.</param>
/// <param name="Kind">The kind: <see cref="LibraryTitleKind.Movie"/>, <see cref="LibraryTitleKind.Series"/> or <see cref="LibraryTitleKind.Audiobook"/>.</param>
/// <param name="Category">The library it belongs to.</param>
/// <param name="Title">The title.</param>
/// <param name="Year">The production year, when known.</param>
/// <param name="TmdbId">The TMDB id, when known.</param>
/// <param name="TvdbId">The TVDB id of a series, when known.</param>
/// <param name="SizeBytes">The size of its files.</param>
/// <param name="FilePaths">The files of an audiobook, matched to their torrents by inode; empty otherwise.</param>
/// <param name="Seasons">The seasons of a series on disk; none for another kind.</param>
public sealed record LibraryEntry(
    Guid ItemId,
    LibraryTitleKind Kind,
    ContentCategory Category,
    string Title,
    int? Year,
    int? TmdbId,
    int? TvdbId,
    long SizeBytes,
    IReadOnlyList<string> FilePaths,
    IReadOnlyList<LibrarySeason>? Seasons = null);
