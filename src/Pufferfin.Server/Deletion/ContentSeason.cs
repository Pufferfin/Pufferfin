using System;
using System.Collections.Generic;

namespace Pufferfin.Server.Deletion;

/// <summary>
/// A season of a series of the content manager, deleted on its own: its files go, its torrents are kept the way the
/// deletion keeps them.
/// </summary>
/// <param name="ItemId">The Jellyfin item id of the season.</param>
/// <param name="Number">The season number; 0 for the specials.</param>
/// <param name="Title">The name of the season.</param>
/// <param name="EpisodeCount">How many of its episodes are on disk.</param>
/// <param name="SizeBytes">The size of their files.</param>
/// <param name="Torrents">The torrents its episodes came from; a pack of several seasons is listed under each.</param>
/// <param name="Ratio">The share ratio of its torrents, weighted by their size, when it has one.</param>
/// <param name="Tracker">The kind of trackers its torrents come from.</param>
/// <param name="ProtectedUntil">Until when one of its private torrents must keep seeding, if any.</param>
/// <param name="DeletionDueAt">When its scheduled deletion is due, if one waits.</param>
public sealed record ContentSeason(
    Guid ItemId,
    int Number,
    string Title,
    int EpisodeCount,
    long SizeBytes,
    IReadOnlyList<ContentTorrent> Torrents,
    double? Ratio,
    ContentTracker Tracker,
    DateTimeOffset? ProtectedUntil,
    DateTimeOffset? DeletionDueAt);
