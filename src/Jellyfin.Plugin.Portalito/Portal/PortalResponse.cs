using System.Text.Json.Nodes;

namespace Jellyfin.Plugin.Portalito.Portal;

/// <summary>Outcome of one portal call: decrypted payload, a portal error code, or a transport failure.</summary>
public sealed record PortalResponse(
    string Path,
    JsonObject? Data,
    string? ErrorCode = null,
    string? ErrorMessage = null,
    string? TransportError = null)
{
    public bool IsSuccess => Data is not null && ErrorCode is null && TransportError is null;

    /// <summary>Returns the payload or throws <see cref="PortalException"/> describing the failure.</summary>
    public JsonObject Require()
    {
        if (IsSuccess)
        {
            return Data!;
        }

        var detail = ErrorCode is not null
            ? $"portal error {ErrorCode}: {ErrorMessage}"
            : $"transport failure: {TransportError}";
        throw new PortalException($"{Path} failed ({detail})");
    }
}
