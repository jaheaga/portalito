using System.Collections.Concurrent;

namespace Jellyfin.Plugin.Portalito.Catalog;

/// <summary>
/// Shares one in-flight fetch per key among concurrent callers. A cold channel root plans every catalog's collages at
/// once, and without this each concurrent caller repeated the same portal calls (parent id, filter vocabulary) -- the
/// burst the portal answered with incomplete listings (2026-09-21) -- all while Jellyfin holds its single channel lock.
/// </summary>
public sealed class SingleFlight
{
    private readonly ConcurrentDictionary<string, Lazy<Task<object>>> _flights = new(StringComparer.Ordinal);

    /// <summary>
    /// Runs <paramref name="fetch"/> for <paramref name="key"/> unless it's already running, in which case its result is
    /// shared. The fetch itself isn't cancelled by any one caller (others may still want it); a caller that gives up
    /// stops waiting.
    /// </summary>
    public async Task<T> RunAsync<T>(string key, Func<Task<T>> fetch, CancellationToken cancellationToken)
        where T : notnull
    {
        var flight = _flights.GetOrAdd(key, k => new Lazy<Task<object>>(async () =>
        {
            try
            {
                return await fetch().ConfigureAwait(false);
            }
            finally
            {
                _flights.TryRemove(k, out var done);
            }
        }));
        return (T)await flight.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
    }
}
