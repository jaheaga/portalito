using System.Net;
using System.Security.Cryptography;
using Jellyfin.Plugin.Portalito.Portal;

namespace Jellyfin.Plugin.Portalito.Api;

/// <summary>One line of the config page's connection report.</summary>
/// <param name="Name">What was checked.</param>
/// <param name="Status"><c>ok</c>, <c>warning</c> (works, with a caveat) or <c>error</c>.</param>
/// <param name="Message">What was found, in words a person can act on.</param>
public sealed record ConnectionCheck(string Name, string Status, string Message)
{
    public static ConnectionCheck Ok(string name, string message) => new(name, "ok", message);

    public static ConnectionCheck Warning(string name, string message) => new(name, "warning", message);

    public static ConnectionCheck Error(string name, string message) => new(name, "error", message);
}

/// <summary>
/// Checks the saved settings end to end for the config page, so a setup mistake shows up as a sentence on the page
/// instead of as a playback failure to dig out of the server log: the settings themselves, a real portal call (which
/// signs in first), whether live TV is available, whether ffmpeg can reach the proxy, and the guide's time zone.
/// </summary>
public sealed class ConnectionTester
{
    private static readonly TimeSpan PortalTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PingTimeout = TimeSpan.FromSeconds(5);

    private readonly IPortalitoServicesProvider _provider;
    private readonly HttpClient _selfClient;

    /// <param name="provider">The live services, built from the saved settings.</param>
    /// <param name="selfClient">Calls this server's own proxy URL, like ffmpeg does.</param>
    public ConnectionTester(IPortalitoServicesProvider provider, HttpClient selfClient)
    {
        _provider = provider;
        _selfClient = selfClient;
    }

    /// <summary>
    /// A client for <see cref="ConnectionTester"/>'s self-check. Certificates aren't checked: it only ever calls this
    /// server itself, usually on a loopback address its certificate wasn't issued for -- and ffmpeg doesn't check
    /// them either, so checking here would report a problem playback doesn't have.
    /// </summary>
    public static HttpClient CreateSelfClient()
        => new(new SocketsHttpHandler { SslOptions = { RemoteCertificateValidationCallback = (_, _, _, _) => true } }) { Timeout = Timeout.InfiniteTimeSpan };

    public async Task<IReadOnlyList<ConnectionCheck>> RunAsync(CancellationToken cancellationToken)
    {
        PortalitoServices services;
        try
        {
            services = _provider.Get();
        }
        catch (PortalException ex)
        {
            return new[] { ConnectionCheck.Error("Settings", ex.Message) };
        }

        var checks = new List<ConnectionCheck> { ConnectionCheck.Ok("Settings", "The saved settings are complete.") };
        checks.Add(await PortalAsync(services, cancellationToken).ConfigureAwait(false));
        checks.Add(services.Portal.HasAccount
            ? ConnectionCheck.Ok("Live TV", "An account is set, so live TV is available.")
            : ConnectionCheck.Warning("Live TV", "No account is set: the catalog and VOD work, but live TV needs a Portalito account."));
        checks.Add(await ProxyAsync(services, cancellationToken).ConfigureAwait(false));
        checks.Add(services.EpgTimeZoneProblem is { } problem
            ? ConnectionCheck.Warning("Guide time zone", problem)
            : ConnectionCheck.Ok("Guide time zone", $"Guide times are read as {Describe(services.EpgTimeZone ?? TimeZoneInfo.Local)}."));
        return checks;
    }

    private static async Task<ConnectionCheck> PortalAsync(PortalitoServices services, CancellationToken cancellationToken)
    {
        const string Name = "Portal";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(PortalTimeout);
        try
        {
            // Any real call signs in first (account login, or device activation) -- the part that fails on a wrong
            // key, wrong credentials, an unreachable host or a certificate problem. filterGenre is small and harmless.
            (await services.Portal.FilterGenreAsync(cancellationToken: timeout.Token).ConfigureAwait(false)).Require();
            return ConnectionCheck.Ok(Name, services.Portal.HasAccount ? "Signed in with the account and read the catalog." : "Activated as a device and read the catalog.");
        }
        catch (PortalException ex)
        {
            return ConnectionCheck.Error(Name, ex.Message + Hint(ex.Message));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ConnectionCheck.Error(Name, $"The portal didn't answer within {PortalTimeout.TotalSeconds:0} seconds.");
        }
    }

    private async Task<ConnectionCheck> ProxyAsync(PortalitoServices services, CancellationToken cancellationToken)
    {
        const string Name = "Proxy URL";
        var baseUrl = services.Signer.BaseUrl;
        const string Fix = " Set 'Proxy base URL' to the address this server listens on, including its base path if it has one -- or leave it empty to use the server's own.";
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(8));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(PingTimeout);
        try
        {
            using var response = await _selfClient.GetAsync(services.Signer.PingUrl(nonce, TimeSpan.FromMinutes(1)), timeout.Token).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.OK && body == nonce)
            {
                return ConnectionCheck.Ok(Name, $"ffmpeg can reach this plugin at {baseUrl}.");
            }

            return ConnectionCheck.Error(Name, response.StatusCode == HttpStatusCode.OK
                ? $"{baseUrl} answered, but not as this server's Portalito plugin (another server on that address?).{Fix}"
                : $"{baseUrl}/Portalito/ping answered {(int)response.StatusCode} {response.ReasonPhrase}.{Fix}");
        }
        catch (HttpRequestException ex)
        {
            return ConnectionCheck.Error(Name, $"{baseUrl} can't be reached ({ex.Message}).{Fix}");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ConnectionCheck.Error(Name, $"{baseUrl} didn't answer within {PingTimeout.TotalSeconds:0} seconds.{Fix}");
        }
    }

    // The two failures with a setting that fixes them; everything else is best read as the portal put it.
    private static string Hint(string message)
    {
        if (message.Contains("SSL", StringComparison.OrdinalIgnoreCase) || message.Contains("certificate", StringComparison.OrdinalIgnoreCase))
        {
            return " The portal's TLS certificate was rejected; if its hosts really do present an invalid certificate, tick 'Skip portal TLS certificate verification'.";
        }

        return message.Contains("aaa100083", StringComparison.Ordinal)
            ? " The account is signed in on another device; sign it out there (the account holds one session at a time)."
            : string.Empty;
    }

    private static string Describe(TimeZoneInfo zone)
    {
        var offset = zone.GetUtcOffset(DateTime.UtcNow);
        var sign = offset < TimeSpan.Zero ? "-" : "+";
        return $"{zone.Id} (UTC{sign}{offset.Duration():hh\\:mm})";
    }
}
