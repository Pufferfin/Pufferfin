using System;
using System.Collections.Generic;
using System.Linq;
using Pufferfin.Server.Arr;
using Pufferfin.Server.Seeding;

namespace Pufferfin.Server.Deletion;

/// <summary>
/// Builds the lines of the content manager: a title, its torrents, their age, ratio and trackers.
/// </summary>
public static class ContentRows
{
    /// <summary>
    /// Describes one torrent.
    /// </summary>
    /// <param name="torrent">The qBittorrent torrent.</param>
    /// <param name="isPrivate">Whether its tracker is private, when known.</param>
    /// <param name="policy">The seeding policy.</param>
    /// <param name="now">The current time.</param>
    /// <returns>The torrent of the line.</returns>
    public static ContentTorrent Describe(QBittorrentTorrent torrent, bool? isPrivate, SeedPolicy policy, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(torrent);
        ArgumentNullException.ThrowIfNull(policy);

        // The same rule as the deletion: a torrent of unknown privacy is protected like a private one
        DateTimeOffset? protectedUntil = null;
        if (isPrivate != false)
        {
            var evaluation = SeedEvaluator.Evaluate(torrent, policy, now, released: true);
            if (evaluation.Verdict is SeedVerdict.Protected or SeedVerdict.Downloading)
            {
                protectedUntil = evaluation.ProtectedUntil ?? now + policy.MinSeedingTime;
            }
        }

        return new ContentTorrent(
            torrent.Hash!.ToLowerInvariant(),
            torrent.Name ?? torrent.Hash!,
            torrent.AddedOn > 0 ? DateTimeOffset.FromUnixTimeSeconds(torrent.AddedOn) : null,
            torrent.Ratio,
            isPrivate,
            torrent.Size,
            protectedUntil);
    }

    /// <summary>
    /// Builds the line of a title.
    /// </summary>
    /// <param name="entry">The title.</param>
    /// <param name="torrents">Its torrents.</param>
    /// <param name="deletionDueAt">When its scheduled deletion is due, if one waits.</param>
    /// <returns>The line.</returns>
    public static ContentRow Build(LibraryEntry entry, IReadOnlyList<ContentTorrent> torrents, DateTimeOffset? deletionDueAt)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(torrents);

        var ordered = torrents.OrderBy(t => t.AddedAt ?? DateTimeOffset.MaxValue).ToList();
        return new ContentRow(
            entry.ItemId,
            entry.Kind,
            entry.Category,
            entry.Title,
            entry.Year,
            entry.SizeBytes,
            ordered,
            ordered.Select(t => t.AddedAt).Where(d => d is not null).Min(),
            Ratio(ordered),
            Tracker(ordered),
            ordered.Select(t => t.ProtectedUntil).Where(d => d is not null).Max(),
            deletionDueAt);
    }

    /// <summary>
    /// Builds the line of a season.
    /// </summary>
    /// <param name="season">The season.</param>
    /// <param name="torrents">The torrents its episodes came from.</param>
    /// <param name="deletionDueAt">When its scheduled deletion is due, if one waits.</param>
    /// <returns>The line.</returns>
    public static ContentSeason BuildSeason(LibrarySeason season, IReadOnlyList<ContentTorrent> torrents, DateTimeOffset? deletionDueAt)
    {
        ArgumentNullException.ThrowIfNull(season);
        ArgumentNullException.ThrowIfNull(torrents);

        var ordered = torrents.OrderBy(t => t.AddedAt ?? DateTimeOffset.MaxValue).ToList();
        return new ContentSeason(
            season.ItemId,
            season.Number,
            season.Title,
            season.EpisodeCount,
            season.SizeBytes,
            ordered,
            Ratio(ordered),
            Tracker(ordered),
            ordered.Select(t => t.ProtectedUntil).Where(d => d is not null).Max(),
            deletionDueAt);
    }

    /// <summary>
    /// The share ratio of several torrents, each weighted by its size, so a big season counts more than a small extra.
    /// </summary>
    /// <param name="torrents">The torrents.</param>
    /// <returns>The ratio, or <c>null</c> without a torrent.</returns>
    public static double? Ratio(IReadOnlyCollection<ContentTorrent> torrents)
    {
        ArgumentNullException.ThrowIfNull(torrents);
        if (torrents.Count == 0)
        {
            return null;
        }

        var size = torrents.Sum(t => (double)Math.Max(t.SizeBytes, 0));
        return size > 0
            ? torrents.Sum(t => t.Ratio * Math.Max(t.SizeBytes, 0)) / size
            : torrents.Average(t => t.Ratio);
    }

    /// <summary>
    /// The kind of trackers of several torrents.
    /// </summary>
    /// <param name="torrents">The torrents.</param>
    /// <returns>The kind.</returns>
    public static ContentTracker Tracker(IReadOnlyCollection<ContentTorrent> torrents)
    {
        ArgumentNullException.ThrowIfNull(torrents);
        if (torrents.Count == 0)
        {
            return ContentTracker.None;
        }

        if (torrents.Any(t => t.IsPrivate is null))
        {
            return ContentTracker.Unknown;
        }

        var isPrivate = torrents.Any(t => t.IsPrivate == true);
        var isPublic = torrents.Any(t => t.IsPrivate == false);
        return (isPrivate, isPublic) switch
        {
            (true, true) => ContentTracker.Mixed,
            (true, false) => ContentTracker.Private,
            _ => ContentTracker.Public
        };
    }
}
