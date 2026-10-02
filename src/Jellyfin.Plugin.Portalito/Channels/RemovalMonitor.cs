using Jellyfin.Plugin.Portalito.Catalog;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Portalito.Channels;

/// <summary>
/// Logs every Portalito movie or episode Jellyfin deletes. Jellyfin re-parents a channel title to whichever folder listed
/// it last and deletes it when that folder is re-listed without it, even if other folders still hold it
/// (<c>ChannelManager.GetChannelItemsInternal</c>). It recovers -- the title comes back, with its watch history, the next
/// time any folder lists it -- but meanwhile it's missing from search and Continue Watching. Production showed no such
/// deletions over three days (2026-09-30..10-02), so movies keep a single id; these lines (with a running daily count)
/// are the evidence to revisit that.
/// </summary>
public sealed class RemovalMonitor : IHostedService
{
    private readonly ILibraryManager _library;
    private readonly ILogger<RemovalMonitor> _logger;
    private readonly object _gate = new();
    private DateOnly _day;
    private int _today;

    public RemovalMonitor(ILibraryManager library, ILogger<RemovalMonitor> logger)
    {
        _library = library;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _library.ItemRemoved += OnItemRemoved;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _library.ItemRemoved -= OnItemRemoved;
        return Task.CompletedTask;
    }

    /// <summary>Whether a removed item is a Portalito channel title worth counting.</summary>
    internal static bool IsPortalitoTitle(BaseItem item)
        => item is Movie or Episode
           && item.SourceType == SourceType.Channel
           && VodItemId.TryParse(item.ExternalId, out var id)
           && id.Kind is VodItemKind.Movie or VodItemKind.Episode;

    private void OnItemRemoved(object? sender, ItemChangeEventArgs e)
    {
        if (e.Item is not { } item || !IsPortalitoTitle(item))
        {
            return;
        }

        int count;
        lock (_gate)
        {
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            if (today != _day)
            {
                _day = today;
                _today = 0;
            }

            count = ++_today;
        }

        _logger.LogInformation("Portalito title removed by Jellyfin: {Name} ({Id}); {Count} today", item.Name, item.ExternalId, count);
    }
}
