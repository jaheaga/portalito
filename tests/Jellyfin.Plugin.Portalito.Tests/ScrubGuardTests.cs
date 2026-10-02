using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace Jellyfin.Plugin.Portalito.Tests;

/// <summary>
/// Build-time twin of <c>scripts/scrub-check.sh</c>: the public tree must never carry a real portal's name, hosts,
/// keys, signing constants or captured values. A hand-sync that reintroduces any of them fails the build here, not
/// only in the CI shell step. Both read <c>scripts/scrub-denylist.sha256</c>: each token's length and the SHA-256 of
/// its lowercase form, never the token itself (until 2026-10-02 both listed the literals, publishing the very values
/// they were meant to keep out).
/// </summary>
public class ScrubGuardTests
{
    private static readonly string[] SkipDirs = { ".git", "bin", "obj", "artifacts", "private" };

    [Fact]
    public void The_denylist_holds_only_hashes()
    {
        var lines = DenylistLines(RepoRoot());

        Assert.NotEmpty(lines);
        Assert.All(lines, l => Assert.Matches("^[0-9]{1,3} [0-9a-f]{64}$", l));
    }

    [Fact]
    public void No_source_file_carries_a_forbidden_token()
    {
        var root = RepoRoot();
        var wanted = DenylistLines(root)
            .Select(l => l.Split(' '))
            .GroupBy(p => int.Parse(p[0], System.Globalization.CultureInfo.InvariantCulture))
            .ToDictionary(g => g.Key, g => g.Select(p => p[1]).ToHashSet(StringComparer.Ordinal));

        var offenders = new List<string>();
        foreach (var file in EnumerateFiles(root))
        {
            var data = File.ReadAllBytes(file);
            if (Array.IndexOf(data, (byte)0, 0, Math.Min(data.Length, 8000)) >= 0)
            {
                continue; // binary
            }

            var lines = Encoding.UTF8.GetString(data).ToLowerInvariant().Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                var bytes = Encoding.UTF8.GetBytes(lines[i]);
                foreach (var (length, digests) in wanted)
                {
                    if (Enumerable.Range(0, Math.Max(0, bytes.Length - length + 1))
                        .Any(at => digests.Contains(Convert.ToHexString(SHA256.HashData(bytes.AsSpan(at, length))).ToLowerInvariant())))
                    {
                        offenders.Add($"{Path.GetRelativePath(root, file)}:{i + 1}: a forbidden {length}-byte token");
                        break;
                    }
                }
            }
        }

        Assert.True(offenders.Count == 0, "Forbidden tokens found:\n" + string.Join("\n", offenders));
    }

    private static string[] DenylistLines(string root)
        => File.ReadAllLines(Path.Combine(root, "scripts", "scrub-denylist.sha256"))
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .ToArray();

    private static IEnumerable<string> EnumerateFiles(string root)
    {
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(root, file);
            if (rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(seg => SkipDirs.Contains(seg)))
            {
                continue;
            }

            // The operator's real .env is git-ignored and never published, so it is not scanned; the blank
            // .env.example template still is.
            var name = Path.GetFileName(file);
            if ((name == ".env" || name.StartsWith(".env.", StringComparison.Ordinal)) && name != ".env.example")
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
