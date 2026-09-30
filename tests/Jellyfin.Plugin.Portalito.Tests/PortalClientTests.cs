using System.Text.Json.Nodes;
using Jellyfin.Plugin.Portalito.Crypto;
using Jellyfin.Plugin.Portalito.Portal;
using Xunit;

namespace Jellyfin.Plugin.Portalito.Tests;

/// <summary>An in-memory portal: decrypts each request body and answers with a scripted, encrypted envelope.</summary>
internal sealed class FakePortalTransport : IPortalTransport
{
    private readonly PortalCipher _cipher = new(PortalCipherTests.TestKeyHex);
    private readonly object _gate = new();

    public List<FakeRequest> Requests { get; } = new();

    public Func<FakeRequest, string> Handler { get; set; } = _ => Error("nohandler", "no handler");

    public Task<string> PostAsync(string host, string path, string wireBody, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var request = new FakeRequest(host, path, (JsonObject)JsonNode.Parse(_cipher.Decrypt(wireBody))!);
        lock (_gate)
        {
            Requests.Add(request);
        }

        return Task.FromResult(Handler(request));
    }

    public string Ok(JsonObject payload)
        => new JsonObject { ["returnCode"] = "0", ["data"] = _cipher.Encrypt(payload.ToJsonString()) }.ToJsonString();

    public static string Error(string code, string message)
        => new JsonObject { ["returnCode"] = code, ["errorMessage"] = message }.ToJsonString();

    public int Count(string path)
    {
        lock (_gate)
        {
            return Requests.Count(r => r.Path == path);
        }
    }
}

internal sealed record FakeRequest(string Host, string Path, JsonObject Body);

public class PortalClientTests
{
    private static PortalOptions Options(params string[] hosts) => new()
    {
        TripleDesKeyHex = PortalCipherTests.TestKeyHex,
        Hosts = hosts.Length > 0 ? hosts : new[] { "host-a.test", "host-b.test" },
        AppId = "com.example.app",
        ApkVersion = "12345",
        DeviceSn = "TESTSN0001",
        Portal = "portal1",
        ApiBasePath = "/api/core/",
        LoginPasswordSalt = "testsalt",
        LiveColumnCode = "live_all",
        AllChannelsColumnId = 999,
    };

    private static JsonObject Activation(string token, string userId = "u1") => new()
    {
        ["userId"] = userId,
        ["userToken"] = token,
        ["jwtToken"] = "jwt-" + token,
    };

    [Fact]
    public async Task Activate_sends_device_fields_without_base_fields_and_stores_session()
    {
        var transport = new FakePortalTransport();
        transport.Handler = _ => transport.Ok(Activation("tok1"));
        var client = new PortalClient(Options(), transport);

        var response = await client.ActivateAsync();

        Assert.True(response.IsSuccess);
        Assert.Equal(new PortalSession("u1", "tok1", "jwt-tok1"), client.Session);

        var req = Assert.Single(transport.Requests);
        Assert.Equal("v8/active", req.Path);
        Assert.Null(req.Body["userToken"]);
        Assert.Null(req.Body["portalCode"]);
        Assert.Equal("TESTSN0001", req.Body["sn"]!.GetValue<string>());
        Assert.Equal("com.example.app", req.Body["appId"]!.GetValue<string>());
        Assert.Equal("12345", req.Body["apkVersion"]!.GetValue<string>());
        Assert.Equal(36, req.Body["sdkVer"]!.GetValue<int>());
        Assert.Equal(4, req.Body["openNum"]!.GetValue<int>());
        Assert.Equal("default", req.Body["channel"]!.GetValue<string>());
    }

    private static string Md5Hex(string value)
        => Convert.ToHexString(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    [Fact]
    public async Task Provisions_a_fresh_device_when_no_serial_is_configured()
    {
        var transport = new FakePortalTransport();
        transport.Handler = r => r.Path switch
        {
            "v3/snToken" => transport.Ok(new JsonObject { ["snToken"] = "TOK" }),
            "v8/active" => transport.Ok(Activation("tok1")),
            _ => FakePortalTransport.Error("x", r.Path),
        };
        string? persisted = null;
        var client = new PortalClient(Options() with { DeviceSn = string.Empty, SnTokenSalt = "s" }, transport, sn => persisted = sn);

        var response = await client.ActivateAsync();

        var expectedSn = Md5Hex("TOKs"); // MD5(snToken + salt)
        Assert.True(response.IsSuccess);
        Assert.Equal(new PortalSession("u1", "tok1", "jwt-tok1"), client.Session);
        Assert.Equal(expectedSn, persisted);

        var snReq = transport.Requests.Single(r => r.Path == "v3/snToken");
        Assert.Equal(string.Empty, snReq.Body["sn"]!.GetValue<string>()); // stored serial cleared for the handshake
        Assert.NotNull(snReq.Body["androidId"]);                          // a fresh fingerprint was sent
        var activeReq = transport.Requests.Single(r => r.Path == "v8/active");
        Assert.Equal("TOK", activeReq.Body["snToken"]!.GetValue<string>());
        Assert.Equal(expectedSn, activeReq.Body["sn"]!.GetValue<string>());
    }

    [Fact]
    public async Task Provisioning_uses_the_serial_the_portal_returns_when_present()
    {
        var transport = new FakePortalTransport();
        transport.Handler = r => r.Path == "v3/snToken"
            ? transport.Ok(new JsonObject { ["snToken"] = "TOK", ["sn"] = "SERVER_SN" })
            : transport.Ok(Activation("tok1"));
        string? persisted = null;
        var client = new PortalClient(Options() with { DeviceSn = string.Empty, SnTokenSalt = string.Empty }, transport, sn => persisted = sn);

        await client.ActivateAsync();

        Assert.Equal("SERVER_SN", persisted); // no salt needed when the portal hands back the serial
        Assert.Equal("SERVER_SN", transport.Requests.Single(r => r.Path == "v8/active").Body["sn"]!.GetValue<string>());
    }

    [Fact]
    public async Task Re_provisions_once_when_the_configured_device_is_expired()
    {
        var transport = new FakePortalTransport();
        transport.Handler = r =>
        {
            if (r.Path == "v3/snToken")
            {
                return transport.Ok(new JsonObject { ["snToken"] = "TOK", ["sn"] = "NEW_SN" });
            }

            // The fast path (empty snToken + the configured, expired serial) is rejected; the provisioned one works.
            return r.Body["snToken"]!.GetValue<string>().Length == 0
                ? FakePortalTransport.Error("aaa100080", "snToken expired")
                : transport.Ok(Activation("tok1"));
        };
        string? persisted = null;
        var client = new PortalClient(Options() with { DeviceSn = "OLD_SN", SnTokenSalt = "s" }, transport, sn => persisted = sn);

        var response = await client.ActivateAsync();

        Assert.True(response.IsSuccess);
        Assert.Equal("NEW_SN", persisted);
        Assert.Equal(2, transport.Count("v8/active")); // failed fast path + successful provisioned activation
    }

    [Fact]
    public async Task First_call_activates_then_carries_the_session()
    {
        var transport = new FakePortalTransport();
        transport.Handler = r => r.Path == "v8/active"
            ? transport.Ok(Activation("tok1"))
            : transport.Ok(new JsonObject { ["hits"] = 3 });
        var client = new PortalClient(Options(), transport);

        var response = await client.SearchAsync("star", size: 8);

        Assert.Equal(3, response.Require()["hits"]!.GetValue<int>());
        Assert.Equal(new[] { "v8/active", "v3/searchByName" }, transport.Requests.Select(r => r.Path));

        var search = transport.Requests[1].Body;
        Assert.Equal("portal1", search["portalCode"]!.GetValue<string>());
        Assert.Equal("u1", search["userId"]!.GetValue<string>());
        Assert.Equal("tok1", search["userToken"]!.GetValue<string>());
        Assert.Equal("star", search["value"]!.GetValue<string>());
        Assert.Equal(8, search["pageSize"]!.GetValue<int>());
        Assert.Equal("TESTSN0001", search["sn"]!.GetValue<string>());
    }

    [Fact]
    public async Task Device_fields_override_same_named_bean_fields()
    {
        var transport = new FakePortalTransport();
        transport.Handler = r => r.Path == "v8/active"
            ? transport.Ok(Activation("tok1"))
            : transport.Ok(new JsonObject());
        var client = new PortalClient(Options(), transport);

        await client.CallAsync("some/path", new JsonObject { ["sn"] = "spoofed", ["extra"] = "kept" });

        var body = transport.Requests[^1].Body;
        Assert.Equal("TESTSN0001", body["sn"]!.GetValue<string>());
        Assert.Equal("kept", body["extra"]!.GetValue<string>());
    }

    [Fact]
    public async Task Portal_error_code_is_returned_without_failing_over()
    {
        var transport = new FakePortalTransport();
        transport.Handler = r => r.Path == "v8/active"
            ? transport.Ok(Activation("tok1"))
            : FakePortalTransport.Error("bbb200001", "nope");
        var client = new PortalClient(Options(), transport);

        var response = await client.DetailAsync("abc");

        Assert.False(response.IsSuccess);
        Assert.Equal("bbb200001", response.ErrorCode);
        Assert.Equal("nope", response.ErrorMessage);
        Assert.Equal(1, transport.Count("v4/getItemData"));
        Assert.Throws<PortalException>(() => response.Require());
    }

    [Fact]
    public async Task Fails_over_to_the_next_host_and_remembers_the_good_one()
    {
        var transport = new FakePortalTransport();
        transport.Handler = r => r.Host == "host-a.test"
            ? throw new HttpRequestException("connection refused")
            : transport.Ok(Activation("tok1"));
        var client = new PortalClient(Options(), transport);

        Assert.True((await client.ActivateAsync()).IsSuccess);
        Assert.True((await client.ActivateAsync()).IsSuccess);

        Assert.Equal(new[] { "host-a.test", "host-b.test", "host-b.test" }, transport.Requests.Select(r => r.Host));
    }

    [Fact]
    public async Task All_hosts_failing_reports_a_transport_error()
    {
        var transport = new FakePortalTransport();
        transport.Handler = _ => throw new HttpRequestException("down");
        var client = new PortalClient(Options(), transport);

        var response = await client.ActivateAsync();

        Assert.False(response.IsSuccess);
        Assert.Equal("down", response.TransportError);
        Assert.Null(client.Session);
    }

    [Fact]
    public async Task Dead_session_reactivates_once_and_retries_with_the_new_token()
    {
        var transport = new FakePortalTransport();
        var activations = 0;
        transport.Handler = r =>
        {
            if (r.Path == "v8/active")
            {
                return transport.Ok(Activation("tok" + Interlocked.Increment(ref activations)));
            }

            return r.Body["userToken"]!.GetValue<string>() == "tok1"
                ? FakePortalTransport.Error("aaa100028", "not logged in")
                : transport.Ok(new JsonObject { ["fine"] = true });
        };
        var client = new PortalClient(Options(), transport);

        var response = await client.DetailAsync("abc");

        Assert.True(response.IsSuccess);
        Assert.Equal(2, activations);
        Assert.Equal(2, transport.Count("v4/getItemData"));
        Assert.Equal("tok2", client.Session!.UserToken);
    }

    [Theory]
    [InlineData("aaa100027")]
    [InlineData("aaa100083")]
    public async Task Other_session_error_codes_also_trigger_reauth(string code)
    {
        var transport = new FakePortalTransport();
        var activations = 0;
        transport.Handler = r =>
        {
            if (r.Path == "v8/active")
            {
                return transport.Ok(Activation("tok" + Interlocked.Increment(ref activations)));
            }

            return r.Body["userToken"]!.GetValue<string>() == "tok1"
                ? FakePortalTransport.Error(code, "dead")
                : transport.Ok(new JsonObject());
        };
        var client = new PortalClient(Options(), transport);

        Assert.True((await client.DetailAsync("abc")).IsSuccess);
        Assert.Equal(2, activations);
    }

    [Fact]
    public async Task Reauth_happens_only_once_per_call_when_the_new_session_is_also_dead()
    {
        var transport = new FakePortalTransport();
        var activations = 0;
        transport.Handler = r => r.Path == "v8/active"
            ? transport.Ok(Activation("tok" + Interlocked.Increment(ref activations)))
            : FakePortalTransport.Error("aaa100028", "still dead");
        var client = new PortalClient(Options(), transport);

        var response = await client.DetailAsync("abc");

        Assert.Equal("aaa100028", response.ErrorCode);
        Assert.Equal(2, activations);
        Assert.Equal(2, transport.Count("v4/getItemData"));
    }

    // ---- account login (live TV needs an account; anonymous activation only gets catalog + VOD) ----

    private static PortalOptions AccountOptions() => Options() with { AccountEmail = "me@example.test", AccountPassword = "secret" };

    [Fact]
    public async Task Login_sends_the_salted_md5_password_and_stores_the_session()
    {
        var transport = new FakePortalTransport();
        transport.Handler = _ => transport.Ok(Activation("tok1"));
        var client = new PortalClient(AccountOptions(), transport);

        Assert.True((await client.LoginAsync()).IsSuccess);

        Assert.Equal(new PortalSession("u1", "tok1", "jwt-tok1"), client.Session);
        var req = Assert.Single(transport.Requests);
        Assert.Equal("v8/login", req.Path);
        Assert.Equal("me@example.test", req.Body["userName"]!.GetValue<string>());
        // md5("secret" + the configured login salt "testsalt"), computed independently.
        Assert.Equal("49dff77dfdfd423bd412615ce1e82703", req.Body["password"]!.GetValue<string>());
        Assert.Equal("2", req.Body["accountType"]!.GetValue<string>());
        Assert.Equal("1", req.Body["type"]!.GetValue<string>());
        Assert.Null(req.Body["userToken"]);
        Assert.Equal("TESTSN0001", req.Body["sn"]!.GetValue<string>());
    }

    [Fact]
    public async Task With_an_account_the_first_call_logs_in_instead_of_activating()
    {
        var transport = new FakePortalTransport();
        transport.Handler = r => r.Path == "v8/login"
            ? transport.Ok(Activation("tok1"))
            : transport.Ok(new JsonObject());
        var client = new PortalClient(AccountOptions(), transport);

        await client.SearchAsync("star");

        Assert.Equal(new[] { "v8/login", "v3/searchByName" }, transport.Requests.Select(r => r.Path));
        Assert.Equal("tok1", transport.Requests[1].Body["userToken"]!.GetValue<string>());
    }

    [Fact]
    public async Task With_an_account_an_expired_session_logs_in_again()
    {
        var transport = new FakePortalTransport();
        var logins = 0;
        transport.Handler = r =>
        {
            if (r.Path == "v8/login")
            {
                return transport.Ok(Activation("tok" + Interlocked.Increment(ref logins)));
            }

            return r.Body["userToken"]!.GetValue<string>() == "tok1"
                ? FakePortalTransport.Error("aaa100028", "not logged in")
                : transport.Ok(new JsonObject());
        };
        var client = new PortalClient(AccountOptions(), transport);

        Assert.True((await client.DetailAsync("abc")).IsSuccess);
        Assert.Equal(2, logins);
        Assert.Equal(0, transport.Count("v8/active"));
    }

    [Fact]
    public async Task With_an_account_a_session_taken_by_another_device_is_not_fought_over()
    {
        var transport = new FakePortalTransport();
        var logins = 0;
        transport.Handler = r => r.Path == "v8/login"
            ? transport.Ok(Activation("tok" + Interlocked.Increment(ref logins)))
            : FakePortalTransport.Error("aaa100083", "logged in on another device");
        var client = new PortalClient(AccountOptions(), transport);

        var response = await client.DetailAsync("abc");

        // Logging straight back in would sign the other device (the owner's phone) out, and it would do the same.
        Assert.Equal("aaa100083", response.ErrorCode);
        Assert.Equal(1, logins);
        Assert.Equal(1, transport.Count("v4/getItemData"));
    }

    [Fact]
    public async Task A_failed_login_surfaces_the_portals_own_error_instead_of_calling_on_with_no_session()
    {
        var transport = new FakePortalTransport();
        transport.Handler = r => r.Path == "v8/login"
            ? FakePortalTransport.Error("aaa100010", "wrong password")
            : transport.Ok(new JsonObject());
        var client = new PortalClient(AccountOptions(), transport);

        var ex = await Assert.ThrowsAsync<PortalException>(() => client.LiveDataAsync());

        Assert.Contains("v8/login", ex.Message);
        Assert.Contains("aaa100010", ex.Message);
        Assert.Contains("wrong password", ex.Message);
        Assert.Equal(new[] { "v8/login" }, transport.Requests.Select(r => r.Path));
    }

    [Theory]
    [InlineData("me@example.test", "")]
    [InlineData("", "secret")]
    public void An_account_needs_both_email_and_password(string email, string password)
    {
        Assert.Throws<PortalException>(() => new PortalClient(Options() with { AccountEmail = email, AccountPassword = password }, new FakePortalTransport()));
    }

    [Fact]
    public async Task Session_paths_never_trigger_reauth()
    {
        var transport = new FakePortalTransport();
        transport.Handler = _ => FakePortalTransport.Error("aaa100028", "dead");
        var client = new PortalClient(Options(), transport);

        var response = await client.ActivateAsync();

        Assert.Equal("aaa100028", response.ErrorCode);
        Assert.Single(transport.Requests);
    }

    [Fact]
    public async Task Concurrent_dead_session_calls_share_one_reactivation()
    {
        var transport = new FakePortalTransport();
        var activations = 0;
        transport.Handler = r =>
        {
            if (r.Path == "v8/active")
            {
                return transport.Ok(Activation("tok" + Interlocked.Increment(ref activations)));
            }

            return r.Body["userToken"]!.GetValue<string>() == "tok1"
                ? FakePortalTransport.Error("aaa100028", "dead")
                : transport.Ok(new JsonObject());
        };
        var client = new PortalClient(Options(), transport);
        await client.ActivateAsync();

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => client.DetailAsync("id" + i)));

        Assert.All(results, r => Assert.True(r.IsSuccess));
        Assert.Equal(2, activations);
    }

    [Fact]
    public async Task Cancellation_propagates()
    {
        var transport = new FakePortalTransport();
        transport.Handler = _ => transport.Ok(Activation("tok1"));
        var client = new PortalClient(Options(), transport);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ActivateAsync(cts.Token));
    }

    [Fact]
    public async Task Envelope_without_encrypted_data_is_returned_as_is()
    {
        var transport = new FakePortalTransport();
        transport.Handler = r => r.Path == "v8/active"
            ? transport.Ok(Activation("tok1"))
            : new JsonObject { ["returnCode"] = "0", ["plain"] = "yes" }.ToJsonString();
        var client = new PortalClient(Options(), transport);

        var response = await client.CallAsync("x/y");

        Assert.Equal("yes", response.Require()["plain"]!.GetValue<string>());
    }

    [Fact]
    public async Task Episodes_reads_simpleProgramList()
    {
        var transport = new FakePortalTransport();
        transport.Handler = r => r.Path == "v8/active"
            ? transport.Ok(Activation("tok1"))
            : transport.Ok(new JsonObject
            {
                ["assetData"] = new JsonObject
                {
                    ["simpleProgramList"] = new JsonArray(
                        new JsonObject { ["contentId"] = "e1", ["seriesNumber"] = 1 },
                        new JsonObject { ["contentId"] = "e2", ["seriesNumber"] = 2 }),
                },
            });
        var client = new PortalClient(Options(), transport);

        var episodes = await client.EpisodesAsync("series1");

        Assert.Equal(new[] { "e1", "e2" }, episodes.Select(e => e["contentId"]!.GetValue<string>()));
        var req = transport.Requests.Single(r => r.Path == "v4/getItemData");
        Assert.Equal("series1", req.Body["contentId"]!.GetValue<string>());
        Assert.Equal("0", req.Body["type"]!.GetValue<string>());
    }

    [Fact]
    public void Constructor_rejects_incomplete_options()
    {
        var transport = new FakePortalTransport();
        Assert.Throws<PortalException>(() => new PortalClient(Options() with { TripleDesKeyHex = string.Empty }, transport));
        Assert.Throws<PortalException>(() => new PortalClient(Options() with { Hosts = Array.Empty<string>() }, transport));
        Assert.Throws<PortalException>(() => new PortalClient(Options() with { AppId = string.Empty }, transport));
    }

    [Theory]
    [InlineData("not-hex-not-hex-not-hex-not-hex!")]
    [InlineData("0123456789abcdef")]
    [InlineData("0123456789abcdef0123456789abcdef01")]
    public void A_malformed_3des_key_is_a_configuration_error_not_an_argument_exception(string key)
    {
        // PortalException is what every caller treats as "not configured"; ArgumentException escaped them all.
        Assert.Throws<PortalException>(() => new PortalClient(Options() with { TripleDesKeyHex = key }, new FakePortalTransport()));
    }

    [Fact]
    public async Task A_session_whose_ids_arrive_as_numbers_is_stored()
    {
        var transport = new FakePortalTransport();
        transport.Handler = _ => transport.Ok(new JsonObject { ["userId"] = 1234567890123L, ["userToken"] = "tok1", ["jwtToken"] = 5 });
        var client = new PortalClient(Options(), transport);

        await client.ActivateAsync();

        Assert.Equal(new PortalSession("1234567890123", "tok1", "5"), client.Session);
    }

    [Fact]
    public async Task A_numeric_return_code_keeps_all_its_digits()
    {
        var transport = new FakePortalTransport();
        transport.Handler = _ => new JsonObject { ["returnCode"] = 12345678901234567L, ["errorMessage"] = "x" }.ToJsonString();
        var client = new PortalClient(Options(), transport);

        var response = await client.ActivateAsync();

        Assert.Equal("12345678901234567", response.ErrorCode);
    }

    [Fact]
    public void ParseHosts_splits_trims_and_strips_scheme_and_path()
    {
        Assert.Equal(
            new[] { "a.test", "b.test:8443", "c.test" },
            PortalOptions.ParseHosts(" a.test , https://b.test:8443/api ,, c.test/ "));
        Assert.Empty(PortalOptions.ParseHosts(null));
    }
}
