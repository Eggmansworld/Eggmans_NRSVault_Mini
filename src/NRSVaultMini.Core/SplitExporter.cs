using System.Security.Cryptography;

namespace NRSVaultMini.Core;

/// <summary>One cartridge chip image inside the ROM data, with the hashes nes20db.xml lists for it.</summary>
public sealed record RomPiece(string Kind, string Extension, long Offset, long Size, string Crc, string Sha1);

/// <summary>Where PRG and CHR sit inside the headerless ROM data. File order is
/// trainer, PRG-ROM, CHR-ROM, misc ROM, footer (whatever order the XML lists them in).</summary>
public static class SplitLayout
{
    public static List<RomPiece> Pieces(DbEntry e)
    {
        var list = new List<RomPiece>();
        var prg = e.Elements.LastOrDefault(x => x.Name == "prgrom");
        var chr = e.Elements.LastOrDefault(x => x.Name == "chrrom");
        long offset = e.Spec.Trainer;
        if (prg != null)
        {
            long size = long.Parse(prg.Get("size") ?? "0");
            if (size > 0) list.Add(new RomPiece("PRG", ".prg", offset, size, (prg.Get("crc32") ?? "").ToUpperInvariant(), (prg.Get("sha1") ?? "").ToUpperInvariant()));
            offset += size;
        }
        if (chr != null)
        {
            long size = long.Parse(chr.Get("size") ?? "0");
            if (size > 0) list.Add(new RomPiece("CHR", ".chr", offset, size, (chr.Get("crc32") ?? "").ToUpperInvariant(), (chr.Get("sha1") ?? "").ToUpperInvariant()));
        }
        return list;
    }
}

public sealed class SplitExportResult
{
    public int Written, AlreadyThere, NoSource, Failed, Rows;
    public List<string> Errors { get; } = new();
}

/// <summary>Writes the split set (&lt;name&gt;.prg / &lt;name&gt;.chr in the nes20db folder layout) into a separate
/// folder, cut from whatever copy of each ROM is available: the collection itself or a source folder.</summary>
public static class SplitExporter
{
    public static SplitExportResult Export(CollectionState state, string root, string outDir, bool useSources, bool trialRun,
                                           Action<string> log, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        root = Path.GetFullPath(root).TrimEnd('\\');
        outDir = Path.GetFullPath(outDir).TrimEnd('\\');
        if ((outDir + "\\").StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Choose a split folder outside the collection folder, or the collection scan would treat the split files as unknown.");

        var res = new SplitExportResult();
        // Any file in the collection with the entry's exact ROM data can be cut up, whatever its header or location.
        var inCollection = new Dictionary<DbEntry, FileInfoRecord>();
        foreach (var f in state.Files)
            if (state.MatchFull(f) is { } e) inCollection.TryAdd(e, f);

        var groups = state.Rows.GroupBy(r => r.Row.Entry).ToList();
        int done = 0;
        foreach (var g in groups)
        {
            ct.ThrowIfCancellationRequested();
            var entry = g.Key;
            var pieces = SplitLayout.Pieces(entry);
            res.Rows += g.Count();
            if (++done % 50 == 0 || done == groups.Count) progress?.Report(new ScanProgress(done, groups.Count, g.First().Row.RelPath));
            if (pieces.Count == 0) continue;

            inCollection.TryGetValue(entry, out var file);
            var ext = useSources && state.SourceMatches.TryGetValue(entry, out var sm) ? sm.Best : null;
            if (file == null && ext == null)
            {
                res.NoSource += g.Count();
                continue;
            }

            try
            {
                var targets = g.SelectMany(r => pieces.Select(p => (p, path: Path.Combine(outDir, r.Row.Folder,
                                  Path.GetFileNameWithoutExtension(r.Row.FileName) + p.Extension)))).ToList();
                var todo = targets.Where(t => !AlreadyThere(t.path, t.p)).ToList();
                res.AlreadyThere += targets.Count - todo.Count;
                if (todo.Count == 0) continue;

                string from = file != null ? file.RelPath : "[source] " + ext!.Display;
                if (trialRun)
                {
                    foreach (var t in todo) log($"TRIAL  SPLIT  {Path.GetRelativePath(outDir, t.path)}  ←  {from}");
                    res.Written += todo.Count;
                    continue;
                }

                byte[] payload = file != null ? RomFile.ReadPayload(Path.Combine(root, file.RelPath)) : SourceScanner.ReadPayload(ext!);
                foreach (var t in todo)
                {
                    if (t.p.Offset + t.p.Size > payload.Length)
                        throw new InvalidDataException($"{t.p.Kind} at {t.p.Offset}+{t.p.Size} is past the end of the ROM data");
                    var slice = payload.AsSpan((int)t.p.Offset, (int)t.p.Size);
                    var sha = Convert.ToHexString(SHA1.HashData(slice));
                    if (!sha.Equals(t.p.Sha1, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException($"{t.p.Kind} SHA1 {sha} does not match nes20db {t.p.Sha1}");
                    Directory.CreateDirectory(Path.GetDirectoryName(t.path)!);
                    var tmp = t.path + Scanner.TempSuffix;
                    File.WriteAllBytes(tmp, slice.ToArray());
                    File.Move(tmp, t.path, true);
                    res.Written++;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                res.Failed++;
                res.Errors.Add($"{g.First().Row.RelPath}: {ex.Message}");
                log($"ERROR split {g.First().Row.RelPath}: {ex.Message}");
            }
        }
        return res;
    }

    private static bool AlreadyThere(string path, RomPiece p)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists || fi.Length != p.Size) return false;
            return Convert.ToHexString(SHA1.HashData(File.ReadAllBytes(path))).Equals(p.Sha1, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
}
