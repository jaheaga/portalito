using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace Jellyfin.Plugin.Portalito.Portal;

/// <summary>HttpClient-backed transport carrying the app-identity headers the portal requires (all from config).</summary>
public sealed class HttpPortalTransport : IPortalTransport
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    private readonly HttpClient _http;
    private readonly string _appId;
    private readonly string _basePath;
    private readonly string _apkVer;
    private readonly string _spkgVer;
    private readonly string _userAgent;
    private readonly string _signSecret;

    public HttpPortalTransport(HttpClient http, PortalOptions options)
    {
        _http = http;
        _appId = options.AppId;
        _basePath = options.ApiBasePath;
        _apkVer = options.ApkVer;
        _spkgVer = options.SpkgVer;
        _userAgent = options.PortalUserAgent;
        _signSecret = options.RequestSignSecret;
    }

    /// <summary>
    /// The per-request signature the portal's middleware checks: a random nonce plus its MD5 with the shared secret.
    /// Returns (<c>null</c>, <c>null</c>) when no secret is configured, so nothing is sent.
    /// </summary>
    internal static (string? Trace, string? Meta) SignatureHeaders(string secret, string nonce)
    {
        if (string.IsNullOrEmpty(secret))
        {
            return (null, null);
        }

        var meta = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(nonce + secret))).ToLowerInvariant();
        return (nonce, meta);
    }

    /// <summary>Creates the HttpClient the portal transport should use.</summary>
    public static HttpClient CreateHttpClient(bool skipTlsVerification)
    {
        var handler = new SocketsHttpHandler();
        if (skipTlsVerification)
        {
            handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        }

        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task<string> PostAsync(string host, string path, string wireBody, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"https://{host}{_basePath}{path}");
        request.Content = new ByteArrayContent(Encoding.ASCII.GetBytes(wireBody));
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        request.Headers.TryAddWithoutValidation("apk", _appId);
        request.Headers.TryAddWithoutValidation("apkVer", _apkVer);
        request.Headers.TryAddWithoutValidation("spkgVer", _spkgVer);
        request.Headers.UserAgent.Clear();
        request.Headers.TryAddWithoutValidation("User-Agent", _userAgent);

        var (trace, meta) = SignatureHeaders(_signSecret, Guid.NewGuid().ToString());
        if (trace is not null)
        {
            request.Headers.TryAddWithoutValidation("X-Request-Trace", trace);
            request.Headers.TryAddWithoutValidation("X-Client-Meta", meta);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token)
            .ConfigureAwait(false);
        return await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
    }
}
