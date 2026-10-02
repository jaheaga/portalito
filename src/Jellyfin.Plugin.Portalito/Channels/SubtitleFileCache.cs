using System.Security.Cryptography;
using System.Text;
using Jellyfin.Plugin.Portalito.Portal;

namespace Jellyfin.Plugin.Portalito.Channels;

/// <summary>
/// Keeps local copies of the portal's subtitle files. Jellyfin 10.11.11 cannot read an external subtitle whose Path
/// is an http URL: <c>SubtitleEncoder.GetStream</c> returns the response stream from inside a <c>using var response</c>,
/// so the stream is already closed when it is read ("Cannot access a closed Stream", measured live 2026-09-23). Local
/// files go through its <c>AsyncFile.OpenRead</c> branch and work, so each file is downloaded once and served from disk.
/// </summary>
public sealed class SubtitleFileCache
{
    /// <summary>A subtitle download that takes longer than this is skipped; playback doesn't wait on it.</summary>
    internal static readonly TimeSpan DownloadTimeout = TimeSpan.FromSeconds(8);

    private readonly string _directory;
    private readonly HttpClient _http;

    public SubtitleFileCache(string directory, HttpClient http)
    {
        _directory = directory;
        _http = http;
    }

    /// <summary>
    /// The files that could be stored locally, each with <see cref="SubtitleFile.Url"/> replaced by its local path.
    /// One that fails to download is left out -- a missing subtitle must not stop playback.
    /// </summary>
    public async Task<IReadOnlyList<SubtitleFile>> LocalCopiesAsync(IReadOnlyList<SubtitleFile> files, CancellationToken cancellationToken)
    {
        var copies = await Task.WhenAll(files.Select(f => LocalCopyAsync(f, cancellationToken))).ConfigureAwait(false);
        return copies.OfType<SubtitleFile>().ToList();
    }

    private async Task<SubtitleFile?> LocalCopyAsync(SubtitleFile file, CancellationToken cancellationToken)
    {
        // Named by the URL's hash: the same portal file is only ever downloaded once.
        var name = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(file.Url)))[..32].ToLowerInvariant();
        var path = Path.Combine(_directory, $"{name}.{file.Format}");
        if (File.Exists(path))
        {
            return file with { Url = path };
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(DownloadTimeout);
        try
        {
            using var response = await _http.GetAsync(new Uri(file.Url), timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);
            if (!LooksLikeSubtitles(bytes, file.Format))
            {
                // An error page served as 200 would otherwise be kept as this title's subtitles for good.
                return null;
            }

            Directory.CreateDirectory(_directory);

            // Written aside then moved, so a concurrent reader never sees a half-written file.
            var partial = path + "." + Guid.NewGuid().ToString("N") + ".part";
            try
            {
                await File.WriteAllBytesAsync(partial, bytes, timeout.Token).ConfigureAwait(false);
                File.Move(partial, path, overwrite: true);
            }
            finally
            {
                File.Delete(partial);
            }

            return file with { Url = path };
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException
            || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return null;
        }
    }

    /// <summary>
    /// Whether a download is plausibly a subtitle file of <paramref name="format"/>: a VTT starts with "WEBVTT", an SRT
    /// has a "-->" timing line near the top; neither starts like HTML. Checked on the first 4 KB.
    /// </summary>
    internal static bool LooksLikeSubtitles(byte[] bytes, string format)
    {
        if (bytes.Length == 0)
        {
            return false;
        }

        var head = Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, 4096)).TrimStart('\uFEFF', ' ', '\r', '\n', '\t');
        if (head.StartsWith('<'))
        {
            return false;
        }

        return format == "vtt" ? head.StartsWith("WEBVTT", StringComparison.Ordinal) || head.Contains("-->", StringComparison.Ordinal)
            : head.Contains("-->", StringComparison.Ordinal);
    }
}
