using System.Globalization;
using System.Text;
using Jellyfin.Plugin.Portalito.Crypto;

namespace Jellyfin.Plugin.Portalito.Proxy;

/// <summary>
/// The generic <see cref="IContentAuthSigner"/>: the per-request signature is
/// <c>md5( "token={token}&amp;sign2_method={method}&amp;instance=0&amp;start_moment={ms}" + salt )</c>, and the
/// header is that prefix plus <c>&amp;sign2={signature}</c> appended to the base auth. The signing method name, the
/// salt bytes, and any non-standard MD5 deviation all come from config — nothing portal-specific is compiled in. With
/// an empty method and salt and standard MD5 (the default), it produces an inert signature that no real CDN accepts;
/// an operator supplies their portal's values to make live playback work.
/// </summary>
public sealed class ConfigurableContentAuthSigner : IContentAuthSigner
{
    private readonly string _method;
    private readonly byte[] _salt;
    private readonly ConfigurableMd5 _md5;

    public ConfigurableContentAuthSigner(string method, byte[] salt, int[]? md5Round1Schedule, IReadOnlyDictionary<int, uint>? md5KOverrides)
    {
        _method = method;
        _salt = salt;
        _md5 = new ConfigurableMd5(md5Round1Schedule, md5KOverrides);
    }

    public string BuildContentAuth(string baseAuth, string token, long startMomentMs)
    {
        var prefix = string.Create(
            CultureInfo.InvariantCulture,
            $"token={token}&sign2_method={_method}&instance=0&start_moment={startMomentMs}");
        var signature = _md5.ComputeHashHex(Concat(Encoding.ASCII.GetBytes(prefix), _salt));
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{baseAuth}&sign2_method={_method}&instance=0&start_moment={startMomentMs}&sign2={signature}");
    }

    private static byte[] Concat(byte[] first, byte[] second)
    {
        var result = new byte[first.Length + second.Length];
        first.CopyTo(result, 0);
        second.CopyTo(result, first.Length);
        return result;
    }
}
