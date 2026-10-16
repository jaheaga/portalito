using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PortalitoTV.Streaming;

/// <summary>Runs ffmpeg and copies its output to a stream (the HTTP response).</summary>
public static class FfmpegRunner
{
    /// <summary>Runs ffmpeg and streams what it writes to stdout.</summary>
    /// <param name="ffmpegPath">Path to ffmpeg.</param>
    /// <param name="args">Its arguments.</param>
    /// <param name="output">Where its output goes.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="label">What is being run, for the log.</param>
    /// <param name="ct">Cancellation (the client closed the connection).</param>
    /// <returns>The exit code and the bytes written.</returns>
    public static async Task<(int ExitCode, long Bytes)> RunToStreamAsync(
        string ffmpegPath, IReadOnlyList<string> args, Stream output, ILogger logger, string label, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(ffmpegPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var proc = new Process { StartInfo = psi };
        proc.Start();

        // stderr: only the last lines, without the input URL (it is signed).
        var errTail = new Queue<string>();
        var errTask = Task.Run(async () =>
        {
            string? line;
            while ((line = await proc.StandardError.ReadLineAsync().ConfigureAwait(false)) is not null)
            {
                if (line.Contains("POC", StringComparison.Ordinal) || line.Contains("RPS", StringComparison.Ordinal))
                {
                    continue; // HEVC decoder noise when entering mid-GOP
                }

                lock (errTail)
                {
                    errTail.Enqueue(Redact(line));
                    if (errTail.Count > 8)
                    {
                        errTail.Dequeue();
                    }
                }
            }
        });

        long bytes = 0;
        var buffer = new byte[64 * 1024];
        try
        {
            var src = proc.StandardOutput.BaseStream;
            int n;
            while ((n = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                await output.FlushAsync(ct).ConfigureAwait(false);
                bytes += n;
            }

            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is OperationCanceledException or IOException)
        {
            // The client closed the connection: ffmpeg is killed below.
        }
        finally
        {
            if (!proc.HasExited)
            {
                try
                {
                    proc.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // It exited on its own between the check and the kill.
                }

                await proc.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }

            await errTask.ConfigureAwait(false);
        }

        var exit = proc.ExitCode;
        if (exit != 0 && !ct.IsCancellationRequested)
        {
            string tail;
            lock (errTail)
            {
                tail = string.Join(" | ", errTail);
            }

            logger.LogWarning("{Label}: ffmpeg exited with {Exit} after {Bytes} bytes: {Tail}", label, exit, bytes, tail);
        }

        return (exit, bytes);
    }

    /// <summary>Removes URLs (they may carry a signature) from a log line.</summary>
    /// <param name="line">The line.</param>
    /// <returns>The line with every URL replaced by a placeholder.</returns>
    public static string Redact(string line) =>
        System.Text.RegularExpressions.Regex.Replace(line, @"https?://\S+", "<url>");
}
