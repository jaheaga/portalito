using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace Jellyfin.Plugin.Portalito.Tests;

/// <summary>
/// Keeps <c>packaging/meta.json</c> (what the Jellyfin catalog reads) in step with the code. A wrong guid makes Jellyfin
/// treat an update as a different plugin; a wrong version or ABI breaks update/compatibility checks.
/// </summary>
public class PackagingMetaTests
{
    private static readonly string RepoRoot = FindRepoRoot();

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
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot, "packaging", "meta.json")));
        return doc.RootElement.Clone();
    }

    // Id/Name/Description are constant expressions of the instance; the constructor only needs Jellyfin's services.
    private static Plugin PluginWithoutHost() => (Plugin)RuntimeHelpers.GetUninitializedObject(typeof(Plugin));

    [Fact]
    public void Guid_matches_the_plugin_id()
        => Assert.Equal(PluginWithoutHost().Id, Guid.Parse(Meta().GetProperty("guid").GetString()!));

    [Fact]
    public void Name_and_description_match_the_plugin()
    {
        var plugin = PluginWithoutHost();

        Assert.Equal(plugin.Name, Meta().GetProperty("name").GetString());
        Assert.Equal(plugin.Description, Meta().GetProperty("description").GetString());
    }

    [Fact]
    public void Version_matches_the_assembly_version()
        => Assert.Equal(typeof(Plugin).Assembly.GetName().Version, Version.Parse(Meta().GetProperty("version").GetString()!));

    [Fact]
    public void Target_abi_is_the_jellyfin_controller_major_minor_the_plugin_builds_against()
    {
        var csproj = File.ReadAllText(Path.Combine(RepoRoot, "src", "Jellyfin.Plugin.Portalito", "Jellyfin.Plugin.Portalito.csproj"));
        var match = Regex.Match(csproj, @"Jellyfin\.Controller""\s+Version=""(\d+)\.(\d+)\.");
        Assert.True(match.Success, "Jellyfin.Controller PackageReference not found");

        var abi = Version.Parse(Meta().GetProperty("targetAbi").GetString()!);

        Assert.Equal(int.Parse(match.Groups[1].Value), abi.Major);
        Assert.Equal(int.Parse(match.Groups[2].Value), abi.Minor);
    }

    [Fact]
    public void Manifest_carries_no_secret_shaped_fields()
    {
        var names = Meta().EnumerateObject().Select(p => p.Name.ToLowerInvariant()).ToArray();

        Assert.DoesNotContain(names, n => n.Contains("key") || n.Contains("token") || n.Contains("secret") || n.Contains("sn"));
    }
}
