using System.Net;
using System.Xml;

namespace NRSVaultMini.Core;

/// <summary>An XML element copied verbatim (name + attributes in original order).</summary>
public sealed record RawElement(string Name, IReadOnlyList<KeyValuePair<string, string>> Attributes)
{
    public string? Get(string attr)
    {
        foreach (var kv in Attributes)
            if (kv.Key == attr) return kv.Value;
        return null;
    }
}

/// <summary>One &lt;game&gt; in nes20db.xml. One entry may be listed at several paths.</summary>
public sealed class DbEntry
{
    public int Index { get; init; }
    public string Comment { get; set; } = "";
    public List<string> Paths { get; } = new();
    public List<RawElement> Elements { get; } = new();
    public HeaderSpec Spec { get; } = new();

    public long RomSize { get; set; }
    public uint RomCrc { get; set; }
    public string RomSha1 { get; set; } = "";

    public byte[] Header { get; set; } = Array.Empty<byte>();
    public long HeaderedSize => RomSize + NesHeader.Size;
    public uint HeaderedCrc { get; set; }

    public bool HasFooter { get; set; }
    public string? ParseError { get; set; }

    public string MapperText => $"{Spec.Mapper}.{Spec.SubMapper}";
}

/// <summary>One file location from the database (a game entry at one of its paths).</summary>
public sealed class DbRow
{
    public required DbEntry Entry { get; init; }
    /// <summary>Relative path using '\' separators, exactly as in nes20db.xml.</summary>
    public required string RelPath { get; init; }
    public string FileName => RelPath[(RelPath.LastIndexOf('\\') + 1)..];
    public string Folder => RelPath.LastIndexOf('\\') is var i && i >= 0 ? RelPath[..i] : "";
}

public sealed class NesDb
{
    public string SourcePath { get; private set; } = "";
    public List<KeyValuePair<string, string>> RootAttributes { get; } = new();
    public string Date => RootAttributes.FirstOrDefault(a => a.Key == "date").Value ?? "";
    public List<string> TrailingComments { get; } = new();
    public List<DbEntry> Entries { get; } = new();
    public List<DbRow> Rows { get; } = new();
    public Dictionary<string, DbEntry> BySha1 { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Warnings { get; } = new();

    public static NesDb Load(string path)
    {
        var db = new NesDb { SourcePath = path };
        var settings = new XmlReaderSettings { IgnoreComments = false, IgnoreWhitespace = true, DtdProcessing = DtdProcessing.Ignore };
        using var reader = XmlReader.Create(path, settings);

        bool inRoot = false;
        DbEntry? current = null;
        while (reader.Read())
        {
            switch (reader.NodeType)
            {
                case XmlNodeType.Element when !inRoot:
                    inRoot = true;
                    db.RootAttributes.AddRange(ReadAttributes(reader));
                    break;

                case XmlNodeType.Element when reader.Depth == 1 && reader.Name == "game":
                    current = new DbEntry { Index = db.Entries.Count };
                    if (reader.IsEmptyElement) { db.Finish(current); current = null; }
                    break;

                case XmlNodeType.Comment when current != null && reader.Depth == 2 && current.Comment.Length == 0:
                    current.Comment = reader.Value.Trim();
                    break;

                case XmlNodeType.Comment when current == null:
                    db.TrailingComments.Add(reader.Value.Trim());
                    break;

                case XmlNodeType.Element when current != null && reader.Depth == 2:
                    current.Elements.Add(new RawElement(reader.Name, ReadAttributes(reader)));
                    break;

                case XmlNodeType.EndElement when reader.Depth == 1 && reader.Name == "game" && current != null:
                    db.Finish(current);
                    current = null;
                    break;
            }
        }

        return db;
    }

    private static List<KeyValuePair<string, string>> ReadAttributes(XmlReader reader)
    {
        var list = new List<KeyValuePair<string, string>>();
        if (reader.MoveToFirstAttribute())
        {
            do list.Add(new(reader.Name, reader.Value));
            while (reader.MoveToNextAttribute());
            reader.MoveToElement();
        }
        return list;
    }

    private void Finish(DbEntry e)
    {
        // Paths live in the comment, separated by "; ". Comments are not entity-decoded by XML, so decode &amp; etc.
        foreach (var p in WebUtility.HtmlDecode(e.Comment).Split("; ", StringSplitOptions.RemoveEmptyEntries))
        {
            var rel = p.Trim().Replace('/', '\\');
            if (rel.Length > 0) e.Paths.Add(rel);
        }

        var s = e.Spec;
        try
        {
            // Same semantics as the Python script: last occurrence of an element wins.
            foreach (var el in e.Elements)
            {
                switch (el.Name)
                {
                    case "rom":
                        e.RomSize = long.Parse(el.Get("size") ?? "0");
                        e.RomCrc = Crc32Util.ParseHex(el.Get("crc32"));
                        e.RomSha1 = (el.Get("sha1") ?? "").ToUpperInvariant();
                        break;
                    case "prgrom": s.PrgRom = Size(el); break;
                    case "prgram": s.PrgRam = Size(el); break;
                    case "prgnvram": s.PrgNvram = Size(el); break;
                    case "chrrom": s.ChrRom = Size(el); break;
                    case "chrram": s.ChrRam = Size(el); break;
                    case "chrnvram": s.ChrNvram = Size(el); break;
                    case "trainer": s.Trainer = Size(el); break;
                    case "miscrom": s.MiscRom = int.Parse(el.Get("number") ?? "0"); break;
                    case "footer": e.HasFooter = true; break;
                    case "console":
                        s.ConsoleType = int.Parse(el.Get("type") ?? "0");
                        s.ConsoleRegion = int.Parse(el.Get("region") ?? "0");
                        break;
                    case "expansion": s.Expansion = int.Parse(el.Get("type") ?? "0"); break;
                    case "pcb":
                        s.Mapper = int.Parse(el.Get("mapper") ?? "0");
                        s.SubMapper = int.Parse(el.Get("submapper") ?? "0");
                        s.Mirroring = el.Get("mirroring") ?? "H";
                        s.Battery = int.Parse(el.Get("battery") ?? "0");
                        break;
                    case "vs":
                        s.VsHardware = int.Parse(el.Get("hardware") ?? "0");
                        s.VsPpu = int.Parse(el.Get("ppu") ?? "0");
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            e.ParseError = ex.Message;
            Warnings.Add($"Entry #{e.Index} ({e.Comment}): {ex.Message}");
        }

        e.Header = NesHeader.Build(s);
        e.HeaderedCrc = Crc32Util.Combine(Crc32Util.Compute(e.Header), e.RomCrc, e.RomSize);

        Entries.Add(e);
        if (string.IsNullOrEmpty(e.RomSha1))
        {
            Warnings.Add($"Entry #{e.Index} ({e.Comment}) has no <rom sha1>; skipped for matching.");
        }
        else if (!BySha1.TryAdd(e.RomSha1, e))
        {
            Warnings.Add($"Duplicate ROM SHA1 {e.RomSha1} in entry #{e.Index}; first entry kept for matching.");
        }

        foreach (var p in e.Paths)
            Rows.Add(new DbRow { Entry = e, RelPath = p });
    }

    private static long Size(RawElement el) => long.Parse(el.Get("size") ?? "0");
}
