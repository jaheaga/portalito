using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace Jellyfin.Plugin.PortalitoTV.Tests;

/// <summary>
/// Keeps <c>packaging/meta-tv.json</c> (what the Jellyfin catalog will read) in step with the code. A wrong guid makes Jellyfin
/// treat an update as a different plugin; a wrong version or ABI breaks update/compatibility checks.
/// </summary>
public class PackagingMetaTests
{
    private static readonly string RepoRoot = FindRepoRoot();

    [Fact]
    public void Guid_matches_the_plugin_id() =>
        Assert.Equal(PluginWithoutHost().Id, Guid.Parse(Meta().GetProperty("guid").GetString()!));

    [Fact]
    public void Guid_differs_from_the_portalito_plugin()
    {
        using var other = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot, "packaging", "meta.json")));
        Assert.NotEqual(other.RootElement.GetProperty("guid").GetString(), Meta().GetProperty("guid").GetString());
    }

    [Fact]
    public void Name_and_description_match_the_plugin()
    {
        var plugin = PluginWithoutHost();

        Assert.Equal(plugin.Name, Meta().GetProperty("name").GetString());
        Assert.Equal(plugin.Description, Meta().GetProperty("description").GetString());
    }

    [Fact]
    public void Version_matches_the_assembly_version() =>
        Assert.Equal(typeof(Plugin).Assembly.GetName().Version, Version.Parse(Meta().GetProperty("version").GetString()!));

    [Fact]
    public void Target_abi_is_the_jellyfin_controller_major_minor_the_plugin_builds_against()
    {
        var csproj = File.ReadAllText(Path.Combine(RepoRoot, "src", "Jellyfin.Plugin.PortalitoTV", "Jellyfin.Plugin.PortalitoTV.csproj"));
        var match = Regex.Match(csproj, @"Jellyfin\.Controller""\s+Version=""(\d+)\.(\d+)\.");
        Assert.True(match.Success, "Jellyfin.Controller PackageReference not found");

        var abi = Version.Parse(Meta().GetProperty("targetAbi").GetString()!);
        Assert.Equal(int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture), abi.Major);
        Assert.Equal(int.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture), abi.Minor);
    }

    [Fact]
    public void Controller_is_pinned_to_the_abi_floor()
    {
        // Built against a higher patch, the plugin would demand shared libraries an older 10.11.x server lacks (NotSupported).
        var csproj = File.ReadAllText(Path.Combine(RepoRoot, "src", "Jellyfin.Plugin.PortalitoTV", "Jellyfin.Plugin.PortalitoTV.csproj"));
        Assert.Contains("Jellyfin.Controller\" Version=\"10.11.0\"", csproj);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Jellyfin.Plugin.Portalito.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repo root (Jellyfin.Plugin.Portalito.sln) not found.");
    }

    private static JsonElement Meta()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot, "packaging", "meta-tv.json")));
        return doc.RootElement.Clone();
    }

    // Id/Name/Description are constant expressions of the instance; the constructor only needs Jellyfin services.
    private static Plugin PluginWithoutHost() => (Plugin)RuntimeHelpers.GetUninitializedObject(typeof(Plugin));
}
