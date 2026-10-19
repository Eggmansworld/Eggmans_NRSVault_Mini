using System.IO.Hashing;

namespace NRSVaultMini.Core;

/// <summary>CRC32 helpers, including zlib-style crc32_combine so the headered CRC
/// can be derived from the header CRC plus the database's headerless ROM CRC.</summary>
public static class Crc32Util
{
    public static uint Compute(ReadOnlySpan<byte> data) => Crc32.HashToUInt32(data);

    public static uint Combine(uint crc1, uint crc2, long len2)
    {
        if (len2 <= 0) return crc1;

        var even = new uint[32];
        var odd = new uint[32];

        odd[0] = 0xEDB88320u;
        uint row = 1;
        for (int n = 1; n < 32; n++)
        {
            odd[n] = row;
            row <<= 1;
        }

        Gf2Square(even, odd);
        Gf2Square(odd, even);

        do
        {
            Gf2Square(even, odd);
            if ((len2 & 1) != 0) crc1 = Gf2Times(even, crc1);
            len2 >>= 1;
            if (len2 == 0) break;

            Gf2Square(odd, even);
            if ((len2 & 1) != 0) crc1 = Gf2Times(odd, crc1);
            len2 >>= 1;
        } while (len2 != 0);

        return crc1 ^ crc2;
    }

    private static uint Gf2Times(uint[] mat, uint vec)
    {
        uint sum = 0;
        int i = 0;
        while (vec != 0)
        {
            if ((vec & 1) != 0) sum ^= mat[i];
            vec >>= 1;
            i++;
        }
        return sum;
    }

    private static void Gf2Square(uint[] square, uint[] mat)
    {
        for (int n = 0; n < 32; n++) square[n] = Gf2Times(mat, mat[n]);
    }

    public static string ToHex(uint crc) => crc.ToString("X8");

    public static uint ParseHex(string? s) =>
        string.IsNullOrEmpty(s) ? 0 : Convert.ToUInt32(s, 16);
}
