using System;

namespace Pufferfin.Server.Arr.Models;

/// <summary>
/// A result of a Prowlarr search.
/// </summary>
public sealed class ProwlarrRelease
{
    /// <summary>
    /// Gets or sets the guid, unique per indexer.
    /// </summary>
    public string? Guid { get; set; }

    /// <summary>
    /// Gets or sets the title, as the indexer names the release.
    /// </summary>
    public string? Title { get; set; }

    /// <summary>
    /// Gets or sets the id of the indexer in Prowlarr.
    /// </summary>
    public int IndexerId { get; set; }

    /// <summary>
    /// Gets or sets the name of the indexer in Prowlarr.
    /// </summary>
    public string? Indexer { get; set; }

    /// <summary>
    /// Gets or sets the size in bytes.
    /// </summary>
    public long Size { get; set; }

    /// <summary>
    /// Gets or sets the number of seeders the indexer announces.
    /// </summary>
    public int? Seeders { get; set; }

    /// <summary>
    /// Gets or sets the protocol: torrent or usenet.
    /// </summary>
    public string? Protocol { get; set; }

    /// <summary>
    /// Gets or sets the download link, through Prowlarr.
    /// </summary>
    public string? DownloadUrl { get; set; }

    /// <summary>
    /// Gets or sets the magnet link, if any.
    /// </summary>
    public string? MagnetUrl { get; set; }

    /// <summary>
    /// Gets or sets the info hash, if the indexer gives it.
    /// </summary>
    public string? InfoHash { get; set; }

    /// <summary>
    /// Gets or sets the publication date.
    /// </summary>
    public DateTimeOffset? PublishDate { get; set; }
}
