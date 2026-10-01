namespace Jellyfin.Plugin.Portalito.Following;

/// <summary>
/// Writes a <see cref="MirrorShow"/> into the "Siguiendo" folder and removes what no longer belongs there. Only touches
/// files whose content changed, so an unchanged show costs no library scan; reports whether anything changed.
/// </summary>
public sealed class FollowWriter
{
    private static readonly string[] OwnedExtensions = { ".strm", ".nfo" };

    private readonly string _root;
    private readonly Func<string, CancellationToken, Task<byte[]?>> _download;

    /// <param name="root">The library folder.</param>
    /// <param name="download">Fetches an image URL; null (or an exception) means it's skipped this time.</param>
    public FollowWriter(string root, Func<string, CancellationToken, Task<byte[]?>> download)
    {
        _root = root;
        _download = download;
    }

    /// <summary>Writes the show into <see cref="FollowFiles.ShowFolder"/>, deleting <paramref name="previousFolder"/> if the show moved.</summary>
    public async Task<bool> WriteAsync(MirrorShow show, Func<string, string, string> playUrl, string? previousFolder, CancellationToken cancellationToken)
    {
        var folder = FollowFiles.ShowFolder(show.Name, show.ShowId);
        var changed = previousFolder is not null && previousFolder != folder && Delete(previousFolder);
        var showDir = Path.Combine(_root, folder);
        Directory.CreateDirectory(showDir);

        var files = FollowFiles.TextFiles(show, playUrl);
        foreach (var (relative, content) in files)
        {
            changed |= WriteIfChanged(Path.Combine(showDir, relative), content);
        }

        // Images are fetched once: an existing poster/thumb is kept, a failed fetch is retried on the next refresh.
        var images = new HashSet<string>(StringComparer.Ordinal);
        if (show.PosterUrl is { } poster)
        {
            images.Add("poster");
            changed |= await ImageAsync(Path.Combine(showDir, "poster"), poster, cancellationToken).ConfigureAwait(false);
        }

        foreach (var episode in show.Episodes.Where(e => e.ImageUrl is not null && e.ImageUrl != show.PosterUrl))
        {
            var thumb = FollowFiles.EpisodePath(episode) + "-thumb";
            images.Add(thumb);
            changed |= await ImageAsync(Path.Combine(showDir, thumb), episode.ImageUrl!, cancellationToken).ConfigureAwait(false);
        }

        return RemoveStale(showDir, files.Keys.ToHashSet(StringComparer.Ordinal), images) | changed;
    }

    /// <summary>Deletes a show's folder; returns whether there was one.</summary>
    public bool Delete(string folder)
    {
        var dir = Path.Combine(_root, folder);
        if (!Directory.Exists(dir))
        {
            return false;
        }

        Directory.Delete(dir, recursive: true);
        return true;
    }

    private static bool WriteIfChanged(string path, string content)
    {
        if (File.Exists(path) && File.ReadAllText(path) == content)
        {
            return false;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return true;
    }

    /// <summary>Saves an image at <paramref name="pathWithoutExtension"/> plus the extension its bytes call for, unless one is there.</summary>
    private async Task<bool> ImageAsync(string pathWithoutExtension, string url, CancellationToken cancellationToken)
    {
        var dir = Path.GetDirectoryName(pathWithoutExtension)!;
        var name = Path.GetFileName(pathWithoutExtension);
        if (Directory.Exists(dir) && Directory.EnumerateFiles(dir, name + ".*").Any())
        {
            return false;
        }

        byte[]? bytes;
        try
        {
            bytes = await _download(url, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or ArgumentException or UriFormatException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return false;
        }

        if (bytes is not { Length: > 0 })
        {
            return false;
        }

        var extension = Proxy.ImageTypes.Sniff(bytes) switch
        {
            "image/png" => ".png",
            "image/webp" => ".webp",
            "image/gif" => ".gif",
            "image/bmp" => ".bmp",
            _ => ".jpg",
        };
        Directory.CreateDirectory(dir);
        await File.WriteAllBytesAsync(pathWithoutExtension + extension, bytes, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Removes episodes the portal no longer lists: our .strm/.nfo files that aren't expected, thumbs of episodes that
    /// are gone, and season folders left empty. Anything else in the folder is left alone.
    /// </summary>
    private static bool RemoveStale(string showDir, HashSet<string> textFiles, HashSet<string> images)
    {
        var changed = false;
        foreach (var file in Directory.EnumerateFiles(showDir, "*", SearchOption.AllDirectories).ToList())
        {
            var relative = Path.GetRelativePath(showDir, file).Replace('\\', '/');
            var extension = Path.GetExtension(file);
            var stem = relative[..^extension.Length];
            var stale = OwnedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase)
                ? !textFiles.Contains(relative)
                : stem.EndsWith("-thumb", StringComparison.Ordinal) && !images.Contains(stem);
            if (stale)
            {
                File.Delete(file);
                changed = true;
            }
        }

        foreach (var dir in Directory.EnumerateDirectories(showDir).Where(d => !Directory.EnumerateFileSystemEntries(d).Any()).ToList())
        {
            Directory.Delete(dir);
            changed = true;
        }

        return changed;
    }
}
