using System.Text.RegularExpressions;
using Xunit;

namespace Jellyfin.Plugin.Portalito.Tests;

/// <summary>
/// Build-time twin of <c>scripts/scrub-check.sh</c>: the public tree must never carry a real portal's name, hosts,
/// keys, signing constants or captured values. A hand-sync that reintroduces any of them fails the build here, not
/// only in the CI shell step. Keep this list and the script's <c>deny</c> array identical.
/// </summary>
public class ScrubGuardTests
{
    // Case-insensitive regexes, mirroring scripts/scrub-check.sh.
    private static readonly string[] Deny =
    {
        "***REMOVED***", "***REMOVED***", "***REMOVED***", "***REMOVED***", "***REMOVED***", "kino-?light", "***REMOVED***", "***REMOVED***", "***REMOVED***",
        "***REMOVED***", "***REMOVED***", "***REMOVED***", "***REMOVED***", @"okhttp/3\.12\.12", @"com\.android\.msandroid",
        "***REMOVED***", "***REMOVED***", "***REMOVED***", "***REMOVED***",
        "***REMOVED***", "***REMOVED***",
        "***REMOVED***",
        @"100\.64\.0\.5", "***REMOVED***", "***REMOVED***", "***REMOVED***",
        "***REMOVED***", "***REMOVED***",
    };

    private static readonly string[] SkipDirs = { "bin", "obj", ".git", "artifacts", "node_modules" };
    private static readonly string[] SkipFiles = { "scrub-check.sh", "ScrubGuardTests.cs" };

    [Fact]
    public void No_source_file_carries_a_forbidden_token()
    {
        var root = RepoRoot();
        var pattern = new Regex(string.Join("|", Deny), RegexOptions.IgnoreCase | RegexOptions.Compiled);

        var offenders = new List<string>();
        foreach (var file in EnumerateFiles(root))
        {
            string text;
            try
            {
                text = File.ReadAllText(file);
            }
            catch (IOException)
            {
                continue; // binary/locked — the shell scan is the backstop for those
            }

            var lines = text.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                if (pattern.IsMatch(lines[i]))
                {
                    offenders.Add($"{Path.GetRelativePath(root, file)}:{i + 1}: {lines[i].Trim()}");
                }
            }
        }

        Assert.True(offenders.Count == 0, "Forbidden tokens found:\n" + string.Join("\n", offenders));
    }

    private static IEnumerable<string> EnumerateFiles(string root)
    {
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(root, file);
            if (rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(seg => SkipDirs.Contains(seg)))
            {
                continue;
            }

            if (SkipFiles.Contains(Path.GetFileName(file)))
            {
                continue;
            }

            yield return file;
        }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Jellyfin.Plugin.Portalito.sln")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
