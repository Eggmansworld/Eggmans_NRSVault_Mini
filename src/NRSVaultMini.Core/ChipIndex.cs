using System.Security.Cryptography;
using System.Text;

namespace NRSVaultMini.Core;

/// <summary>A run of bytes inside a file's ROM data (payload) whose SHA1 is a nes20db PRG or CHR chip.</summary>
public sealed class ChipHit
{
    public string Sha1 { get; set; } = "";
    public long Offset { get; set; }
    public long Size { get; set; }
}

/// <summary>Every PRG-ROM and CHR-ROM chip in nes20db.xml, by SHA1. Used to find chips anywhere:
/// inside other entries' ROMs, inside ROMs that are not in the database (hacks, bad dumps, overdumps),
/// and in loose .prg/.chr files.</summary>
public sealed class ChipIndex
{
    public HashSet<string> Sha1s { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<long> PrgSizes { get; } = new();
    public HashSet<long> ChrSizes { get; } = new();
    public HashSet<long> AllSizes { get; } = new();
    /// <summary>Changes whenever the chip set changes, so cached chip hits get recomputed.</summary>
    public string Stamp { get; }

    public ChipIndex(NesDb db)
    {
        foreach (var e in db.Entries)
            foreach (var p in SplitLayout.Pieces(e))
            {
                Sha1s.Add(p.Sha1);
                (p.Kind == "PRG" ? PrgSizes : ChrSizes).Add(p.Size);
                AllSizes.Add(p.Size);
            }
        Stamp = "c1-" + Convert.ToHexString(SHA1.HashData(Encoding.ASCII.GetBytes(string.Join(",", Sha1s.Order(StringComparer.Ordinal)))))[..12];
    }

    /// <summary>Hashes the likely chip positions in a ROM's data and keeps those that are nes20db chips:
    /// the whole data (a loose .prg/.chr or a PRG-only cart), the PRG/CHR the file's own header describes,
    /// and every split into a known PRG size followed by a known CHR size (for headerless or wrongly headered ROMs).</summary>
    public List<ChipHit> Find(byte[]? header, byte[] payload)
    {
        long n = payload.Length;
        var spans = new HashSet<(long off, long size)> { (0, n) };

        if (header != null && NesHeader.HasINesMagic(header))
        {
            long t = (header[6] & 0x4) != 0 ? 512 : 0;
            long prg = NesHeader.PrgSize(header), chr = NesHeader.ChrSize(header);
            if (prg > 0 && t + prg <= n) spans.Add((t, prg));
            if (chr > 0 && t + prg + chr <= n) spans.Add((t + prg, chr));
        }

        foreach (long t in new long[] { 0, 512 })
            foreach (var p in PrgSizes)
            {
                long rest = n - t - p;
                if (rest < 0) continue;
                if (rest == 0) spans.Add((t, p));
                else if (ChrSizes.Contains(rest)) { spans.Add((t, p)); spans.Add((t + p, rest)); }
            }

        var hits = new List<ChipHit>();
        foreach (var (off, size) in spans)
        {
            if (size <= 0 || !AllSizes.Contains(size)) continue;
            var sha = Convert.ToHexString(SHA1.HashData(payload.AsSpan((int)off, (int)size)));
            if (Sha1s.Contains(sha)) hits.Add(new ChipHit { Sha1 = sha, Offset = off, Size = size });
        }
        return hits;
    }

    /// <summary>Whether a file of this size could hold a chip worth reading (raw chip, or chip + header/trainer).</summary>
    public bool MightHoldChips(long fileSize) =>
        AllSizes.Contains(fileSize) || fileSize % 8192 == 0 || (fileSize - 16) % 8192 == 0 || (fileSize - 528) % 8192 == 0;
}
