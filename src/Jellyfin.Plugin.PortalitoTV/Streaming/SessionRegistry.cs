using System.Collections.Concurrent;

namespace Jellyfin.Plugin.PortalitoTV.Streaming;

/// <summary>A channel stream in progress (one open live.ts).</summary>
public sealed class ChannelSession
{
    /// <summary>Initializes a new instance of the <see cref="ChannelSession"/> class.</summary>
    /// <param name="channel">Channel number.</param>
    public ChannelSession(string channel)
    {
        Channel = channel;
    }

    /// <summary>Gets the session id.</summary>
    public string Id { get; } = Guid.NewGuid().ToString("N");

    /// <summary>Gets the channel number.</summary>
    public string Channel { get; }

    /// <summary>Gets or sets the moment of the grid where the next item.ts must start (null = now).</summary>
    public DateTime? CursorUtc { get; set; }
}

/// <summary>The active sessions, so each item.ts knows where its stream is.</summary>
public sealed class SessionRegistry
{
    private readonly ConcurrentDictionary<string, ChannelSession> _sessions = new();

    /// <summary>Starts a session on a channel.</summary>
    /// <param name="channel">Channel number.</param>
    /// <returns>The new session.</returns>
    public ChannelSession Create(string channel)
    {
        var session = new ChannelSession(channel);
        _sessions[session.Id] = session;
        return session;
    }

    /// <summary>Looks a session up.</summary>
    /// <param name="id">Session id.</param>
    /// <returns>The session, or null if it ended.</returns>
    public ChannelSession? Get(string id) => _sessions.TryGetValue(id, out var s) ? s : null;

    /// <summary>Ends a session.</summary>
    /// <param name="id">Session id.</param>
    public void Remove(string id) => _sessions.TryRemove(id, out _);
}
