using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace NRSVaultMini.Core;

public enum FileKind { Raw, INes, Unif, Archive, Error }

/// <summary>Everything learned about one file on disk. Serialized into the scan cache.</summary>
public sealed class FileInfoRecord
{
    public string RelPath { get; set; } = "";
    public long Size { get; set; }
    public long MTimeTicks { get; set; }
    public FileKind Kind { get; set; }
    public string? HeaderHex { get; set; }
    public string FileCrc { get; set; } = "";
    public string FileSha1 { get; set; } = "";
    /// <summary>The ROM data with any iNES/UNIF wrapper removed (what nes20db's &lt;rom&gt; hashes).</summary>
    public long PayloadSize { get; set; }
    public string PayloadSha1 { get; set; } = "";
    /// <summary>Set when an existing header reports a smaller size than the payload: SHA1 of the reported-size part.</summary>
    public long ReportedSize { get; set; }
    public string? ReportedSha1 { get; set; }
    public string? Error { get; set; }

    [JsonIgnore] public byte[]? HeaderBytes => HeaderHex == null ? null : Convert.FromHexString(HeaderHex);
}

public static class RomFile
{
    public static readonly string[] ArchiveExtensions = { ".zip", ".7z", ".rar" };

    public static bool IsArchive(string path) =>
        ArchiveExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public static FileInfoRecord Analyze(string fullPath, string relPath)
    {
        var fi = new FileInfo(fullPath);
        var rec = new FileInfoRecord { RelPath = relPath, Size = fi.Length, MTimeTicks = fi.LastWriteTimeUtc.Ticks };
        try
        {
            if (IsArchive(fullPath))
            {
                rec.Kind = FileKind.Archive;
                return rec;
            }

            byte[] data = File.ReadAllBytes(fullPath);
            rec.FileCrc = Crc32Util.ToHex(Crc32Util.Compute(data));
            rec.FileSha1 = Convert.ToHexString(SHA1.HashData(data));

            var (kind, header, payload) = Split(data);
            rec.Kind = kind;
            rec.HeaderHex = header == null ? null : Convert.ToHexString(header);
            rec.PayloadSize = payload.Length;
            rec.PayloadSha1 = kind == FileKind.Raw ? rec.FileSha1 : Convert.ToHexString(SHA1.HashData(payload));

            if (header != null)
            {
                long reported = NesHeader.ReportedRomSize(header);
                if (reported > 0 && reported < payload.Length)
                {
                    rec.ReportedSize = reported;
                    rec.ReportedSha1 = Convert.ToHexString(SHA1.HashData(payload.AsSpan(0, (int)reported)));
                }
            }
        }
        catch (Exception ex)
        {
            rec.Kind = FileKind.Error;
            rec.Error = ex.Message;
        }
        return rec;
    }

    /// <summary>Reads the file and returns the bare ROM payload (header/UNIF wrapper removed).</summary>
    public static byte[] ReadPayload(string fullPath)
    {
        var (_, _, payload) = Split(File.ReadAllBytes(fullPath));
        return payload;
    }

    public static (FileKind kind, byte[]? header, byte[] payload) Split(byte[] data)
    {
        if (NesHeader.HasINesMagic(data))
            return (FileKind.INes, data[..NesHeader.Size], data[NesHeader.Size..]);
        if (data.Length >= 32 && data[0] == 'U' && data[1] == 'N' && data[2] == 'I' && data[3] == 'F')
            return (FileKind.Unif, null, UnifPayload(data));
        return (FileKind.Raw, null, data);
    }

    /// <summary>Concatenates PRG chunks then CHR chunks in file order (same as the Python script).</summary>
    private static byte[] UnifPayload(byte[] data)
    {
        var prg = new MemoryStream();
        var chr = new MemoryStream();
        int pos = 32;
        while (pos + 8 <= data.Length)
        {
            // Bad UNIF: DINF chunk off by one
            if (data[pos] == 0x00 && data[pos + 1] == (byte)'D' && data[pos + 2] == (byte)'I' && data[pos + 3] == (byte)'N')
                pos += 1;
            if (pos + 8 > data.Length) break;

            string id = System.Text.Encoding.ASCII.GetString(data, pos, 3);
            long len = BitConverter.ToUInt32(data, pos + 4);
            pos += 8;
            if (id == "DIN" && len == 0) len = 204; // Bad UNIF: DINF chunk with specified length of 0

            int take = (int)Math.Min(len, data.Length - pos);
            if (id == "PRG") prg.Write(data, pos, take);
            else if (id == "CHR") chr.Write(data, pos, take);
            pos += take;
        }
        chr.Position = 0;
        chr.CopyTo(prg);
        return prg.ToArray();
    }
}
