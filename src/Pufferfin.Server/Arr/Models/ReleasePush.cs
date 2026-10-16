using System;

namespace Pufferfin.Server.Arr.Models;

/// <summary>
/// A release found by Pufferfin's own search through Prowlarr, to push to Radarr or Sonarr. It carries the title
/// rewritten with the name and year the service knows the title by, so the service maps it to the right one.
/// </summary>
/// <param name="Title">The rewritten title, pushed.</param>
/// <param name="OriginalTitle">The title as the indexer named it.</param>
/// <param name="DownloadUrl">The link of the torrent or the NZB, through Prowlarr.</param>
/// <param name="MagnetUrl">The magnet link, if any.</param>
/// <param name="IndexerId">The id of the indexer in the service, or 0 when the service does not have it.</param>
/// <param name="Indexer">The name of the indexer in the service, or as Prowlarr names it.</param>
/// <param name="PublishDate">The publication date.</param>
/// <param name="Query">The search that found it.</param>
public sealed record ReleasePush(
    string Title,
    string OriginalTitle,
    string? DownloadUrl,
    string? MagnetUrl,
    int IndexerId,
    string Indexer,
    DateTimeOffset? PublishDate,
    string Query);
