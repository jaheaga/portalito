using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
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

    public PortalitoAdminController(ConnectionTester tester) => _tester = tester;

    /// <summary>Tests the saved settings (see <see cref="ConnectionTester"/>); one line per check.</summary>
    [HttpPost("Test")]
    public async Task<ActionResult<IReadOnlyList<ConnectionCheck>>> Test(CancellationToken cancellationToken)
        => Ok(await _tester.RunAsync(cancellationToken).ConfigureAwait(false));
}
