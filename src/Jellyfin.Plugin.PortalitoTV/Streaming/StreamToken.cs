using System.Security.Cryptography;
using System.Text;

namespace Jellyfin.Plugin.PortalitoTV.Streaming;

/// <summary>
/// The key that protects the stream and guide endpoints: Jellyfin's M3U tuner calls them without a user session, so they
/// can't ask for a login. Persistent (plugin configuration) because it is stored in the tuner's URL.
/// </summary>
public sealed class StreamToken
{
    /// <summary>Initializes a new instance of the <see cref="StreamToken"/> class, generating the key on first use.</summary>
    public StreamToken()
    {
        var plugin = Plugin.Instance!;
        if (string.IsNullOrEmpty(plugin.Configuration.StreamKey))
        {
            plugin.Configuration.StreamKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            plugin.SaveConfiguration();
        }

        Value = plugin.Configuration.StreamKey;
    }

    /// <summary>Gets the key.</summary>
    public string Value { get; }

    /// <summary>Whether a candidate key is the right one (constant-time comparison).</summary>
    /// <param name="candidate">The key from the request.</param>
    /// <returns>True if it matches.</returns>
    public bool IsValid(string? candidate) =>
        candidate is not null &&
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(candidate), Encoding.ASCII.GetBytes(Value));
}
