using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SharpCompress.Archives;

namespace NRSVaultMini.Core;

/// <summary>A ROM candidate found in a source folder: a loose file, or one entry inside an archive.</summary>
public sealed class SourceRecord
{
    [System.Text.Json.Serialization.JsonIgnore] public string Path { get; set; } = "";
    /// <summary>Entry name inside an archive; null for a loose file.</summary>
    public string? EntryKey { get; set; }
    public FileKind Kind { get; set; }
    public string? HeaderHex { get; set; }
    public long PayloadSize { get; set; }
    public string PayloadSha1 { get; set; } = "";

    public string Display => EntryKey == null ? Path : Path + " » " + EntryKey;
    public string KindText => (EntryKey != null ? "in archive, " : "") + Kind switch
    {
        FileKind.INes => "headered",
        FileKind.Unif => "UNIF",
        _ => "headerless",
    };
}

/// <summary>Per-file cache item. Records may be empty: "looked at, nothing of the right size".</summary>
public sealed class SourceCacheItem
{
    public long Size { get; set; }
    public long MTimeTicks { get; set; }
    public string Stamp { get; set; } = "";
    public List<SourceRecord> Records { get; set; } = new();
}

public sealed class SourceScanResult
{
    public List<SourceRecord> Records { get; } = new();
    public int FilesSeen, FilesRead, ArchivesRead, Errors;
    public List<string> ErrorMessages { get; } = new();
}

/// <summary>Recursively searches extra folders (read-only) for ROMs whose data matches a nes20db entry.
/// Only files whose size can match a database ROM (raw, or +16 for an iNES header) are read.</summary>
public static class SourceScanner
{
    private static readonly string[] UnifExt = { ".unf", ".unif" };

    /// <summary>Every ROM payload size in the database; files of other sizes are never read.</summary>
    public static HashSet<long> DbSizes(NesDb db) => db.Entries.Select(e => e.RomSize).Where(s => s > 0).ToHashSet();

    private static string Stamp(HashSet<long> sizes) =>
        Convert.ToHexString(SHA1.HashData(Encoding.ASCII.GetBytes(string.Join(",", sizes.Order()))))[..12];

    private static string CachePathFor(string folder)
    {
        var key = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(folder).TrimEnd('\\').ToLowerInvariant())))[..12];
        return Path.Combine(Scanner.CacheDir, $"source_cache_{key}.json");
    }

    private static Dictionary<string, SourceCacheItem> LoadCache(string folder)
    {
        try
        {
            var p = CachePathFor(folder);
            if (File.Exists(p))
                return JsonSerializer.Deserialize<Dictionary<string, SourceCacheItem>>(File.ReadAllText(p))
                       ?? new();
        }
        catch { /* rebuild cache */ }
        return new();
    }

    private static void SaveCache(string folder, IDictionary<string, SourceCacheItem> cache)
    {
        Directory.CreateDirectory(Scanner.CacheDir);
        var p = CachePathFor(folder);
        File.WriteAllText(p + ".tmp", JsonSerializer.Serialize(new SortedDictionary<string, SourceCacheItem>(cache, StringComparer.OrdinalIgnoreCase)));
        File.Move(p + ".tmp", p, true);
    }

    private static bool SizeCandidate(long size, HashSet<long> sizes) =>
        sizes.Contains(size) || sizes.Contains(size - NesHeader.Size);

    public static SourceScanResult Scan(IEnumerable<string> folders, NesDb db, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var sizes = DbSizes(db);
        var stamp = Stamp(sizes);
        var result = new SourceScanResult();

        foreach (var folderRaw in folders)
        {
            ct.ThrowIfCancellationRequested();
            var folder = Path.GetFullPath(folderRaw).TrimEnd('\\');
            if (!Directory.Exists(folder))
            {
                result.Errors++;
                result.ErrorMessages.Add("Source folder not found: " + folder);
                continue;
            }

            progress?.Report(new ScanProgress(0, 0, "Listing " + folder));
            var files = new List<FileInfo>();
            var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System };
            foreach (var f in new DirectoryInfo(folder).EnumerateFiles("*", opts))
            {
                ct.ThrowIfCancellationRequested();
                files.Add(f);
                if (files.Count % 5000 == 0) progress?.Report(new ScanProgress(0, 0, $"Listing {folder}: {files.Count:N0} files"));
            }

            var old = LoadCache(folder);
            var cache = new ConcurrentDictionary<string, SourceCacheItem>(StringComparer.OrdinalIgnoreCase);
            var bag = new ConcurrentBag<SourceRecord>();
            int done = 0, sinceSave = 0;
            object saveLock = new();

            try
            {
                Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, fi =>
                {
                    var rel = fi.FullName[(folder.Length + 1)..];
                    SourceCacheItem? item = null;
                    try
                    {
                        long len = fi.Length, mt = fi.LastWriteTimeUtc.Ticks;
                        if (old.TryGetValue(rel, out var c) && c.Size == len && c.MTimeTicks == mt && c.Stamp == stamp)
                            item = c;
                        else
                        {
                            item = new SourceCacheItem { Size = len, MTimeTicks = mt, Stamp = stamp };
                            if (RomFile.IsArchive(fi.FullName))
                            {
                                item.Records.AddRange(ReadArchive(fi.FullName, sizes, ct));
                                Interlocked.Increment(ref result.ArchivesRead);
                            }
                            else if (SizeCandidate(len, sizes) || UnifExt.Contains(fi.Extension, StringComparer.OrdinalIgnoreCase))
                            {
                                if (Analyze(fi.FullName, null, File.ReadAllBytes(fi.FullName), sizes) is { } r)
                                    item.Records.Add(r);
                                Interlocked.Increment(ref result.FilesRead);
                            }
                        }
                        foreach (var r in item.Records)
                        {
                            r.Path = fi.FullName;
                            bag.Add(r);
                        }
                        cache[rel] = item;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref result.Errors);
                        lock (result.ErrorMessages)
                            if (result.ErrorMessages.Count < 500) result.ErrorMessages.Add($"{fi.FullName}: {ex.Message}");
                    }

                    int n = Interlocked.Increment(ref done);
                    if (n % 200 == 0 || n == files.Count) progress?.Report(new ScanProgress(n, files.Count, fi.FullName));
                    if (Interlocked.Increment(ref sinceSave) >= 5000)
                    {
                        lock (saveLock)
                        {
                            if (sinceSave >= 5000) { Interlocked.Exchange(ref sinceSave, 0); SaveMerged(folder, old, cache); }
                        }
                    }
                });
            }
            finally
            {
                // Keep progress even when cancelled: unchanged old items + everything read this time.
                SaveMerged(folder, old, cache);
            }

            result.FilesSeen += files.Count;
            result.Records.AddRange(bag);
        }
        return result;
    }

    private static void SaveMerged(string folder, Dictionary<string, SourceCacheItem> old, ConcurrentDictionary<string, SourceCacheItem> fresh)
    {
        var merged = new Dictionary<string, SourceCacheItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in old) merged[kv.Key] = kv.Value;
        foreach (var kv in fresh) merged[kv.Key] = kv.Value;
        try { SaveCache(folder, merged); } catch { /* cache is a convenience */ }
    }

    private static SourceRecord? Analyze(string path, string? entryKey, byte[] data, HashSet<long> sizes)
    {
        var (kind, header, payload) = RomFile.Split(data);
        if (!sizes.Contains(payload.Length)) return null;
        return new SourceRecord
        {
            Path = path,
            EntryKey = entryKey,
            Kind = kind,
            HeaderHex = header == null ? null : Convert.ToHexString(header),
            PayloadSize = payload.Length,
            PayloadSha1 = Convert.ToHexString(SHA1.HashData(payload)),
        };
    }

    /// <summary>Calls <paramref name="visit"/> for each file entry; it returns true to have the entry read.
    /// Solid archives and 7z are read sequentially (one pass); zip and friends entry by entry.</summary>
    private static void ForEachEntry(string path, Func<string, long, bool> wanted, Action<string, byte[]> got, CancellationToken ct)
    {
        using var archive = ArchiveFactory.OpenArchive(path, null);
        if (archive.IsSolid || archive.Type == SharpCompress.Common.ArchiveType.SevenZip)
        {
            using var reader = archive.ExtractAllEntries();
            while (reader.MoveToNextEntry())
            {
                ct.ThrowIfCancellationRequested();
                var e = reader.Entry;
                if (e.IsDirectory || !wanted(e.Key ?? "", e.Size)) continue;
                using var ms = new MemoryStream();
                reader.WriteEntryTo(ms);
                got(e.Key ?? "", ms.ToArray());
            }
            return;
        }
        foreach (var e in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();
            if (e.IsDirectory || !wanted(e.Key ?? "", e.Size)) continue;
            using var es = e.OpenEntryStream();
            using var ms = new MemoryStream();
            es.CopyTo(ms);
            got(e.Key ?? "", ms.ToArray());
        }
    }

    private static List<SourceRecord> ReadArchive(string path, HashSet<long> sizes, CancellationToken ct)
    {
        var list = new List<SourceRecord>();
        ForEachEntry(path,
            (key, size) => size <= 512L * 1024 * 1024 &&
                           (SizeCandidate(size, sizes) || UnifExt.Contains(System.IO.Path.GetExtension(key), StringComparer.OrdinalIgnoreCase)),
            (key, data) => { if (Analyze(path, key, data, sizes) is { } r) list.Add(r); },
            ct);
        return list;
    }

    /// <summary>Reads the bare ROM data for a source record (from the loose file or out of its archive).</summary>
    public static byte[] ReadPayload(SourceRecord rec)
    {
        if (rec.EntryKey == null) return RomFile.ReadPayload(rec.Path);
        byte[]? found = null;
        ForEachEntry(rec.Path, (key, _) => found == null && key == rec.EntryKey, (_, data) => found = data, default);
        return found != null
            ? RomFile.Split(found).payload
            : throw new FileNotFoundException($"Entry {rec.EntryKey} no longer in {rec.Path}");
    }
}
