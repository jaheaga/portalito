using System.Buffers.Binary;
using System.Numerics;

namespace Jellyfin.Plugin.Portalito.Crypto;

/// <summary>
/// Standard MD5 (RFC 1321) with two optional, configurable deviations: the round-1 message schedule and per-round K
/// constants. Some portals sign requests with a lightly modified MD5; rather than bake any specific variant in, the
/// deviations are supplied by the operator through the plugin config. With no overrides this is ordinary MD5, so the
/// default build ships only the public standard constants.
/// </summary>
public sealed class ConfigurableMd5
{
    private const uint IvA = 0x67452301;
    private const uint IvB = 0xefcdab89;
    private const uint IvC = 0x98badcfe;
    private const uint IvD = 0x10325476;

    // Standard MD5 table: floor(abs(sin(i+1)) * 2^32), spelled out to avoid floating-point drift (RFC 1321).
    private static readonly uint[] StandardK =
    {
        0xd76aa478, 0xe8c7b756, 0x242070db, 0xc1bdceee,
        0xf57c0faf, 0x4787c62a, 0xa8304613, 0xfd469501,
        0x698098d8, 0x8b44f7af, 0xffff5bb1, 0x895cd7be,
        0x6b901122, 0xfd987193, 0xa679438e, 0x49b40821,
        0xf61e2562, 0xc040b340, 0x265e5a51, 0xe9b6c7aa,
        0xd62f105d, 0x02441453, 0xd8a1e681, 0xe7d3fbc8,
        0x21e1cde6, 0xc33707d6, 0xf4d50d87, 0x455a14ed,
        0xa9e3e905, 0xfcefa3f8, 0x676f02d9, 0x8d2a4c8a,
        0xfffa3942, 0x8771f681, 0x6d9d6122, 0xfde5380c,
        0xa4beea44, 0x4bdecfa9, 0xf6bb4b60, 0xbebfbc70,
        0x289b7ec6, 0xeaa127fa, 0xd4ef3085, 0x04881d05,
        0xd9d4d039, 0xe6db99e5, 0x1fa27cf8, 0xc4ac5665,
        0xf4292244, 0x432aff97, 0xab9423a7, 0xfc93a039,
        0x655b59c3, 0x8f0ccc92, 0xffeff47d, 0x85845dd1,
        0x6fa87e4f, 0xfe2ce6e0, 0xa3014314, 0x4e0811a1,
        0xf7537e82, 0xbd3af235, 0x2ad7d2bb, 0xeb86d391,
    };

    private static readonly int[] S =
    {
        7, 12, 17, 22, 7, 12, 17, 22, 7, 12, 17, 22, 7, 12, 17, 22,
        5, 9, 14, 20, 5, 9, 14, 20, 5, 9, 14, 20, 5, 9, 14, 20,
        4, 11, 16, 23, 4, 11, 16, 23, 4, 11, 16, 23, 4, 11, 16, 23,
        6, 10, 15, 21, 6, 10, 15, 21, 6, 10, 15, 21, 6, 10, 15, 21,
    };

    private readonly uint[] _k;
    private readonly int[] _g;

    /// <summary>Ordinary MD5.</summary>
    public ConfigurableMd5()
        : this(null, null)
    {
    }

    /// <param name="round1Schedule">16 indices for rounds 0-15; null keeps the standard 0..15.</param>
    /// <param name="kOverrides">Per-round K replacements by round index (0-63); null/empty keeps the standard table.</param>
    public ConfigurableMd5(int[]? round1Schedule, IReadOnlyDictionary<int, uint>? kOverrides)
    {
        _k = (uint[])StandardK.Clone();
        if (kOverrides is not null)
        {
            foreach (var (index, value) in kOverrides)
            {
                if (index is < 0 or > 63)
                {
                    throw new ArgumentOutOfRangeException(nameof(kOverrides), $"K index {index} is out of range 0-63.");
                }

                _k[index] = value;
            }
        }

        _g = BuildSchedule(round1Schedule);
    }

    private static int[] BuildSchedule(int[]? round1)
    {
        var g = new int[64];
        if (round1 is not null)
        {
            if (round1.Length != 16 || round1.Any(x => x is < 0 or > 15))
            {
                throw new ArgumentException("The round-1 schedule must be 16 indices, each 0-15.", nameof(round1));
            }

            Array.Copy(round1, g, 16);
        }
        else
        {
            for (var i = 0; i < 16; i++)
            {
                g[i] = i;
            }
        }

        for (var i = 16; i < 32; i++)
        {
            g[i] = ((5 * i) + 1) % 16;
        }

        for (var i = 32; i < 48; i++)
        {
            g[i] = ((3 * i) + 5) % 16;
        }

        for (var i = 48; i < 64; i++)
        {
            g[i] = (7 * i) % 16;
        }

        return g;
    }

    private void Compress(ref uint a0, ref uint b0, ref uint c0, ref uint d0, ReadOnlySpan<byte> block)
    {
        Span<uint> m = stackalloc uint[16];
        for (var i = 0; i < 16; i++)
        {
            m[i] = BinaryPrimitives.ReadUInt32LittleEndian(block.Slice(i * 4, 4));
        }

        uint a = a0, b = b0, c = c0, d = d0;
        for (var i = 0; i < 64; i++)
        {
            uint f;
            if (i < 16)
            {
                f = (b & c) | (~b & d);
            }
            else if (i < 32)
            {
                f = (d & b) | (~d & c);
            }
            else if (i < 48)
            {
                f = b ^ c ^ d;
            }
            else
            {
                f = c ^ (b | ~d);
            }

            f = unchecked(f + a + _k[i] + m[_g[i]]);
            a = d;
            d = c;
            c = b;
            b = unchecked(b + BitOperations.RotateLeft(f, S[i]));
        }

        unchecked
        {
            a0 += a;
            b0 += b;
            c0 += c;
            d0 += d;
        }
    }

    /// <summary>The 16-byte digest of <paramref name="message"/>.</summary>
    public byte[] ComputeHash(ReadOnlySpan<byte> message)
    {
        uint a = IvA, b = IvB, c = IvC, d = IvD;

        var fullBlocks = message.Length / 64;
        for (var i = 0; i < fullBlocks; i++)
        {
            Compress(ref a, ref b, ref c, ref d, message.Slice(i * 64, 64));
        }

        var remainder = message.Slice(fullBlocks * 64);
        var paddedLength = (((remainder.Length + 8) / 64) + 1) * 64;
        var tail = new byte[paddedLength];
        remainder.CopyTo(tail);
        tail[remainder.Length] = 0x80;
        BinaryPrimitives.WriteUInt64LittleEndian(tail.AsSpan(paddedLength - 8), (ulong)message.Length * 8);

        for (var offset = 0; offset < paddedLength; offset += 64)
        {
            Compress(ref a, ref b, ref c, ref d, tail.AsSpan(offset, 64));
        }

        var digest = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(digest.AsSpan(0, 4), a);
        BinaryPrimitives.WriteUInt32LittleEndian(digest.AsSpan(4, 4), b);
        BinaryPrimitives.WriteUInt32LittleEndian(digest.AsSpan(8, 4), c);
        BinaryPrimitives.WriteUInt32LittleEndian(digest.AsSpan(12, 4), d);
        return digest;
    }

    /// <summary>The digest as 32 lowercase hex characters.</summary>
    public string ComputeHashHex(ReadOnlySpan<byte> message)
        => Convert.ToHexString(ComputeHash(message)).ToLowerInvariant();
}
