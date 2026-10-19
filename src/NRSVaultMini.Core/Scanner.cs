using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NRSVaultMini.Core;

public sealed record ScanProgress(int Done, int Total, string Current);

/// <summary>Walks the collection folder and analyzes every file, reusing cached hashes
/// for files whose size and modification time are unchanged.</summary>
public static class Scanner
{
    public const string UnknownFolder = "_Unknown";
    public const string DuplicatesFolder = "_Duplicates";
    public const string TempSuffix = ".nrstmp";

    private static readonly HashSet<string> IgnoredNames = new(StringComparer.OrdinalIgnoreCase)
        { "desktop.ini", "thumbs.db", ".ds_store" };

    public static string CacheDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NRSVaultMini");

    public static string CachePathFor(string root)
    {
        var key = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(root).TrimEnd('\\').ToLowerInvariant())))[..12];
        return Path.Combine(CacheDir, $"scan_cache_{key}.json");
    }

    public static Dictionary<string, FileInfoRecord> LoadCache(string root)
    {
        try
        {
            var path = CachePathFor(root);
            if (!File.Exists(path)) return new(StringComparer.OrdinalIgnoreCase);
            var list = JsonSerializer.Deserialize<List<FileInfoRecord>>(File.ReadAllText(path)) ?? new();
            return list.Where(r => r.Kind != FileKind.Error)
                       .GroupBy(r => r.RelPath, StringComparer.OrdinalIgnoreCase)
                       .ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }

    public static void SaveCache(string root, IEnumerable<FileInfoRecord> records)
    {
        Directory.CreateDirectory(CacheDir);
        var tmp = CachePathFor(root) + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(records.ToList()));
        File.Move(tmp, CachePathFor(root), true);
    }

    /// <param name="excludeDirs">Folders inside root to skip (backup folder, source folders).</param>
    public static List<FileInfoRecord> Scan(string root, IEnumerable<string>? excludeDirs, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        root = Path.GetFullPath(root).TrimEnd('\\');
        var excludes = (excludeDirs ?? Array.Empty<string>()).Where(d => !string.IsNullOrWhiteSpace(d))
            .Select(d => Path.GetFullPath(d).TrimEnd('\\') + "\\").ToList();

        var files = new List<string>();
        var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System };
        foreach (var f in Directory.EnumerateFiles(root, "*", opts))
        {
            ct.ThrowIfCancellationRequested();
            if (excludes.Any(x => f.StartsWith(x, StringComparison.OrdinalIgnoreCase))) continue;
            var name = Path.GetFileName(f);
            if (IgnoredNames.Contains(name) || name.EndsWith(TempSuffix, StringComparison.OrdinalIgnoreCase)) continue;
            files.Add(f);
        }

        var cache = LoadCache(root);
        var results = new ConcurrentBag<FileInfoRecord>();
        int done = 0;
        progress?.Report(new ScanProgress(0, files.Count, "Scanning..."));

        Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, full =>
        {
            var rel = full[(root.Length + 1)..];
            FileInfoRecord rec;
            try
            {
                var fi = new FileInfo(full);
                if (cache.TryGetValue(rel, out var cached) && cached.Size == fi.Length &&
                    cached.MTimeTicks == fi.LastWriteTimeUtc.Ticks)
                {
                    cached.RelPath = rel; // keep on-disk case
                    rec = cached;
                }
                else
                {
                    rec = RomFile.Analyze(full, rel);
                }
            }
            catch (Exception ex)
            {
                rec = new FileInfoRecord { RelPath = rel, Kind = FileKind.Error, Error = ex.Message };
            }
            results.Add(rec);
            int n = Interlocked.Increment(ref done);
            if (n % 50 == 0 || n == files.Count) progress?.Report(new ScanProgress(n, files.Count, rel));
        });

        var list = results.OrderBy(r => r.RelPath, StringComparer.OrdinalIgnoreCase).ToList();
        SaveCache(root, list);
        return list;
    }

    public static bool IsInSpecialFolder(string relPath) =>
        relPath.StartsWith(UnknownFolder + "\\", StringComparison.OrdinalIgnoreCase) ||
        relPath.StartsWith(DuplicatesFolder + "\\", StringComparison.OrdinalIgnoreCase);
}
