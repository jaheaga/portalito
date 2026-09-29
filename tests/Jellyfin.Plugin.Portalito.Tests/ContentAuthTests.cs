using System.Security.Cryptography;
using System.Text;
using Jellyfin.Plugin.Portalito.Crypto;
using Jellyfin.Plugin.Portalito.Proxy;
using Xunit;

namespace Jellyfin.Plugin.Portalito.Tests;

/// <summary>A fixed, synthetic Content-Auth signer for the proxy tests — no real portal parameters.</summary>
internal static class TestContentAuth
{
    public const string Method = "sig1";
    public static readonly byte[] Salt = Encoding.ASCII.GetBytes("portalito-test-salt");

    public static IContentAuthSigner Signer() => new ConfigurableContentAuthSigner(Method, Salt, null, null);
}

public class ConfigurableMd5Tests
{
    // Public RFC 1321 MD5 test vectors — unconfigured, ConfigurableMd5 is ordinary MD5.
    [Theory]
    [InlineData("", "d41d8cd98f00b204e9800998ecf8427e")]
    [InlineData("abc", "900150983cd24fb0d6963f7d28e17f72")]
    [InlineData("message digest", "f96b697d7cb7938d525a2f31aaf161d0")]
    [InlineData("The quick brown fox jumps over the lazy dog", "9e107d9d372bb6826bd81d3542a419d6")]
    public void With_no_overrides_it_is_standard_md5(string input, string expected)
        => Assert.Equal(expected, new ConfigurableMd5().ComputeHashHex(Encoding.ASCII.GetBytes(input)));

    [Fact]
    public void With_no_overrides_it_matches_the_framework_md5_across_padding_boundaries()
    {
        var md5 = new ConfigurableMd5();
        foreach (var length in new[] { 0, 1, 55, 56, 63, 64, 65, 119, 128 })
        {
            var msg = new byte[length];
            for (var i = 0; i < length; i++)
            {
                msg[i] = (byte)(i * 7);
            }

            Assert.Equal(Convert.ToHexString(MD5.HashData(msg)).ToLowerInvariant(), md5.ComputeHashHex(msg));
        }
    }

    [Fact]
    public void A_schedule_or_k_override_changes_the_digest_deterministically()
    {
        var input = Encoding.ASCII.GetBytes("abc");
        var standard = new ConfigurableMd5().ComputeHashHex(input);

        var reschedule = new int[] { 10, 11, 12, 13, 14, 15, 6, 7, 8, 9, 0, 1, 2, 3, 4, 5 };
        var variant = new ConfigurableMd5(reschedule, new Dictionary<int, uint> { [42] = 0x12345678u });

        Assert.NotEqual(standard, variant.ComputeHashHex(input));
        Assert.Equal(variant.ComputeHashHex(input), variant.ComputeHashHex(input)); // deterministic
    }

    [Theory]
    [InlineData(new int[] { 0 })]                  // wrong length
    [InlineData(new int[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 99 })] // out of range
    public void An_invalid_schedule_is_rejected(int[] schedule)
        => Assert.Throws<ArgumentException>(() => new ConfigurableMd5(schedule, null));
}

public class ConfigurableContentAuthSignerTests
{
    [Fact]
    public void It_appends_a_signed_moment_to_the_base_auth()
    {
        var signer = new ConfigurableContentAuthSigner("sig1", Encoding.ASCII.GetBytes("s"), null, null);

        var auth = signer.BuildContentAuth("user_id=1&token=abc", "abc", 1700000000000L);

        Assert.StartsWith("user_id=1&token=abc&sign2_method=sig1&instance=0&start_moment=1700000000000&sign2=", auth);
        Assert.Matches("sign2=[0-9a-f]{32}$", auth);
    }

    [Fact]
    public void The_signature_is_deterministic_and_depends_on_moment_salt_and_method()
    {
        byte[] Salt(string s) => Encoding.ASCII.GetBytes(s);
        var a = new ConfigurableContentAuthSigner("sig1", Salt("x"), null, null);

        var s1 = a.BuildContentAuth("base", "tok", 1000L);
        Assert.Equal(s1, a.BuildContentAuth("base", "tok", 1000L));
        Assert.NotEqual(s1, a.BuildContentAuth("base", "tok", 1001L));
        Assert.NotEqual(s1, new ConfigurableContentAuthSigner("sig1", Salt("y"), null, null).BuildContentAuth("base", "tok", 1000L));
        Assert.NotEqual(s1, new ConfigurableContentAuthSigner("sig2", Salt("x"), null, null).BuildContentAuth("base", "tok", 1000L));
    }
}
