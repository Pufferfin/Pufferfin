using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;

namespace Pufferfin.Server.Deletion;

/// <summary>
/// <see cref="ILibraryContent"/> backed by the Jellyfin library manager.
/// </summary>
public sealed class JellyfinLibraryContent : ILibraryContent
{
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="JellyfinLibraryContent"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="userManager">The user manager.</param>
    public JellyfinLibraryContent(ILibraryManager libraryManager, IUserManager userManager)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
    }

    /// <inheritdoc />
    public LibraryTitle? Find(Guid itemId, Guid? visibleTo)
    {
        BaseItem? item;
        if (visibleTo is Guid userId)
        {
            // Jellyfin treats a missing user as "no restriction": an unknown user must find nothing instead.
            if (_userManager.GetUserById(userId) is not { } user)
            {
                return null;
            }

            item = _libraryManager.GetItemById<BaseItem>(itemId, user);
        }
        else
        {
            item = _libraryManager.GetItemById(itemId);
        }

        return item switch
        {
            Movie movie => new LibraryTitle(itemId, LibraryTitleKind.Movie, movie.Name, Id(movie, MetadataProvider.Tmdb), null, null, null, []),
            Series series => new LibraryTitle(itemId, LibraryTitleKind.Series, series.Name, Id(series, MetadataProvider.Tmdb), Id(series, MetadataProvider.Tvdb), null, null, []),
            Season { Series: { } series } season when season.IndexNumber is int number => new LibraryTitle(
                itemId,
                LibraryTitleKind.Season,
                string.Create(CultureInfo.InvariantCulture, $"{series.Name} S{number:00}"),
                Id(series, MetadataProvider.Tmdb),
                Id(series, MetadataProvider.Tvdb),
                number,
                null,
                []),
            Episode { Series: { } series } episode when episode.ParentIndexNumber is int season && episode.IndexNumber is int number => new LibraryTitle(
                itemId,
                LibraryTitleKind.Episode,
                string.Create(CultureInfo.InvariantCulture, $"{series.Name} S{season:00}E{number:00}"),
                Id(series, MetadataProvider.Tmdb),
                Id(series, MetadataProvider.Tvdb),
                season,
                number,
                []),
            AudioBook book => new LibraryTitle(itemId, LibraryTitleKind.Audiobook, book.Name, null, null, null, null, Files(book)),
            Folder folder when IsAudiobookFolder(folder) => new LibraryTitle(itemId, LibraryTitleKind.Audiobook, folder.Name, null, null, null, null, Files(folder)),
            _ => null
        };
    }

    /// <inheritdoc />
    public IReadOnlyList<Guid> FindMovies(int tmdbId)
        => _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Movie],
            HasAnyProviderIds = new Dictionary<string, string[]> { [MetadataProvider.Tmdb.ToString()] = [tmdbId.ToString(CultureInfo.InvariantCulture)] },
            IsVirtualItem = false,
            Recursive = true,
            DtoOptions = new DtoOptions(false)
        }).Select(item => item.Id).ToList();

    /// <inheritdoc />
    public IReadOnlyList<LibraryEntry> ListTitles()
    {
        var entries = new List<LibraryEntry>();
        var titles = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series],
            IsVirtualItem = false,
            Recursive = true,
            // With the provider ids: the torrents of a title are found through its TMDB or TVDB id
            DtoOptions = new DtoOptions(true)
        });
        foreach (var item in titles)
        {
            switch (item)
            {
                case Movie movie:
                    entries.Add(new LibraryEntry(
                        movie.Id,
                        LibraryTitleKind.Movie,
                        CategoryOf(movie.Path, LibraryTitleKind.Movie),
                        movie.Name,
                        movie.ProductionYear,
                        Id(movie, MetadataProvider.Tmdb),
                        null,
                        SizeOf([movie]),
                        []));
                    break;
                case Series series:
                    var episodes = series.GetRecursiveChildren(i => i is Episode && !i.IsVirtualItem);
                    entries.Add(new LibraryEntry(
                        series.Id,
                        LibraryTitleKind.Series,
                        CategoryOf(series.Path, LibraryTitleKind.Series),
                        series.Name,
                        series.ProductionYear,
                        Id(series, MetadataProvider.Tmdb),
                        Id(series, MetadataProvider.Tvdb),
                        SizeOf(episodes),
                        [],
                        SeasonsOf(episodes)));
                    break;
                default:
                    break;
            }
        }

        // An audiobook is the folder of its parts, as the deletion takes it
        var parts = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.AudioBook],
            IsVirtualItem = false,
            Recursive = true,
            DtoOptions = new DtoOptions(false)
        });
        foreach (var group in parts.GroupBy(part => part.ParentId))
        {
            var parent = _libraryManager.GetItemById(group.Key);
            // Only a folder that holds nothing but the parts is one book: an author folder holding books stays apart
            var title = parent is Folder folder && folder is not CollectionFolder && folder is not UserRootFolder
                && folder.GetRecursiveChildren(i => i.IsFolder).Count == 0 && IsAudiobookFolder(folder)
                ? Find(folder.Id, null)
                : null;
            if (title is not null && parent is not null)
            {
                entries.Add(new LibraryEntry(
                    parent.Id,
                    LibraryTitleKind.Audiobook,
                    ContentCategory.Audiobook,
                    parent.Name,
                    parent.ProductionYear ?? group.Select(part => part.ProductionYear).FirstOrDefault(year => year is not null),
                    null,
                    null,
                    SizeOf(group),
                    title.FilePaths));
                continue;
            }

            // Parts lying loose in the library root: each one a book of its own
            foreach (var part in group)
            {
                entries.Add(new LibraryEntry(part.Id, LibraryTitleKind.Audiobook, ContentCategory.Audiobook, part.Name, part.ProductionYear, null, null, SizeOf([part]), Files(part)));
            }
        }

        return entries;
    }

    /// <inheritdoc />
    public void DeleteFiles(Guid itemId)
    {
        if (_libraryManager.GetItemById(itemId) is { } item)
        {
            _libraryManager.DeleteItem(item, new DeleteOptions { DeleteFileLocation = true }, notifyParentItem: true);
        }
    }

    // The seasons that have episodes on disk, each found the way a deletion finds it
    private static List<LibrarySeason> SeasonsOf(IEnumerable<BaseItem> episodes)
    {
        var seasons = new List<LibrarySeason>();
        foreach (var group in episodes.OfType<Episode>().Where(e => e.Season is not null).GroupBy(e => e.Season!.Id))
        {
            if (group.First().Season is { IndexNumber: int number } season)
            {
                seasons.Add(new LibrarySeason(season.Id, number, season.Name, group.Count(), SizeOf(group)));
            }
        }

        return seasons;
    }

    private static ContentCategory CategoryOf(string? path, LibraryTitleKind kind)
    {
        // The libraries are folders of the media layout: anime and documentaries have folders of their own
        var segments = (path ?? string.Empty).Split('/', '\\');
        if (segments.Any(s => s.StartsWith("anime", StringComparison.OrdinalIgnoreCase)))
        {
            return ContentCategory.Anime;
        }

        if (segments.Any(s => s.StartsWith("documentar", StringComparison.OrdinalIgnoreCase)))
        {
            return ContentCategory.Documentary;
        }

        return kind == LibraryTitleKind.Movie ? ContentCategory.Movie : ContentCategory.Series;
    }

    private static long SizeOf(IEnumerable<BaseItem> items)
    {
        long total = 0;
        foreach (var item in items)
        {
            if (item.Size is long size && size > 0)
            {
                total += size;
                continue;
            }

            if (string.IsNullOrEmpty(item.Path))
            {
                continue;
            }

            try
            {
                var file = new FileInfo(item.Path);
                total += file.Exists ? file.Length : 0;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A file that cannot be read counts for nothing.
            }
        }

        return total;
    }

    private static int? Id(IHasProviderIds item, MetadataProvider provider)
        => item.TryGetProviderId(provider, out var id) && int.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static bool IsAudiobookFolder(Folder folder)
    {
        var children = folder.GetRecursiveChildren(i => !i.IsFolder);
        return children.Count > 0 && children.All(c => c is AudioBook);
    }

    private static List<string> Files(BaseItem item)
    {
        var items = item is Folder folder ? folder.GetRecursiveChildren(i => !i.IsFolder) : [item];
        return items.Where(i => !string.IsNullOrEmpty(i.Path) && !i.IsVirtualItem).Select(i => i.Path).Distinct(StringComparer.Ordinal).ToList();
    }
}
