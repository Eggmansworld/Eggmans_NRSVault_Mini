namespace NRSVaultMini.Core;

/// <summary>Values from a nes20db.xml game entry that drive the NES 2.0 header.</summary>
public sealed class HeaderSpec
{
    public long PrgRom, PrgRam, PrgNvram, ChrRom, ChrRam, ChrNvram, Trainer;
    public int MiscRom;
    public int ConsoleType, ConsoleRegion, Expansion;
    public int VsHardware, VsPpu;
    public string Mirroring = "H";
    public int Mapper, SubMapper, Battery;
}

/// <summary>NES 2.0 header construction and inspection.
/// Header construction is a straight port of Kitrinx's NES_Header_Repair (make_header),
/// which follows NewRisingSun's own logic. See https://www.nesdev.org/wiki/NES_2.0</summary>
public static class NesHeader
{
    public const int Size = 16;

    public static bool HasINesMagic(ReadOnlySpan<byte> data) =>
        data.Length >= Size && data[0] == 0x4E && data[1] == 0x45 && data[2] == 0x53 && data[3] == 0x1A;

    public static bool IsNes20(ReadOnlySpan<byte> header) => (header[7] & 0x0C) == 0x08;

    public static byte[] Build(HeaderSpec s)
    {
        // default to 'H' for anything invalid
        int mirrorNibble = 0;
        if (s.Mapper == 30)
        {
            if (s.Mirroring == "V") mirrorNibble = 0x1;
            else if (s.Mirroring == "1") mirrorNibble = 0x8;
            else if (s.Mirroring == "4") mirrorNibble = 0x9;
        }
        else if (s.Mapper == 218)
        {
            if (s.Mirroring == "V") mirrorNibble = 0x1;
            else if (s.Mirroring == "0") mirrorNibble = 0x8;
            else if (s.Mirroring == "1") mirrorNibble = 0x9;
        }
        else
        {
            if (s.Mirroring == "V") mirrorNibble = 0x1;
            else if (s.Mirroring == "4") mirrorNibble = 0x8;
        }

        var h = new byte[Size];
        h[0] = 0x4E;
        h[1] = 0x45;
        h[2] = 0x53;
        h[3] = 0x1A;
        h[4] = RomByte(s.PrgRom, 16384);
        h[5] = RomByte(s.ChrRom, 8192);
        h[6] = (byte)(((s.Battery << 1) | mirrorNibble | (s.Trainer > 0 ? 0x4 : 0) | ((s.Mapper & 0xF) << 4)) & 0xFF);
        h[7] = (byte)(0x8 | (s.ConsoleType >= 3 ? 3 : s.ConsoleType) | (s.Mapper & 0xF0));
        h[8] = (byte)(((s.Mapper & 0xF00) >> 8) | ((s.SubMapper & 0xF) << 4));
        h[9] = (byte)(RomNibble(s.PrgRom, 16384) | (RomNibble(s.ChrRom, 8192) << 4));
        h[10] = (byte)(PowerOfTwo(s.PrgRam) | (PowerOfTwo(s.PrgNvram) << 4));
        h[11] = (byte)(PowerOfTwo(s.ChrRam) | (PowerOfTwo(s.ChrNvram) << 4));
        h[12] = (byte)(s.ConsoleRegion & 0x03);
        int consoleBits = h[7] & 0x3;
        h[13] = consoleBits == 0x1
            ? (byte)(((s.VsHardware & 0x0F) << 4) | (s.VsPpu & 0x0F))
            : consoleBits == 0x3 ? (byte)(s.ConsoleType & 0xF) : (byte)0;
        h[14] = (byte)(s.MiscRom & 0x3);
        h[15] = (byte)(s.Expansion & 0x3F);
        return h;
    }

    private static byte RomByte(long romSize, long divis)
    {
        // logic borrowed from NRS (exponent-multiplier notation for odd sizes)
        if (romSize > 0xEFF * divis || romSize % divis != 0)
        {
            long multi = 1;
            if (romSize % 3 == 0) multi = 3;
            else if (romSize % 5 == 0) multi = 5;
            else if (romSize % 7 == 0) multi = 7;

            int exponent = BitLength(romSize / multi) - 1;
            return (byte)((exponent << 2) | (int)((multi - 1) >> 1));
        }
        return (byte)((romSize / divis) & 0xFF);
    }

    private static int RomNibble(long romSize, long divis)
    {
        if (romSize > 0xEFF * divis || romSize % divis != 0) return 0xF;
        return (int)(((romSize / divis) & 0xF00) >> 8);
    }

    private static int BitLength(long v)
    {
        int n = 0;
        while (v > 0) { n++; v >>= 1; }
        return n;
    }

    /// <summary>RAM size shift count: size = 64 &lt;&lt; n. Matches int(log2(size/64)) &amp; 0xF.</summary>
    private static int PowerOfTwo(long size)
    {
        if (size <= 0) return 0;
        return (int)Math.Log2(size / 64.0) & 0xF; // cast truncates toward zero like Python int()
    }

    /// <summary>PRG+CHR(+trainer) size implied by an existing header (calc_rom_size).</summary>
    public static long ReportedRomSize(ReadOnlySpan<byte> h)
    {
        long size = (h[6] & 0x4) != 0 ? 512 : 0;
        if (IsNes20(h))
        {
            if ((h[9] & 0x0F) == 0x0F)
                size += (1L << ((h[4] & 0xFC) >> 2)) * (((h[4] & 0x3) * 2) + 1);
            else
                size += 16384L * (((h[9] & 0x0F) << 8) | h[4]);

            if ((h[9] & 0xF0) == 0xF0)
                size += (1L << ((h[5] & 0xFC) >> 2)) * (((h[5] & 0x3) * 2) + 1);
            else
                size += 8192L * (((h[9] & 0xF0) << 4) | h[5]);
        }
        else
        {
            size += 8192L * h[5];
            size += 16384L * h[4];
        }
        return size;
    }

    /// <summary>PRG-ROM size an existing header claims.</summary>
    public static long PrgSize(ReadOnlySpan<byte> h)
    {
        if (IsNes20(h) && (h[9] & 0x0F) == 0x0F) return (1L << ((h[4] & 0xFC) >> 2)) * (((h[4] & 0x3) * 2) + 1);
        return 16384L * ((IsNes20(h) ? (h[9] & 0x0F) << 8 : 0) | h[4]);
    }

    /// <summary>CHR-ROM size an existing header claims.</summary>
    public static long ChrSize(ReadOnlySpan<byte> h)
    {
        if (IsNes20(h) && (h[9] & 0xF0) == 0xF0) return (1L << ((h[5] & 0xFC) >> 2)) * (((h[5] & 0x3) * 2) + 1);
        return 8192L * ((IsNes20(h) ? (h[9] & 0xF0) << 4 : 0) | h[5]);
    }

    public static int Mapper(ReadOnlySpan<byte> h)
    {
        int m = (h[7] & 0xF0) | ((h[6] & 0xF0) >> 4);
        if (IsNes20(h)) m |= (h[8] & 0x0F) << 8;
        return m;
    }

    public static int SubMapper(ReadOnlySpan<byte> h) => IsNes20(h) ? (h[8] >> 4) & 0xF : 0;

    public static string ToHex(ReadOnlySpan<byte> h) => Convert.ToHexString(h);

    public static string ToSpacedHex(ReadOnlySpan<byte> h) =>
        string.Join(' ', h.ToArray().Select(b => b.ToString("X2")));

    public static string Describe(ReadOnlySpan<byte> h)
    {
        if (!HasINesMagic(h)) return "(no iNES header)";
        string fmt = IsNes20(h) ? "NES 2.0" : ((h[7] & 0x0C) == 0 ? "iNES 1.0" : "archaic iNES");
        return $"{fmt}, mapper {Mapper(h)}" + (IsNes20(h) ? $".{SubMapper(h)}" : "") +
               $", battery {((h[6] & 2) != 0 ? 1 : 0)}, trainer {((h[6] & 4) != 0 ? 1 : 0)}";
    }
}
