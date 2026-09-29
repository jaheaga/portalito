using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.Portalito.Crypto;
using Jellyfin.Plugin.Portalito.Proxy;

namespace Jellyfin.Plugin.Portalito.Portal;

/// <summary>
/// The portal client (port of the relevant parts of reference/the reference client): encrypted request bodies, host
/// failover, anonymous device activation or account login, and one automatic re-auth when the portal declares the
/// session dead.
/// </summary>
public sealed partial class PortalClient : IStreamResolver
{
    // aaa100027 / aaa100028: session dead. aaa100083: account logged in elsewhere -- for the device tier,
    // re-activating is harmless and is what unsticks it (with a real account it is not retried, see ReauthAsync).
    private static readonly string[] SessionErrorCodes = { "aaa100027", "aaa100028", "aaa100083" };

    // Paths that establish or close a session: re-authing on these makes no sense.
    private static readonly string[] NoReauthPaths = { "v8/active", "v8/login", "v5/loginOut", "v3/snToken" };

    private readonly PortalOptions _options;
    private readonly IPortalTransport _transport;
    private readonly PortalCipher _cipher;
    private readonly JsonObject _device;
    private readonly SemaphoreSlim _authLock = new(1, 1);
    private volatile PortalSession? _session;
    private volatile string? _preferredHost;

    public PortalClient(PortalOptions options, IPortalTransport transport)
    {
        options.Validate();
        _options = options;
        _transport = transport;
        _cipher = new PortalCipher(options.TripleDesKeyHex);
        _device = BuildDeviceFields(options);
    }

    /// <summary>Gets a value indicating whether this client logs in with an account (live TV) rather than as an anonymous device.</summary>
    public bool HasAccount => _options.HasAccount;

    /// <summary>Gets the current session, or <c>null</c> before the first activation.</summary>
    public PortalSession? Session => _session;

    // ---- transport ----

    /// <summary>Calls a portal endpoint, activating first if there is no session and re-authing once on a dead session.</summary>
    public async Task<PortalResponse> CallAsync(
        string path,
        JsonObject? bean = null,
        bool baseFields = true,
        CancellationToken cancellationToken = default)
    {
        var managesSession = NoReauthPaths.Any(p => path.StartsWith(p, StringComparison.Ordinal));

        if (!managesSession && _session is null)
        {
            await EnsureSessionAsync(cancellationToken).ConfigureAwait(false);
        }

        var sessionUsed = _session;
        var response = await CallOnceAsync(path, bean, baseFields, sessionUsed, cancellationToken).ConfigureAwait(false);

        if (!managesSession
            && response.ErrorCode is not null
            && Array.IndexOf(SessionErrorCodes, response.ErrorCode) >= 0
            && await ReauthAsync(sessionUsed, response.ErrorCode, cancellationToken).ConfigureAwait(false))
        {
            response = await CallOnceAsync(path, bean, baseFields, _session, cancellationToken).ConfigureAwait(false);
        }

        return response;
    }

    private async Task<PortalResponse> CallOnceAsync(
        string path,
        JsonObject? bean,
        bool baseFields,
        PortalSession? session,
        CancellationToken cancellationToken)
    {
        var body = new JsonObject();
        if (baseFields)
        {
            body["portalCode"] = _options.Portal;
            body["userId"] = session?.UserId ?? string.Empty;
            body["userToken"] = session?.UserToken ?? string.Empty;
        }

        if (bean is not null)
        {
            foreach (var (key, value) in bean)
            {
                body[key] = value?.DeepClone();
            }
        }

        // Device fields go last so they win over same-named bean fields, as in the reference.
        foreach (var (key, value) in _device)
        {
            body[key] = value?.DeepClone();
        }

        var wire = _cipher.Encrypt(body.ToJsonString());

        string? lastError = null;
        foreach (var host in HostOrder())
        {
            try
            {
                var text = await _transport.PostAsync(host, path, wire, cancellationToken).ConfigureAwait(false);
                var response = ParseResponse(path, text);
                _preferredHost = host;
                return response;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex.Message;
            }
        }

        return new PortalResponse(path, null, TransportError: lastError ?? "no portal hosts");
    }

    private PortalResponse ParseResponse(string path, string text)
    {
        if (JsonNode.Parse(text) is not JsonObject envelope)
        {
            throw new PortalException("portal response is not a JSON object");
        }

        var returnCode = ReadScalar(envelope["returnCode"]);
        if (returnCode is not null && returnCode != "0")
        {
            return new PortalResponse(path, null, returnCode, ReadScalar(envelope["errorMessage"]));
        }

        if (envelope["data"] is JsonValue value
            && value.TryGetValue<string>(out var blob)
            && blob.Length > 0)
        {
            if (JsonNode.Parse(_cipher.Decrypt(blob)) is not JsonObject payload)
            {
                throw new PortalException("decrypted portal payload is not a JSON object");
            }

            return new PortalResponse(path, payload);
        }

        return new PortalResponse(path, envelope);
    }

    private static string? ReadScalar(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue<string>(out var s))
        {
            return s;
        }

        // The number's own JSON text: going through double lost digits of long ids and printed large ones as "1E+16".
        return value.GetValueKind() == JsonValueKind.Number ? value.ToJsonString() : null;
    }

    private IEnumerable<string> HostOrder()
    {
        var preferred = _preferredHost;
        if (preferred is not null)
        {
            yield return preferred;
        }

        foreach (var host in _options.Hosts)
        {
            if (host != preferred)
            {
                yield return host;
            }
        }
    }

    private static JsonObject BuildDeviceFields(PortalOptions o) => new()
    {
        ["loginType"] = "2",
        ["appLanguage"] = "en",
        ["apkVersion"] = o.ApkVersion,
        ["sysVersion"] = o.SpkgVer,
        ["appId"] = o.AppId,
        ["hardwareInfo"] = "ranchu",
        ["model"] = "sdk_gphone64_arm64",
        ["product"] = "sdk_gphone64_arm64",
        ["cpu"] = "arm64-v8a",
        ["B29"] = string.Empty,
        ["reserve1"] = o.DeviceReserve1,
        ["deviceToken"] = o.DeviceToken,
        ["sn"] = o.DeviceSn,
        ["drmId"] = o.DeviceDrmId,
        ["sdkVer"] = 36,
    };

    // ---- session ----

    private async Task EnsureSessionAsync(CancellationToken cancellationToken)
    {
        await _authLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_session is null)
            {
                // Without this, a failed login/activation went unnoticed and the next call went out with an empty
                // userToken, which the portal answers with a generic "请求参数异常" that hides the real cause.
                var auth = await AuthenticateCoreAsync(cancellationToken).ConfigureAwait(false);
                if (_session is null)
                {
                    var detail = auth.ErrorCode is not null
                        ? $"portal error {auth.ErrorCode}: {auth.ErrorMessage}"
                        : auth.TransportError ?? "no userToken in the response";
                    throw new PortalException($"{auth.Path} failed ({detail})");
                }
            }
        }
        finally
        {
            _authLock.Release();
        }
    }

    // Returns true when a usable session exists to retry with. Several concurrent requests can hit a dead
    // session together; only the first re-authenticates, the rest see the already-refreshed session.
    private async Task<bool> ReauthAsync(PortalSession? stale, string errorCode, CancellationToken cancellationToken)
    {
        // aaa100083 with a real account means someone else (the owner's phone, say) signed in with it since.
        // Logging straight back in would sign THEM out, and they'd do the same -- a ping-pong the reference
        // clients (the reference client's session layer) deliberately refuse. Fail instead.
        if (_options.HasAccount && errorCode == "aaa100083")
        {
            return false;
        }

        await _authLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!ReferenceEquals(_session, stale))
            {
                return true;
            }

            return (await AuthenticateCoreAsync(cancellationToken).ConfigureAwait(false)).IsSuccess;
        }
        finally
        {
            _authLock.Release();
        }
    }

    /// <summary>Free-tier device activation: mints a fresh userToken from the device SN.</summary>
    public async Task<PortalResponse> ActivateAsync(CancellationToken cancellationToken = default)
    {
        await _authLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ActivateCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _authLock.Release();
        }
    }

    private async Task<PortalResponse> ActivateCoreAsync(CancellationToken cancellationToken)
    {
        var bean = new JsonObject
        {
            ["snToken"] = string.Empty,
            ["authVersion"] = string.Empty,
            ["authCode"] = string.Empty,
            ["preCode"] = string.Empty,
            ["macAddr"] = "02:00:00:00:00:00",
            ["reserve1"] = _options.DeviceReserve1,
            ["openNum"] = 4,
            ["channel"] = "default",
            ["matadata"] = string.Empty,
            ["signdata"] = string.Empty,
        };

        var response = await CallOnceAsync("v8/active", bean, false, null, cancellationToken).ConfigureAwait(false);
        StoreSession(response);
        return response;
    }

    /// <summary>Account login: mints a userToken for the configured account (needed for live TV).</summary>
    public async Task<PortalResponse> LoginAsync(CancellationToken cancellationToken = default)
    {
        await _authLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await LoginCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _authLock.Release();
        }
    }

    private Task<PortalResponse> AuthenticateCoreAsync(CancellationToken cancellationToken)
        => _options.HasAccount ? LoginCoreAsync(cancellationToken) : ActivateCoreAsync(cancellationToken);

    // Bean from the reference client's PortalitoSession.loginWithoutLock, itself from the decompiled app. Logging in does not
    // activate the device, so it doesn't use up an activation of the configured SN.
    private async Task<PortalResponse> LoginCoreAsync(CancellationToken cancellationToken)
    {
        var bean = new JsonObject
        {
            ["accountType"] = "2",
            ["userName"] = _options.AccountEmail,
            ["password"] = PasswordHash(_options.AccountPassword),
            ["type"] = "1",
            ["macAddr"] = "02:00:00:00:00:00",
            ["areaCode"] = string.Empty,
            ["verificationCode"] = string.Empty,
            ["verificationToken"] = string.Empty,
            ["matadata"] = string.Empty,
            ["signdata"] = string.Empty,
            ["channel"] = "default",
        };

        var response = await CallOnceAsync("v8/login", bean, false, null, cancellationToken).ConfigureAwait(false);
        StoreSession(response);
        return response;
    }

    /// <summary>
    /// The password as the portal expects it: lowercase hex MD5 of the password plus the configured login salt. Some
    /// portals reject correct credentials without their fixed salt; it is supplied through config.
    /// </summary>
    internal string PasswordHash(string password)
        => Convert.ToHexString(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(password + _options.LoginPasswordSalt))).ToLowerInvariant();

    private void StoreSession(PortalResponse response)
    {
        // ReadScalar, not GetValue<string>(): the portal sends ids as strings or numbers, and GetValue<string>() on a
        // number throws InvalidOperationException, which escaped every caller's PortalException handling.
        var userToken = ReadScalar(response.Data?["userToken"]);
        if (response.IsSuccess && !string.IsNullOrEmpty(userToken))
        {
            _session = new PortalSession(
                ReadScalar(response.Data!["userId"]) ?? string.Empty,
                userToken,
                ReadScalar(response.Data["jwtToken"]) ?? string.Empty);
        }
    }

    // ---- catalog / browse ----

    public Task<PortalResponse> ConfigAsync(string group = "ApkConfig", string module = "Config", CancellationToken cancellationToken = default)
        => CallAsync("config/get", new JsonObject { ["groupName"] = group, ["moduleName"] = module }, cancellationToken: cancellationToken);

    public Task<PortalResponse> NextColumnsAsync(string columnCode, int page = 1, int size = 50, string version = "", CancellationToken cancellationToken = default)
        => CallAsync(
            "getNextColumns",
            new JsonObject { ["columnCode"] = columnCode, ["pageNum"] = page, ["pageSize"] = size, ["version"] = version },
            cancellationToken: cancellationToken);

    public Task<PortalResponse> ColumnContentsAsync(long columnId, int page = 1, int size = 30, string special = "", int numDisplay = 0, string isAv1 = "", CancellationToken cancellationToken = default)
        => CallAsync(
            "v3/getColumnContents",
            new JsonObject
            {
                ["columnId"] = columnId,
                ["pageNum"] = page,
                ["pageSize"] = size,
                ["specialFlag"] = special,
                ["numDisplay"] = numDisplay,
                ["isAv1"] = isAv1,
            },
            cancellationToken: cancellationToken);

    public Task<PortalResponse> SearchAsync(string value, string type = "0", string columnId = "", int page = 1, int size = 20, string filter = "", CancellationToken cancellationToken = default)
        => CallAsync(
            "v3/searchByName",
            new JsonObject
            {
                ["value"] = value,
                ["type"] = type,
                ["columnId"] = columnId,
                ["filter"] = filter,
                ["pageNum"] = page,
                ["pageSize"] = size,
            },
            cancellationToken: cancellationToken);

    /// <summary>
    /// The filter vocabulary for <see cref="FilterByContentAsync"/>: <c>tags</c> (24 genres, English names),
    /// <c>year</c> (this year down to 1990) and <c>audio</c>/<c>originalCountry</c> lists. Measured live 2026-09-23.
    /// </summary>
    public Task<PortalResponse> FilterGenreAsync(string language = "es", CancellationToken cancellationToken = default)
        => CallAsync("v3/filterGenre", new JsonObject { ["language"] = language }, cancellationToken: cancellationToken);

    /// <summary>
    /// The full catalog behind one root's shelves, paginated -- unlike the shelves themselves (10-item carousels),
    /// this genuinely pages: measured live 2026-09-23, columnId 76177 (movies' parent) returned 30,839 total,
    /// newest first, 200 honoured in one call. <paramref name="columnId"/> is NOT a shelf's own columnId; it is the
    /// shelf's <c>parentId</c> (see <see cref="RecommendColumnContentsAsync"/>). <paramref name="tags"/>/<paramref
    /// name="year"/> come from <see cref="FilterGenreAsync"/>'s vocabulary; empty means unfiltered.
    /// </summary>
    public Task<PortalResponse> FilterByContentAsync(long columnId, string tags = "", string year = "", int page = 1, int size = 30, CancellationToken cancellationToken = default)
        => CallAsync(
            "v3/filterByContent",
            new JsonObject
            {
                ["columnId"] = columnId,
                ["tags"] = tags,
                ["year"] = year,
                ["language"] = string.Empty,
                ["audio"] = string.Empty,
                ["originalCountry"] = string.Empty,
                ["pageNum"] = page,
                ["pageSize"] = size,
            },
            cancellationToken: cancellationToken);

    /// <summary>
    /// A shelf's own metadata, including <c>parentId</c> -- the id <see cref="FilterByContentAsync"/> actually
    /// wants (measured live 2026-09-23: a shelf's own <c>columnId</c>/<c>code</c> are carousel ids, not the
    /// paginated catalog's).
    /// </summary>
    public Task<PortalResponse> RecommendColumnContentsAsync(long columnId, int page = 1, int size = 1, CancellationToken cancellationToken = default)
        => CallAsync(
            "getRecommendColumnContents",
            new JsonObject { ["columnId"] = columnId, ["pageNum"] = page, ["pageSize"] = size },
            cancellationToken: cancellationToken);

    public Task<PortalResponse> DetailAsync(string contentId, string type = "1", string sortType = "0", string lang = "en", CancellationToken cancellationToken = default)
        => CallAsync(
            "v4/getItemData",
            new JsonObject
            {
                ["contentId"] = contentId,
                ["type"] = type,
                ["sortType"] = sortType,
                ["language"] = lang,
                ["macAddr"] = "02:00:00:00:00:00",
            },
            cancellationToken: cancellationToken);

    /// <summary>Episodes of a series (<c>assetData.simpleProgramList</c> of the type-0 detail).</summary>
    public async Task<IReadOnlyList<JsonObject>> EpisodesAsync(string seriesId, CancellationToken cancellationToken = default)
    {
        var detail = (await DetailAsync(seriesId, type: "0", cancellationToken: cancellationToken).ConfigureAwait(false)).Require();
        return detail["assetData"]?["simpleProgramList"] is JsonArray list
            ? list.OfType<JsonObject>().ToArray()
            : Array.Empty<JsonObject>();
    }

    // ---- live / EPG ----

    public Task<PortalResponse> LiveCategoriesAsync(CancellationToken cancellationToken = default)
        => NextColumnsAsync(_options.LiveColumnCode, size: 30, cancellationToken: cancellationToken);

    /// <summary>The configured live column holding every channel; 0 if the portal has no such column.</summary>
    public long AllChannelsColumnId => _options.AllChannelsColumnId;

    /// <summary>A live column's channels; <c>columnId</c> 0 uses <see cref="AllChannelsColumnId"/> (every channel).</summary>
    public Task<PortalResponse> LiveDataAsync(long columnId = 0, int page = 1, int size = 1000, string dataVersion = "", string expire = "", CancellationToken cancellationToken = default)
        => CallAsync(
            "v6/getLiveData",
            new JsonObject
            {
                ["columnId"] = columnId == 0 ? _options.AllChannelsColumnId : columnId,
                ["pageNum"] = page,
                ["pageSize"] = size,
                ["dataVersion"] = dataVersion,
                ["expireTimeStr"] = expire,
            },
            cancellationToken: cancellationToken);

    public Task<PortalResponse> EpgAsync(string channelCode, long columnId = 0, string type = "2", CancellationToken cancellationToken = default)
        => CallAsync(
            "v3/getProgram",
            new JsonObject { ["channelCode"] = channelCode, ["columnId"] = columnId, ["type"] = type },
            cancellationToken: cancellationToken);

    // ---- playback ----

    public Task<PortalResponse> PlayLiveAsync(string channelCode, long columnId = 0, string type = "1", CancellationToken cancellationToken = default)
        => CallAsync(
            "v4/startPlayLive",
            new JsonObject { ["channelCode"] = channelCode, ["columnId"] = columnId, ["type"] = type },
            cancellationToken: cancellationToken);

    public Task<PortalResponse> PlayVodAsync(string contentId, string seriesContentId = "", int startTime = 0, string type = "1", long columnId = 0, string authType = "", CancellationToken cancellationToken = default)
        => CallAsync(
            "v10/startPlayVOD",
            new JsonObject
            {
                ["contentId"] = contentId,
                ["seriesContentId"] = seriesContentId,
                ["startTime"] = startTime,
                ["type"] = type,
                ["columnId"] = columnId,
                ["authType"] = authType,
            },
            cancellationToken: cancellationToken);

    /// <summary>SLB / load balancing: <c>cdn_list</c> with the real CDN hosts per tag (vod / live / record).</summary>
    public Task<PortalResponse> GetSlbAsync(string type = "merge", IEnumerable<string>? liveCodes = null, string hasPay = "0", CancellationToken cancellationToken = default)
        => CallAsync(
            "v14/getSlbInfo",
            new JsonObject
            {
                ["hasPay"] = hasPay,
                ["userIdentity"] = "1",
                ["type"] = type,
                ["appVer"] = _options.ApkVersion,
                ["lang"] = "es",
                ["encMediaSupported"] = 1,
                ["liveCodeList"] = new JsonArray((liveCodes ?? new[] { _options.LiveColumnCode }).Select(c => (JsonNode?)JsonValue.Create(c)).ToArray()),
                ["appParams"] = string.Empty,
                ["reserve1"] = "02:00:00:00:00:00",
                ["pipFlag"] = "0",
            },
            cancellationToken: cancellationToken);

    // ---- stream resolution ----

    /// <summary>play_live + get_slb → the cfl CDN entry, license and Content-Auth token for a live channel.</summary>
    public async Task<LiveStream> ResolveLiveAsync(string channelCode, CancellationToken cancellationToken = default)
    {
        var play = (await PlayLiveAsync(channelCode, cancellationToken: cancellationToken).ConfigureAwait(false)).Require();
        var (playCode, license) = LiveSignal(play);
        if (string.IsNullOrEmpty(license))
        {
            throw new PortalException($"play_live returned no liveAddressList license for {channelCode}");
        }

        var slb = (await GetSlbAsync("merge", new[] { channelCode }, cancellationToken: cancellationToken).ConfigureAwait(false)).Require();

        // Every cfl CDN the portal returned, each with its own 32-hex Content-Auth token; entries with no token are unusable
        // and dropped. The proxy fails over through them, so the primary is just the first (see LiveStream.Cdns).
        var cdns = FindCflEntries(slb, "live")
            .Select(e => (Entry: e, Token: TokenRegex().Match(e.Url) is { Success: true } m ? m.Groups[1].Value : string.Empty))
            .Where(x => x.Token.Length > 0)
            .Select(x => new LiveCdn(x.Entry.Host, x.Entry.Url, x.Token))
            .ToList();
        if (cdns.Count == 0)
        {
            throw new PortalException("get_slb has no cfl CDN entry for live with a 32-hex token");
        }

        var primary = cdns[0];
        return new LiveStream(channelCode, primary.Host, primary.AuthUrl, license, primary.Token, ParseExpiry(primary.AuthUrl), playCode, cdns.Skip(1).ToList());
    }

    /// <summary>
    /// The playCode and license from THE SAME <c>liveAddressList</c> entry, never crossed: the license authorizes one
    /// signal, and pairing it with another entry's playCode is exactly what the CDN rejects. Port of the reference client's
    /// <c>PortalitoLive.signalFrom</c> (itself from the decompiled app): the first entry with both wins; failing that, the
    /// first license with no playCode, since some channels get no playCode and work with the channel code.
    /// </summary>
    private static (string? PlayCode, string? License) LiveSignal(JsonObject play)
    {
        string? firstLicense = null;
        foreach (var address in (play["liveAddressList"] as JsonArray)?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
        {
            var license = ReadScalar(address["license"]);
            var playCode = ReadScalar(address["playCode"]);
            firstLicense ??= license;
            if (!string.IsNullOrEmpty(playCode) && !string.IsNullOrEmpty(license))
            {
                return (playCode, license);
            }
        }

        return (null, firstLicense);
    }

    /// <summary>play_vod + get_slb → the cfl CDN entry, license and media code for a VOD item.</summary>
    public async Task<VodStream> ResolveVodAsync(string contentId, string seriesContentId = "", CancellationToken cancellationToken = default)
    {
        var play = (await PlayVodAsync(contentId, seriesContentId, cancellationToken: cancellationToken).ConfigureAwait(false)).Require();
        var movie = (((play["episodeList"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault()?["totalMovieList"] as JsonArray)
                ?.OfType<JsonObject>().FirstOrDefault()?["movieList"] as JsonArray)
            ?.OfType<JsonObject>().FirstOrDefault();
        var mediaCode = ReadScalar(movie?["contentId"]);
        var license = ReadScalar((movie?["licenseList"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault()?["license"]);
        if (string.IsNullOrEmpty(mediaCode) || string.IsNullOrEmpty(license))
        {
            throw new PortalException($"play_vod returned no media code/license for {contentId}");
        }

        var videoFormat = ReadScalar(movie?["videoFormat"]);
        var subtitles = SubtitleFiles((play["episodeList"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault());

        var slb = (await GetSlbAsync(cancellationToken: cancellationToken).ConfigureAwait(false)).Require();
        var entry = FindCflEntries(slb, "vod").FirstOrDefault()
            ?? throw new PortalException("get_slb has no cfl CDN entry for vod");

        return new VodStream(mediaCode, entry.Host, entry.Url, license, ParseExpiry(entry.Url), videoFormat, subtitles);
    }

    /// <summary>
    /// <c>subtitleList[]</c> = <c>{language, file:[{url, fileType}]}</c> (the reference client's stream resolver,
    /// PortalitoResolve.readSubtitles). Entries with no url are dropped: the portal sometimes lists a language that never
    /// got uploaded. The format comes from the URL's extension first (the reference client measured <c>.srt</c> files parsing as
    /// SubRip), then <c>fileType</c>, then srt.
    /// </summary>
    internal static IReadOnlyList<SubtitleFile> SubtitleFiles(JsonObject? episode)
    {
        var files = new List<SubtitleFile>();
        foreach (var sub in (episode?["subtitleList"] as JsonArray)?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
        {
            var file = (sub["file"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault();
            var url = ReadScalar(file?["url"]);
            if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            {
                continue;
            }

            var extension = System.IO.Path.GetExtension(uri.AbsolutePath).TrimStart('.').ToLowerInvariant();
            var fileType = ReadScalar(file?["fileType"])?.ToLowerInvariant();
            var format = extension is "srt" or "vtt" ? extension : fileType is "srt" or "vtt" ? fileType : "srt";
            files.Add(new SubtitleFile(ReadScalar(sub["language"]) ?? string.Empty, url, format));
        }

        return files;
    }

    private sealed record CflEntry(string Host, string Url);

    /// <summary>
    /// Every cfl CDN url the portal lists for <paramref name="tag"/> (live / vod), in the portal's order, deduped by
    /// (host, url). The reference clients keep only the first; keeping all of them lets the proxy fail over when one
    /// CDN is down or fronted by a stale Cloudflare negative cache.
    /// </summary>
    private static IReadOnlyList<CflEntry> FindCflEntries(JsonObject slb, string tag)
    {
        var entries = new List<CflEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (slb["cdn_list"] is not JsonArray cdnList)
        {
            return entries;
        }

        foreach (var cdn in cdnList.OfType<JsonObject>())
        {
            if (ReadScalar(cdn["tag"]) != tag || cdn["url_list"] is not JsonArray urls)
            {
                continue;
            }

            var host = HostOf(ReadScalar(cdn["main_addr"]) ?? string.Empty);
            foreach (var u in urls.OfType<JsonObject>())
            {
                var url = ReadScalar(u["url"]) ?? string.Empty;

                // the reference client's PortalitoLive.liveCdns accepts either marker: in the URL, or as the entry's own field.
                if ((url.Contains("sign_type=cfl", StringComparison.Ordinal) || ReadScalar(u["sign_type"]) == "cfl")
                    && seen.Add($"{host}\n{url}"))
                {
                    entries.Add(new CflEntry(host, url));
                }
            }
        }

        return entries;
    }

    private static string HostOf(string mainAddr)
    {
        var h = mainAddr.Replace("http://", string.Empty, StringComparison.Ordinal)
            .Replace("https://", string.Empty, StringComparison.Ordinal);
        var slash = h.IndexOf('/');
        return slash >= 0 ? h[..slash] : h;
    }

    /// <summary>Reads <c>expired=&lt;epoch&gt;</c> from an auth URL (seconds; milliseconds are tolerated).</summary>
    internal static DateTimeOffset? ParseExpiry(string authUrl)
    {
        var m = ExpiredRegex().Match(authUrl);
        if (!m.Success || !long.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var epoch))
        {
            return null;
        }

        return epoch > 100_000_000_000L
            ? DateTimeOffset.FromUnixTimeMilliseconds(epoch)
            : DateTimeOffset.FromUnixTimeSeconds(epoch);
    }

    [GeneratedRegex("token=([0-9A-Fa-f]{32})")]
    private static partial Regex TokenRegex();

    [GeneratedRegex(@"(?:^|[?&])expired=(\d+)")]
    private static partial Regex ExpiredRegex();
}

/// <summary>A minted portal session.</summary>
public sealed record PortalSession(string UserId, string UserToken, string JwtToken);
