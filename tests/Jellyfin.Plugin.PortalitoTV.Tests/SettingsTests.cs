using System.Reflection;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.PortalitoTV.Api;
using Jellyfin.Plugin.PortalitoTV.Configuration;
using Jellyfin.Plugin.PortalitoTV.Scheduling;
using Jellyfin.Plugin.PortalitoTV.Util;
using Xunit;

namespace Jellyfin.Plugin.PortalitoTV.Tests;

public class SettingsTests
{
    private static string Page()
    {
        using var stream = typeof(Plugin).Assembly.GetManifestResourceStream("Jellyfin.Plugin.PortalitoTV.Web.configPage.html")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    // ---------- defaults: the plugin ships no value of any installation ----------

    [Fact]
    public void DefaultsAreNeutral()
    {
        var c = new PluginConfiguration();
        Assert.Equal(string.Empty, c.FavoritesUser); // empty = the first administrator
        Assert.Equal(string.Empty, c.TimeZone); // empty = the server's own
        Assert.Equal(string.Empty, c.TmdbToken);
        Assert.Equal(string.Empty, c.StreamKey);
        Assert.Equal(ServerUrl.Default, c.ServerUrl);
        Assert.Equal("/dev/dri/renderD128", c.VaapiDevice);
        Assert.Empty(c.Channels);
        Assert.Empty(c.Series);
        Assert.Empty(c.Movies);
    }

    // ---------- server address ----------

    [Theory]
    [InlineData("http://127.0.0.1:8096", "http://127.0.0.1:8096")]
    [InlineData("  http://jellyfin:8096/  ", "http://jellyfin:8096")]
    [InlineData("https://tv.example.test", "https://tv.example.test")]
    [InlineData("http://host-a.test:8920/base/", "http://host-a.test:8920/base")]
    public void ServerUrlIsTrimmedAndLosesTheTrailingSlash(string configured, string expected) =>
        Assert.Equal(expected, ServerUrl.Normalize(configured));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("jellyfin:8096")]
    [InlineData("ftp://host-a.test")]
    [InlineData("not a url")]
    [InlineData("/relative/path")]
    public void ServerUrlFallsBackToTheDefaultWhenItIsNotAnHttpAddress(string? configured) =>
        Assert.Equal(ServerUrl.Default, ServerUrl.Normalize(configured));

    // ---------- time zone ----------

    [Fact]
    public void EmptyTimeZoneMeansTheServersOwn()
    {
        Assert.Equal(TimeZoneInfo.Local, PlayoutManager.ResolveTimeZone(null));
        Assert.Equal(TimeZoneInfo.Local, PlayoutManager.ResolveTimeZone(string.Empty));
        Assert.Equal(TimeZoneInfo.Local, PlayoutManager.ResolveTimeZone("   "));
    }

    [Fact]
    public void KnownTimeZoneIsUsedAndAnUnknownOneFallsBackToUtc()
    {
        Assert.Equal(TimeZoneInfo.Utc, PlayoutManager.ResolveTimeZone("UTC"));
        Assert.Equal(TimeZoneInfo.Utc, PlayoutManager.ResolveTimeZone("Not/AZone"));
    }

    // ---------- validation of what the page sends ----------

    [Fact]
    public void ApplyAcceptsTheBlankDefaults()
    {
        var request = new ApplyRequest { TimeZone = string.Empty, FavoritesUser = string.Empty, ServerUrl = string.Empty, VaapiDevice = string.Empty };
        Assert.Null(ConfigApiController.Validate(request));
    }

    [Theory]
    [InlineData("http://127.0.0.1:8096")]
    [InlineData("https://tv.example.test/")]
    public void ApplyAcceptsAnHttpServerUrl(string url) =>
        Assert.Null(ConfigApiController.Validate(new ApplyRequest { ServerUrl = url }));

    [Theory]
    [InlineData("jellyfin:8096")]
    [InlineData("ftp://host-a.test")]
    [InlineData("nonsense")]
    public void ApplyRejectsAServerUrlThatIsNotHttp(string url) =>
        Assert.NotNull(ConfigApiController.Validate(new ApplyRequest { ServerUrl = url }));

    [Fact]
    public void ApplyRejectsAnUnknownTimeZoneAndAcceptsAKnownOne()
    {
        Assert.NotNull(ConfigApiController.Validate(new ApplyRequest { TimeZone = "Not/AZone" }));
        Assert.Null(ConfigApiController.Validate(new ApplyRequest { TimeZone = "UTC" }));
    }

    [Fact]
    public void ApplyRejectsBadChannels()
    {
        ApplyRequest With(params ChannelSettings[] channels) => new() { Channels = channels.ToList() };
        ChannelSettings Ch(string number, string name = "Name") => new() { Number = number, Name = name };

        Assert.NotNull(ConfigApiController.Validate(With(Ch("abc"))));
        Assert.NotNull(ConfigApiController.Validate(With(Ch("1"), Ch("1"))));
        Assert.NotNull(ConfigApiController.Validate(With(Ch("1", string.Empty))));
        Assert.NotNull(ConfigApiController.Validate(With(new ChannelSettings
        {
            Number = "1", Name = "N", Blocks = new() { new BlockSettings { Start = "25:00" } },
        })));
        Assert.NotNull(ConfigApiController.Validate(With(new ChannelSettings
        {
            Number = "1", Name = "N", Blocks = new() { new BlockSettings { Start = "20:00", End = "20:00" } },
        })));
        Assert.NotNull(ConfigApiController.Validate(With(new ChannelSettings
        {
            Number = "1", Name = "N", Blocks = new() { new BlockSettings { Start = "20:00" }, new BlockSettings { Start = "20:00" } },
        })));
        Assert.Null(ConfigApiController.Validate(With(Ch("1"), Ch("2"))));
    }

    // ---------- the page and the API agree ----------

    [Fact]
    public void EveryApplyRequestFieldIsSentByThePage()
    {
        var page = Page();
        foreach (var property in typeof(ApplyRequest).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            Assert.True(Regex.IsMatch(page, @"\b" + property.Name + @"\s*:"), $"the page doesn't send ApplyRequest.{property.Name}");
        }
    }

    [Fact]
    public void EveryElementTheScriptLooksUpExistsInTheMarkup()
    {
        var page = Page();
        var ids = Regex.Matches(page, @"\$\('#(?<id>[A-Za-z0-9_-]+)'\)").Select(m => m.Groups["id"].Value).Distinct().ToList();
        Assert.NotEmpty(ids);
        foreach (var id in ids)
        {
            Assert.True(page.Contains($"id=\"{id}\"", StringComparison.Ordinal), $"the script uses #{id} but the markup has no such id");
        }
    }

    [Fact]
    public void ThePageHasAnInputForEveryEditableSetting()
    {
        var page = Page();
        foreach (var id in new[] { "ptvUser", "ptvTz", "ptvServer", "ptvVaapi", "ptvTmdb" })
        {
            Assert.Contains($"id=\"{id}\"", page);
        }

        // The TMDB token is a secret: its input is a password field.
        Assert.Matches(@"id=""ptvTmdb""\s+type=""password""", page);
    }
}
