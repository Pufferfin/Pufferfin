using System;
using System.Collections.Generic;
using System.Linq;
using Pufferfin.Server.Arr.Models;

namespace Pufferfin.Server.Requests.Search;

/// <summary>
/// Puts together the releases of the service's own search and of Pufferfin's searches, and ranks them.
/// </summary>
/// <remarks>
/// A torrent with a few seeders announced may be stalled for good, as "Psychomagic" was with one: a release well
/// seeded comes first, then the best resolution, then the most seeders. The language is chosen afterwards, release by
/// release in this order, by the selector.
/// </remarks>
public static class ReleaseRanking
{
    /// <summary>The seeders from which a torrent counts as well seeded.</summary>
    public const int HealthySeeders = 3;

    /// <summary>
    /// Merges two lists of releases, without the same release twice: the service's own copy is kept.
    /// </summary>
    /// <param name="service">The releases of the service's interactive search.</param>
    /// <param name="found">The releases found by Pufferfin through Prowlarr.</param>
    /// <returns>The releases, ranked.</returns>
    public static List<ArrRelease> Merge(IReadOnlyList<ArrRelease> service, IReadOnlyList<ArrRelease> found)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(found);
        var known = new HashSet<string>(service.Select(Key), StringComparer.Ordinal);
        var all = service.Concat(found.Where(r => known.Add(Key(r)))).ToList();
        return Rank(all);
    }

    /// <summary>
    /// Ranks releases: well seeded first, then the best resolution, then the most seeders. Equal ones keep their order.
    /// </summary>
    /// <param name="releases">The releases.</param>
    /// <returns>The releases, ranked.</returns>
    public static List<ArrRelease> Rank(IEnumerable<ArrRelease> releases)
    {
        ArgumentNullException.ThrowIfNull(releases);
        return releases
            .OrderByDescending(IsHealthy)
            .ThenByDescending(r => r.Quality?.Quality?.Resolution ?? 0)
            .ThenByDescending(r => r.Seeders ?? 0)
            .ToList();
    }

    /// <summary>
    /// Gets a value indicating whether a release is well seeded; a usenet one always is.
    /// </summary>
    /// <param name="release">The release.</param>
    /// <returns><c>true</c> when well seeded.</returns>
    public static bool IsHealthy(ArrRelease release)
    {
        ArgumentNullException.ThrowIfNull(release);
        return !string.Equals(release.Protocol, "torrent", StringComparison.OrdinalIgnoreCase) || (release.Seeders ?? 0) >= HealthySeeders;
    }

    // The same torrent comes from several searches and indexers: by its hash, else by its name and size.
    private static string Key(ArrRelease release)
        => !string.IsNullOrWhiteSpace(release.InfoHash)
            ? "h:" + release.InfoHash.ToUpperInvariant()
            : "t:" + ReleaseTitles.Normalize(release.Push?.OriginalTitle ?? release.Title ?? string.Empty) + "|" + (release.Size / 1_000_000);
}
