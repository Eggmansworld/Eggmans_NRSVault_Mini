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

/// <summary>Writes a RomVault-compatible Logiqx-style dat of the headered collection. Every value from
/// nes20db.xml is carried along inside a &lt;nes20db&gt; element per game, which RomVault ignores.</summary>
public static class DatWriter
{
    public static DatResult Write(string path, CollectionState state, DatOptions opt, string appVersion)
    {
        var db = state.Db;
        int verified = 0, unverified = 0, skipped = 0;
        var settings = new XmlWriterSettings { Indent = true, IndentChars = "\t", Encoding = new UTF8Encoding(false) };
        using var w = XmlWriter.Create(path, settings);

        w.WriteStartDocument();
        w.WriteStartElement("datafile");

        w.WriteStartElement("header");
        w.WriteElementString("name", opt.Name);
        w.WriteElementString("description", $"{opt.Name} (nes20db {db.Date})");
        w.WriteElementString("version", db.Date);
        w.WriteElementString("date", DateTime.Now.ToString("yyyy-MM-dd"));
        w.WriteElementString("author", opt.Author);
        w.WriteElementString("comment",
            $"Built from nes20db.xml dated {db.Date} ({db.Entries.Count} entries, {db.Rows.Count} files) by NRSVault Mini {appVersion}. " +
            "ROM sizes/hashes are for the files WITH their NES 2.0 header. Original nes20db values are kept in each game's <nes20db> element.");
        // No <romvault forcepacking>: RomVault's default (zip per game) keeps the folder layout,
        // and emulators run headered ROMs from zips fine.
        w.WriteStartElement("nes20db");
        foreach (var a in db.RootAttributes) w.WriteAttributeString(a.Key, a.Value);
        foreach (var c in db.TrailingComments) w.WriteComment(" " + c + " ");
        w.WriteEndElement();
        w.WriteEndElement(); // header

        foreach (var rs in state.Rows.OrderBy(r => r.Row.RelPath, StringComparer.OrdinalIgnoreCase))
        {
            bool ok = rs.Status == RowStatus.Ok && rs.File != null;
            if (!ok && !opt.IncludeUnverified) { skipped++; continue; }
            var e = rs.Row.Entry;
            var rel = rs.Row.RelPath;
            var fileName = rs.Row.FileName;
            var gameName = rel.EndsWith(".nes", StringComparison.OrdinalIgnoreCase) ? rel[..^4] : rel;

            w.WriteStartElement("game");
            w.WriteAttributeString("name", gameName);
            w.WriteElementString("description", Path.GetFileNameWithoutExtension(fileName));

            w.WriteStartElement("rom");
            w.WriteAttributeString("name", fileName);
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

            // Everything from the original <game>, verbatim, including the headerless <rom>.
            w.WriteStartElement("nes20db");
            w.WriteComment(" " + e.Comment + " ");
            foreach (var el in e.Elements)
            {
                w.WriteStartElement(el.Name);
                foreach (var a in el.Attributes) w.WriteAttributeString(a.Key, a.Value);
                w.WriteEndElement();
            }
            w.WriteEndElement();

            w.WriteEndElement(); // game
        }

        w.WriteEndElement();
        w.WriteEndDocument();
        return new DatResult(verified, unverified, skipped);
    }
}
