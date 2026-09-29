namespace Jellyfin.Plugin.Portalito.Portal;

public class PortalException : Exception
{
    public PortalException(string message)
        : base(message)
    {
    }

    public PortalException(string message, Exception inner)
        : base(message, inner)
    {
    }
}
