using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Reflection;
using System.Text;
using Avalonia.Media;
using Avalonia.Threading;
using NRSVaultMini.Core;

namespace NRSVaultMini;

public sealed class StatItem
{
    public string Label { get; init; } = "";
    public string Value { get; init; } = "";
    public bool IsHeader { get; init; }
    public IBrush Swatch { get; init; } = Brushes.Transparent;
    public string? FilterKey { get; init; }
    public bool IsClickable => FilterKey != null;
}

public sealed class ExtraFileVm
{
    public required ExtraFile Extra { get; init; }
    public string Kind => Extra.Kind switch
    {
        ExtraKind.Duplicate => "Surplus copy",
        ExtraKind.Archive => "Archive",
        ExtraKind.Error => "Error",
        _ => "Unknown",
    };
    public string Path => Extra.File.RelPath;
    public long Size => Extra.File.Size;
    public string Note => Extra.Note;
    public string Sha1 => Extra.File.FileSha1;
    public IBrush RowBrush => Extra.Kind switch
    {
        ExtraKind.Error => TreeNode.RedBrush,
        ExtraKind.Unknown or ExtraKind.Archive => TreeNode.OrangeBrush,
        _ => Brushes.Transparent,
    };
}

public sealed class SourceMatchVm
{
    public required SourceMatch Match { get; init; }
    public string Fills => Match.Needed ? "Fills a gap" : "Already have";
    public string Target => (Match.Entry.Paths.FirstOrDefault() ?? "") + (Match.Entry.Paths.Count > 1 ? $"  (+{Match.Entry.Paths.Count - 1})" : "");
    public string Source => Match.Best.Display;
    public string Kind => Match.Best.KindText;
    public int Copies => Match.Records.Count;
    public long Size => Match.Entry.HeaderedSize;
    public IBrush RowBrush => Match.Needed ? TreeNode.GreenBrush : Brushes.Transparent;
}

public sealed record FilterOption(string Name, Func<RowState, bool>? Predicate)
{
    public override string ToString() => Name;
}

public sealed class MainViewModel : ViewModelBase
{
    public static string AppVersion =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";

    private readonly AppSettings _settings = AppSettings.Load();
    private NesDb? _db;
    private ChipIndex? _chipIndex;
    private CollectionState? _state;
    private TreeNode _root = new();
    private CancellationTokenSource? _cts;
    private readonly ConcurrentQueue<string> _logQueue = new();
    private readonly DispatcherTimer _logTimer;

    public AppSettings Settings => _settings;
    public BulkObservableCollection<TreeNode> Visible { get; } = new();
    public BulkObservableCollection<StatItem> Stats { get; } = new();
    public BulkObservableCollection<ExtraFileVm> Extras { get; } = new();
    public ObservableCollection<string> SourceFolders { get; } = new();
    public BulkObservableCollection<SourceMatchVm> SourceMatchList { get; } = new();
    private SourceScanResult? _lastSourceScan;
    private int _lastSourceFolderCount;
    public ObservableCollection<string> Log { get; } = new();
    public event Action? LogAppended;

    public List<FilterOption> FilterOptions { get; } = new()
    {
        new("All ROMs", null),
        new("Needs attention (not OK)", r => r.Status != RowStatus.Ok),
        new("Fixable", r => r.Fixable),
        new("OK", r => r.Status == RowStatus.Ok),
        new("Missing", r => r.Status == RowStatus.Missing),
        new("Errors", r => r.Status == RowStatus.Error),
        new("Bad / no header, UNIF", r => r.Status is RowStatus.BadHeader or RowStatus.NoHeader or RowStatus.Unif),
        new("Copy available", r => r.Status == RowStatus.CopyAvailable),
        new("In source folder", r => r.External != null),
        new("Misplaced / wrong name", r => r.Status is RowStatus.Misplaced or RowStatus.WrongCase),
        new("Wrong file / extra data", r => r.Status is RowStatus.WrongFile or RowStatus.ExtraData),
    };

    public MainViewModel()
    {
        _logTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(200), DispatcherPriority.Background, (_, _) => FlushLog());
        _logTimer.Start();
        _filter = FilterOptions[0];
        if (string.IsNullOrEmpty(_settings.XmlPath))
        {
            foreach (var c in new[] { Path.Combine(AppContext.BaseDirectory, "nes20db.xml"), Path.Combine(_settings.RootFolder ?? "", "nes20db.xml") })
                if (File.Exists(c)) { _settings.XmlPath = c; break; }
        }
        foreach (var f in _settings.SourceFolders) SourceFolders.Add(f);
        if (string.IsNullOrEmpty(_settings.BackupFolder) && !string.IsNullOrEmpty(_settings.RootFolder))
            _settings.BackupFolder = _settings.RootFolder.TrimEnd('\\') + " (NRSVault backup)";
        BuildStats();
    }

    // ---------------- Bindable settings ----------------

    public string RootFolder
    {
        get => _settings.RootFolder;
        set
        {
            _settings.RootFolder = value;
            Raise();
            if (string.IsNullOrWhiteSpace(BackupFolder) && !string.IsNullOrWhiteSpace(value))
                BackupFolder = value.TrimEnd('\\') + " (NRSVault backup)";
        }
    }
    public string XmlPath { get => _settings.XmlPath; set { _settings.XmlPath = value; Raise(); } }
    public bool TrialRun { get => _settings.TrialRun; set { _settings.TrialRun = value; Raise(); Raise(nameof(FixButtonText)); } }
    public bool Rebuild { get => _settings.Rebuild; set { _settings.Rebuild = value; Raise(); } }
    public bool MoveUnknown { get => _settings.MoveUnknown; set { _settings.MoveUnknown = value; Raise(); } }
    public bool MoveDuplicates { get => _settings.MoveDuplicates; set { _settings.MoveDuplicates = value; Raise(); } }
    public bool RemoveEmptyFolders { get => _settings.RemoveEmptyFolders; set { _settings.RemoveEmptyFolders = value; Raise(); } }
    public bool BackupEnabled { get => _settings.BackupEnabled; set { _settings.BackupEnabled = value; Raise(); } }
    public string BackupFolder { get => _settings.BackupFolder; set { _settings.BackupFolder = value; Raise(); } }
    public bool DatIncludeUnverified { get => _settings.DatIncludeUnverified; set { _settings.DatIncludeUnverified = value; Raise(); } }
    public bool UseSources { get => _settings.UseSources; set { _settings.UseSources = value; Raise(); } }
    public bool SourcesOnlyNeeded { get => _settings.SourcesOnlyNeeded; set { _settings.SourcesOnlyNeeded = value; Raise(); RefreshSourceList(); } }

    private string? _selectedSource;
    public string? SelectedSourceFolder { get => _selectedSource; set => Set(ref _selectedSource, value); }

    public void AddSourceFolder(string path)
    {
        path = Path.GetFullPath(path).TrimEnd('\\');
        if (SourceFolders.Contains(path, StringComparer.OrdinalIgnoreCase)) return;
        if (!string.IsNullOrWhiteSpace(RootFolder) && string.Equals(path, Path.GetFullPath(RootFolder).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
        {
            AddLog("The collection folder itself cannot be a source folder.");
            return;
        }
        SourceFolders.Add(path);
        _settings.SourceFolders = SourceFolders.ToList();
        AddLog("Added source folder: " + path);
    }

    public void RemoveSourceFolder()
    {
        if (SelectedSourceFolder is not { } f) return;
        SourceFolders.Remove(f);
        _settings.SourceFolders = SourceFolders.ToList();
        AddLog("Removed source folder: " + f + " (press Scan sources to refresh matches)");
    }

    public string DatName { get => _settings.DatName; set { _settings.DatName = value; Raise(); } }
    public string DatAuthor { get => _settings.DatAuthor; set { _settings.DatAuthor = value; Raise(); } }

    public string FixButtonText => TrialRun ? "Fix / Rebuild (trial)" : "Fix / Rebuild";
    public string Title => "NRSVault Mini " + AppVersion + (_db != null ? $"  —  nes20db {_db.Date}" : "");

    // ---------------- State ----------------

    private bool _busy;
    public bool IsBusy { get => _busy; private set { if (Set(ref _busy, value)) Raise(nameof(IsIdle)); } }
    public bool IsIdle => !_busy;
    public bool HasDb => _state != null;
    public bool IsScanned => _state?.Scanned == true;

    private string _status = "Ready.";
    public string StatusText { get => _status; private set => Set(ref _status, value); }

    private double _progress;
    public double Progress { get => _progress; private set => Set(ref _progress, value); }

    private bool _indeterminate;
    public bool ProgressIndeterminate { get => _indeterminate; private set => Set(ref _indeterminate, value); }

    private string _details = "Select a ROM or folder to see its details.";
    public string Details { get => _details; private set => Set(ref _details, value); }

    private TreeNode? _selected;
    public TreeNode? Selected
    {
        get => _selected;
        set { if (Set(ref _selected, value)) Details = BuildDetails(value); }
    }

    private string _search = "";
    private CancellationTokenSource? _searchDebounce;
    public string SearchText
    {
        get => _search;
        set
        {
            if (!Set(ref _search, value)) return;
            _searchDebounce?.Cancel();
            var cts = _searchDebounce = new CancellationTokenSource();
            Task.Delay(300, cts.Token).ContinueWith(t =>
            {
                if (!t.IsCanceled) Dispatcher.UIThread.Post(() => RebuildVisible(expandMatches: true));
            });
        }
    }

    private FilterOption _filter;
    public FilterOption Filter
    {
        get => _filter;
        set { if (value != null && Set(ref _filter, value)) RebuildVisible(expandMatches: true); }
    }

    private bool FilterActive => _filter.Predicate != null || !string.IsNullOrWhiteSpace(_search);

    private bool Matches(TreeNode n)
    {
        if (n.Row == null) return false;
        if (_filter.Predicate != null && !_filter.Predicate(n.Row)) return false;
        var s = _search.Trim();
        return s.Length == 0 || n.Path.Contains(s, StringComparison.OrdinalIgnoreCase) ||
               n.Row.Note.Contains(s, StringComparison.OrdinalIgnoreCase) ||
               n.Row.ShownCrc.Equals(s, StringComparison.OrdinalIgnoreCase) ||
               (n.Row.ShownSha1.Length > 0 && n.Row.ShownSha1.Equals(s, StringComparison.OrdinalIgnoreCase));
    }

    // ---------------- Logging & progress ----------------

    public void AddLog(string line) => _logQueue.Enqueue($"{DateTime.Now:HH:mm:ss}  {line}");

    private void FlushLog()
    {
        if (_logQueue.IsEmpty) return;
        int n = 0;
        while (n < 5000 && _logQueue.TryDequeue(out var l)) { Log.Add(l); n++; }
        while (Log.Count > 200_000) Log.RemoveAt(0);
        LogAppended?.Invoke();
    }

    private IProgress<ScanProgress> MakeProgress(string verb) => new Progress<ScanProgress>(p =>
    {
        ProgressIndeterminate = p.Total == 0;
        Progress = p.Total == 0 ? 0 : 100.0 * p.Done / p.Total;
        StatusText = $"{verb} {p.Done:N0} / {p.Total:N0}   {p.Current}";
    });

    private async Task RunBusy(string what, Func<CancellationToken, Task> work)
    {
        if (IsBusy) return;
        IsBusy = true;
        _cts = new CancellationTokenSource();
        StatusText = what + "...";
        ProgressIndeterminate = true;
        try
        {
            await work(_cts.Token);
        }
        catch (OperationCanceledException)
        {
            AddLog(what + " cancelled.");
            StatusText = what + " cancelled.";
        }
        catch (Exception ex)
        {
            AddLog($"ERROR during {what}: {ex.Message}");
            StatusText = $"Error: {ex.Message}";
        }
        finally
        {
            ProgressIndeterminate = false;
            Progress = 0;
            IsBusy = false;
            _cts = null;
            _settings.Save();
        }
    }

    public void Cancel() => _cts?.Cancel();

    // ---------------- Commands ----------------

    public Task LoadDbAsync() => RunBusy("Loading nes20db.xml", async _ =>
    {
        if (!File.Exists(XmlPath)) throw new FileNotFoundException("nes20db.xml not found: " + XmlPath);
        var path = XmlPath;
        var db = await Task.Run(() => NesDb.Load(path));
        _db = db;
        _chipIndex = null;
        _state = new CollectionState(db);
        _lastSourceScan = null;
        SourceMatchList.ReplaceAll(Array.Empty<SourceMatchVm>());
        foreach (var w in db.Warnings) AddLog("DB warning: " + w);
        AddLog($"Loaded nes20db.xml dated {db.Date}: {db.Entries.Count:N0} entries, {db.Rows.Count:N0} file paths.");
        BuildTree();
        BuildStats();
        Extras.ReplaceAll(Array.Empty<ExtraFileVm>());
        Raise(nameof(Title));
        Raise(nameof(HasDb));
        StatusText = $"Database loaded ({db.Rows.Count:N0} ROMs). Press Scan to check the collection.";
    });

    public Task ScanAsync() => RunBusy("Scanning", async ct =>
    {
        await EnsureDbAsync();
        await ScanCoreAsync(ct);
        StatusText = $"Scan complete: {_state!.Stats.Count(RowStatus.Ok):N0} OK of {_state.Stats.DbRows:N0}.";
    });

    private async Task EnsureDbAsync()
    {
        if (_state != null && _db != null && string.Equals(_db.SourcePath, XmlPath, StringComparison.OrdinalIgnoreCase)) return;
        if (!File.Exists(XmlPath)) throw new FileNotFoundException("nes20db.xml not found: " + XmlPath);
        var path = XmlPath;
        _db = await Task.Run(() => NesDb.Load(path));
        _chipIndex = null;
        _state = new CollectionState(_db);
        if (_lastSourceScan != null) _state.ApplySources(_lastSourceScan, _lastSourceFolderCount);
        AddLog($"Loaded nes20db.xml dated {_db.Date}: {_db.Entries.Count:N0} entries, {_db.Rows.Count:N0} file paths.");
        BuildTree();
        Raise(nameof(Title));
        Raise(nameof(HasDb));
    }

    private async Task ScanCoreAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(RootFolder))
            throw new InvalidOperationException("Choose your collection folder first (the … button next to Collection folder).");
        if (!Directory.Exists(RootFolder)) throw new DirectoryNotFoundException("Collection folder not found: " + RootFolder);
        var root = RootFolder;
        var exclude = SourceFolders.ToList();
        if (BackupEnabled) exclude.Add(BackupFolder);
        AddLog($"Scanning {root} ...");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var progress = MakeProgress("Scanning");
        var chips = _chipIndex ??= new ChipIndex(_db!);
        var files = await Task.Run(() => Scanner.Scan(root, exclude, progress, ct, chips), ct);
        _state!.Apply(files);
        var s = _state.Stats;
        AddLog($"Scanned {files.Count:N0} files in {sw.Elapsed.TotalSeconds:F1}s: {s.Count(RowStatus.Ok):N0} OK, " +
               $"{s.Count(RowStatus.Missing):N0} missing, {s.Fixable:N0} fixable, {s.ExtraUnknown:N0} unknown, {s.ExtraDuplicates:N0} surplus copies.");
        RefreshAfterScan();
    }

    private void RefreshAfterScan()
    {
        _root.Aggregate();
        _root.NotifyAll();
        RebuildVisible(expandMatches: false);
        BuildStats();
        Extras.ReplaceAll(_state!.Extras.Select(x => new ExtraFileVm { Extra = x }));
        RefreshSourceList();
        Details = BuildDetails(Selected);
        Raise(nameof(IsScanned));
    }

    public Task ScanSourcesAsync() => RunBusy("Scanning source folders", async ct =>
    {
        if (SourceFolders.Count == 0) throw new InvalidOperationException("Add at least one source folder first.");
        await EnsureDbAsync();
        if (!_state!.Scanned && Directory.Exists(RootFolder)) await ScanCoreAsync(ct);
        var folders = SourceFolders.ToList();
        var db = _db!;
        AddLog($"Scanning {folders.Count} source folder(s) recursively (read-only)...");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var res = await Task.Run(() => SourceScanner.Scan(folders, db, MakeProgress("Source files"), ct), ct);
        _lastSourceScan = res;
        _lastSourceFolderCount = folders.Count;
        _state.ApplySources(res, folders.Count);
        foreach (var e in res.ErrorMessages.Take(50)) AddLog("  source error: " + e);
        if (res.Errors > 50) AddLog($"  ... {res.Errors - 50:N0} more source errors");
        var st = _state.Stats;
        AddLog($"Source scan done in {sw.Elapsed.TotalSeconds:F1}s: {res.FilesSeen:N0} files, {res.FilesRead:N0} read, {res.ArchivesRead:N0} archives opened, " +
               $"{st.SourceMatchedEntries:N0} nes20db ROMs found, {st.SourceNeededEntries:N0} of them fill gaps ({st.Count(RowStatus.InSource):N0} missing paths).");
        RefreshAfterScan();
        StatusText = $"Sources: {st.SourceNeededEntries:N0} ROM(s) can fill gaps in the collection. Use Fix / Rebuild to copy them in.";
    });

    private void RefreshSourceList()
    {
        if (_state == null) return;
        var list = _state.SourceMatches.Values.Where(m => !SourcesOnlyNeeded || m.Needed)
            .OrderByDescending(m => m.Needed).ThenBy(m => m.Entry.Paths.FirstOrDefault(), StringComparer.OrdinalIgnoreCase)
            .Select(m => new SourceMatchVm { Match = m });
        SourceMatchList.ReplaceAll(list);
        Raise(nameof(SourceSummary));
    }

    public string SourceSummary => _state?.SourceScan == null
        ? "Not scanned yet. Add folders, then press Scan sources."
        : $"{_state.Stats.SourceFilesSeen:N0} files searched, {_state.Stats.SourceMatchedEntries:N0} nes20db ROMs found, {_state.Stats.SourceNeededEntries:N0} fill gaps";

    public Task FixAsync(Func<string, Task<bool>> confirm) => RunBusy("Fix / Rebuild", async ct =>
    {
        await EnsureDbAsync();
        if (!_state!.Scanned) await ScanCoreAsync(ct);

        var opt = new FixOptions
        {
            TrialRun = TrialRun, Rebuild = Rebuild, MoveUnknown = MoveUnknown, MoveDuplicates = MoveDuplicates,
            RemoveEmptyFolders = RemoveEmptyFolders, BackupEnabled = BackupEnabled, BackupFolder = BackupFolder,
            UseSources = UseSources,
        };
        if (BackupEnabled && string.IsNullOrWhiteSpace(BackupFolder))
            throw new InvalidOperationException("Backup is ticked but no backup folder is set.");
        var fixer = new Fixer(RootFolder, opt, AddLog);
        var extract = fixer.PlanExtraction(_state);
        var plan = await Task.Run(() => fixer.PlanRebuild(_state), ct);

        if (extract.Count == 0 && plan.Count == 0)
        {
            AddLog("Nothing to do: every ROM that can be fixed already is.");
            StatusText = "Nothing to fix.";
            return;
        }

        string summary = Summarize(extract, plan);
        if (opt.TrialRun)
        {
            AddLog("===== TRIAL RUN: no files will be changed =====");
            foreach (var a in extract.Concat(plan)) AddLog("TRIAL  " + a);
            if (extract.Count > 0) AddLog("Note: archive contents are unknown until extracted; the plan above is based on files already uncompressed.");
            AddLog("TRIAL RUN summary: " + summary);
            StatusText = "Trial run complete: " + summary + ". Turn off TRIAL RUN to apply.";
            return;
        }

        if (!await confirm($"This will modify files in:\n{RootFolder}\n\n{summary}\n\n" +
                           (BackupEnabled ? $"Originals will be backed up to:\n{BackupFolder}" : "Backup is OFF.") + "\n\nContinue?"))
        {
            AddLog("Fix cancelled by user.");
            return;
        }

        if (extract.Count > 0)
        {
            AddLog($"Extracting {extract.Count:N0} archive(s)...");
            var er = await Task.Run(() => fixer.ExtractArchives(extract, MakeProgress("Extracting"), ct), ct);
            AddLog($"Extracted {er.Extracted:N0} file(s); {er.Failed:N0} archive(s) failed.");
            await ScanCoreAsync(ct);
            plan = await Task.Run(() => fixer.PlanRebuild(_state), ct);
        }

        AddLog($"Applying {plan.Count:N0} action(s)...");
        var res = await Task.Run(() => fixer.Execute(plan, MakeProgress("Fixing"), ct), ct);
        AddLog($"Done: {res.Written:N0} written, {res.Moved:N0} moved, {res.DirsRemoved:N0} empty folders removed, {res.Failed:N0} failed.");
        foreach (var e in res.Errors) AddLog("  FAILED " + e);
        await ScanCoreAsync(ct);
        StatusText = $"Fix complete. {_state.Stats.Count(RowStatus.Ok):N0} of {_state.Stats.DbRows:N0} ROMs OK.";
    });

    private static string Summarize(List<PlanAction> extract, List<PlanAction> plan)
    {
        var parts = new List<string>();
        if (extract.Count > 0) parts.Add($"{extract.Count:N0} archive(s) to extract");
        int w = plan.Count(a => a.Type == ActionType.Write);
        int ws = plan.Count(a => a.Type == ActionType.Write && a.External != null);
        if (w > 0) parts.Add($"{w:N0} ROM(s) to write" + (ws > 0 ? $" ({ws:N0} from source folders)" : ""));
        int m = plan.Count(a => a.Type is ActionType.MoveAside);
        if (m > 0) parts.Add($"{m:N0} file(s) moved out of the way");
        int d = plan.Count(a => a.Type is ActionType.MoveToDuplicates);
        if (d > 0) parts.Add($"{d:N0} surplus cop(ies) to _Duplicates");
        int u = plan.Count(a => a.Type is ActionType.MoveToUnknown);
        if (u > 0) parts.Add($"{u:N0} unknown file(s) to _Unknown");
        int r = plan.Count(a => a.Type == ActionType.RemoveEmptyDir);
        if (r > 0) parts.Add($"{r:N0} empty folder(s) to remove");
        return string.Join(", ", parts);
    }

    public Task GenerateDatAsync(string path) => RunBusy("Generating DAT", async _ =>
    {
        await EnsureDbAsync();
        var state = _state!;
        var opt = new DatOptions { Name = DatName, Author = DatAuthor, IncludeUnverified = DatIncludeUnverified };
        var r = await Task.Run(() => DatWriter.Write(path, state, opt, AppVersion));
        _settings.LastDatFolder = Path.GetDirectoryName(path) ?? "";
        AddLog($"DAT written: {path}");
        AddLog($"  {r.Verified:N0} verified ROMs (size + CRC32 + SHA1)" +
               (opt.IncludeUnverified ? $", {r.Unverified:N0} unverified (size + CRC32 only, flagged)" : $", {r.Skipped:N0} unverified ROMs left out"));
        StatusText = $"DAT written with {r.Verified + r.Unverified:N0} ROMs.";
    });

    public Task GenerateSplitDatAsync(string path) => RunBusy("Generating split DAT", async _ =>
    {
        await EnsureDbAsync();
        var db = _db!;
        var opt = new DatOptions { Name = DatWriter.SplitName(DatName), Author = DatAuthor };
        int n = await Task.Run(() => DatWriter.WriteSplit(path, db, opt, AppVersion));
        _settings.LastDatFolder = Path.GetDirectoryName(path) ?? "";
        AddLog($"Split DAT written: {path}");
        AddLog($"  {n:N0} games with .prg/.chr files (hashes taken from nes20db.xml, so every entry is included).");
        StatusText = $"Split DAT written with {n:N0} games.";
    });

    public string DefaultSplitDatFileName => $"{DatWriter.SplitName(DatName)} ({_db?.Date ?? DateTime.Now.ToString("yyyy-MM-dd")}).dat";

    /// <summary>Cuts every available ROM into &lt;name&gt;.prg / &lt;name&gt;.chr under <paramref name="folder"/>.</summary>
    public Task ExportSplitAsync(string folder, Func<string, Task<bool>> confirm) => RunBusy("Exporting split files", async ct =>
    {
        await EnsureDbAsync();
        if (!_state!.Scanned) await ScanCoreAsync(ct);
        _settings.SplitFolder = folder;
        bool trial = TrialRun;
        if (!trial && !await confirm($"Write split .prg / .chr files to:\n{folder}\n\nFiles already there with the right contents are skipped.\n\nContinue?"))
        {
            AddLog("Split export cancelled by user.");
            return;
        }
        var state = _state;
        var root = RootFolder;
        bool useSources = UseSources;
        AddLog((trial ? "===== TRIAL RUN: split export, no files will be written =====" : "Exporting split files to " + folder) + " ...");
        var r = await Task.Run(() => SplitExporter.Export(state, root, folder, useSources, trial, AddLog, MakeProgress("Splitting"), ct), ct);
        AddLog($"{(trial ? "TRIAL RUN summary" : "Split export done")}: {r.Written:N0} chip file(s) {(trial ? "would be written" : "written")} " +
               $"({r.FromElsewhere:N0} taken from other ROMs or loose chip files), {r.AlreadyThere:N0} already up to date, " +
               $"{r.Missing:N0} chip(s) not found anywhere, {r.Failed:N0} failed.");
        AddLog($"  Paths: {r.PathsComplete:N0} complete, {r.PathsPartial:N0} partial (some chips still missing), {r.PathsEmpty:N0} with no chips yet.");
        StatusText = trial
            ? $"Trial run: {r.Written:N0} split files would be written, {r.Missing:N0} chips not found. Turn off TRIAL RUN to export."
            : $"Split export: {r.Written:N0} written, {r.AlreadyThere:N0} already up to date, {r.Missing:N0} chips not found.";
    });

    public string DefaultDatFileName =>$"{DatName} ({_db?.Date ?? DateTime.Now.ToString("yyyy-MM-dd")}).dat";

    public Task ExportReportAsync(string path) => RunBusy("Exporting report", async _ =>
    {
        var state = _state ?? throw new InvalidOperationException("Load the database first.");
        await Task.Run(() =>
        {
            using var w = new StreamWriter(path, false, new UTF8Encoding(true));
            w.WriteLine("Path,Status,Note,HeaderedSize,CRC32,SHA1,Mapper,ExpectedHeader,CurrentHeader");
            foreach (var r in state.Rows.OrderBy(r => r.Row.RelPath, StringComparer.OrdinalIgnoreCase))
                w.WriteLine(string.Join(",", Csv(r.Row.RelPath), Csv(RowState.Text(r.Status)), Csv(r.Note), r.ShownSize,
                    r.ShownCrc, r.ShownSha1, r.Row.Entry.MapperText, NesHeader.ToHex(r.Row.Entry.Header), r.File?.HeaderHex ?? ""));
            w.WriteLine();
            w.WriteLine("Files not in database / surplus");
            w.WriteLine("Path,Kind,Note,Size,SHA1");
            foreach (var x in state.Extras)
                w.WriteLine(string.Join(",", Csv(x.File.RelPath), x.Kind, Csv(x.Note), x.File.Size, x.File.FileSha1));
            if (state.SourceMatches.Count > 0)
            {
                w.WriteLine();
                w.WriteLine("ROMs found in source folders");
                w.WriteLine("FillsGap,CollectionPath,SourceFile,Kind,Copies");
                foreach (var m in state.SourceMatches.Values.OrderByDescending(m => m.Needed))
                    w.WriteLine(string.Join(",", m.Needed, Csv(m.Entry.Paths.FirstOrDefault() ?? ""), Csv(m.Best.Display), Csv(m.Best.KindText), m.Records.Count));
            }
        });
        AddLog("Report written: " + path);
        StatusText = "Report exported.";
    });

    public Task ExportLogAsync(string path) => RunBusy("Exporting log", async _ =>
    {
        FlushLog();
        var lines = Log.ToList();
        await File.WriteAllLinesAsync(path, lines, new UTF8Encoding(true));
        StatusText = "Log exported.";
    });

    private static string Csv(string s) => s.Contains(',') || s.Contains('"') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    public void SaveSettings() => _settings.Save();

    // ---------------- Tree ----------------

    private void BuildTree()
    {
        var expanded = new HashSet<string>(EnumerateFolders(_root).Where(f => f.IsExpanded).Select(f => f.Path), StringComparer.OrdinalIgnoreCase);
        var root = new TreeNode { Name = "", Path = "", Depth = -1 };
        var folders = new Dictionary<string, TreeNode>(StringComparer.OrdinalIgnoreCase) { [""] = root };

        TreeNode FolderFor(string path)
        {
            if (folders.TryGetValue(path, out var f)) return f;
            int i = path.LastIndexOf('\\');
            var parent = FolderFor(i < 0 ? "" : path[..i]);
            f = new TreeNode { Name = i < 0 ? path : path[(i + 1)..], Path = path, Parent = parent, Depth = parent.Depth + 1 };
            f.IsExpanded = expanded.Contains(path);
            parent.Children.Add(f);
            folders[path] = f;
            return f;
        }

        foreach (var rs in _state!.Rows)
        {
            var folder = FolderFor(rs.Row.Folder);
            folder.Children.Add(new TreeNode { Name = rs.Row.FileName, Path = rs.Row.RelPath, Parent = folder, Depth = folder.Depth + 1, Row = rs });
        }

        _root = root;
        _root.Aggregate();
        SortTree(_root);
        RebuildVisible(expandMatches: false);
    }

    private static IEnumerable<TreeNode> EnumerateFolders(TreeNode n)
    {
        foreach (var c in n.Children.Where(c => c.IsFolder))
        {
            yield return c;
            foreach (var d in EnumerateFolders(c)) yield return d;
        }
    }

    private string _sortKey = "name";
    private bool _sortDesc;

    public (string key, bool desc) SortState => (_sortKey, _sortDesc);

    public void SortBy(string key)
    {
        if (_sortKey == key) _sortDesc = !_sortDesc;
        else { _sortKey = key; _sortDesc = false; }
        SortTree(_root);
        RebuildVisible(expandMatches: false);
    }

    private void SortTree(TreeNode n)
    {
        Comparison<TreeNode> cmp = _sortKey switch
        {
            "status" => (a, b) => a.Row!.Status.CompareTo(b.Row!.Status),
            "size" => (a, b) => a.Row!.ShownSize.CompareTo(b.Row!.ShownSize),
            "crc" => (a, b) => string.CompareOrdinal(a.Crc, b.Crc),
            "sha1" => (a, b) => string.CompareOrdinal(a.Sha1, b.Sha1),
            "mapper" => (a, b) => a.Row!.Row.Entry.Spec.Mapper != b.Row!.Row.Entry.Spec.Mapper
                ? a.Row.Row.Entry.Spec.Mapper.CompareTo(b.Row.Row.Entry.Spec.Mapper)
                : a.Row.Row.Entry.Spec.SubMapper.CompareTo(b.Row.Row.Entry.Spec.SubMapper),
            "note" => (a, b) => string.Compare(a.Note, b.Note, StringComparison.OrdinalIgnoreCase),
            _ => (_, _) => 0,
        };
        int dir = _sortDesc ? -1 : 1;
        int folderStatusDir = _sortKey == "status" ? dir : 1;

        n.Children.Sort((a, b) =>
        {
            if (a.IsFolder != b.IsFolder) return a.IsFolder ? -1 : 1;
            int r;
            if (a.IsFolder)
            {
                // Folders: by name, or by health when sorting on status.
                r = _sortKey == "status"
                    ? folderStatusDir * (b.Total - b.Ok).CompareTo(a.Total - a.Ok) * -1
                    : (_sortKey == "name" ? dir : 1) * string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
                return r != 0 ? r : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            }
            r = _sortKey == "name" ? 0 : dir * cmp(a, b);
            if (r == 0) r = (_sortKey == "name" ? dir : 1) * string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            return r;
        });
        foreach (var c in n.Children.Where(c => c.IsFolder)) SortTree(c);
    }

    /// <summary>Recomputes the flattened visible list. With a filter, only matching ROMs and their folders show.</summary>
    public void RebuildVisible(bool expandMatches)
    {
        var list = new List<TreeNode>();
        bool filtering = FilterActive;
        if (filtering)
        {
            var hasMatch = new Dictionary<TreeNode, bool>();
            bool Mark(TreeNode f)
            {
                bool any = false;
                foreach (var c in f.Children)
                    any |= c.IsFolder ? Mark(c) : Matches(c);
                hasMatch[f] = any;
                if (any && expandMatches) f.IsExpanded = true;
                return any;
            }
            Mark(_root);
            AddVisible(_root, list, c => c.IsFolder ? hasMatch.GetValueOrDefault(c) : Matches(c));
        }
        else AddVisible(_root, list, _ => true);
        Visible.ReplaceAll(list);
        Raise(nameof(VisibleSummary));
    }

    private static void AddVisible(TreeNode folder, List<TreeNode> list, Func<TreeNode, bool> include)
    {
        foreach (var c in folder.Children)
        {
            if (!include(c)) continue;
            list.Add(c);
            if (c.IsFolder && c.IsExpanded) AddVisible(c, list, include);
        }
    }

    public string VisibleSummary => FilterActive
        ? $"{Visible.Count(n => !n.IsFolder):N0} matching ROMs shown"
        : $"{_state?.Rows.Count ?? 0:N0} ROMs in {EnumerateFolders(_root).Count():N0} folders";

    public void Toggle(TreeNode node)
    {
        if (!node.IsFolder) return;
        int index = Visible.IndexOf(node);
        node.IsExpanded = !node.IsExpanded;
        if (index < 0) return;

        if (node.IsExpanded)
        {
            var add = new List<TreeNode>();
            if (FilterActive)
            {
                bool Has(TreeNode f) => f.Children.Any(c => c.IsFolder ? Has(c) : Matches(c));
                AddVisible(node, add, c => c.IsFolder ? Has(c) : Matches(c));
            }
            else AddVisible(node, add, _ => true);

            if (add.Count > 400)
            {
                var all = Visible.ToList();
                all.InsertRange(index + 1, add);
                Visible.ReplaceAll(all);
            }
            else
                for (int i = 0; i < add.Count; i++) Visible.Insert(index + 1 + i, add[i]);
        }
        else
        {
            int end = index + 1;
            while (end < Visible.Count && Visible[end].Depth > node.Depth) end++;
            int count = end - index - 1;
            if (count > 400)
            {
                var all = Visible.ToList();
                all.RemoveRange(index + 1, count);
                Visible.ReplaceAll(all);
            }
            else
                for (int i = 0; i < count; i++) Visible.RemoveAt(index + 1);
        }
    }

    public void ExpandAll(bool expand)
    {
        foreach (var f in EnumerateFolders(_root)) f.IsExpanded = expand;
        RebuildVisible(expandMatches: false);
    }

    public void ApplyStatFilter(string key)
    {
        var opt = FilterOptions.FirstOrDefault(f => f.Name == key);
        if (opt != null) Filter = opt;
    }

    // ---------------- Stats sidebar ----------------

    private static readonly IBrush GreyBrush = new SolidColorBrush(Color.Parse("#BDBDBD"));

    private void BuildStats()
    {
        var items = new List<StatItem>();
        void H(string t) => items.Add(new StatItem { Label = t, IsHeader = true });
        void I(string label, string value, IBrush? swatch = null, string? filter = null) =>
            items.Add(new StatItem { Label = label, Value = value, Swatch = swatch ?? Brushes.Transparent, FilterKey = filter });

        if (_state == null)
        {
            H("Database");
            I("Not loaded", "");
            Stats.ReplaceAll(items);
            return;
        }

        var s = _state.Stats;
        H("Database (nes20db.xml)");
        I("Date", _db!.Date);
        I("Game entries", s.DbEntries.ToString("N0"));
        I("ROM files (paths)", s.DbRows.ToString("N0"), null, "All ROMs");

        if (!_state.Scanned)
        {
            H("Collection");
            I("Not scanned yet", "");
            Stats.ReplaceAll(items);
            return;
        }

        int ok = s.Count(RowStatus.Ok);
        H("Collection");
        I("Verified OK", ok.ToString("N0"), TreeNode.GreenBrush, "OK");
        I("Needs attention", (s.DbRows - ok - s.Count(RowStatus.Error)).ToString("N0"), TreeNode.OrangeBrush, "Needs attention (not OK)");
        I("Errors", s.Count(RowStatus.Error).ToString("N0"), TreeNode.RedBrush, "Errors");
        I("Found", s.Found.ToString("N0"));
        I("Missing", s.Count(RowStatus.Missing).ToString("N0"), TreeNode.OrangeBrush, "Missing");
        I("Fixable now", s.Fixable.ToString("N0"), null, "Fixable");
        I("Complete", s.DbRows == 0 ? "0%" : $"{100.0 * ok / s.DbRows:F2}%");

        H("ROM status");
        foreach (var st in Enum.GetValues<RowStatus>())
        {
            if (st is RowStatus.NotScanned or RowStatus.Ok or RowStatus.Missing or RowStatus.Error) continue;
            string? filter = st switch
            {
                RowStatus.BadHeader or RowStatus.NoHeader or RowStatus.Unif => "Bad / no header, UNIF",
                RowStatus.CopyAvailable => "Copy available",
                RowStatus.InSource => "In source folder",
                RowStatus.Misplaced or RowStatus.WrongCase => "Misplaced / wrong name",
                _ => "Wrong file / extra data",
            };
            I(RowState.Text(st), s.Count(st).ToString("N0"), TreeNode.OrangeBrush, filter);
        }

        H("Game entries");
        I("Complete", s.EntriesComplete.ToString("N0"), TreeNode.GreenBrush);
        I("Partial", s.EntriesPartial.ToString("N0"), TreeNode.OrangeBrush);
        I("Missing", s.EntriesMissing.ToString("N0"), TreeNode.OrangeBrush);

        H("Files on disk");
        I("Total files", s.DiskFiles.ToString("N0"));
        I("Total size", FormatBytes(s.DiskBytes));
        I("NES 2.0 headered", s.DiskNes20.ToString("N0"));
        I("iNES 1.0 headered", s.DiskINes1.ToString("N0"));
        I("Headerless", s.DiskHeaderless.ToString("N0"));
        I("UNIF", s.DiskUnif.ToString("N0"));
        I("Compressed (to extract)", s.DiskArchives.ToString("N0"), s.DiskArchives > 0 ? TreeNode.OrangeBrush : null);
        I("Unreadable", s.DiskErrors.ToString("N0"), s.DiskErrors > 0 ? TreeNode.RedBrush : null);

        H("Source folders");
        if (!s.SourcesScanned)
            I(SourceFolders.Count == 0 ? "None added" : $"{SourceFolders.Count} folder(s), not scanned", "");
        else
        {
            I("Folders", s.SourceFolders.ToString("N0"));
            I("Files searched", s.SourceFilesSeen.ToString("N0"));
            I("Candidate ROMs", s.SourceCandidates.ToString("N0"));
            I("nes20db ROMs found", s.SourceMatchedEntries.ToString("N0"));
            I("Fill gaps", s.SourceNeededEntries.ToString("N0"), TreeNode.GreenBrush, "In source folder");
            I("Missing paths fillable", s.Count(RowStatus.InSource).ToString("N0"), TreeNode.OrangeBrush, "In source folder");
            if (s.SourceErrors > 0) I("Unreadable", s.SourceErrors.ToString("N0"), TreeNode.RedBrush);
        }

        H("Not in the XML layout");
        I("Unknown files", s.ExtraUnknown.ToString("N0"), s.ExtraUnknown > 0 ? TreeNode.OrangeBrush : null);
        I("Surplus copies", s.ExtraDuplicates.ToString("N0"), s.ExtraDuplicates > 0 ? GreyBrush : null);
        I("In _Unknown", s.InUnknownFolder.ToString("N0"));
        I("In _Duplicates", s.InDuplicatesFolder.ToString("N0"));

        Stats.ReplaceAll(items);
    }

    private static string FormatBytes(long b) =>
        b >= 1L << 30 ? $"{b / (double)(1L << 30):F2} GB" : b >= 1 << 20 ? $"{b / (double)(1 << 20):F1} MB" : $"{b:N0} B";

    // ---------------- Details panel ----------------

    private string BuildDetails(TreeNode? n)
    {
        if (n == null) return "Select a ROM or folder to see its details.";
        var sb = new StringBuilder();
        if (n.IsFolder)
        {
            sb.AppendLine("FOLDER").AppendLine(n.Path).AppendLine();
            sb.AppendLine($"ROMs:        {n.Total:N0}");
            sb.AppendLine($"OK:          {n.Ok:N0}");
            sb.AppendLine($"Missing:     {n.Missing:N0}");
            sb.AppendLine($"Other issue: {n.Issues:N0}");
            sb.AppendLine($"Errors:      {n.Errors:N0}");
            return sb.ToString();
        }

        var r = n.Row!;
        var e = r.Row.Entry;
        sb.AppendLine(r.Row.FileName);
        sb.AppendLine(r.Row.Folder).AppendLine();
        sb.AppendLine($"Status:  {RowState.Text(r.Status)}");
        if (r.Note.Length > 0) sb.AppendLine($"Note:    {r.Note}");
        sb.AppendLine();

        sb.AppendLine("HEADERED ROM (final file)");
        sb.AppendLine($"  Size   {e.HeaderedSize:N0}");
        sb.AppendLine($"  CRC32  {Crc32Util.ToHex(e.HeaderedCrc)}" + (r.Status == RowStatus.Ok ? "  (verified)" : "  (computed)"));
        sb.AppendLine($"  SHA1   {(r.Status == RowStatus.Ok && r.File != null ? r.File.FileSha1 : "(known once verified)")}");
        sb.AppendLine();

        sb.AppendLine("EXPECTED NES 2.0 HEADER");
        sb.AppendLine("  " + NesHeader.ToSpacedHex(e.Header));
        sb.AppendLine("  " + NesHeader.Describe(e.Header));
        if (r.File?.HeaderBytes is { } cur)
        {
            sb.AppendLine("CURRENT HEADER");
            sb.AppendLine("  " + NesHeader.ToSpacedHex(cur));
            sb.AppendLine("  " + NesHeader.Describe(cur));
            var diffs = Enumerable.Range(0, 16).Where(i => cur[i] != e.Header[i]).ToList();
            if (diffs.Count > 0) sb.AppendLine("  differs at byte(s): " + string.Join(", ", diffs));
        }
        else if (r.File != null)
        {
            sb.AppendLine("CURRENT HEADER");
            sb.AppendLine(r.File.Kind == FileKind.Unif ? "  (UNIF container)" : "  (none, headerless)");
        }
        sb.AppendLine();

        if (r.File != null)
        {
            sb.AppendLine("FILE ON DISK");
            sb.AppendLine($"  {r.File.RelPath}");
            sb.AppendLine($"  Size   {r.File.Size:N0}");
            sb.AppendLine($"  CRC32  {r.File.FileCrc}");
            sb.AppendLine($"  SHA1   {r.File.FileSha1}");
            sb.AppendLine();
        }
        if (r.Source != null && r.Status != RowStatus.Ok)
            sb.AppendLine($"SOURCE FOR FIX\n  {r.Source.RelPath}\n");
        else if (r.External != null && r.Status != RowStatus.Ok)
        {
            sb.AppendLine($"SOURCE FOLDER MATCH ({r.External.KindText})");
            sb.AppendLine("  " + r.External.Display);
            if (_state!.SourceMatches.TryGetValue(e, out var sm) && sm.Records.Count > 1)
                sb.AppendLine($"  (+{sm.Records.Count - 1} more cop{(sm.Records.Count == 2 ? "y" : "ies")} in source folders)");
            sb.AppendLine();
        }

        sb.AppendLine("PCB / CONSOLE");
        var s = e.Spec;
        sb.AppendLine($"  Mapper {s.Mapper}  Submapper {s.SubMapper}  Mirroring {s.Mirroring}  Battery {s.Battery}");
        sb.AppendLine($"  Console type {s.ConsoleType}  Region {s.ConsoleRegion}  Expansion {s.Expansion}");
        sb.AppendLine();

        if (e.Paths.Count > 1)
        {
            sb.AppendLine($"SAME ROM AT {e.Paths.Count} PATHS");
            foreach (var p in e.Paths) sb.AppendLine("  " + p);
            sb.AppendLine();
        }

        sb.AppendLine("NES20DB.XML ENTRY");
        foreach (var el in e.Elements)
            sb.AppendLine($"  <{el.Name} " + string.Join(" ", el.Attributes.Select(a => $"{a.Key}=\"{a.Value}\"")) + " />");
        return sb.ToString();
    }
}
