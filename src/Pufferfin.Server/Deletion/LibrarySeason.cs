using System;

namespace Pufferfin.Server.Deletion;

/// <summary>
/// A season of a series on disk, as the content manager lists it.
/// </summary>
/// <param name="ItemId">The Jellyfin item id of the season, the one a deletion takes.</param>
/// <param name="Number">The season number; 0 for the specials.</param>
/// <param name="Title">The name of the season.</param>
/// <param name="EpisodeCount">How many of its episodes are on disk.</param>
/// <param name="SizeBytes">The size of their files.</param>
public sealed record LibrarySeason(
    Guid ItemId,
    int Number,
    string Title,
    int EpisodeCount,
    long SizeBytes);
