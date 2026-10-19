using System;
using System.Collections.Generic;

namespace Pufferfin.Server.Deletion;

/// <summary>
/// A line of the content manager: a whole title on disk, with the torrents it came from.
/// </summary>
/// <param name="ItemId">The Jellyfin item id.</param>
/// <param name="Kind">The kind of the title.</param>
/// <param name="Category">The library it belongs to.</param>
/// <param name="Title">The title.</param>
/// <param name="Year">The production year, when known.</param>
/// <param name="SizeBytes">The size of its files.</param>
/// <param name="Torrents">Its torrents still in qBittorrent.</param>
/// <param name="AddedAt">When its oldest torrent was added, when it has one.</param>
/// <param name="Ratio">The share ratio of its torrents, weighted by their size, when it has one.</param>
/// <param name="Tracker">The kind of trackers its torrents come from.</param>
/// <param name="ProtectedUntil">Until when one of its private torrents must keep seeding, if any.</param>
/// <param name="DeletionDueAt">When its scheduled deletion is due, if one waits.</param>
public sealed record ContentRow(
    Guid ItemId,
    LibraryTitleKind Kind,
    ContentCategory Category,
    string Title,
    int? Year,
    long SizeBytes,
    IReadOnlyList<ContentTorrent> Torrents,
    DateTimeOffset? AddedAt,
    double? Ratio,
    ContentTracker Tracker,
    DateTimeOffset? ProtectedUntil,
    DateTimeOffset? DeletionDueAt)
{
    /// <summary>
    /// Gets the seasons of a series, each deleted on its own; none for another kind.
    /// </summary>
    public IReadOnlyList<ContentSeason> Seasons { get; init; } = [];
}
