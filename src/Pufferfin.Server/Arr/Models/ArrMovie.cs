namespace Pufferfin.Server.Arr.Models;

/// <summary>
/// A film in Radarr, reduced to what Pufferfin reads.
/// </summary>
public sealed class ArrMovie
{
    /// <summary>Gets or sets the Radarr id.</summary>
    public int Id { get; set; }

    /// <summary>Gets or sets the title.</summary>
    public string? Title { get; set; }

    /// <summary>Gets or sets the TMDB id.</summary>
    public int TmdbId { get; set; }

    /// <summary>
    /// Gets or sets the year Radarr knows the film by: a release is mapped to the film with it.
    /// </summary>
    public int Year { get; set; }

    /// <summary>Gets or sets a value indicating whether the film has a file on disk.</summary>
    public bool HasFile { get; set; }

    /// <summary>Gets or sets a value indicating whether the film is monitored.</summary>
    public bool Monitored { get; set; }

    /// <summary>Gets or sets the quality profile id.</summary>
    public int QualityProfileId { get; set; }

    /// <summary>Gets or sets the folder of the film.</summary>
    public string? Path { get; set; }
}
