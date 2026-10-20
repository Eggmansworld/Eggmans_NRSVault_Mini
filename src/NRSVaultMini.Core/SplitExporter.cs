using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

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
    /// <summary>Counted per chip file (.prg or .chr), not per ROM.</summary>
    public int Written, AlreadyThere, Missing, Failed;
    /// <summary>Chips that came from something other than the entry's own ROM (another entry, an unknown ROM, a loose chip file, the split folder).</summary>
    public int FromElsewhere;
    public int PathsComplete, PathsPartial, PathsEmpty;
    public List<string> Errors { get; } = new();
}

/// <summary>Writes the split set (&lt;name&gt;.prg / &lt;name&gt;.chr in the nes20db folder layout) into a separate folder.
/// Works chip by chip: each PRG/CHR is taken from any verified copy of those exact bytes — the entry's own ROM,
/// another entry's ROM that shares the chip, a ROM that is not in nes20db, a loose chip file, or a chip already
/// in the split folder — in the collection or (optionally) the source folders.</summary>
public static class SplitExporter
{
    /// <summary>Where a chip's bytes can be read from.</summary>
    private sealed record Provider(int Rank, string Key, string Display, FileInfoRecord? File, SourceRecord? Src, string? OutFile, long Offset, bool OwnRom);

    private sealed record Target(DbRow Row, RomPiece Piece, string Path);

    public static SplitExportResult Export(CollectionState state, string root, string outDir, bool useSources, bool trialRun,
                                           Action<string> log, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        root = Path.GetFullPath(root).TrimEnd('\\');
        outDir = Path.GetFullPath(outDir).TrimEnd('\\');
        if ((outDir + "\\").StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Choose a split folder outside the collection folder, or the collection scan would treat the split files as unknown.");

        var res = new SplitExportResult();
        var db = state.Db;

        // ---- Split folder contents (cached SHA1 per file, so re-runs do not re-hash everything) ----
        progress?.Report(new ScanProgress(0, 0, "Checking the split folder..."));
        var outHashes = SplitFolderCache.Hash(outDir, ct);

        // ---- Every place each chip can be read from ----
        var providers = new Dictionary<string, List<Provider>>(StringComparer.OrdinalIgnoreCase);
        void Add(string sha1, Provider p) =>
            (providers.TryGetValue(sha1, out var l) ? l : providers[sha1] = new()).Add(p);

        foreach (var f in state.Files)
        {
            if (f.Kind is FileKind.Archive or FileKind.Error) continue;
            if (state.MatchFull(f) is { } e)
                foreach (var p in SplitLayout.Pieces(e))
                    Add(p.Sha1, new Provider(0, "c:" + f.RelPath, f.RelPath, f, null, null, p.Offset, false));
            foreach (var h in f.Chips ?? new())
                Add(h.Sha1, new Provider(1, "c:" + f.RelPath, f.RelPath, f, null, null, h.Offset, false));
        }
        if (useSources && state.SourceScan != null)
            foreach (var r in state.SourceScan.Records)
            {
                if (db.BySha1.TryGetValue(r.PayloadSha1, out var e))
                    foreach (var p in SplitLayout.Pieces(e))
                        Add(p.Sha1, new Provider(2, "s:" + r.Display, "[source] " + r.Display, null, r, null, p.Offset, false));
                foreach (var h in r.Chips ?? new())
                    Add(h.Sha1, new Provider(3, "s:" + r.Display, "[source] " + r.Display, null, r, null, h.Offset, false));
            }
        foreach (var (rel, sha) in outHashes)
            Add(sha, new Provider(4, "o:" + rel, "[split folder] " + rel, null, null, Path.Combine(outDir, rel), 0, false));

        // ---- What has to be written ----
        var todo = new List<(Target t, Provider p)>();
        var rowHave = new Dictionary<DbRow, (int have, int total)>();
        int i = 0;
        foreach (var row in db.Rows)
        {
            ct.ThrowIfCancellationRequested();
            if (++i % 500 == 0) progress?.Report(new ScanProgress(i, db.Rows.Count, "Planning " + row.RelPath));
            var pieces = SplitLayout.Pieces(row.Entry);
            int have = 0;
            foreach (var piece in pieces)
            {
                var rel = Path.Combine(row.Folder, Path.GetFileNameWithoutExtension(row.FileName) + piece.Extension);
                if (outHashes.TryGetValue(rel, out var existing) && existing.Equals(piece.Sha1, StringComparison.OrdinalIgnoreCase))
                {
                    res.AlreadyThere++;
                    have++;
                    continue;
                }
                if (!providers.TryGetValue(piece.Sha1, out var list) || list.Count == 0)
                {
                    res.Missing++;
                    continue;
                }
                // The entry's own ROM first, then the cheapest other copy.
                var own = list.FirstOrDefault(p => p.Rank is 0 or 2 && OwnsRow(state, p, row.Entry));
                var best = own != null ? own with { OwnRom = true } : list.OrderBy(p => p.Rank).First();
                todo.Add((new Target(row, piece, Path.Combine(outDir, rel)), best));
                have++;
            }
            rowHave[row] = (have, pieces.Count);
        }
        foreach (var (have, total) in rowHave.Values)
        {
            if (total > 0 && have == total) res.PathsComplete++;
            else if (have > 0) res.PathsPartial++;
            else res.PathsEmpty++;
        }

        // ---- Write, reading each provider file once ----
        var groups = todo.GroupBy(x => x.p.Key).ToList();
        int done = 0;
        foreach (var g in groups)
        {
            ct.ThrowIfCancellationRequested();
            if (++done % 20 == 0 || done == groups.Count) progress?.Report(new ScanProgress(done, groups.Count, g.First().p.Display));
            var first = g.First().p;
            if (trialRun)
            {
                foreach (var (t, p) in g)
                {
                    log($"TRIAL  SPLIT  {Path.GetRelativePath(outDir, t.Path)}  ←  {p.Display}{(p.OwnRom ? "" : "  (chip shared/found elsewhere)")}");
                    res.Written++;
                    if (!p.OwnRom) res.FromElsewhere++;
                }
                continue;
            }

            byte[] data;
            try
            {
                data = first.OutFile != null ? File.ReadAllBytes(first.OutFile)
                     : first.File != null ? RomFile.ReadPayload(Path.Combine(root, first.File.RelPath))
                     : SourceScanner.ReadPayload(first.Src!);
            }
            catch (Exception ex)
            {
                res.Failed += g.Count();
                res.Errors.Add($"{first.Display}: {ex.Message}");
                log($"ERROR reading {first.Display}: {ex.Message}");
                continue;
            }

            foreach (var (t, p) in g)
            {
                try
                {
                    if (p.Offset + t.Piece.Size > data.Length)
                        throw new InvalidDataException($"{t.Piece.Kind} at {p.Offset}+{t.Piece.Size} is past the end of the data");
                    var slice = data.AsSpan((int)p.Offset, (int)t.Piece.Size);
                    var sha = Convert.ToHexString(SHA1.HashData(slice));
                    if (!sha.Equals(t.Piece.Sha1, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException($"{t.Piece.Kind} SHA1 {sha} does not match nes20db {t.Piece.Sha1}");
                    Directory.CreateDirectory(Path.GetDirectoryName(t.Path)!);
                    var tmp = t.Path + Scanner.TempSuffix;
                    File.WriteAllBytes(tmp, slice.ToArray());
                    File.Move(tmp, t.Path, true);
                    SplitFolderCache.Remember(outDir, Path.GetRelativePath(outDir, t.Path), t.Piece.Sha1);
                    res.Written++;
                    if (!p.OwnRom)
                    {
                        res.FromElsewhere++;
                        log($"SPLIT  {Path.GetRelativePath(outDir, t.Path)}  ←  {p.Display}  (chip shared/found elsewhere)");
                    }
                }
                catch (Exception ex)
                {
                    res.Failed++;
                    res.Errors.Add($"{t.Row.RelPath} {t.Piece.Kind}: {ex.Message}");
                    log($"ERROR split {t.Row.RelPath} {t.Piece.Kind}: {ex.Message}");
                }
            }
        }
        if (!trialRun) SplitFolderCache.Save(outDir);
        return res;
    }

    private static bool OwnsRow(CollectionState state, Provider p, DbEntry entry) =>
        p.File != null ? state.MatchFull(p.File) == entry
        : p.Src != null && state.Db.BySha1.TryGetValue(p.Src.PayloadSha1, out var e) && e == entry;
}

/// <summary>Remembers the SHA1 of every .prg/.chr in a split folder (by size + modified time).</summary>
internal static class SplitFolderCache
{
    private sealed class Item
    {
        public long Size { get; set; }
        public long MTimeTicks { get; set; }
        public string Sha1 { get; set; } = "";
    }

    private static readonly Dictionary<string, Dictionary<string, Item>> Loaded = new(StringComparer.OrdinalIgnoreCase);

    private static string CachePath(string outDir) =>
        Path.Combine(Scanner.CacheDir, "split_cache_" +
            Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(outDir.ToLowerInvariant())))[..12] + ".json");

    private static Dictionary<string, Item> Get(string outDir)
    {
        lock (Loaded)
        {
            if (Loaded.TryGetValue(outDir, out var c)) return c;
            try
            {
                var p = CachePath(outDir);
                c = File.Exists(p) ? JsonSerializer.Deserialize<Dictionary<string, Item>>(File.ReadAllText(p)) : null;
            }
            catch { c = null; }
            c = new Dictionary<string, Item>(c ?? new(), StringComparer.OrdinalIgnoreCase);
            Loaded[outDir] = c;
            return c;
        }
    }

    /// <summary>Returns relative path → SHA1 for every .prg/.chr in the folder, hashing only new or changed files.</summary>
    public static Dictionary<string, string> Hash(string outDir, CancellationToken ct)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(outDir)) return result;
        var cache = Get(outDir);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        foreach (var fi in new DirectoryInfo(outDir).EnumerateFiles("*", opts))
        {
            ct.ThrowIfCancellationRequested();
            var ext = fi.Extension.ToLowerInvariant();
            if (ext is not (".prg" or ".chr")) continue;
            var rel = fi.FullName[(outDir.Length + 1)..];
            seen.Add(rel);
            long mt = fi.LastWriteTimeUtc.Ticks;
            if (!cache.TryGetValue(rel, out var it) || it.Size != fi.Length || it.MTimeTicks != mt)
            {
                it = new Item { Size = fi.Length, MTimeTicks = mt, Sha1 = Convert.ToHexString(SHA1.HashData(File.ReadAllBytes(fi.FullName))) };
                cache[rel] = it;
            }
            result[rel] = it.Sha1;
        }
        foreach (var gone in cache.Keys.Where(k => !seen.Contains(k)).ToList()) cache.Remove(gone);
        Save(outDir);
        return result;
    }

    public static void Remember(string outDir, string rel, string sha1)
    {
        var fi = new FileInfo(Path.Combine(outDir, rel));
        Get(outDir)[rel] = new Item { Size = fi.Length, MTimeTicks = fi.LastWriteTimeUtc.Ticks, Sha1 = sha1 };
    }

    public static void Save(string outDir)
    {
        try
        {
            Directory.CreateDirectory(Scanner.CacheDir);
            var p = CachePath(outDir);
            File.WriteAllText(p + ".tmp", JsonSerializer.Serialize(Get(outDir)));
            File.Move(p + ".tmp", p, true);
        }
        catch { /* cache is a convenience */ }
    }
}
