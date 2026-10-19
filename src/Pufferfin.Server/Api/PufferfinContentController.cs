using System;
using System.ComponentModel.DataAnnotations;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Api;
using Jellyfin.Api.Extensions;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Pufferfin.Server.Deletion;

namespace Pufferfin.Server.Api;

/// <summary>
/// The content manager of the administrators: every title on disk with its torrents, deleted now or seven days later.
/// Every deletion is the safe one: the library files go, a private torrent keeps seeding until its minimum.
/// </summary>
[Route("Pufferfin/Content")]
[Tags("Pufferfin")]
[Authorize(Policy = Policies.RequiresElevation)]
public class PufferfinContentController : BaseJellyfinApiController
{
    private readonly ContentInventory _inventory;
    private readonly ContentDeletionService _deletion;
    private readonly ScheduledDeletionStore _schedules;
    private readonly ILibraryContent _library;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="PufferfinContentController"/> class.
    /// </summary>
    /// <param name="inventory">The content inventory.</param>
    /// <param name="deletion">The deletion service.</param>
    /// <param name="schedules">The scheduled deletions.</param>
    /// <param name="library">The library content.</param>
    /// <param name="timeProvider">The time provider.</param>
    public PufferfinContentController(ContentInventory inventory, ContentDeletionService deletion, ScheduledDeletionStore schedules, ILibraryContent library, TimeProvider timeProvider)
    {
        _inventory = inventory;
        _deletion = deletion;
        _schedules = schedules;
        _library = library;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Lists every whole title on disk: films, series and audiobooks, with their size, torrents, age, ratio and trackers.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">List returned.</response>
    /// <returns>The list.</returns>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<ContentInventoryResult>> GetPufferfinContent(CancellationToken cancellationToken)
        => await _inventory.ListAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Deletes a title now, with the safe deletion: its files and its public torrents go now, its private torrents
    /// keep seeding until their minimum, then the hourly task removes them with their files.
    /// </summary>
    /// <param name="itemId">The Jellyfin item id of a film, a series, a season or an audiobook.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">Title deleted.</response>
    /// <response code="404">No such title.</response>
    /// <response code="409">Radarr or Sonarr, which holds the title, is not configured.</response>
    /// <response code="503">A service could not be reached. Nothing was deleted.</response>
    /// <returns>The outcome, with the torrents kept seeding.</returns>
    [HttpDelete("{itemId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<DeletionOutcome>> DeletePufferfinContent([FromRoute, Required] Guid itemId, CancellationToken cancellationToken)
    {
        var outcome = await _deletion.DeleteAsync(itemId, DeletionMode.Safe, false, User.GetUserId(), true, cancellationToken).ConfigureAwait(false);
        if (outcome.Status == DeletionStatus.Deleted)
        {
            // Deleted by hand: nothing left for a schedule to do
            _schedules.Cancel(itemId);
        }

        return outcome.Status switch
        {
            DeletionStatus.Deleted => outcome,
            DeletionStatus.NotFound => Problem(detail: outcome.Message, statusCode: StatusCodes.Status404NotFound, title: outcome.Status.ToString()),
            DeletionStatus.ServiceDisabled => Problem(detail: outcome.Message, statusCode: StatusCodes.Status409Conflict, title: outcome.Status.ToString()),
            DeletionStatus.Forbidden => Problem(detail: outcome.Message, statusCode: StatusCodes.Status403Forbidden, title: outcome.Status.ToString()),
            _ => Problem(detail: outcome.Message, statusCode: StatusCodes.Status503ServiceUnavailable, title: outcome.Status.ToString())
        };
    }

    /// <summary>
    /// Schedules the safe deletion of a title seven days from now. A title already scheduled keeps its date.
    /// </summary>
    /// <param name="itemId">The Jellyfin item id of a film, a series, a season or an audiobook.</param>
    /// <response code="200">Deletion scheduled.</response>
    /// <response code="404">No such film, series, season or audiobook.</response>
    /// <returns>The scheduled deletion.</returns>
    [HttpPost("{itemId}/Schedule")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public ActionResult<ScheduledDeletion> SchedulePufferfinContentDeletion([FromRoute, Required] Guid itemId)
    {
        if (_library.Find(itemId, null) is not { Kind: LibraryTitleKind.Movie or LibraryTitleKind.Series or LibraryTitleKind.Season or LibraryTitleKind.Audiobook } title)
        {
            return Problem(detail: "No such film, series, season or audiobook in the library.", statusCode: StatusCodes.Status404NotFound, title: nameof(DeletionStatus.NotFound));
        }

        return _schedules.Add(ScheduledDeletions.Schedule(title, User.GetUserId(), _timeProvider.GetUtcNow()));
    }

    /// <summary>
    /// Cancels the scheduled deletion of a title.
    /// </summary>
    /// <param name="itemId">The Jellyfin item id.</param>
    /// <response code="204">Deletion cancelled.</response>
    /// <response code="404">No deletion waited for this title.</response>
    /// <returns>Nothing.</returns>
    [HttpDelete("{itemId}/Schedule")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public ActionResult CancelPufferfinContentDeletion([FromRoute, Required] Guid itemId)
        => _schedules.Cancel(itemId)
            ? NoContent()
            : Problem(detail: "No deletion was scheduled for this title.", statusCode: StatusCodes.Status404NotFound, title: nameof(DeletionStatus.NotFound));
}
