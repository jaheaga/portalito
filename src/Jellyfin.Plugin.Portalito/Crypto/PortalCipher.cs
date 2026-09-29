using System.Security.Cryptography;
using System.Text;

namespace Jellyfin.Plugin.Portalito.Crypto;

/// <summary>
/// The portal body cipher: <c>hex(base64(3DES-ECB-PKCS7(json)))</c>. Responses' <c>data</c> field uses the same scheme.
/// The key comes from plugin configuration and is never embedded in the assembly.
/// </summary>
public sealed class PortalCipher
{
    private readonly byte[] _key;

    public PortalCipher(string keyHex)
    {
        if (string.IsNullOrWhiteSpace(keyHex))
        {
            throw new ArgumentException("The 3DES key is not configured.", nameof(keyHex));
        }

        try
        {
            _key = Convert.FromHexString(keyHex.Trim());
        }
        catch (FormatException ex)
        {
            throw new ArgumentException("The 3DES key must be hex.", nameof(keyHex), ex);
        }

        if (_key.Length is not (16 or 24))
        {
            throw new ArgumentException("The 3DES key must be 16 or 24 bytes (32 or 48 hex chars).", nameof(keyHex));
        }
    }

    public string Encrypt(string plain)
    {
        using var tdes = CreateCipher();
        var plainBytes = Encoding.UTF8.GetBytes(plain);
        var cipherBytes = tdes.EncryptEcb(plainBytes, PaddingMode.PKCS7);
        var b64 = Convert.ToBase64String(cipherBytes);
        return Convert.ToHexString(Encoding.ASCII.GetBytes(b64)).ToLowerInvariant();
    }

    public string Decrypt(string wire)
    {
        var b64 = Encoding.ASCII.GetString(Convert.FromHexString(wire.Trim()));
        var cipherBytes = Convert.FromBase64String(b64);
        using var tdes = CreateCipher();
        var plainBytes = tdes.DecryptEcb(cipherBytes, PaddingMode.PKCS7);
        return Encoding.UTF8.GetString(plainBytes);
    }

    private TripleDES CreateCipher()
    {
        var tdes = TripleDES.Create();
        tdes.Key = _key;
        return tdes;
    }
}
