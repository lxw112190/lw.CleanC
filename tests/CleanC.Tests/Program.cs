using System.Runtime.InteropServices;
using System.Text.Json;
using CleanC.Core;

if (args.Contains("--scan-installers"))
{
    using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(60));
    var plans = new InstallerCleanup().Scan([], null, limit.Token);
    foreach (var plan in plans) Console.WriteLine($"{plan.Root}: {plan.SizeText}, {plan.Count} files");
    var safety = InstallerCleanup.GetSafetyStatus(plans.Select(p => p.Root).ToArray());
    Console.WriteLine("Read-only installer scan: " + Display.Bytes(plans.Sum(p => p.Size)) + "; " + (safety.BlockReason ?? safety.Advisory ?? "No installer/reboot block detected; user confirmation still required."));
    return 0;
}

if (args.Contains("--scan-local"))
{
    using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    try
    {
        var rules = RuleResolver.Resolve(JsonFiles.Read<RuleDocument>("rules/default.rules.json"));
        var rows = new CleanupEngine(new PathPolicy()).Scan(rules, [], null, limit.Token,
            options: new(args.Contains("--include-recent-browser-caches")));
        foreach (var row in rows) Console.WriteLine($"{row.Name}: {row.Count:N0} files, {row.SizeText}; {row.Status}");
        Console.WriteLine($"Read-only scan completed: {rows.Sum(r => r.Count):N0} candidates; {Display.Bytes(rows.Sum(r => r.Size))}. No cleanup performed.");
        return 0;
    }
    catch (OperationCanceledException) { Console.WriteLine("Read-only scan stopped at 30 second diagnostic limit. No cleanup performed."); return 0; }
}

// Dependency-free integration tests. All files and deletion targets are isolated inside this workspace.
string fixture = Path.GetFullPath(Path.Combine("artifacts", "tests", Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(fixture);
var policy = new PathPolicy(fixture);
var scanner = new Scanner(policy);
var cleaner = new Cleaner(policy);
int passed = 0, failed = 0;

void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
void Reject(Action action)
{
    bool rejected = false;
    try { action(); } catch (Exception e) when (e is InvalidDataException or IOException) { rejected = true; }
    Check(rejected, "Expected operation to be rejected");
}
void Test(string name, Action action)
{
    try { action(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception e) { failed++; Console.WriteLine("FAIL " + name + ": " + e); }
}
CleanupRule Rule(string id) => new() { Id = id, Name = id, Category = "Test", Impact = "Synthetic test files only", MinAgeDays = 7, Paths = [Path.Combine(fixture, id)] };
string Write(CleanupRule rule, string name = "old.tmp", int age = 30, string content = "synthetic cache")
{
    Directory.CreateDirectory(rule.Paths[0]);
    string file = Path.Combine(rule.Paths[0], name);
    Directory.CreateDirectory(Path.GetDirectoryName(file)!);
    File.WriteAllText(file, content); File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddDays(-age));
    return file;
}
List<RuleResult> Scan(CleanupRule rule, string[]? excluded = null, CancellationToken token = default) =>
    scanner.Scan(new() { Rules = [rule] }, excluded ?? [], null, token);
CleanupSummary Clean(List<RuleResult> rows, string[]? excluded = null, CancellationToken token = default) =>
    cleaner.Clean(rows, excluded ?? [], Path.Combine(fixture, "logs"), null, token);

Test("Default rule catalog validates with production path policy", () =>
    new PathPolicy().Validate(JsonFiles.Read<RuleDocument>("rules/default.rules.json")));
Test("Path boundary does not match sibling prefix", () => Check(!PathPolicy.Within(fixture + "-other", fixture), "Sibling matched"));
Test("Reject UNC and device paths", () => { Reject(() => PathPolicy.Normalize(@"\\server\share\Cache")); Reject(() => PathPolicy.Normalize(@"\\?\C:\Cache")); });
Test("Reject alternate data streams and unresolved variables", () => { Reject(() => PathPolicy.Normalize(@"C:\cache\a:stream")); Reject(() => PathPolicy.Normalize(@"%MISSING%\Cache")); });
Test("Reject drive and profile roots", () =>
{
    var p = new PathPolicy();
    Check(!p.IsAllowedRoot(@"C:\"), "Drive root permitted");
    Check(!p.IsAllowedRoot(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)), "Profile permitted");
    Check(!p.IsAllowedRoot(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)), "AppData root permitted");
    Check(!p.IsAllowedRoot(@"C:\Windows\Installer"), "Installer permitted");
});
Test("Reject duplicate IDs and overlapping roots", () =>
{
    var r = Rule("duplicate");
    Reject(() => policy.Validate(new() { Rules = [r, r] }));
    Reject(() => policy.Validate(new() { Rules = [r, r with { Id = "child", Paths = [Path.Combine(r.Paths[0], "child")] }] }));
});
Test("Reject root escape and zero age", () =>
{
    Reject(() => policy.Validate(new() { Rules = [Rule("escape") with { Paths = [Path.GetFullPath(Path.Combine(fixture, "..", "escape"))] }] }));
    Reject(() => policy.Validate(new() { Rules = [Rule("zero") with { MinAgeDays = 0 }] }));
});
Test("Preview respects age and exact file exclusions", () =>
{
    var r = Rule("age"); string old = Write(r); Write(r, "recent.tmp", 0); string excluded = Write(r, "exclude.tmp");
    var rows = Scan(r, [excluded]); Check(rows[0].Count == 1 && rows[0].Files[0].Path == old, "Unexpected scan contents");
});
Test("Literal percent signs in real filenames do not abort preview or cleanup", () =>
{
    var r = Rule("percent"); string file = Write(r, "%TEMP%-100%.tmp");
    var rows = Scan(r); Check(rows[0].Count == 1, "Percent filename skipped");
    var summary = Clean(rows); Check(summary.Deleted == 1 && !File.Exists(file), "Percent filename not cleaned");
    Reject(() => PathPolicy.ExpandRoot(@"C:\%CLEANC_MISSING_VARIABLE%\Cache"));
});
Test("Directory exclusion also skips descendants", () =>
{
    var r = Rule("excluded-dir"); Write(r, "skip/old.tmp"); Write(r, "keep.tmp");
    Check(Scan(r, [Path.Combine(r.Paths[0], "skip")])[0].Count == 1, "Excluded directory traversed");
});
Test("Actual cleanup deletes only previewed old file and creates audit trail", () =>
{
    var r = Rule("delete"); string old = Write(r), recent = Write(r, "recent.tmp", 0);
    var summary = Clean(Scan(r));
    Check(summary.Deleted == 1 && summary.Skipped == 0, "Unexpected cleanup counts");
    Check(!File.Exists(old) && File.Exists(recent), "Wrong files deleted");
    string[] lines = File.ReadAllLines(summary.LogPath);
    var kinds = lines.Select(l => { using var j = JsonDocument.Parse(l); return j.RootElement.GetProperty("kind").GetString(); }).ToArray();
    Check(kinds.SequenceEqual(new[] { "start", "intent", "deleted", "summary" }), "Invalid audit ordering");
});
Test("Changed file survives stale preview", () =>
{
    var r = Rule("changed"); string file = Write(r); var rows = Scan(r);
    File.AppendAllText(file, "new data");
    var summary = Clean(rows); Check(summary.Deleted == 0 && summary.Skipped == 1 && File.Exists(file), "Changed file deleted");
});
Test("Replaced file identity survives even with same size and timestamp", () =>
{
    var r = Rule("replaced"); string file = Write(r); var rows = Scan(r); DateTime stamp = File.GetLastWriteTimeUtc(file);
    File.Move(file, file + ".original"); File.WriteAllText(file, "synthetic cache"); File.SetLastWriteTimeUtc(file, stamp);
    var summary = Clean(rows); Check(summary.Deleted == 0 && summary.Skipped == 1 && File.Exists(file), "Replaced file deleted");
});
Test("Locked file is skipped without force", () =>
{
    var r = Rule("locked"); string file = Write(r); var rows = Scan(r);
    using var locked = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None);
    var summary = Clean(rows); Check(summary.Deleted == 0 && summary.Skipped == 1, "Lock ignored");
});
Test("New exclusion is rechecked at deletion time", () =>
{
    var r = Rule("new-exclusion"); string file = Write(r); var summary = Clean(Scan(r), [file]);
    Check(summary.Deleted == 0 && summary.Skipped == 1 && File.Exists(file), "Exclusion ignored");
});
Test("Cancellation preserves all files", () =>
{
    var r = Rule("cancel"); string file = Write(r); var rows = Scan(r);
    using var cts = new CancellationTokenSource(); cts.Cancel(); var summary = Clean(rows, token: cts.Token);
    Check(summary.Cancelled && summary.Deleted == 0 && File.Exists(file), "Cancellation ignored");
});
Test("Cancelled preview does not return a cleanable partial result", () =>
{
    var r = Rule("scan-cancel"); Write(r); using var cts = new CancellationTokenSource(); cts.Cancel();
    bool cancelled = false; try { Scan(r, token: cts.Token); } catch (OperationCanceledException) { cancelled = true; }
    Check(cancelled, "Scan did not cancel");
});
Test("Hardlinked files never enter cleanup preview", () =>
{
    var r = Rule("hardlink"); string file = Write(r);
    Check(TestNative.CreateHardLinkW(file + ".link", file, IntPtr.Zero), "Unable to create hardlink fixture");
    Check(Scan(r)[0].Count == 0, "Hardlinked files selected");
});
Test("Running related process blocks both preview and stale cleanup", () =>
{
    var r = Rule("running"); string file = Write(r); var rows = Scan(r);
    string current = System.Diagnostics.Process.GetCurrentProcess().ProcessName;
    var blocked = r with { Processes = [current] };
    Check(Scan(blocked)[0].Count == 0, "Active process did not block preview");
    var stale = new RuleResult(blocked); stale.Files.AddRange(rows[0].Files);
    var summary = Clean([stale]); Check(summary.Deleted == 0 && summary.Skipped == 1 && File.Exists(file), "Active process did not block cleanup");
});
Test("Audit creation failure aborts before deleting any file", () =>
{
    var r = Rule("audit-failure"); string file = Write(r); var rows = Scan(r);
    string blockedLog = Path.Combine(fixture, "not-a-directory"); File.WriteAllText(blockedLog, "fixture");
    bool aborted = false;
    try { cleaner.Clean(rows, [], blockedLog, null, default); } catch (IOException) { aborted = true; }
    Check(aborted && File.Exists(file), "Deletion proceeded without audit");
});
Test("Candidate outside the preview root is rejected at cleanup time", () =>
{
    var r = Rule("forged"); Write(r); var outside = Rule("outside"); string target = Write(outside);
    var row = new RuleResult(r); row.Files.Add(NativeFile.Snapshot(target, r.Paths[0], r.Id));
    var summary = Clean([row]); Check(summary.Deleted == 0 && summary.Skipped == 1 && File.Exists(target), "Root boundary bypassed");
});
Test("Tool-managed UV root cannot be converted to direct deletion by editing JSON", () =>
{
    var uv = new CleanupRule { Id = "uv", Name = "uv", Impact = "Official tool", Paths = [UvMaintenance.CacheRoot], AnalysisOnly = false };
    var row = new RuleResult(uv);
    Reject(() => new Cleaner(new PathPolicy()).Clean([row], [], Path.Combine(fixture, "logs"), null, default));
    var readonlyRow = new RuleResult(uv with { AnalysisOnly = true }); readonlyRow.Selected = true;
    Check(!readonlyRow.Selected, "Inspection-only row selectable");
});
Test("Official UV cleanup refuses exclusions inside or above the cache", () =>
{
    string root = Path.Combine(fixture, "managed-uv", "cache");
    Reject(() => UvMaintenance.ValidateExclusions(root, [Path.Combine(root, "keep")]));
    Reject(() => UvMaintenance.ValidateExclusions(root, [Path.GetDirectoryName(root)!]));
    UvMaintenance.ValidateExclusions(root, [root + "-sibling"]);
});
Test("Official UV operation uses fixed argument list and no shell", () =>
{
    var start = UvMaintenance.BuildStartInfo(@"C:\Tools With Spaces\uv.exe");
    Check(!start.UseShellExecute && start.CreateNoWindow && start.RedirectStandardError, "Unsafe process setup");
    Check(start.ArgumentList.SequenceEqual(new[] { "cache", "clean", "--cache-dir", UvMaintenance.CacheRoot }), "Incorrect UV command scope");
    Check(!start.ArgumentList.Contains("--force"), "Lock bypass enabled");
});
Test("Directory junction is never traversed or cleaned", () =>
{
    var r = Rule("junction"); string file = Write(r); var rows = Scan(r);
    string outside = Path.Combine(fixture, "outside-junction"); Directory.CreateDirectory(outside);
    string outsideFile = Path.Combine(outside, "old.tmp"); File.WriteAllText(outsideFile, "synthetic cache"); File.SetLastWriteTimeUtc(outsideFile, DateTime.UtcNow.AddDays(-30));
    // A local directory symbolic link needs developer mode or an enabled symlink privilege.
    // Use a junction via the Windows reparse API so the test works without global settings changes.
    Directory.Move(r.Paths[0], r.Paths[0] + ".original");
    TestNative.CreateJunction(r.Paths[0], outside);
    Check(Scan(r)[0].Count == 0, "Junction was traversed");
    var summary = Clean(rows); Check(summary.Deleted == 0 && File.Exists(outsideFile), "Junction target deleted");
});
Test("Analyzer aggregates directories, exclusions and largest files", () =>
{
    var r = Rule("analyze"); Write(r, "a/one.bin", content: new string('a', 100)); Write(r, "a/two.bin", content: new string('b', 40));
    Write(r, "root.bin", content: "12345"); string excluded = Write(r, "skip.bin", content: "123456789");
    var a = Scanner.Analyze(r.Paths[0], [excluded], null, default);
    Check(a.Size == 145 && a.FileCount == 3 && a.Folders.Count == 2 && a.LargestFiles[0].Size == 100, "Incorrect analysis totals");
    var sub = a.Folders.Single(x => x.Path.EndsWith("\\a")); Check(sub.Size == 140 && sub.FileCount == 2, "Subtree incorrect");
});
Test("Analyzer limits largest file collection and preserves partial results on cancel", () =>
{
    var r = Rule("bounded"); for (int i = 0; i < 210; i++) Write(r, i + ".tmp");
    var a = Scanner.Analyze(r.Paths[0], [], null, default); Check(a.LargestFiles.Count == 200 && a.FileCount == 210, "Top file bound failed");
    using var cts = new CancellationTokenSource(); cts.Cancel(); var stopped = Scanner.Analyze(r.Paths[0], [], null, cts.Token);
    Check(stopped.Cancelled && stopped.FileCount == 0, "Cancelled analysis not labeled");
});
Test("File type totals agree with directory totals and label root files", () =>
{
    var r = Rule("types"); Write(r, "a.TXT", content: "123"); Write(r, "sub/b.txt", content: "12345"); Write(r, "LICENSE", content: "1234567");
    var a = Scanner.Analyze(r.Paths[0], [], null, default);
    Check(a.FileTypes.Sum(x => x.Size) == a.Size && a.FileTypes.Sum(x => x.FileCount) == a.FileCount, "Type totals disagree");
    Check(a.FileTypes.Single(x => x.Path == ".txt").Size == 8, "Extensions not grouped case-insensitively");
    Check(a.Folders.Single(x => x.Path == r.Paths[0]).Name == "当前目录中的文件", "Root file label ambiguous");
});
Test("Treemap tiles preserve area, proportions and never overlap", () =>
{
    UsageEntry[] entries = [new("a", 60, 1), new("b", 30, 1), new("c", 10, 1), new("zero", 0, 0)];
    var tiles = TreemapLayout.Create(entries, 800, 150);
    Check(tiles.Count == 3 && Math.Abs(tiles.Sum(t => t.Width * t.Height) - 120000) < 0.01, "Map area incorrect");
    foreach (var t in tiles) Check(Math.Abs(t.Width * t.Height / 120000 - t.Entry.Size / 100.0) < 0.00001, "Map proportion incorrect");
    for (int i = 0; i < tiles.Count; i++) for (int j = i + 1; j < tiles.Count; j++)
    {
        var a = tiles[i]; var b = tiles[j];
        Check(Math.Min(a.X + a.Width, b.X + b.Width) - Math.Max(a.X, b.X) < 0.001 ||
            Math.Min(a.Y + a.Height, b.Y + b.Height) - Math.Max(a.Y, b.Y) < 0.001, "Map tiles overlap");
    }
    Check(TreemapLayout.Create(entries, 0, 150).Count == 0, "Zero-width map created");
});
Test("Numbered browser profiles discovered without arbitrary or linked directories", () =>
{
    string browser = Path.Combine(fixture, "profiles"); Directory.CreateDirectory(browser);
    foreach (string name in new[] { "Default", "Profile 1", "Profile 23", "Profile Other", "Guest Profile" }) Directory.CreateDirectory(Path.Combine(browser, name));
    string external = Path.Combine(fixture, "linked-profile"); Directory.CreateDirectory(external);
    TestNative.CreateJunction(Path.Combine(browser, "Profile 2"), external);
    var found = RuleResolver.DiscoverProfiles(browser, "Cache").ToArray();
    Check(found.Length == 2 && found.Any(x => x.Contains("Profile 23")) && !found.Any(x => x.Contains("Profile 2\\")), "Profile discovery scope incorrect");
});
Test("Paused scan blocks then resumes at a checkpoint", () =>
{
    using var control = new ScanControl(); control.Pause();
    using var arrived = new ManualResetEventSlim();
    var task = Task.Run(() => { arrived.Set(); control.Checkpoint(default); });
    Check(arrived.Wait(2000) && !task.Wait(100), "Pause did not block");
    control.Resume(); Check(task.Wait(2000), "Resume did not release checkpoint");
});
Test("Cancelling a paused scan releases the worker without needing resume", () =>
{
    using var control = new ScanControl(); using var cts = new CancellationTokenSource(); control.Pause();
    var task = Task.Run(() => { try { control.Checkpoint(cts.Token); return false; } catch (OperationCanceledException) { return true; } });
    cts.Cancel(); Check(task.Wait(2000) && task.Result, "Paused cancellation stuck");
});
Test("Duplicate finder matches content across names and does not change files", () =>
{
    var r = Rule("dupes"); string a = Write(r, "alpha.bin", content: new string('x', 20000));
    string b = Write(r, "sub/beta.other", content: new string('x', 20000)); Write(r, "unique.bin", content: new string('y', 22000));
    var result = DuplicateFinder.Find(r.Paths[0], [], 1, null, default);
    Check(result.Groups.Count == 1 && result.Groups[0].Count == 2 && result.Groups[0].ExtraLogicalBytes == 20000, "Duplicates not grouped");
    Check(result.Groups[0].Hash.Length == 64 && File.Exists(a) && File.Exists(b), "Invalid hash or files modified");
});
Test("Full SHA-256 separates files with identical head and tail samples", () =>
{
    var r = Rule("sample-collision"); Write(r, "a.bin", content: new string('x', 9000) + "A" + new string('x', 9000));
    Write(r, "b.bin", content: new string('x', 9000) + "B" + new string('x', 9000));
    Check(DuplicateFinder.Find(r.Paths[0], [], 1, null, default).Groups.Count == 0, "Sample falsely treated as full match");
});
Test("Unique sizes need no file-content reads and minimum size is respected", () =>
{
    var r = Rule("size-filter"); Write(r, "one", content: "a"); Write(r, "two", content: "bb"); Write(r, "three", content: "ccc");
    var result = DuplicateFinder.Find(r.Paths[0], [], 2, null, default);
    Check(result.Groups.Count == 0 && result.BytesRead == 0, "Unnecessary content reads");
});
Test("Duplicate finder respects exclusions and skips hardlinks", () =>
{
    var r = Rule("dupe-exclusions"); string a = Write(r, "a", content: "same"); Write(r, "b", content: "same");
    string excluded = Write(r, "excluded", content: "same");
    Check(TestNative.CreateHardLinkW(a + ".link", a, IntPtr.Zero), "Hardlink fixture failed");
    var result = DuplicateFinder.Find(r.Paths[0], [excluded], 1, null, default);
    Check(result.Groups.Count == 0 && result.Skipped >= 2, "Exclusion or hardlink safeguard ignored");
});
Test("Duplicate finder rejects changed files before reading contents", () =>
{
    var r = Rule("verify-reader"); string file = Write(r); var snapshot = NativeFile.Snapshot(file, r.Paths[0], "duplicates");
    File.AppendAllText(file, "changed"); Reject(() => { using var stream = NativeFile.OpenReadVerified(snapshot); });
});
Test("Cancelled duplicate scan is explicitly partial", () =>
{
    var r = Rule("dupe-cancel"); Write(r); using var cts = new CancellationTokenSource(); cts.Cancel();
    var result = DuplicateFinder.Find(r.Paths[0], [], 1, null, cts.Token);
    Check(result.Cancelled && result.Groups.Count == 0, "Cancelled scan not labeled");
});
Test("History summary reads a bounded tail instead of traversing a large audit", () =>
{
    string file = Path.Combine(fixture, "large-history.jsonl");
    File.WriteAllText(file, new string('a', 700000) + "\n{\"kind\":\"summary\",\"note\":\"中文\"}\r\n");
    Check(HistoryTail.ReadLastLine(file) == "{\"kind\":\"summary\",\"note\":\"中文\"}", "History tail incorrect");
});
Test("C++ rule filters non-ipch files in both preview and stale cleanup", () =>
{
    var r = Rule("cpp-types") with { FileExtensions = [".ipch"] };
    string valid = Write(r,"index.ipch"), protectedFile = Write(r,"history.db");
    Check(Scan(r)[0].Count == 1, "Database included");
    var forged = new RuleResult(r); forged.Files.Add(NativeFile.Snapshot(protectedFile, r.Paths[0], r.Id));
    var summary = Clean([forged]); Check(summary.Deleted == 0 && File.Exists(protectedFile), "File filter bypassed at deletion");
    Check(File.Exists(valid), "Unselected file changed");
});
Test("Built-in C++ protection cannot be bypassed by clearing JSON extension filter", () =>
{
    string root = Path.Combine(fixture,"Microsoft","vscode-cpptools","ipch");
    var r = Rule("cpp-hard-limit") with { Paths = [root] };
    Write(r,"cache.ipch"); Write(r,"important.db");
    Check(Scan(r)[0].Count == 1, "Hardcoded C++ type boundary failed");
});
Test("Catalog migration preserves modified and intentionally removed old rules", () =>
{
    var old = Rule("catalog-old") with { MinAgeDays = 30 }; var added = Rule("catalog-new");
    var deleted = Rule("catalog-deleted");
    var migrated = RuleCatalog.Upgrade(new(){ Rules = [old] }, new(){ Rules = [old with { MinAgeDays = 7 }, added, deleted] }, [old.Id,deleted.Id], policy);
    Check(migrated.Rules.Count == 2 && migrated.Rules[0].MinAgeDays == 30 && migrated.Rules[1].Id == added.Id, "User customization overwritten");
    var again = RuleCatalog.Upgrade(migrated, new(){ Rules = [old,added,deleted] }, [old.Id,added.Id,deleted.Id], policy);
    Check(again.Rules.Count == 2, "Repeated migration changed catalog");
});
Test("Catalog migration skips a new overlapping root instead of breaking existing rules", () =>
{
    var old = Rule("overlap-catalog"); var child = old with { Id = "new-overlap", Paths = [Path.Combine(old.Paths[0],"nested")] };
    var merged = RuleCatalog.Upgrade(new(){ Rules = [old] },new(){ Rules = [child] },[old.Id],policy);
    Check(merged.Rules.Count == 1, "Overlapping rule appended");
});
(InstallerCleanup Service, string Root, string Package, string File) InstallerFixture(string id, Func<string?>? block = null)
{
    string temp = Path.Combine(fixture,id,"Temp"), root = Path.Combine(temp,"bundle");
    string package = Path.Combine(root,"Microsoft.Test.Package.0123456789ABCDEF"); Directory.CreateDirectory(package);
    string file = Path.Combine(package,"payload.msi"); File.WriteAllText(file,"synthetic installer payload");
    return (new InstallerCleanup(temp, _ => block?.Invoke()),root,package,file);
}
Test("Unrelated pending rename records advise without blocking installer cleanup", () =>
{
    var status = InstallerSafety.CheckPendingOperations(new[] { @"*1\??\C:\Other\old.tmp", "", @"\??\E:\Other\old", @"!\??\E:\Other\new" }, [@"C:\Temp\bundle"]);
    Check(status.BlockReason is null && status.Advisory is not null, "Unrelated operation blocked cleanup");
    Check(InstallerSafety.CheckPendingOperations(null, [@"C:\Temp\bundle"]).BlockReason is null, "Missing value blocked cleanup");
    Check(InstallerSafety.CheckPendingOperations(Array.Empty<string>(), [@"C:\Temp\bundle"]).Advisory is null, "Empty queue advised pending restart");
});
Test("Pending operations involving a package source or destination block cleanup", () =>
{
    foreach (var pair in new[] {
        new[] { @"\??\C:\Temp\bundle\payload.msi", "" },
        new[] { @"C:\Other\new.msi", @"!\??\C:\Temp\bundle\payload.msi" },
        new[] { @"*1\??\c:\TEMP\bundle", "" },
        new[] { @"\\?\C:\Temp\bundle\payload.msi", "" } })
        Check(InstallerSafety.CheckPendingOperations(pair, [@"C:\Temp\bundle"]).BlockReason is not null, "Related operation was ignored");
});
Test("Pending operations on ancestor directories block but sibling prefixes do not", () =>
{
    foreach (string path in new[] { @"C:\Temp", @"C:\" })
        Check(InstallerSafety.CheckPendingOperations(new[] { path, "" }, [@"C:\Temp\bundle"]).BlockReason is not null, "Ancestor was ignored");
    Check(InstallerSafety.CheckPendingOperations(new[] { @"C:\Temp\bundle-other\payload.msi", "" }, [@"C:\Temp\bundle"]).BlockReason is null, "Sibling prefix overlapped");
    Check(InstallerSafety.CheckPendingOperations(new[] { @"C:\Temp\other\..\bundle\payload.msi", "" }, [@"C:\Temp\bundle"]).BlockReason is not null, "Relative segment escaped matching");
});
Test("Malformed or unsupported pending records fail closed", () =>
{
    object[] invalid = ["wrong registry type", new[] { @"C:\Temp\bundle" }, new[] { "", "" },
        new[] { @"\Device\HarddiskVolume3\Temp\file", "" }, new[] { @"*9\??\C:\Other\file", "" },
        new[] { @"relative\file", "" }, new[] { Path.Combine(fixture, "MISSING~1", "payload.msi"), "" }];
    foreach (object value in invalid)
        Check(InstallerSafety.CheckPendingOperations(value, [@"C:\Temp\bundle"]).BlockReason is not null, "Unverifiable queue permitted cleanup");
});
Test("Pending rename check uses only the selected package roots", () =>
{
    string[] queue = [@"C:\Temp\blocked\payload.msi", ""];
    Check(InstallerSafety.CheckPendingOperations(queue, [@"C:\Temp\safe"]).BlockReason is null, "Unselected root blocked cleanup");
    Check(InstallerSafety.CheckPendingOperations(queue, [@"C:\Temp\safe", @"C:\Temp\blocked"]).BlockReason is not null, "Selected blocked root ignored");
});
Test("Installer cleanup checks its actual roots before and during deletion", () =>
{
    var f = InstallerFixture("installer-root-check"); int calls = 0;
    var service = new InstallerCleanup(Path.GetDirectoryName(f.Root)!, roots =>
    {
        calls++; Check(roots.SequenceEqual(new[]{f.Root}), "Incorrect cleanup safety scope");
        return InstallerSafety.CheckPendingOperations(new[] { @"*1\??\C:\Other\old.tmp", "" }, roots).BlockReason;
    });
    var summary = service.Clean(service.Scan([], null, default), [], true, Path.Combine(fixture,"logs"), null, default);
    Check(summary.Deleted == 1 && calls == 2, "Unrelated queue blocked cleanup or checks were omitted");
});
Test("Related pending record aborts installer cleanup without deleting a file", () =>
{
    var f = InstallerFixture("installer-related-pending");
    var service = new InstallerCleanup(Path.GetDirectoryName(f.Root)!, roots =>
        InstallerSafety.CheckPendingOperations(new[] { @"\??\" + f.File, "" }, roots).BlockReason);
    Reject(() => service.Clean(service.Scan([], null, default), [], true, Path.Combine(fixture,"logs"), null, default));
    Check(File.Exists(f.File), "Related pending file deleted");
});
Test("Installer preview finds recent package payloads while excluding unrelated files", () =>
{
    var f = InstallerFixture("installer-preview"); File.WriteAllText(Path.Combine(f.Package,"personal.docx"),"keep");
    File.WriteAllText(Path.Combine(f.Root,"root.exe"),"keep");
    var plans = f.Service.Scan([],null,default);
    Check(plans.Count == 1 && plans[0].Count == 1 && plans[0].Files[0].Path == f.File, "Installer pattern too broad");
});
Test("Installer cleanup refuses absent completion acknowledgement", () =>
{
    var f = InstallerFixture("installer-confirm"); bool rejected = false;
    try { f.Service.Clean(f.Service.Scan([],null,default),[],false,Path.Combine(fixture,"logs"),null,default); }
    catch (InvalidOperationException) { rejected = true; }
    Check(rejected && File.Exists(f.File), "Unconfirmed cleanup allowed");
});
Test("Installer cleanup handles recent files only after explicit confirmation and audits them", () =>
{
    var f = InstallerFixture("installer-clean"); var plans = f.Service.Scan([],null,default);
    var summary = f.Service.Clean(plans,[],true,Path.Combine(fixture,"logs"),null,default);
    Check(summary.Deleted == 1 && !File.Exists(f.File) && Directory.Exists(f.Package), "Incorrect installer deletion");
    Check(File.ReadAllLines(summary.LogPath).Length == 4, "Missing intent/result/summary audit");
});
Test("Installer cleanup blocks pending restart or active installation", () =>
{
    var f = InstallerFixture("installer-block",()=>"Synthetic pending restart");
    Reject(()=>f.Service.Clean(f.Service.Scan([],null,default),[],true,Path.Combine(fixture,"logs"),null,default));
    Check(File.Exists(f.File), "Blocked installer deleted");
});
Test("Installer cleanup rechecks blockers during deletion", () =>
{
    int calls = 0; var f = InstallerFixture("installer-race",()=>++calls > 1 ? "Installer started" : null);
    var summary = f.Service.Clean(f.Service.Scan([],null,default),[],true,Path.Combine(fixture,"logs"),null,default);
    Check(summary.Deleted == 0 && summary.Skipped == 1 && File.Exists(f.File), "Late blocker ignored");
});
Test("Installer cleanup rechecks exclusions and file identity", () =>
{
    var f = InstallerFixture("installer-changed"); var plans = f.Service.Scan([],null,default); File.AppendAllText(f.File,"changed");
    var summary = f.Service.Clean(plans,[],true,Path.Combine(fixture,"logs"),null,default);
    Check(summary.Deleted == 0 && File.Exists(f.File), "Changed installer deleted");
    var fresh = f.Service.Scan([],null,default);
    var excluded = f.Service.Clean(fresh,[f.File],true,Path.Combine(fixture,"logs"),null,default);
    Check(excluded.Deleted == 0 && excluded.Skipped == 1, "Installer exclusion ignored");
});
Test("Installer cleanup rejects forged candidates outside direct Temp children", () =>
{
    var f = InstallerFixture("installer-escape"); string outside = Path.Combine(fixture,"outside-installer"); Directory.CreateDirectory(outside);
    string file = Path.Combine(outside,"payload.msi"); File.WriteAllText(file,"keep");
    var plan = new InstallerPlan(outside); plan.Files.Add(NativeFile.Snapshot(file,outside,"vs-installer-temp"));
    var summary = f.Service.Clean([plan],[],true,Path.Combine(fixture,"logs"),null,default);
    Check(summary.Deleted == 0 && summary.Skipped == 1 && File.Exists(file), "Installer boundary bypassed");
});
Test("Installer scan never follows a package junction", () =>
{
    var f = InstallerFixture("installer-junction");
    string target = Path.Combine(fixture,"installer-link-target");Directory.CreateDirectory(target);File.WriteAllText(Path.Combine(target,"payload.msi"),"keep");
    TestNative.CreateJunction(Path.Combine(f.Root,"Microsoft.Linked.0123456789ABCDEF"),target);
    Check(f.Service.Scan([],null,default).Single().Count == 1,"Installer scan followed junction");
});
Test("Installer cleanup preserves files on cancellation", () =>
{
    var f = InstallerFixture("installer-cancel"); var plans = f.Service.Scan([],null,default);
    using var cts = new CancellationTokenSource();cts.Cancel();
    var summary = f.Service.Clean(plans,[],true,Path.Combine(fixture,"logs"),null,cts.Token);
    Check(summary.Cancelled && summary.Deleted == 0 && File.Exists(f.File),"Installer cancellation ignored");
});
Test("Installer cleanup skips locked files without forcing deletion", () =>
{
    var f = InstallerFixture("installer-locked"); var plans = f.Service.Scan([],null,default);
    using var handle = new FileStream(f.File,FileMode.Open,FileAccess.Read,FileShare.None);
    var summary = f.Service.Clean(plans,[],true,Path.Combine(fixture,"logs"),null,default);
    Check(summary.Deleted == 0 && summary.Skipped == 1,"Installer file lock ignored");
});
(CleanupEngine Engine, PathPolicy Policy, RuleDocument Rules, string Ordinary, string Installer, string Root) UnifiedFixture(string id, Func<IReadOnlyList<string>, string?>? block = null)
{
    var f = InstallerFixture("unified-" + id); string temp = Path.GetDirectoryName(f.Root)!;
    var p = new PathPolicy(fixture, temp);
    var service = new InstallerCleanup(temp, block ?? (_ => null));
    var ordinary = Rule("temp-" + id) with { Paths = [temp] }; string old = Write(ordinary);
    File.SetLastWriteTimeUtc(f.File, DateTime.UtcNow.AddDays(-30));
    var install = new CleanupRule { Id = "installer-" + id, Name = "安装包", Handler = "vs-installer-temp", Category = "安装缓存",
        Paths = [temp], MinAgeDays = 0, Impact = "确认安装结束，修复可能重新下载。" };
    return (new CleanupEngine(p, service), p, new() { Rules = [ordinary, install] }, old, f.File, f.Root);
}
CleanupSummary UnifiedClean(CleanupEngine engine, List<RuleResult> rows, IReadOnlySet<string>? confirmed = null, string[]? excluded = null, CancellationToken token = default) =>
    engine.Clean(rows, excluded ?? [], confirmed ?? new HashSet<string>(), Path.Combine(fixture,"logs"), null, token);

Test("Unified scan assigns old installer payloads exclusively to their handler", () =>
{
    var f = UnifiedFixture("ownership"); var rows = f.Engine.Scan(f.Rules, [], null, default);
    Check(rows.Count == 2 && rows[0].Files.Single().Path == f.Ordinary && rows[1].Files.Single().Path == f.Installer, "Duplicate or missing ownership");
    Check(rows[1].Scope == f.Root && rows[1].RequiresConfirmation, "Installer directory lost its scope or confirmation");
    var ordinaryOnly = f.Engine.Scan(new() { Rules = [f.Rules.Rules[0]] }, [], null, default);
    Check(ordinaryOnly.Single().Files.Single().Path == f.Ordinary, "Removing installer JSON exposed payloads to normal cleaning");
});
Test("Ordinary deletion cannot bypass installer protection using a forged candidate", () =>
{
    var f = UnifiedFixture("bypass"); var ordinary = f.Rules.Rules[0];
    var row = new RuleResult(ordinary); row.Files.Add(NativeFile.Snapshot(f.Installer, ordinary.Paths[0], ordinary.Id));
    var summary = UnifiedClean(f.Engine, [row]);
    Check(summary.Deleted == 0 && summary.Skipped == 1 && File.Exists(f.Installer), "Ordinary cleanup bypassed handler checks");
});
Test("Unified cleanup refuses missing acknowledgements before deleting any selected file", () =>
{
    var f = UnifiedFixture("ack"); var rows = f.Engine.Scan(f.Rules, [], null, default); bool rejected = false;
    try { UnifiedClean(f.Engine, rows); } catch (InvalidOperationException) { rejected = true; }
    Check(rejected && File.Exists(f.Ordinary) && File.Exists(f.Installer), "Mixed batch partially deleted without acknowledgement");
});
Test("JSON cannot remove required installer confirmation or replace its meaning", () =>
{
    var f = UnifiedFixture("required-ack"); var rule = f.Rules.Rules[1] with { ReviewRequired = false, Confirmation = "额外条件" };
    var rows = f.Engine.Scan(new() { Rules = [rule] }, [], null, default);
    Check(rows.Single().RequiresConfirmation && rows[0].ConfirmationText.Contains("安装已完成") && rows[0].ConfirmationText.Contains("额外条件"), "Mandatory consent was overridden");
    bool rejected = false; try { UnifiedClean(f.Engine, rows, new HashSet<string> { "different-rule" }); } catch (InvalidOperationException) { rejected = true; }
    Check(rejected && File.Exists(f.Installer), "Unrelated rule acknowledgement authorized installer cleanup");
});
Test("Mixed cleanup shares one deletion pipeline and records required consent", () =>
{
    var f = UnifiedFixture("mixed"); var rows = f.Engine.Scan(f.Rules, [], null, default);
    var summary = UnifiedClean(f.Engine, rows, new HashSet<string> { f.Rules.Rules[1].Id });
    Check(summary.Deleted == 2 && summary.Skipped == 0 && !File.Exists(f.Ordinary) && !File.Exists(f.Installer), "Mixed cleanup failed");
    string[] lines = File.ReadAllLines(summary.LogPath);
    using var start = JsonDocument.Parse(lines[0]);
    Check(lines.Length == 6 && start.RootElement.GetProperty("confirmations")[0].GetProperty("Text").GetString()!.Contains("安装已完成"), "Common audit missing consent or per-file records");
});
Test("Multiple installer scopes share a rule while remaining independently cleanable", () =>
{
    var f = UnifiedFixture("scopes"); string temp = Path.GetDirectoryName(f.Root)!;
    string second = Path.Combine(temp,"second","Microsoft.Other.0123456789ABCDEF","payload.msi");
    Directory.CreateDirectory(Path.GetDirectoryName(second)!); File.WriteAllText(second,"second synthetic package");
    var rows = f.Engine.Scan(f.Rules, [], null, default);
    var installRows = rows.Where(r => r.Rule.Handler == "vs-installer-temp").ToList();
    Check(installRows.Count == 2, "Installer roots merged into one selection");
    using var stop = new CancellationTokenSource(); stop.Cancel();
    Check(UnifiedClean(f.Engine, installRows, new HashSet<string> { f.Rules.Rules[1].Id }, token: stop.Token).Cancelled,
        "Multiple scopes with a shared rule failed batch validation");
    var selected = installRows.Where(r => r.Scope == f.Root).ToList();
    var summary = UnifiedClean(f.Engine, selected, new HashSet<string> { f.Rules.Rules[1].Id });
    Check(summary.Deleted == 1 && File.Exists(second) && File.Exists(f.Ordinary), "Unselected scope was cleaned");
    var fresh = f.Engine.Scan(f.Rules, [], null, default).Where(r => r.Count > 0).ToList();
    Check(UnifiedClean(f.Engine, fresh, new HashSet<string> { f.Rules.Rules[1].Id }).Deleted == 2, "Shared rule scopes invalidated common cleanup");
});
Test("Related pending operations block only their installer row and refresh clears stale blockers", () =>
{
    bool blocked = true;
    var f = UnifiedFixture("state", _ => blocked ? "Synthetic pending restart" : null);
    var rows = f.Engine.Scan(f.Rules, [], null, default);
    Check(rows[0].BlockReason is null && rows[1].BlockReason is not null, "Installer state blocked ordinary files");
    Reject(() => UnifiedClean(f.Engine, rows, new HashSet<string> { f.Rules.Rules[1].Id }));
    Check(File.Exists(f.Ordinary), "Blocked batch began deleting ordinary files");
    blocked = false; f.Engine.RefreshState(rows);
    Check(rows[1].BlockReason is null && UnifiedClean(f.Engine, rows, new HashSet<string> { f.Rules.Rules[1].Id }).Deleted == 2, "State refresh did not unblock cleanup");
});
Test("Installer handler still rejects changes, locks and new exclusions in the common pipeline", () =>
{
    var f = UnifiedFixture("stale"); var rows = f.Engine.Scan(f.Rules, [], null, default);
    File.AppendAllText(f.Installer,"changed");
    var summary = UnifiedClean(f.Engine, rows, new HashSet<string> { f.Rules.Rules[1].Id }, [f.Ordinary]);
    Check(summary.Deleted == 0 && summary.Skipped == 2, "Stale installer or new exclusion ignored");
    var fresh = f.Engine.Scan(f.Rules, [], null, default).Where(r => r.Rule.Handler == "vs-installer-temp").ToList();
    using var handle = new FileStream(f.Installer,FileMode.Open,FileAccess.Read,FileShare.None);
    Check(UnifiedClean(f.Engine, fresh, new HashSet<string> { f.Rules.Rules[1].Id }).Skipped == 1, "Unified installer handler forced a lock");
});
Test("Unknown handlers, invalid installer roots and ordinary zero-day rules fail validation", () =>
{
    var f = UnifiedFixture("validation");
    foreach (var rule in new[] { f.Rules.Rules[0] with { Handler = "run-command" }, f.Rules.Rules[0] with { MinAgeDays = 0 },
        f.Rules.Rules[1] with { Paths = [Path.GetDirectoryName(f.Root)! + "-other"] }, f.Rules.Rules[1] with { MinAgeDays = 7 } })
        Reject(() => f.Policy.Validate(new() { Rules = [rule] }));
    Reject(() => new Scanner(f.Policy).Scan(f.Rules, [], null, default));
});
Test("Rule migration adds the installer handler beside Temp without changing user edits", () =>
{
    var f = UnifiedFixture("migration"); var ordinary = f.Rules.Rules[0] with { MinAgeDays = 40, Name = "My customized Temp" };
    var migrated = RuleCatalog.Upgrade(new() { Rules = [ordinary] }, f.Rules, [ordinary.Id], f.Policy);
    Check(migrated.Rules.Count == 2 && migrated.Rules[0].MinAgeDays == 40 && migrated.Rules[0].Name == ordinary.Name, "Handler migration lost or overwrote existing rules");
    var deleted = RuleCatalog.Upgrade(new() { Rules = [ordinary] }, f.Rules, f.Rules.Rules.Select(r => r.Id), f.Policy);
    Check(deleted.Rules.Count == 1, "Explicitly removed handler rule was restored");
});
Test("New ordinary JSON rule needs no UI or handler changes and can impose extra consent", () =>
{
    var r = Rule("json-added") with { Confirmation = "确认缓存可以重新下载。" }; string file = Write(r);
    string json = JsonSerializer.Serialize(new RuleDocument { Rules = [r] }, JsonFiles.Options);
    var parsed = JsonSerializer.Deserialize<RuleDocument>(json, JsonFiles.Options)!;
    var engine = new CleanupEngine(policy); var rows = engine.Scan(parsed, [], null, default);
    Check(rows.Single().Count == 1 && rows[0].RequiresConfirmation, "JSON addition did not join common flow");
    Check(UnifiedClean(engine, rows, new HashSet<string> { r.Id }).Deleted == 1 && !File.Exists(file), "JSON-added rule could not clean");
});
Test("Read-only and cancelled unified operations preserve installer payloads", () =>
{
    var f = UnifiedFixture("readonly"); var readonlyRule = f.Rules.Rules[1] with { AnalysisOnly = true };
    var rows = f.Engine.Scan(new() { Rules = [readonlyRule] }, [], null, default);
    Reject(() => UnifiedClean(f.Engine, rows, new HashSet<string> { readonlyRule.Id }));
    using var cts = new CancellationTokenSource(); cts.Cancel();
    bool cancelled = false; try { f.Engine.Scan(f.Rules, [], null, cts.Token); } catch (OperationCanceledException) { cancelled = true; }
    Check(cancelled && File.Exists(f.Installer), "Cancelled scan offered or performed cleanup");
});

Test("Filename filters narrow both preview and stale cleanup", () =>
{
    var r = Rule("names") with { FileNamePatterns = ["report-??.log"] };
    string good = Write(r,"report-01.log"), keep = Write(r,"report-private.db");
    Check(Scan(r).Single().Files.Single().Path == good, "Filename pattern ignored during preview");
    var forged = new RuleResult(r); forged.Files.Add(NativeFile.Snapshot(keep,r.Paths[0],r.Id));
    Check(Clean([forged]).Skipped == 1 && File.Exists(keep), "Filename filter bypassed during deletion");
    foreach (string pattern in new[] { @"..\*.log", "../*.log", "a:stream", ".", "[ab]*", "" })
        Reject(() => policy.Validate(new() { Rules = [r with { FileNamePatterns = [pattern] }] }));
});
Test("QQ diagnostic guard survives clearing editable filename filters", () =>
{
    var isolated = new PathPolicy(fixture,processCheck:_=>false);
    var r = Rule("qq-log-guard") with { Paths = [Path.Combine(fixture,"diagnostics","Tencent","Logs")] };
    string good = Write(r,"QQ.tlg.2"), keep = Write(r,"Msg3.0.db");
    Write(r,"QQ.tlg.config"); Write(r,"sub/QQ.tlg.1");
    Check(new Scanner(isolated).Scan(new(){Rules=[r]},[],null,default).Single().Files.Single().Path == good, "QQ log guard too broad");
    var forged = new RuleResult(r); forged.Files.Add(NativeFile.Snapshot(keep,r.Paths[0],r.Id));
    Check(new Cleaner(isolated).Clean([forged],[],Path.Combine(fixture,"logs"),null,default).Skipped == 1 && File.Exists(keep), "QQ database guard bypassed");
});
Test("WeChat only admits dated historical xlogs and excludes mapped databases", () =>
{
    var isolated = new PathPolicy(fixture,processCheck:_=>false);
    var r = Rule("wechat-log-guard") with { Paths = [Path.Combine(fixture,"diagnostics","Tencent","xwechat","log")] };
    Write(r,"mm_20260101.xlog"); Write(r,"radium/host_20260101.xlog");
    string mapped = Write(r,"mm.mmap3"); Write(r,"mm.mmap"); Write(r,"message_0.db"); Write(r,"unknown.xlog"); Write(r,"sub/mm_20260101.xlog");
    Check(new Scanner(isolated).Scan(new(){Rules=[r]},[],null,default).Single().Count == 2, "WeChat mappings or unknown log scope selected");
    var forged = new RuleResult(r); forged.Files.Add(NativeFile.Snapshot(mapped,r.Paths[0],r.Id));
    Check(new Cleaner(isolated).Clean([forged],[],Path.Combine(fixture,"logs"),null,default).Skipped == 1 && File.Exists(mapped), "WeChat mapping deleted");
});
Test("WeMeet restricts logs to the known leaf directory", () =>
{
    var r = Rule("wemeet-log-guard") with { Paths = [Path.Combine(fixture,"diagnostics","Tencent","WeMeet","Global","Logs")] };
    Write(r,"canary_2026010100.log"); Write(r,"recording.mp4"); Write(r,"sub/canary.log");
    Check(new Scanner(new PathPolicy(fixture,processCheck:_=>false)).Scan(new(){Rules=[r]},[],null,default).Single().Count == 1, "WeMeet scope included recordings or nested directories");
});
Test("Production paths permit only explicit Tencent logs and supported browser cache leaves", () =>
{
    var production = new PathPolicy(); string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    foreach (string suffix in new[] { @"Tencent\Logs", @"Tencent\xwechat\log", @"Tencent\WeMeet\Global\Logs" })
        Check(production.IsAllowedRoot(Path.Combine(roaming,suffix)), "Known log root rejected");
    foreach (string suffix in new[] { @"Tencent", @"Tencent\xwechat", @"Tencent\QQ\webkit_cache" })
        Check(!production.IsAllowedRoot(Path.Combine(roaming,suffix)), "Personal or cookie data allowed");
    foreach (var browser in BrowserCaches.Bases(local))
    {
        Check(production.IsAllowedRoot(Path.Combine(browser.Root,"Profile 2","Code Cache")), "Numbered profile rejected");
        Check(production.IsAllowedRoot(Path.Combine(browser.Root,"GrShaderCache")), "Shader root rejected");
        foreach (string leaf in new[] { "", "Default", @"Default\Service Worker\CacheStorage", @"Default\Storage\Cache", "Cookies" })
            Check(!production.IsAllowedRoot(Path.Combine(browser.Root,leaf)), "Browser data root allowed");
    }
});

(PathPolicy Policy, CleanupRule Rule, string Old, string Recent) BrowserFixture(string id)
{
    string local = Path.Combine(fixture,id,"Local"); var p = new PathPolicy(fixture,localRoot:local,
        processCheck:names=>Processes.AnyRunning(names.Where(n=>!new[]{"chrome","msedge","360ChromeX"}.Contains(n,StringComparer.OrdinalIgnoreCase))));
    var r = Rule(id) with { Paths = [Path.Combine(local,"360ChromeX","Chrome","User Data","Default","Cache")] };
    return (p,r,Write(r,"old.bin"),Write(r,"recent.bin",0));
}
Test("Recent browser mode is opt-in and leaves ordinary temp and diagnostic retention intact", () =>
{
    var f = BrowserFixture("browser-optin"); var engine = new CleanupEngine(f.Policy);
    Check(engine.Scan(new(){Rules=[f.Rule]},[],null,default).Single().Count == 1, "Default browser retention changed");
    var deep = engine.Scan(new(){Rules=[f.Rule]},[],null,default,options:new(true)).Single();
    Check(deep.Count == 2 && deep.IncludeRecentFiles && deep.RequiresConfirmation, "Deep cache mode failed or omitted consent");
    var ordinary = Rule("non-browser-recent"); Write(ordinary,"old.tmp"); Write(ordinary,"new.tmp",0);
    var normal = new CleanupEngine(policy).Scan(new(){Rules=[ordinary]},[],null,default,options:new(true)).Single();
    Check(normal.Count == 1 && !normal.IncludeRecentFiles, "Recent option broadened ordinary cleanup");
    var logs = ordinary with { Id="recent-logs", Paths=[Path.Combine(fixture,"recentLogs","Tencent","Logs")] };
    Write(logs,"QQ.tlg",0);
    Check(new CleanupEngine(policy).Scan(new(){Rules=[logs]},[],null,default,options:new(true)).Single().Count == 0, "Recent option broadened diagnostics");
});
Test("Recent browser cleanup refuses absent consent and then audits explicit recent mode", () =>
{
    var f = BrowserFixture("browser-consent"); var engine = new CleanupEngine(f.Policy);
    var rows = engine.Scan(new(){Rules=[f.Rule]},[],null,default,options:new(true)); bool rejected=false;
    try { UnifiedClean(engine,rows); } catch(InvalidOperationException) { rejected=true; }
    Check(rejected && File.Exists(f.Recent), "Unconfirmed recent cleanup started");
    var summary = UnifiedClean(engine,rows,new HashSet<string>{f.Rule.Id});
    Check(summary.Deleted == 2 && !File.Exists(f.Recent), "Confirmed recent cleanup failed");
    using var start = JsonDocument.Parse(File.ReadLines(summary.LogPath).First());
    Check(start.RootElement.GetProperty("projects")[0].GetProperty("IncludeRecentFiles").GetBoolean(), "Recent scope absent from audit");
});
Test("Forged recent flag cannot bypass ordinary retention even with confirmation", () =>
{
    var r = Rule("forged-recent"); string file = Write(r,age:0);
    var row = new RuleResult(r){IncludeRecentFiles=true};row.Files.Add(NativeFile.Snapshot(file,r.Paths[0],r.Id));
    Reject(()=>UnifiedClean(new CleanupEngine(policy),[row],new HashSet<string>{r.Id}));
    Check(File.Exists(file), "Forged recent mode deleted ordinary temp");
});
Test("Browser and diagnostic process guards do not rely on editable JSON process lists", () =>
{
    var f=BrowserFixture("mandatory-processes"); Check(f.Policy.BlockingProcesses(f.Rule).Contains("360ChromeX"), "Missing browser owner guard");
    var log = Rule("owner-log") with { Paths=[Path.Combine(fixture,"owners","Tencent","xwechat","log")], Processes=[] };
    Check(policy.BlockingProcesses(log).Contains("Weixin") && policy.BlockingProcesses(log).Contains("WeChat"), "Missing WeChat guards");
    var running = f.Rule with { Processes=[System.Diagnostics.Process.GetCurrentProcess().ProcessName] };
    Check(new CleanupEngine(f.Policy).Scan(new(){Rules=[running]},[],null,default,options:new(true)).Single().Count==0,"Active process did not block recent preview");
});
Test("Recent browser cleanup retains exclusions, identity and exclusive-lock checks", () =>
{
    var f=BrowserFixture("recent-checks");var engine=new CleanupEngine(f.Policy);var rows=engine.Scan(new(){Rules=[f.Rule]},[],null,default,options:new(true));
    File.AppendAllText(f.Recent,"changed");
    var summary=UnifiedClean(engine,rows,new HashSet<string>{f.Rule.Id},[f.Old]);
    Check(summary.Deleted==0 && summary.Skipped==2,"Recent mode bypassed exclusion or identity");
    rows=engine.Scan(new(){Rules=[f.Rule]},[],null,default,options:new(true));
    using var handle=new FileStream(f.Recent,FileMode.Open,FileAccess.Read,FileShare.None);
    summary=UnifiedClean(engine,rows,new HashSet<string>{f.Rule.Id});
    Check(summary.Deleted==1 && summary.Skipped==1,"Recent mode forced locked file deletion");
});
Test("Recent mode rejects mixed or arbitrary cache roots and supports normalized browser casing", () =>
{
    var f=BrowserFixture("recent-boundaries");
    Check(f.Policy.AllowsRecentBrowserFiles(f.Rule with {Paths=[f.Rule.Paths[0].ToLowerInvariant()]}),"Normalized casing rejected");
    foreach(var r in new[]{ f.Rule with {Paths=[Path.Combine(fixture,"Unknown","Cache")]}, f.Rule with {Paths=[f.Rule.Paths[0],Path.Combine(fixture,"Temp")]}, f.Rule with {AnalysisOnly=true} })
        Check(!f.Policy.AllowsRecentBrowserFiles(r),"Unsupported cache scope broadened");
});
Test("Catalog upgrade adds new log and shader rules without overwriting or restoring deletions", () =>
{
    var p=new PathPolicy();var shipped=JsonFiles.Read<RuleDocument>("rules/default.rules.json");
    string[] newIds=["qq-diagnostic-logs","wechat-diagnostic-logs","wemeet-diagnostic-logs","360chromex","chrome-shaders"];
    var previous=shipped with {Rules=shipped.Rules.Where(r=>!newIds.Contains(r.Id)).ToList()};
    previous.Rules[0]=previous.Rules[0] with {MinAgeDays=44};
    var upgraded=RuleCatalog.Upgrade(previous,shipped,previous.Rules.Select(r=>r.Id),p);
    Check(upgraded.Rules.Count==19 && upgraded.Rules[0].MinAgeDays==44,"New rules not added or user edit overwritten");
    var removed=upgraded with {Rules=upgraded.Rules.Where(r=>r.Id!="qq-diagnostic-logs").ToList()};
    Check(RuleCatalog.Upgrade(removed,shipped,shipped.Rules.Select(r=>r.Id),p).Rules.Count==18,"Deleted log rule was restored");
});

Test("360 numbered profile discovery integrates without traversing junctions or arbitrary profiles", () =>
{
    var f=BrowserFixture("browser-profile-discovery"); string browser=Path.GetDirectoryName(Path.GetDirectoryName(f.Rule.Paths[0])!)!;
    Directory.CreateDirectory(Path.Combine(browser,"Profile 1","Cache"));Directory.CreateDirectory(Path.Combine(browser,"Profile Other","Cache"));
    string target=Path.Combine(fixture,"external-browser-profile");Directory.CreateDirectory(target);
    TestNative.CreateJunction(Path.Combine(browser,"Profile 2"),target);
    string local=Path.Combine(fixture,"browser-profile-discovery","Local");
    var resolved=RuleResolver.Resolve(new(){Rules=[f.Rule]},local);
    Check(resolved.Rules.Single().Paths.Length==2 && resolved.Rules[0].Paths.Any(p=>p.Contains("Profile 1")),"360 profiles not discovered or junction traversed");
    f.Policy.Validate(resolved);
});
Test("Mandatory browser process detection blocks both preview and stale cleanup with empty JSON processes", () =>
{
    var f=BrowserFixture("mandatory-runtime"); bool active=false;
    string local=Path.Combine(fixture,"mandatory-runtime","Local");
    var p=new PathPolicy(fixture,localRoot:local,processCheck:names=>active && names.Contains("360ChromeX"));
    var engine=new CleanupEngine(p); var rows=engine.Scan(new(){Rules=[f.Rule]},[],null,default,options:new(true));
    active=true;
    Check(engine.Scan(new(){Rules=[f.Rule]},[],null,default,options:new(true)).Single().Count==0,"Mandatory browser process failed to block preview");
    Reject(()=>UnifiedClean(engine,rows,new HashSet<string>{f.Rule.Id}));
    Check(File.Exists(f.Recent) && File.Exists(f.Old),"Active browser did not block stale cleanup");
});

Console.WriteLine($"\n{passed} passed; {failed} failed. Fixture: {fixture}");
return failed == 0 ? 0 : 1;

internal static class TestNative
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CreateHardLinkW(string file, string existing, IntPtr security);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(string file, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(Microsoft.Win32.SafeHandles.SafeFileHandle handle, uint control, byte[] input, uint length, IntPtr output, uint outputSize, out uint returned, IntPtr overlapped);
    internal static void CreateJunction(string path, string target)
    {
        Directory.CreateDirectory(path);
        byte[] substitute = System.Text.Encoding.Unicode.GetBytes(@"\??\" + target);
        byte[] print = System.Text.Encoding.Unicode.GetBytes(target);
        byte[] buffer = new byte[16 + substitute.Length + 2 + print.Length + 2];
        BitConverter.GetBytes(0xA0000003u).CopyTo(buffer, 0);
        BitConverter.GetBytes((ushort)(buffer.Length - 8)).CopyTo(buffer, 4);
        BitConverter.GetBytes((ushort)substitute.Length).CopyTo(buffer, 10);
        BitConverter.GetBytes((ushort)(substitute.Length + 2)).CopyTo(buffer, 12);
        BitConverter.GetBytes((ushort)print.Length).CopyTo(buffer, 14);
        substitute.CopyTo(buffer, 16); print.CopyTo(buffer, 18 + substitute.Length);
        using var h = CreateFileW(path, 0x40000000, 0, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (!DeviceIoControl(h, 0x000900A4, buffer, (uint)buffer.Length, IntPtr.Zero, 0, out _, IntPtr.Zero))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }
}
