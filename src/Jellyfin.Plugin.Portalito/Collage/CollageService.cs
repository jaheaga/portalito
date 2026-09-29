using System.Security.Cryptography;
using System.Text;

namespace Jellyfin.Plugin.Portalito.Collage;

/// <summary>
/// Renders folder collages into files that folders use directly as their image path. The same label, fit and images
/// always give the same file, so a collage is drawn once; when a folder's titles change its inputs do too, which
/// yields a new file rather than a stale one.
/// <para>
/// A file, not a URL, on purpose (measured 2026-09-24): Jellyfin downloads a remote folder image into the item's own
/// metadata folder, and its built-in folder image provider then treats that copy as one it generated and, on the next
/// refresh, replaces it with a single child's poster. It leaves alone any image outside the item's metadata folder.
/// The directory is under Jellyfin's data path, not its cache path, which its cache cleanup empties after 30 days.
/// </para>
/// </summary>
public sealed class CollageService
{
    /// <summary>One source image that takes longer than this is left out of the collage.</summary>
    internal static readonly TimeSpan ImageTimeout = TimeSpan.FromSeconds(8);

    public CollageService(string directory) => Directory = directory;

    /// <summary>Where the collage files live.</summary>
    public string Directory { get; }

    /// <summary>The file a collage of these inputs is (or will be) rendered to.</summary>
    public string PathFor(string label, CollageFit fit, IReadOnlyList<string> imageUrls)
    {
        var name = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{label}\n{fit}\n{string.Join('\n', imageUrls)}")))[..32].ToLowerInvariant();
        return Path.Combine(Directory, name + ".jpg");
    }

    /// <summary>The collage's file, rendered first if it doesn't exist yet. <paramref name="fetch"/> downloads one source image.</summary>
    public async Task<string> FileAsync(
        string label,
        CollageFit fit,
        IReadOnlyList<string> imageUrls,
        Func<Uri, CancellationToken, Task<HttpResponseMessage>> fetch,
        CancellationToken cancellationToken)
    {
        var path = PathFor(label, fit, imageUrls);
        if (File.Exists(path))
        {
            return path;
        }

        var images = await Task.WhenAll(imageUrls.Select(url => DownloadAsync(url, fetch, cancellationToken))).ConfigureAwait(false);
        var jpeg = CollageRenderer.Render(images.OfType<byte[]>().ToList(), label, fit);

        System.IO.Directory.CreateDirectory(Directory);
        var partial = path + "." + Guid.NewGuid().ToString("N") + ".part";
        try
        {
            await File.WriteAllBytesAsync(partial, jpeg, cancellationToken).ConfigureAwait(false);
            File.Move(partial, path, overwrite: true);
        }
        finally
        {
            File.Delete(partial);
        }

        return path;
    }

    /// <summary>Deletes collage files older than <paramref name="olderThan"/> that no folder uses any more.</summary>
    public int Prune(IReadOnlySet<string> inUse, TimeSpan olderThan)
    {
        if (!System.IO.Directory.Exists(Directory))
        {
            return 0;
        }

        var cutoff = DateTime.UtcNow - olderThan;
        var deleted = 0;
        foreach (var file in System.IO.Directory.EnumerateFiles(Directory))
        {
            if (!inUse.Contains(file) && File.GetLastWriteTimeUtc(file) < cutoff)
            {
                try
                {
                    File.Delete(file);
                    deleted++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Being read right now; it's still unused next week.
                }
            }
        }

        return deleted;
    }

    private static async Task<byte[]?> DownloadAsync(string url, Func<Uri, CancellationToken, Task<HttpResponseMessage>> fetch, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            return null;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ImageTimeout);
        try
        {
            using var response = await fetch(uri, timeout.Token).ConfigureAwait(false);
            return response.IsSuccessStatusCode ? await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false) : null;
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return null;
        }
    }
}
