using Jellyfin.Plugin.Portalito.Crypto;
using Xunit;

namespace Jellyfin.Plugin.Portalito.Tests;

public class PortalCipherTests
{
    // Synthetic test key -- NOT the portal key.
    internal const string TestKeyHex = "0123456789abcdeffedcba98765432100011223344556677";

    [Theory]
    [InlineData("{\"portalCode\":\"portal1\",\"userId\":\"\",\"userToken\":\"\"}")]
    [InlineData("exactly8")]
    [InlineData("")]
    [InlineData("áé中文")]
    public void Round_trips(string plain)
    {
        var cipher = new PortalCipher(TestKeyHex);
        Assert.Equal(plain, cipher.Decrypt(cipher.Encrypt(plain)));
    }

    // Wire vectors generated with pycryptodome DES3/ECB/PKCS7 using the scheme in reference/the reference client
    // (hex(base64(ciphertext))), proving the .NET path is byte-compatible with the working client.
    [Theory]
    [InlineData("exactly8", "414c77726643474156517854696f6238722f756161673d3d")]
    [InlineData("áé中文", "3652303439443355383979496d422b5454397a386d673d3d")]
    public void Encrypt_matches_python_reference(string plain, string expectedWire)
    {
        var cipher = new PortalCipher(TestKeyHex);
        Assert.Equal(expectedWire, cipher.Encrypt(plain));
        Assert.Equal(plain, cipher.Decrypt(expectedWire));
    }

    [Fact]
    public void Wire_format_is_lowercase_hex_of_base64()
    {
        var wire = new PortalCipher(TestKeyHex).Encrypt("hello");
        Assert.Matches("^[0-9a-f]+$", wire);
        var b64 = System.Text.Encoding.ASCII.GetString(Convert.FromHexString(wire));
        Assert.Equal(12, b64.Length); // 8 bytes of ciphertext -> 12 base64 chars
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("nothex")]
    [InlineData("0123456789abcdef")] // 8 bytes: single DES, not 3DES
    public void Rejects_bad_keys(string key)
    {
        Assert.Throws<ArgumentException>(() => new PortalCipher(key));
    }

    [Fact]
    public void Decrypt_with_wrong_key_does_not_return_the_plaintext()
    {
        var wire = new PortalCipher(TestKeyHex).Encrypt("secret body");
        var other = new PortalCipher("ffeeddccbbaa99887766554433221100aabbccddeeff0011");

        string? recovered = null;
        try
        {
            recovered = other.Decrypt(wire);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            // Bad padding is the expected outcome for most wrong keys.
        }

        Assert.NotEqual("secret body", recovered);
    }
}
