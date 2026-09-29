using System.Net.Http.Headers;
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

    public HttpPortalTransport(HttpClient http, PortalOptions options)
    {
        _http = http;
        _appId = options.AppId;
        _basePath = options.ApiBasePath;
        _apkVer = options.ApkVer;
        _spkgVer = options.SpkgVer;
        _userAgent = options.PortalUserAgent;
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

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token)
            .ConfigureAwait(false);
        return await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
    }
}
