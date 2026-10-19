using System.Security.Cryptography;
using SharpCompress.Archives;

namespace NRSVaultMini.Core;

public enum ActionType { Extract, MoveAside, Write, MoveToDuplicates, MoveToUnknown, RemoveEmptyDir }

public sealed class PlanAction
{
    public ActionType Type { get; init; }
    public string Source { get; init; } = "";
    public string Target { get; init; } = "";
    public DbEntry? Entry { get; init; }
    /// <summary>For Write: delete the source after the target is written and verified (source was surplus).</summary>
    public bool ConsumeSource { get; init; }
    /// <summary>For Write: read the ROM from a source folder (read-only) instead of the collection.</summary>
    public SourceRecord? External { get; init; }
    public string Reason { get; init; } = "";

    public override string ToString() => Type switch
    {
        ActionType.Extract => $"EXTRACT  {Source}",
        ActionType.MoveAside => $"MOVE     {Source}  →  {Target}  ({Reason})",
        ActionType.Write => External != null
            ? $"WRITE    {Target}  ←  [source] {External.Display}  ({External.KindText})"
            : $"WRITE    {Target}  ←  {Source}{(ConsumeSource ? " (source moved)" : "")}  ({Reason})",
        ActionType.MoveToDuplicates => $"DUPE     {Source}  →  {Target}",
        ActionType.MoveToUnknown => $"UNKNOWN  {Source}  →  {Target}",
        ActionType.RemoveEmptyDir => $"RMDIR    {Source}",
        _ => Type.ToString(),
    };
}

public sealed class FixOptions
{
    public bool TrialRun { get; set; } = true;
    public bool Rebuild { get; set; } = true;          // move/copy found ROMs into the XML folder structure
    public bool MoveUnknown { get; set; } = true;       // move unknown files to _Unknown
    public bool MoveDuplicates { get; set; } = true;    // move surplus copies to _Duplicates
    public bool RemoveEmptyFolders { get; set; } = true;
    public bool UseSources { get; set; } = true;        // fill missing ROMs from scanned source folders
    public bool BackupEnabled { get; set; }
    public string BackupFolder { get; set; } = "";
}

public sealed class FixResult
{
    public int Written, Moved, Extracted, Failed, DirsRemoved;
    public List<string> Errors { get; } = new();
}

/// <summary>Plans and performs header fixes, rebuild-to-XML-structure moves and clean-up.
/// Everything is planned first (a pure simulation); TRIAL_RUN only logs the plan.</summary>
public sealed class Fixer
{
    private readonly string _root;
    private readonly FixOptions _opt;
    private readonly Action<string> _log;

    public Fixer(string root, FixOptions options, Action<string> log)
    {
        _root = Path.GetFullPath(root).TrimEnd('\\');
        _opt = options;
        _log = log;
    }

    private string Full(string rel)
    {
        var full = Path.GetFullPath(Path.Combine(_root, rel));
        if (!full.StartsWith(_root + "\\", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Path escapes the collection folder: " + rel);
        return full;
    }

    // ---------------- Archives ----------------

    public List<PlanAction> PlanExtraction(CollectionState state) =>
        state.Files.Where(f => f.Kind == FileKind.Archive)
             .Select(f => new PlanAction { Type = ActionType.Extract, Source = f.RelPath, Reason = "compressed" })
             .ToList();

    /// <summary>Extracts each archive into its own folder, then removes the archive (backed up if enabled).</summary>
    public FixResult ExtractArchives(List<PlanAction> actions, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var res = new FixResult();
        int i = 0;
        foreach (var a in actions)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new ScanProgress(++i, actions.Count, a.Source));
            var full = Full(a.Source);
            var dir = Path.GetDirectoryName(full)!;
            try
            {
                int n = 0;
                using (var archive = ArchiveFactory.OpenArchive(full, null))
                {
                    foreach (var entry in archive.Entries.Where(e => !e.IsDirectory))
                    {
                        var key = (entry.Key ?? "").Replace('/', '\\').TrimStart('\\');
                        var dest = Path.GetFullPath(Path.Combine(dir, key));
                        if (!dest.StartsWith(dir + "\\", StringComparison.OrdinalIgnoreCase))
                            throw new InvalidOperationException("Archive entry escapes folder: " + entry.Key);
                        dest = UniquePath(dest);
                        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                        using (var es = entry.OpenEntryStream())
                        using (var fs = File.Create(dest + Scanner.TempSuffix))
                            es.CopyTo(fs);
                        File.Move(dest + Scanner.TempSuffix, dest);
                        n++;
                        _log($"  extracted {Path.GetRelativePath(_root, dest)}");
                    }
                }
                Backup(a.Source);
                File.Delete(full);
                res.Extracted += n;
                _log($"Extracted {n} file(s) from {a.Source}; archive removed.");
            }
            catch (Exception ex)
            {
                res.Failed++;
                res.Errors.Add($"{a.Source}: {ex.Message}");
                _log($"ERROR extracting {a.Source}: {ex.Message}");
            }
        }
        return res;
    }

    // ---------------- Rebuild / header fix plan ----------------

    public List<PlanAction> PlanRebuild(CollectionState state)
    {
        var actions = new List<PlanAction>();
        var rowPaths = new HashSet<string>(state.Rows.Select(r => r.Row.RelPath), StringComparer.OrdinalIgnoreCase);
        var location = state.Files.ToDictionary(f => f, f => f.RelPath);
        var taken = new HashSet<string>(state.Files.Select(f => f.RelPath), StringComparer.OrdinalIgnoreCase);

        string Reserve(string rel)
        {
            string candidate = rel;
            int n = 1;
            while (taken.Contains(candidate))
            {
                var ext = Path.GetExtension(rel);
                candidate = rel[..^ext.Length] + $" ({n++})" + ext;
            }
            taken.Add(candidate);
            return candidate;
        }

        void MoveAside(FileInfoRecord f, string reason)
        {
            var folder = state.MatchFull(f) != null ? Scanner.DuplicatesFolder : Scanner.UnknownFolder;
            var from = location[f];
            var to = Reserve(folder + "\\" + StripSpecial(from));
            actions.Add(new PlanAction { Type = ActionType.MoveAside, Source = from, Target = to, Reason = reason });
            taken.Remove(from);
            location[f] = to;
        }

        // Without rebuild, only repair files that already sit at their own path.
        var fixable = state.Rows.Where(r => r.Fixable && (_opt.Rebuild ||
            r.Status is RowStatus.BadHeader or RowStatus.NoHeader or RowStatus.Unif or RowStatus.WrongCase)).ToList();
        if (!_opt.UseSources)
            fixable = fixable.Where(r => r.Status != RowStatus.InSource &&
                                         !(r.Status is RowStatus.WrongFile or RowStatus.ExtraData && r.Source == null)).ToList();

        // 1. Wrong files sitting at a path we are about to fill: move them out of the way first.
        foreach (var rs in fixable)
            if (rs.File != null && state.MatchFull(rs.File) != rs.Row.Entry && rs.File.Kind != FileKind.Archive)
                MoveAside(rs.File, $"occupies path of {rs.Row.FileName}");

        // 2. Write every fixable row from one good source per entry.
        var consumed = new HashSet<FileInfoRecord>();
        foreach (var group in fixable.GroupBy(r => r.Row.Entry))
        {
            var entry = group.Key;
            var candidates = state.Files.Where(f => state.MatchFull(f) == entry).ToList();
            if (candidates.Count == 0)
            {
                // Nothing in the collection: copy from a source folder (never modified).
                var ext = group.Select(r => r.External).FirstOrDefault(x => x != null);
                if (ext == null || !_opt.UseSources) continue;
                foreach (var rs in group)
                {
                    actions.Add(new PlanAction
                    {
                        Type = ActionType.Write, Entry = entry, Source = ext.Display, Target = rs.Row.RelPath, External = ext,
                        Reason = RowState.Text(rs.Status),
                    });
                    taken.Add(rs.Row.RelPath);
                }
                continue;
            }

            // Prefer a file already at one of this entry's paths, then anything else.
            var source = candidates.OrderBy(f => rowPaths.Contains(location[f]) ? 0 : 1)
                                   .ThenBy(f => Scanner.IsInSpecialFolder(location[f]) ? 1 : 0).First();
            var targets = group.ToList();
            for (int i = 0; i < targets.Count; i++)
            {
                var rs = targets[i];
                bool last = i == targets.Count - 1;
                bool sourceSurplus = !rowPaths.Contains(location[source]);
                bool consume = _opt.Rebuild && last && sourceSurplus && _opt.MoveDuplicates;
                string reason = RowState.Text(rs.Status) + (rs.Note.Length > 0 ? ": " + rs.Note : "");
                actions.Add(new PlanAction
                {
                    Type = ActionType.Write, Entry = entry, Source = location[source], Target = rs.Row.RelPath,
                    ConsumeSource = consume, Reason = reason,
                });
                taken.Add(rs.Row.RelPath);
                if (consume)
                {
                    taken.Remove(location[source]);
                    consumed.Add(source);
                }
            }
        }

        // 3. Clean-up of files that are not at any database path.
        foreach (var f in state.Files)
        {
            if (consumed.Contains(f) || f.Kind is FileKind.Archive or FileKind.Error) continue;
            var at = location[f];
            if (rowPaths.Contains(at)) continue;
            bool known = state.MatchFull(f) != null;
            if (known && _opt.Rebuild && _opt.MoveDuplicates && !at.StartsWith(Scanner.DuplicatesFolder + "\\", StringComparison.OrdinalIgnoreCase))
            {
                var to = Reserve(Scanner.DuplicatesFolder + "\\" + StripSpecial(at));
                actions.Add(new PlanAction { Type = ActionType.MoveToDuplicates, Source = at, Target = to });
                taken.Remove(at);
                location[f] = to;
            }
            else if (!known && _opt.MoveUnknown && !Scanner.IsInSpecialFolder(at))
            {
                var to = Reserve(Scanner.UnknownFolder + "\\" + at);
                actions.Add(new PlanAction { Type = ActionType.MoveToUnknown, Source = at, Target = to });
                taken.Remove(at);
                location[f] = to;
            }
        }

        // 4. Folders left empty by moves.
        if (_opt.RemoveEmptyFolders)
        {
            var vacated = actions.Where(a => a.Type is ActionType.MoveAside or ActionType.MoveToDuplicates or ActionType.MoveToUnknown
                                              || a is { Type: ActionType.Write, ConsumeSource: true })
                                 .Select(a => Path.GetDirectoryName(a.Source) ?? "")
                                 .Where(d => d.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase);
            var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in vacated)
                for (var cur = d; !string.IsNullOrEmpty(cur); cur = Path.GetDirectoryName(cur) ?? "")
                    dirs.Add(cur);
            foreach (var d in dirs.OrderByDescending(d => d.Length))
                if (!taken.Any(t => t.StartsWith(d + "\\", StringComparison.OrdinalIgnoreCase)) &&
                    !d.Equals(Scanner.UnknownFolder, StringComparison.OrdinalIgnoreCase) &&
                    !d.Equals(Scanner.DuplicatesFolder, StringComparison.OrdinalIgnoreCase))
                    actions.Add(new PlanAction { Type = ActionType.RemoveEmptyDir, Source = d });
        }

        return actions;
    }

    private static string StripSpecial(string rel)
    {
        foreach (var p in new[] { Scanner.UnknownFolder + "\\", Scanner.DuplicatesFolder + "\\" })
            if (rel.StartsWith(p, StringComparison.OrdinalIgnoreCase)) return rel[p.Length..];
        return rel;
    }

    // ---------------- Execution ----------------

    public FixResult Execute(List<PlanAction> actions, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var res = new FixResult();
        int i = 0;
        foreach (var a in actions)
        {
            ct.ThrowIfCancellationRequested();
            if (++i % 10 == 0 || i == actions.Count) progress?.Report(new ScanProgress(i, actions.Count, a.Target.Length > 0 ? a.Target : a.Source));
            try
            {
                switch (a.Type)
                {
                    case ActionType.MoveAside:
                    case ActionType.MoveToDuplicates:
                    case ActionType.MoveToUnknown:
                        MoveFile(a.Source, a.Target);
                        res.Moved++;
                        break;
                    case ActionType.Write:
                        WriteRom(a);
                        res.Written++;
                        break;
                    case ActionType.RemoveEmptyDir:
                        var d = Full(a.Source);
                        if (Directory.Exists(d) && !Directory.EnumerateFileSystemEntries(d).Any())
                        {
                            Directory.Delete(d);
                            res.DirsRemoved++;
                        }
                        break;
                }
                _log(a.ToString());
            }
            catch (Exception ex)
            {
                res.Failed++;
                res.Errors.Add($"{a}: {ex.Message}");
                _log($"ERROR {a}: {ex.Message}");
            }
        }
        return res;
    }

    private void MoveFile(string fromRel, string toRel)
    {
        var to = Full(toRel);
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        File.Move(Full(fromRel), to, false);
    }

    private void WriteRom(PlanAction a)
    {
        var entry = a.Entry!;
        var srcFull = a.External != null ? a.External.Path : Full(a.Source);
        var payload = a.External != null ? SourceScanner.ReadPayload(a.External) : RomFile.ReadPayload(srcFull);
        var sha = Convert.ToHexString(SHA1.HashData(payload));
        if (!sha.Equals(entry.RomSha1, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"source content changed (SHA1 {sha}, expected {entry.RomSha1})");

        var bytes = new byte[NesHeader.Size + payload.Length];
        entry.Header.CopyTo(bytes, 0);
        payload.CopyTo(bytes, NesHeader.Size);

        var target = Full(a.Target);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var tmp = target + Scanner.TempSuffix;
        File.WriteAllBytes(tmp, bytes);

        var existing = FindCaseInsensitive(target);
        if (existing != null)
        {
            Backup(Path.GetRelativePath(_root, existing));
            if (!string.Equals(existing, target, StringComparison.Ordinal))
            {
                // Name differs only by case: remove old name so the new file gets the exact XML name.
                var aside = existing + ".nrscase";
                File.Move(existing, aside);
                File.Move(tmp, target);
                File.Delete(aside); // payload is already in memory, even if this was the source
            }
            else File.Move(tmp, target, true);
        }
        else File.Move(tmp, target);

        if (a.External == null && a.ConsumeSource && !string.Equals(srcFull, target, StringComparison.OrdinalIgnoreCase) && File.Exists(srcFull))
        {
            // Verify the written file before letting go of the source.
            var check = File.ReadAllBytes(target);
            if (!SHA1.HashData(check).AsSpan().SequenceEqual(SHA1.HashData(bytes)))
                throw new IOException("verification of written file failed; source kept");
            Backup(a.Source);
            File.Delete(srcFull);
        }
    }

    private static string? FindCaseInsensitive(string fullPath)
    {
        var dir = Path.GetDirectoryName(fullPath)!;
        if (!Directory.Exists(dir)) return null;
        var name = Path.GetFileName(fullPath);
        foreach (var f in Directory.EnumerateFiles(dir, name, new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive }))
            if (string.Equals(Path.GetFileName(f), name, StringComparison.OrdinalIgnoreCase)) return f;
        return null;
    }

    private void Backup(string rel)
    {
        if (!_opt.BackupEnabled || string.IsNullOrWhiteSpace(_opt.BackupFolder)) return;
        var src = Full(rel);
        if (!File.Exists(src)) return;
        var dest = UniquePath(Path.Combine(_opt.BackupFolder, rel));
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        File.Copy(src, dest);
    }

    private static string UniquePath(string full)
    {
        if (!File.Exists(full)) return full;
        var dir = Path.GetDirectoryName(full)!;
        var stem = Path.GetFileNameWithoutExtension(full);
        var ext = Path.GetExtension(full);
        for (int n = 1; ; n++)
        {
            var c = Path.Combine(dir, $"{stem} ({n}){ext}");
            if (!File.Exists(c)) return c;
        }
    }
}
