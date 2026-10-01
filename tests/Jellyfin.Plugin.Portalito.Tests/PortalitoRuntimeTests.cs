using Jellyfin.Plugin.Portalito.Configuration;
using Jellyfin.Plugin.Portalito.Portal;
using Xunit;

namespace Jellyfin.Plugin.Portalito.Tests;

public class PortalitoRuntimeTests
{
    private static PluginConfiguration ValidConfig() => new()
    {
        TripleDesKeyHex = PortalCipherTests.TestKeyHex,
        Hosts = "host-a.test,host-b.test",
        AppId = "com.example.app",
        DeviceSn = "TESTSN0001",
        ProxyBaseUrl = "http://127.0.0.1:8096",
    };

    [Fact]
    public void Unconfigured_plugin_reports_a_portal_exception()
    {
        var runtime = new PortalitoRuntime(() => new PluginConfiguration(), () => { }, TimeProvider.System);

        Assert.Throws<PortalException>(() => runtime.Get());
    }

    [Theory]
    [InlineData("zz")]
    [InlineData("0123456789abcdef")]
    public void A_malformed_3des_key_reports_a_portal_exception(string key)
    {
        // So the channel hides itself (IsEnabledFor catches PortalException) instead of throwing into Jellyfin.
        var config = ValidConfig();
        config.TripleDesKeyHex = key;
        var runtime = new PortalitoRuntime(() => config, () => { }, TimeProvider.System);

        Assert.Throws<PortalException>(() => runtime.Get());
    }

    [Fact]
    public void An_empty_proxy_base_url_uses_the_servers_own_address()
    {
        // The old hardcoded default (127.0.0.1:8096) was wrong for any other port, HTTPS, or a base path like /jellyfin.
        var config = ValidConfig();
        config.ProxyBaseUrl = " ";
        var runtime = new PortalitoRuntime(() => config, () => { }, TimeProvider.System, () => "https://127.0.0.1:8920/jellyfin");

        Assert.Equal("https://127.0.0.1:8920/jellyfin", runtime.Get().Signer.BaseUrl);
    }

    [Fact]
    public void A_configured_proxy_base_url_wins_over_the_servers_own()
    {
        var config = ValidConfig();
        var runtime = new PortalitoRuntime(() => config, () => { }, TimeProvider.System, () => "http://elsewhere.test");

        Assert.Equal("http://127.0.0.1:8096", runtime.Get().Signer.BaseUrl);
    }

    [Theory]
    [InlineData("127.0.0.1:8096")]
    [InlineData("ftp://127.0.0.1")]
    [InlineData("http://")]
    public void A_proxy_base_url_that_isnt_http_is_a_configuration_error(string url)
    {
        // It used to be accepted and only showed up as ffmpeg failing every stream.
        var config = ValidConfig();
        config.ProxyBaseUrl = url;
        var runtime = new PortalitoRuntime(() => config, () => { }, TimeProvider.System);

        Assert.Throws<PortalException>(() => runtime.Get());
    }

    [Fact]
    public void An_empty_proxy_base_url_with_no_known_server_address_is_a_configuration_error()
    {
        var config = ValidConfig();
        config.ProxyBaseUrl = string.Empty;
        var runtime = new PortalitoRuntime(() => config, () => { }, TimeProvider.System);

        Assert.Throws<PortalException>(() => runtime.Get());
    }

    [Fact]
    public void The_guide_time_zone_is_resolved_and_an_unknown_one_is_reported_not_fatal()
    {
        var config = ValidConfig();
        config.EpgTimeZone = "America/Bogota";
        var runtime = new PortalitoRuntime(() => config, () => { }, TimeProvider.System);
        Assert.Equal("America/Bogota", runtime.Get().EpgTimeZone!.Id);

        config.EpgTimeZone = "Nowhere/Special";
        var services = runtime.Get();
        Assert.Same(TimeZoneInfo.Local, services.EpgTimeZone);
        Assert.Contains("Nowhere/Special", services.EpgTimeZoneProblem);
    }

    [Fact]
    public void Rebuilds_reuse_the_same_upstream_http_client()
    {
        var config = ValidConfig();
        var runtime = new PortalitoRuntime(() => config, () => { }, TimeProvider.System);
        static object Upstream(PortalitoServices s)
            => typeof(Proxy.PortalitoProxyService).GetField("_http", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(s.Proxy)!;

        var first = runtime.Get();
        config.Hosts = "host-c.test";
        var rebuilt = runtime.Get();

        // A new client (and connection pool) per config save was never disposed.
        Assert.NotSame(first, rebuilt);
        Assert.Same(Upstream(first), Upstream(rebuilt));
    }

    [Fact]
    public void Services_are_cached_until_the_configuration_changes()
    {
        var config = ValidConfig();
        var runtime = new PortalitoRuntime(() => config, () => { }, TimeProvider.System);

        var first = runtime.Get();
        Assert.Same(first, runtime.Get());

        config.Hosts = "host-c.test";
        var rebuilt = runtime.Get();
        Assert.NotSame(first, rebuilt);
        Assert.Same(rebuilt, runtime.Get());
    }

    [Fact]
    public void Signing_secret_is_generated_once_and_persisted()
    {
        var config = ValidConfig();
        var saves = 0;
        var runtime = new PortalitoRuntime(() => config, () => saves++, TimeProvider.System);

        runtime.Get();
        var secret = config.ProxySigningSecret;
        runtime.Get();

        Assert.Equal(64, secret.Length);
        Assert.Equal(secret, config.ProxySigningSecret);
        Assert.Equal(1, saves);
    }

    [Fact]
    public void An_existing_secret_is_kept_and_signs_proxy_urls()
    {
        var config = ValidConfig();
        config.ProxySigningSecret = "existing-secret";
        var saves = 0;
        var runtime = new PortalitoRuntime(() => config, () => saves++, TimeProvider.System);

        var url = runtime.Get().Signer.LivePlaylistUrl("chan1", TimeSpan.FromHours(1));

        Assert.Equal("existing-secret", config.ProxySigningSecret);
        Assert.Equal(0, saves);
        Assert.StartsWith("http://127.0.0.1:8096/Portalito/live/chan1.m3u8?e=", url);
    }

    [Fact]
    public void Saving_a_tmdb_key_turns_the_fallback_on_without_a_restart()
    {
        // The 2026-09-22 bug: the key missing from the fingerprint meant saving it did nothing until a restart.
        var config = ValidConfig();
        var runtime = new PortalitoRuntime(() => config, () => { }, TimeProvider.System);
        Assert.Null(runtime.Get().Tmdb);

        config.TmdbApiKey = "abc123";
        Assert.NotNull(runtime.Get().Tmdb);

        config.TmdbApiKey = string.Empty;
        Assert.Null(runtime.Get().Tmdb);
    }

    [Fact]
    public void A_fresh_install_with_a_tmdb_key_gets_the_default_destacado_rows_and_clearing_them_turns_them_off()
    {
        var config = ValidConfig();
        var runtime = new PortalitoRuntime(() => config, () => { }, TimeProvider.System);
        Assert.Null(runtime.Get().Discovery); // no TMDB key yet

        config.TmdbApiKey = "abc123";
        Assert.Equal(11, runtime.Get().Discovery!.Rows.Count);

        config.FeaturedRows = string.Empty;
        Assert.Null(runtime.Get().Discovery); // back to the portal's own rows
    }

    [Fact]
    public void A_config_saved_empty_before_the_default_existed_gets_the_default_rows_once()
    {
        // A 0.1.0.4 install saved FeaturedRows = "" (the default then); 0.1.0.5's default never reached it (2026-09-30).
        var config = new PluginConfiguration { FeaturedRows = string.Empty };

        Assert.True(config.ApplyMigrations());
        Assert.Equal(PluginConfiguration.DefaultFeaturedRows, config.FeaturedRows);
        Assert.True(config.FeaturedRowsSeeded);

        // Cleared on purpose afterwards: stays cleared.
        config.FeaturedRows = string.Empty;
        Assert.False(config.ApplyMigrations());
        Assert.Equal(string.Empty, config.FeaturedRows);
    }

    [Fact]
    public void The_migration_keeps_rows_the_owner_already_set()
    {
        var config = new PluginConfiguration { FeaturedRows = "Solo | trending" };

        Assert.True(config.ApplyMigrations()); // only marks it seeded
        Assert.Equal("Solo | trending", config.FeaturedRows);
    }

    [Fact]
    public void The_cache_stamp_changes_with_the_configuration_so_jellyfin_relists()
    {
        // Jellyfin caches a channel folder's listing for 3 hours; the stamp is part of that cache's file name.
        var config = ValidConfig();
        var runtime = new PortalitoRuntime(() => config, () => { }, TimeProvider.System);
        var before = runtime.CacheStamp();

        Assert.Equal(before, runtime.CacheStamp()); // stable while nothing changes
        var original = config.FeaturedRows;
        config.FeaturedRows = "Solo | trending";
        var changed = runtime.CacheStamp();
        Assert.NotEqual(before, changed);

        // Back to the original config: a NEW key, not the old one -- Jellyfin would serve the old cache file's
        // folder contents, which by then are the changed config's items.
        config.FeaturedRows = original;
        Assert.NotEqual(before, runtime.CacheStamp());
        Assert.NotEqual(changed, runtime.CacheStamp());

        // A restart (a new runtime) never reuses the previous process's keys.
        Assert.NotEqual(before, new PortalitoRuntime(() => config, () => { }, TimeProvider.System).CacheStamp());
    }

    [Theory]
    [InlineData("", null)]
    [InlineData("  https://example.test/logo.png  ", "https://example.test/logo.png")]
    [InlineData("http://example.test/a.jpg", "http://example.test/a.jpg")]
    [InlineData("ftp://example.test/a.png", null)]
    [InlineData("not a url", null)]
    public void Only_an_http_channel_image_url_overrides_the_built_in_logo(string configured, string? expected)
    {
        var config = ValidConfig();
        config.ChannelImageUrl = configured;

        Assert.Equal(expected, new PortalitoRuntime(() => config, () => { }, TimeProvider.System).Get().ChannelImageUrl);
    }

    [Fact]
    public void Becoming_invalid_after_being_valid_throws_instead_of_serving_stale_services()
    {
        var config = ValidConfig();
        var runtime = new PortalitoRuntime(() => config, () => { }, TimeProvider.System);
        runtime.Get();

        config.TripleDesKeyHex = string.Empty;

        Assert.Throws<PortalException>(() => runtime.Get());
    }

    [Fact]
    public void Options_come_from_the_configuration_without_leaking_defaults_over_it()
    {
        var config = ValidConfig();
        config.AppId = " com.example.app ";
        config.ApkVersion = "777";

        var options = PortalOptions.FromConfiguration(config);

        Assert.Equal("com.example.app", options.AppId);
        Assert.Equal("777", options.ApkVersion);
        Assert.Equal(new[] { "host-a.test", "host-b.test" }, options.Hosts);
        Assert.Equal("TESTSN0001", options.DeviceSn);
    }
}
