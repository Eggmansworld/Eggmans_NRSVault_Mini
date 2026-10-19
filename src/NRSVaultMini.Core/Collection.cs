namespace NRSVaultMini.Core;

public enum RowStatus
{
    NotScanned,
    Ok,          // correct content, exact expected NES 2.0 header, exact path
    BadHeader,   // correct content, wrong/outdated header
    NoHeader,    // correct content, headerless
    Unif,        // correct content, UNIF container
    WrongCase,   // correct content, file name differs only in letter case
    CopyAvailable, // not at its path, but the same ROM exists at another path of this entry (needs a copy)
    Misplaced,   // not at its path, but matching content exists elsewhere in the collection
    InSource,    // not in the collection, but a matching ROM was found in a source folder
    WrongFile,   // some other file sits at this path
    ExtraData,   // header-reported size matches, but the file carries extra trailing data
    Missing,
    Error,
}

public enum StatusColor { None, Green, Orange, Red }

public enum ExtraKind { Unknown, Duplicate, Archive, Error }

public sealed class RowState
{
    public required DbRow Row { get; init; }
    public RowStatus Status { get; set; }
    public FileInfoRecord? File { get; set; }
    /// <summary>A file elsewhere with this entry's content (for Misplaced / WrongFile rows).</summary>
    public FileInfoRecord? Source { get; set; }
    /// <summary>A matching ROM in a source folder (used when the collection itself has no copy).</summary>
    public SourceRecord? External { get; set; }
    public string Note { get; set; } = "";

    public bool Fixable => Status is RowStatus.BadHeader or RowStatus.NoHeader or RowStatus.Unif or RowStatus.WrongCase
        or RowStatus.CopyAvailable or RowStatus.Misplaced or RowStatus.InSource
        || (Status is RowStatus.WrongFile or RowStatus.ExtraData && (Source != null || External != null));

    public StatusColor Color => ColorOf(Status);

    public static StatusColor ColorOf(RowStatus s) => s switch
    {
        RowStatus.NotScanned => StatusColor.None,
        RowStatus.Ok => StatusColor.Green,
        RowStatus.Error => StatusColor.Red,
        _ => StatusColor.Orange,
    };

    public static string Text(RowStatus s) => s switch
    {
        RowStatus.NotScanned => "Not scanned",
        RowStatus.Ok => "OK",
        RowStatus.BadHeader => "Bad header",
        RowStatus.NoHeader => "No header",
        RowStatus.Unif => "UNIF",
        RowStatus.WrongCase => "Wrong name case",
        RowStatus.CopyAvailable => "Copy available",
        RowStatus.Misplaced => "Misplaced",
        RowStatus.InSource => "In source folder",
        RowStatus.WrongFile => "Wrong file",
        RowStatus.ExtraData => "Extra data",
        RowStatus.Missing => "Missing",
        RowStatus.Error => "Error",
        _ => s.ToString(),
    };

    /// <summary>Headered hashes: real ones when verified, otherwise the computable expectation (no SHA1).</summary>
    public long ShownSize => Row.Entry.HeaderedSize;
    public string ShownCrc => Status == RowStatus.Ok && File != null ? File.FileCrc : Crc32Util.ToHex(Row.Entry.HeaderedCrc);
    public string ShownSha1 => Status == RowStatus.Ok && File != null ? File.FileSha1 : "";
}

/// <summary>A file on disk that does not occupy a database path.</summary>
public sealed class ExtraFile
{
    public required FileInfoRecord File { get; init; }
    public ExtraKind Kind { get; init; }
    public DbEntry? Entry { get; init; }
    public string Note { get; init; } = "";
}

public sealed class CollectionStats
{
    public int DbEntries, DbRows;
    public Dictionary<RowStatus, int> ByStatus { get; } = Enum.GetValues<RowStatus>().ToDictionary(s => s, _ => 0);
    public int Fixable;
    public int DiskFiles, DiskNes20, DiskINes1, DiskHeaderless, DiskUnif, DiskArchives, DiskErrors;
    public long DiskBytes;
    public int ExtraUnknown, ExtraDuplicates, InUnknownFolder, InDuplicatesFolder;
    public int EntriesComplete, EntriesPartial, EntriesMissing;
    public bool SourcesScanned;
    public int SourceFolders, SourceFilesSeen, SourceCandidates, SourceMatchedEntries, SourceNeededEntries, SourceErrors;
    public int Count(RowStatus s) => ByStatus[s];
    public int Found => DbRows - Count(RowStatus.Missing) - Count(RowStatus.NotScanned);
}

/// <summary>ROMs found in source folders for one database entry.</summary>
public sealed class SourceMatch
{
    public required DbEntry Entry { get; init; }
    public required List<SourceRecord> Records { get; init; }
    public SourceRecord Best => Records[0];
    /// <summary>True when the collection lacks this ROM, so the source would fill something.</summary>
    public bool Needed { get; set; }
}

/// <summary>Matches scanned files to database rows and derives a status for every row.</summary>
public sealed class CollectionState
{
    public NesDb Db { get; }
    public List<RowState> Rows { get; }
    public List<FileInfoRecord> Files { get; private set; } = new();
    public List<ExtraFile> Extras { get; } = new();
    public CollectionStats Stats { get; private set; } = new();
    public bool Scanned { get; private set; }

    public SourceScanResult? SourceScan { get; private set; }
    public int SourceFolderCount { get; private set; }
    public Dictionary<DbEntry, SourceMatch> SourceMatches { get; } = new();

    public CollectionState(NesDb db)
    {
        Db = db;
        Rows = db.Rows.Select(r => new RowState { Row = r, Status = RowStatus.NotScanned }).ToList();
        Stats = BuildStats();
    }

    public DbEntry? MatchFull(FileInfoRecord f) =>
        f.Kind is FileKind.Raw or FileKind.INes or FileKind.Unif && Db.BySha1.TryGetValue(f.PayloadSha1, out var e) ? e : null;

    public DbEntry? MatchReported(FileInfoRecord f) =>
        f.ReportedSha1 != null && Db.BySha1.TryGetValue(f.ReportedSha1, out var e) ? e : null;

    /// <summary>Records what the source folders contain and re-derives every row status.</summary>
    public void ApplySources(SourceScanResult result, int folderCount)
    {
        SourceScan = result;
        SourceFolderCount = folderCount;
        SourceMatches.Clear();
        foreach (var g in result.Records.GroupBy(r => r.PayloadSha1, StringComparer.OrdinalIgnoreCase))
        {
            if (!Db.BySha1.TryGetValue(g.Key, out var e)) continue;
            // Prefer loose files over archive entries, and files that already carry the right header.
            var expected = NesHeader.ToHex(e.Header);
            var recs = g.OrderBy(r => r.EntryKey == null ? 0 : 1)
                        .ThenBy(r => string.Equals(r.HeaderHex, expected, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                        .ThenBy(r => r.Path, StringComparer.OrdinalIgnoreCase).ToList();
            SourceMatches[e] = new SourceMatch { Entry = e, Records = recs };
        }
        if (Scanned) Apply(Files);
        else Stats = BuildStats();
    }

    public void Apply(List<FileInfoRecord> files)
    {
        Files = files;
        Scanned = true;
        Extras.Clear();

        var byPath = new Dictionary<string, FileInfoRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in files) byPath[f.RelPath] = f;

        // Every file with full content match, grouped by entry (sources for rebuilding).
        var sources = new Dictionary<DbEntry, List<FileInfoRecord>>();
        foreach (var f in files)
            if (MatchFull(f) is { } e)
                (sources.TryGetValue(e, out var l) ? l : sources[e] = new()).Add(f);

        var rowPaths = new HashSet<string>(Rows.Select(r => r.Row.RelPath), StringComparer.OrdinalIgnoreCase);

        foreach (var rs in Rows)
        {
            var entry = rs.Row.Entry;
            rs.File = null;
            rs.Source = null;
            rs.External = null;
            rs.Note = "";
            var ext = SourceMatches.TryGetValue(entry, out var sm) ? sm.Best : null;
            sources.TryGetValue(entry, out var src);
            // Prefer a source that is not sitting at another row of this same entry with a good header.
            rs.Source = src?.FirstOrDefault(f => !string.Equals(f.RelPath, rs.Row.RelPath, StringComparison.OrdinalIgnoreCase));

            if (!byPath.TryGetValue(rs.Row.RelPath, out var f))
            {
                if (rs.Source != null)
                {
                    var atRow = src!.FirstOrDefault(x => entry.Paths.Contains(x.RelPath, StringComparer.OrdinalIgnoreCase));
                    if (atRow != null)
                    {
                        rs.Source = atRow;
                        rs.Status = RowStatus.CopyAvailable;
                        rs.Note = "Same ROM as " + atRow.RelPath;
                    }
                    else
                    {
                        rs.Status = RowStatus.Misplaced;
                        rs.Note = "Found at " + rs.Source.RelPath;
                    }
                }
                else if (ext != null)
                {
                    rs.External = ext;
                    rs.Status = RowStatus.InSource;
                    rs.Note = $"Source ({ext.KindText}): {ext.Display}";
                }
                else rs.Status = RowStatus.Missing;
                continue;
            }

            rs.File = f;
            if (f.Kind == FileKind.Error)
            {
                rs.Status = RowStatus.Error;
                rs.Note = f.Error ?? "Unreadable";
                continue;
            }
            if (f.Kind == FileKind.Archive)
            {
                rs.Status = RowStatus.WrongFile;
                rs.Note = "Archive at ROM path (will be extracted)";
                continue;
            }

            var match = MatchFull(f);
            if (match == entry)
            {
                bool caseOk = string.Equals(f.RelPath, rs.Row.RelPath, StringComparison.Ordinal);
                if (f.Kind == FileKind.Unif) { rs.Status = RowStatus.Unif; rs.Note = "UNIF will be converted to NES 2.0"; }
                else if (f.Kind == FileKind.Raw) { rs.Status = RowStatus.NoHeader; rs.Note = "Headerless"; }
                else if (!string.Equals(f.HeaderHex, NesHeader.ToHex(entry.Header), StringComparison.OrdinalIgnoreCase))
                {
                    rs.Status = RowStatus.BadHeader;
                    var hb = f.HeaderBytes!;
                    int oldMapper = NesHeader.Mapper(hb);
                    rs.Note = oldMapper != entry.Spec.Mapper
                        ? $"Mapper {oldMapper} → {entry.Spec.Mapper}"
                        : (NesHeader.IsNes20(hb) ? "NES 2.0 header differs" : "iNES 1.0 header");
                }
                else if (!caseOk) { rs.Status = RowStatus.WrongCase; rs.Note = "On disk as " + f.RelPath; }
                else rs.Status = RowStatus.Ok;
                if (rs.Status != RowStatus.Ok && !caseOk && rs.Status != RowStatus.WrongCase) rs.Note += "; name case differs";
            }
            else if (MatchReported(f) == entry)
            {
                if (rs.Source == null) rs.External = ext;
                rs.Status = RowStatus.ExtraData;
                rs.Note = $"{f.PayloadSize - f.ReportedSize:N0} extra bytes after ROM data" +
                          (rs.Source != null ? "; clean copy at " + rs.Source.RelPath : rs.External != null ? "; clean copy in source: " + rs.External.Display : "");
            }
            else
            {
                if (rs.Source == null) rs.External = ext;
                rs.Status = RowStatus.WrongFile;
                rs.Note = (match != null ? "Contains " + (match.Paths.FirstOrDefault() ?? "another entry") : "Unknown content") +
                          (rs.Source != null ? "; correct ROM at " + rs.Source.RelPath : rs.External != null ? "; correct ROM in source: " + rs.External.Display : "");
            }
        }

        // Files that are not at any database path.
        foreach (var f in files)
        {
            if (rowPaths.Contains(f.RelPath)) continue;
            if (f.Kind == FileKind.Archive)
                Extras.Add(new ExtraFile { File = f, Kind = ExtraKind.Archive, Note = "Compressed; will be extracted in place" });
            else if (f.Kind == FileKind.Error)
                Extras.Add(new ExtraFile { File = f, Kind = ExtraKind.Error, Note = f.Error ?? "Unreadable" });
            else if (MatchFull(f) is { } e)
                Extras.Add(new ExtraFile { File = f, Kind = ExtraKind.Duplicate, Entry = e, Note = "Copy of " + (e.Paths.FirstOrDefault() ?? "") });
            else if (MatchReported(f) is { } e2)
                Extras.Add(new ExtraFile { File = f, Kind = ExtraKind.Unknown, Entry = e2, Note = "Overdump of " + (e2.Paths.FirstOrDefault() ?? "") });
            else
                Extras.Add(new ExtraFile { File = f, Kind = ExtraKind.Unknown, Note = "Not in nes20db.xml" });
        }

        foreach (var m in SourceMatches.Values) m.Needed = false;
        foreach (var rs in Rows)
            if (rs.External != null && SourceMatches.TryGetValue(rs.Row.Entry, out var m)) m.Needed = true;

        Stats = BuildStats();
    }

    private CollectionStats BuildStats()
    {
        var s = new CollectionStats { DbEntries = Db.Entries.Count, DbRows = Rows.Count };
        foreach (var r in Rows)
        {
            s.ByStatus[r.Status]++;
            if (r.Fixable) s.Fixable++;
        }
        foreach (var g in Rows.GroupBy(r => r.Row.Entry))
        {
            int ok = g.Count(r => r.Status == RowStatus.Ok);
            if (ok == g.Count()) s.EntriesComplete++;
            else if (g.All(r => r.Status is RowStatus.Missing or RowStatus.NotScanned)) s.EntriesMissing++;
            else s.EntriesPartial++;
        }
        foreach (var f in Files)
        {
            s.DiskFiles++;
            s.DiskBytes += f.Size;
            switch (f.Kind)
            {
                case FileKind.INes:
                    if (f.HeaderBytes is { } h && NesHeader.IsNes20(h)) s.DiskNes20++; else s.DiskINes1++;
                    break;
                case FileKind.Raw: s.DiskHeaderless++; break;
                case FileKind.Unif: s.DiskUnif++; break;
                case FileKind.Archive: s.DiskArchives++; break;
                case FileKind.Error: s.DiskErrors++; break;
            }
            if (f.RelPath.StartsWith(Scanner.UnknownFolder + "\\", StringComparison.OrdinalIgnoreCase)) s.InUnknownFolder++;
            if (f.RelPath.StartsWith(Scanner.DuplicatesFolder + "\\", StringComparison.OrdinalIgnoreCase)) s.InDuplicatesFolder++;
        }
        s.ExtraUnknown = Extras.Count(x => x.Kind == ExtraKind.Unknown);
        s.ExtraDuplicates = Extras.Count(x => x.Kind == ExtraKind.Duplicate);
        if (SourceScan != null)
        {
            s.SourcesScanned = true;
            s.SourceFolders = SourceFolderCount;
            s.SourceFilesSeen = SourceScan.FilesSeen;
            s.SourceCandidates = SourceScan.Records.Count;
            s.SourceMatchedEntries = SourceMatches.Count;
            s.SourceNeededEntries = SourceMatches.Values.Count(m => m.Needed);
            s.SourceErrors = SourceScan.Errors;
        }
        return s;
    }
}
