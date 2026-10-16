using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Pufferfin.Server.Arr.Models;

/// <summary>
/// A release found by an interactive search.
/// </summary>
public sealed class ArrRelease
{
    /// <summary>Gets or sets the release guid.</summary>
    public string? Guid { get; set; }

    /// <summary>Gets or sets the title.</summary>
    public string? Title { get; set; }

    /// <summary>Gets or sets the indexer id.</summary>
    public int IndexerId { get; set; }

    /// <summary>Gets or sets the parsed audio languages.</summary>
    public IList<ArrLanguage> Languages { get; set; } = [];

    /// <summary>Gets or sets a value indicating whether the service rejected the release.</summary>
    public bool Rejected { get; set; }

    /// <summary>Gets or sets why the service rejected the release.</summary>
    public IList<string> Rejections { get; set; } = [];

    /// <summary>Gets or sets the seeders of a torrent.</summary>
    public int? Seeders { get; set; }

    /// <summary>Gets or sets the size in bytes.</summary>
    public long Size { get; set; }

    /// <summary>Gets or sets the protocol (<c>torrent</c>, <c>usenet</c>).</summary>
    public string? Protocol { get; set; }

    /// <summary>Gets or sets the quality.</summary>
    public ArrQualityModel? Quality { get; set; }

    /// <summary>Gets or sets a value indicating whether a Sonarr release is a full season pack.</summary>
    public bool FullSeason { get; set; }

    /// <summary>Gets or sets the season number of a Sonarr release.</summary>
    public int SeasonNumber { get; set; }

    /// <summary>Gets or sets the episode numbers of a Sonarr release.</summary>
    public IList<int> EpisodeNumbers { get; set; } = [];

    /// <summary>Gets or sets the Sonarr episode ids the release maps to, when Sonarr reports them.</summary>
    public IList<int>? MappedEpisodeIds { get; set; }

    /// <summary>
    /// Gets or sets the season Sonarr maps the release to. An anime release numbered from the start of the series
    /// ("Title - 01") comes as season 0 with absolute numbers; Sonarr maps it to its season and episodes.
    /// </summary>
    public int? MappedSeasonNumber { get; set; }

    /// <summary>Gets or sets the episode numbers Sonarr maps the release to, within <see cref="MappedSeasonNumber"/>.</summary>
    public IList<int>? MappedEpisodeNumbers { get; set; }

    /// <summary>
    /// Gets or sets the info hash of the torrent, when the indexer gives it.
    /// </summary>
    public string? InfoHash { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether a pushed release was approved, and grabbed.
    /// </summary>
    public bool Approved { get; set; }

    /// <summary>
    /// Gets or sets the film a pushed or parsed release was mapped to.
    /// </summary>
    public int? MappedMovieId { get; set; }

    /// <summary>
    /// Gets or sets how to hand a release found outside the interactive search to the service; <c>null</c> for one
    /// the service found itself, grabbed by its guid.
    /// </summary>
    [JsonIgnore]
    public ReleasePush? Push { get; set; }

    /// <summary>Gets the season of the release: the one Sonarr maps it to, else the one of its title.</summary>
    [JsonIgnore]
    public int ResolvedSeasonNumber => MappedSeasonNumber ?? SeasonNumber;

    /// <summary>Gets the episodes of the release: the ones Sonarr maps it to, else the ones of its title.</summary>
    [JsonIgnore]
    public IList<int> ResolvedEpisodeNumbers => MappedEpisodeNumbers is { Count: > 0 } mapped ? mapped : EpisodeNumbers;
}
