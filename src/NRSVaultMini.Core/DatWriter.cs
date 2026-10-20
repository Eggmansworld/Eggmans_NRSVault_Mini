using System.Text;
using System.Xml;

namespace NRSVaultMini.Core;

public sealed class DatOptions
{
    public string Name { get; set; } = "NewRisingSun NES2.0 Headered Collection";
    public string Author { get; set; } = "NewRisingSun (datted by Eggman's NRSVault Mini app)";
    /// <summary>Also list ROMs that are not verified, with size + CRC32 only (CRC computed from the database).</summary>
    public bool IncludeUnverified { get; set; }
}

public sealed record DatResult(int Verified, int Unverified, int Skipped);

/// <summary>Writes RomVault-compatible Logiqx-style dats. Every value from nes20db.xml is carried
/// along inside a &lt;nes20db&gt; element per game, which RomVault ignores.</summary>
public static class DatWriter
{
    /// <summary>Default name of the split dat, derived from the headered dat name.</summary>
    public static string SplitName(string headeredName) =>
        headeredName.Replace("Headered", "Split PRG-CHR", StringComparison.OrdinalIgnoreCase) is var n && n != headeredName
            ? n : headeredName + " (Split PRG-CHR)";

    /// <summary>The headered collection: one .nes per database path, hashes of the file with its NES 2.0 header.</summary>
    public static DatResult Write(string path, CollectionState state, DatOptions opt, string appVersion)
    {
        var db = state.Db;
        int verified = 0, unverified = 0, skipped = 0;
        using var w = Create(path);
        WriteHeader(w, db, opt.Name, opt.Author,
            $"Built from nes20db.xml dated {db.Date} ({db.Entries.Count} entries, {db.Rows.Count} files) by NRSVault Mini {appVersion}. " +
            "ROM sizes/hashes are for the files WITH their NES 2.0 header. Original nes20db values are kept in each game's <nes20db> element.");

        foreach (var rs in state.Rows.OrderBy(r => r.Row.RelPath, StringComparer.OrdinalIgnoreCase))
        {
            bool ok = rs.Status == RowStatus.Ok && rs.File != null;
            if (!ok && !opt.IncludeUnverified) { skipped++; continue; }
            var e = rs.Row.Entry;

            StartGame(w, rs.Row);
            w.WriteStartElement("rom");
            w.WriteAttributeString("name", rs.Row.FileName);
            w.WriteAttributeString("size", e.HeaderedSize.ToString());
            if (ok)
            {
                w.WriteAttributeString("crc", rs.File!.FileCrc.ToLowerInvariant());
                w.WriteAttributeString("sha1", rs.File.FileSha1.ToLowerInvariant());
                verified++;
            }
            else
            {
                w.WriteAttributeString("crc", Crc32Util.ToHex(e.HeaderedCrc).ToLowerInvariant());
                unverified++;
            }
            w.WriteEndElement();

            w.WriteStartElement("nrsvault");
            w.WriteAttributeString("verified", ok ? "true" : "false");
            w.WriteAttributeString("header", NesHeader.ToHex(e.Header));
            w.WriteEndElement();

            WriteNes20db(w, e);
            w.WriteEndElement(); // game
        }

        Finish(w);
        return new DatResult(verified, unverified, skipped);
    }

    /// <summary>The split set: per database path, a .prg and (when the cartridge has CHR-ROM) a .chr file.
    /// All hashes come straight from nes20db.xml, so every entry is complete without scanning anything.</summary>
    public static int WriteSplit(string path, NesDb db, DatOptions opt, string appVersion)
    {
        int games = 0;
        using var w = Create(path);
        WriteHeader(w, db, opt.Name, opt.Author,
            $"Split PRG/CHR set built from nes20db.xml dated {db.Date} ({db.Entries.Count} entries, {db.Rows.Count} paths) by NRSVault Mini {appVersion}. " +
            "Each game holds <name>.prg and, when the cartridge has CHR-ROM, <name>.chr, with no header. Trainers, misc ROMs and footers are not included. " +
            "Original nes20db values (mapper, mirroring, RAM sizes, ...) are kept in each game's <nes20db> element.");

        foreach (var row in db.Rows.OrderBy(r => r.RelPath, StringComparer.OrdinalIgnoreCase))
        {
            var pieces = SplitLayout.Pieces(row.Entry);
            if (pieces.Count == 0) continue;
            StartGame(w, row);
            var stem = Path.GetFileNameWithoutExtension(row.FileName);
            foreach (var p in pieces)
            {
                w.WriteStartElement("rom");
                w.WriteAttributeString("name", stem + p.Extension);
                w.WriteAttributeString("size", p.Size.ToString());
                w.WriteAttributeString("crc", p.Crc.ToLowerInvariant());
                w.WriteAttributeString("sha1", p.Sha1.ToLowerInvariant());
                w.WriteEndElement();
            }
            WriteNes20db(w, row.Entry);
            w.WriteEndElement(); // game
            games++;
        }

        Finish(w);
        return games;
    }

    private static XmlWriter Create(string path)
    {
        var w = XmlWriter.Create(path, new XmlWriterSettings { Indent = true, IndentChars = "\t", Encoding = new UTF8Encoding(false) });
        w.WriteStartDocument();
        w.WriteStartElement("datafile");
        return w;
    }

    private static void Finish(XmlWriter w)
    {
        w.WriteEndElement();
        w.WriteEndDocument();
    }

    private static void WriteHeader(XmlWriter w, NesDb db, string name, string author, string comment)
    {
        w.WriteStartElement("header");
        w.WriteElementString("name", name);
        w.WriteElementString("description", $"{name} (nes20db {db.Date})");
        w.WriteElementString("version", db.Date);
        w.WriteElementString("date", DateTime.Now.ToString("yyyy-MM-dd"));
        w.WriteElementString("author", author);
        w.WriteElementString("comment", comment);
        // No <romvault forcepacking>: RomVault's default (zip per game) keeps the folder layout.
        w.WriteStartElement("nes20db");
        foreach (var a in db.RootAttributes) w.WriteAttributeString(a.Key, a.Value);
        foreach (var c in db.TrailingComments) w.WriteComment(" " + c + " ");
        w.WriteEndElement();
        w.WriteEndElement(); // header
    }

    private static void StartGame(XmlWriter w, DbRow row)
    {
        var rel = row.RelPath;
        w.WriteStartElement("game");
        w.WriteAttributeString("name", rel.EndsWith(".nes", StringComparison.OrdinalIgnoreCase) ? rel[..^4] : rel);
        w.WriteElementString("description", Path.GetFileNameWithoutExtension(row.FileName));
    }

    /// <summary>Everything from the original &lt;game&gt;, verbatim, including the headerless &lt;rom&gt;.</summary>
    private static void WriteNes20db(XmlWriter w, DbEntry e)
    {
        w.WriteStartElement("nes20db");
        w.WriteComment(" " + e.Comment + " ");
        foreach (var el in e.Elements)
        {
            w.WriteStartElement(el.Name);
            foreach (var a in el.Attributes) w.WriteAttributeString(a.Key, a.Value);
            w.WriteEndElement();
        }
        w.WriteEndElement();
    }
}
