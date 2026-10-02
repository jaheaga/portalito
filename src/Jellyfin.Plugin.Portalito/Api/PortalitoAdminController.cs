using System.Security.Cryptography;
using MediaBrowser.Common.Api;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Portalito.Api;

/// <summary>
/// Endpoints for the config page. Unlike the proxy endpoints these are for people, not ffmpeg, so they sit behind
/// Jellyfin's own admin policy: the connection test signs in to the portal with the saved account.
/// </summary>
[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("Portalito/Admin")]
public class PortalitoAdminController : ControllerBase
{
    private readonly ConnectionTester _tester;
    private readonly ITaskManager _tasks;

    public PortalitoAdminController(ConnectionTester tester, ITaskManager tasks)
    {
        _tester = tester;
        _tasks = tasks;
    }

    /// <summary>Tests the saved settings (see <see cref="ConnectionTester"/>); one line per check.</summary>
    [HttpPost("Test")]
    public async Task<ActionResult<IReadOnlyList<ConnectionCheck>>> Test(CancellationToken cancellationToken)
        => Ok(await _tester.RunAsync(cancellationToken).ConfigureAwait(false));

    /// <summary>
    /// Replaces the secret that signs every proxy URL, revoking them all: the 12-hour VOD and 7-day live URLs Jellyfin
    /// handed out, and the never-expiring ones in the "Siguiendo" .strm files (anyone who copied one could otherwise
    /// stream the portal account without signing in to Jellyfin). Running playback stops; the Siguiendo sync is queued so
    /// its files are rewritten with the new signature right away.
    /// </summary>
    [HttpPost("RotateSecret")]
    public IActionResult RotateSecret()
    {
        if (Plugin.Instance is not { } plugin)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        plugin.Configuration.ProxySigningSecret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        plugin.SaveConfiguration();
        _tasks.QueueScheduledTask<Following.FollowSyncTask>();
        return NoContent();
    }
}
