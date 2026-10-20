// Developer check tool for NRSVault Mini's core logic.
//   headers <nes20db.xml> <out.tsv>             dump "ROMSHA1<TAB>HEADERHEX" for every entry (compare with the Python script)
//   scan    <nes20db.xml> <root> [planOut]      scan + status summary + TRIAL plan (never modifies files)
//   fix     <nes20db.xml> <root>                scan, extract, fix/rebuild for real, rescan
//   dat     <nes20db.xml> <root> <out.dat> [all]
//   any command accepts "--src <folder>" (repeatable) to also search source folders
using NRSVaultMini.Core;

var srcFolders = new List<string>();
for (int i = 0; i < args.Length - 1; i++)
    if (args[i] == "--src") srcFolders.Add(args[i + 1]);
args = args.Where((a, i) => a != "--src" && (i == 0 || args[i - 1] != "--src")).ToArray();
SourceScanResult? srcResult = null;

if (args.Length >= 3 && args[0] == "headers")
{
    var db = NesDb.Load(args[1]);
    using var w = new StreamWriter(args[2]);
    foreach (var e in db.Entries.Where(e => e.RomSha1.Length > 0))
        w.WriteLine($"{e.RomSha1}\t{NesHeader.ToHex(e.Header)}");
    Console.WriteLine($"entries={db.Entries.Count} rows={db.Rows.Count} unique-sha1={db.BySha1.Count} warnings={db.Warnings.Count}");
    foreach (var warn in db.Warnings.Take(10)) Console.WriteLine("  " + warn);
    return 0;
}

// splitcheck <xml> <root>          : slice every matching file and compare PRG/CHR with nes20db hashes (read-only)
// splitdat   <xml> <out.dat>
// split      <xml> <root> <outdir> [trial]
if (args.Length >= 3 && args[0] == "splitcheck")
{
    var db = NesDb.Load(args[1]);
    var state = Scan(db, args[2]);
    int files = 0, pieces = 0, bad = 0;
    foreach (var f in state.Files)
    {
        if (state.MatchFull(f) is not { } e) continue;
        files++;
        var payload = RomFile.ReadPayload(Path.Combine(args[2], f.RelPath));
        foreach (var p in SplitLayout.Pieces(e))
        {
            pieces++;
            var sha = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(payload.AsSpan((int)p.Offset, (int)p.Size)));
            var crc = Crc32Util.ToHex(Crc32Util.Compute(payload.AsSpan((int)p.Offset, (int)p.Size)));
            if (sha != p.Sha1 || crc != p.Crc) { bad++; if (bad <= 10) Console.WriteLine($"  MISMATCH {p.Kind} {f.RelPath}"); }
        }
    }
    var withTrainer = state.Files.Count(f => state.MatchFull(f) is { } e && e.Spec.Trainer > 0);
    Console.WriteLine($"splitcheck: {files} files, {pieces} PRG/CHR pieces, {bad} mismatches ({withTrainer} files have trainers)");
    return bad == 0 ? 0 : 2;
}
if (args.Length >= 3 && args[0] == "splitdat")
{
    var db = NesDb.Load(args[1]);
    var n = DatWriter.WriteSplit(args[2], db, new DatOptions { Name = DatWriter.SplitName(new DatOptions().Name) }, "check");
    Console.WriteLine($"split DAT: {n} games");
    return 0;
}
if (args.Length >= 4 && args[0] == "split")
{
    var db = NesDb.Load(args[1]);
    var state = Scan(db, args[2]);
    var r = SplitExporter.Export(state, args[2], args[3], srcFolders.Count > 0, args.Length > 4 && args[4] == "trial", s => { }, null, default);
    Console.WriteLine($"split export: written={r.Written} alreadyThere={r.AlreadyThere} noSource={r.NoSource} failed={r.Failed}");
    foreach (var e in r.Errors.Take(10)) Console.WriteLine("  ERR " + e);
    return 0;
}

if (args.Length >= 3 && args[0] is "scan" or "fix" or "dat")
{
    var db = NesDb.Load(args[1]);
    var root = args[2];
    var state = Scan(db, root);
    PrintStats(state);

    if (args[0] == "dat")
    {
        var r = DatWriter.Write(args[3], state, new DatOptions { IncludeUnverified = args.Length > 4 && args[4] == "all" }, "check");
        Console.WriteLine($"DAT: verified={r.Verified} unverified={r.Unverified} skipped={r.Skipped}");
        return 0;
    }

    var opt = new FixOptions { TrialRun = args[0] == "scan" };
    var fixer = new Fixer(root, opt, s => { if (args[0] == "fix") Console.WriteLine("  " + s); });
    var extract = fixer.PlanExtraction(state);
    if (extract.Count > 0 && !opt.TrialRun)
    {
        var er = fixer.ExtractArchives(extract, null, default);
        Console.WriteLine($"Extracted {er.Extracted}, failed {er.Failed}");
        state = Scan(db, root);
    }
    var plan = fixer.PlanRebuild(state);
    Console.WriteLine($"Plan: {plan.Count} actions; " + string.Join(", ", plan.GroupBy(a => a.Type).Select(g => $"{g.Key}={g.Count()}")) +
                      (extract.Count > 0 ? $", archives={extract.Count}" : ""));
    if (args.Length > 3) File.WriteAllLines(args[3], extract.Concat(plan).Select(a => a.ToString()));

    if (!opt.TrialRun)
    {
        var res = fixer.Execute(plan, null, default);
        Console.WriteLine($"Executed: written={res.Written} moved={res.Moved} dirs={res.DirsRemoved} failed={res.Failed}");
        foreach (var e in res.Errors) Console.WriteLine("  ERR " + e);
        state = Scan(db, root);
        PrintStats(state);
    }
    return 0;
}

Console.WriteLine("usage: headers|scan|fix|dat ...");
return 1;

CollectionState Scan(NesDb db, string root)
{
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var files = Scanner.Scan(root, srcFolders, null, default);
    var state = new CollectionState(db);
    state.Apply(files);
    Console.WriteLine($"Scanned {files.Count} files in {sw.Elapsed.TotalSeconds:F1}s");
    if (srcFolders.Count > 0)
    {
        sw.Restart();
        srcResult ??= SourceScanner.Scan(srcFolders, db, null, default);
        state.ApplySources(srcResult, srcFolders.Count);
        Console.WriteLine($"Sources: seen={srcResult.FilesSeen} read={srcResult.FilesRead} archives={srcResult.ArchivesRead} candidates={srcResult.Records.Count} " +
                          $"matched={state.Stats.SourceMatchedEntries} needed={state.Stats.SourceNeededEntries} errors={srcResult.Errors} in {sw.Elapsed.TotalSeconds:F1}s");
        foreach (var m in state.SourceMatches.Values) Console.WriteLine($"   [src {(m.Needed ? "NEEDED" : "have")}] {m.Entry.Paths[0]} <= {m.Best.Display} ({m.Best.KindText}, {m.Records.Count} copies)");
        foreach (var e in srcResult.ErrorMessages) Console.WriteLine("   [src error] " + e);
    }
    return state;
}

static void PrintStats(CollectionState st)
{
    var s = st.Stats;
    Console.WriteLine($"DB entries={s.DbEntries} rows={s.DbRows} | complete={s.EntriesComplete} partial={s.EntriesPartial} missing={s.EntriesMissing}");
    Console.WriteLine("Rows: " + string.Join(", ", s.ByStatus.Where(k => k.Value > 0).Select(k => $"{k.Key}={k.Value}")) + $" | fixable={s.Fixable}");
    Console.WriteLine($"Disk: files={s.DiskFiles} bytes={s.DiskBytes:N0} nes2={s.DiskNes20} ines1={s.DiskINes1} raw={s.DiskHeaderless} unif={s.DiskUnif} archives={s.DiskArchives} errors={s.DiskErrors} | unknown={s.ExtraUnknown} dupes={s.ExtraDuplicates}");
    var okRows = st.Rows.Where(r => r.Status == RowStatus.Ok).ToList();
    int crcBad = okRows.Count(r => r.File!.FileCrc != Crc32Util.ToHex(r.Row.Entry.HeaderedCrc) || r.File.Size != r.Row.Entry.HeaderedSize);
    Console.WriteLine($"Computed headered CRC/size check: {okRows.Count - crcBad} match, {crcBad} mismatch");
    foreach (var g in st.Rows.Where(r => r.Status is not RowStatus.Ok and not RowStatus.Missing).GroupBy(r => r.Status))
        foreach (var r in g.Take(3)) Console.WriteLine($"   [{g.Key}] {r.Row.RelPath} :: {r.Note}");
    foreach (var x in st.Extras.Take(5)) Console.WriteLine($"   [extra {x.Kind}] {x.File.RelPath} :: {x.Note}");
}
