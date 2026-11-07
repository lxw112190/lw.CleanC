using System.Runtime.InteropServices;
using System.Text.Json;
using CleanC.Core;

if (args.Contains("--scan-installers"))
{
    using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(60));
    var plans = new InstallerCleanup().Scan([], null, limit.Token);
    foreach (var plan in plans) Console.WriteLine($"{plan.Root}: {plan.SizeText}, {plan.Count} files");
    Console.WriteLine("Read-only installer scan: " + Display.Bytes(plans.Sum(p => p.Size)) + "; " + (InstallerCleanup.GetBlockReason() ?? "No installer/reboot block detected; user confirmation still required."));
    return 0;
}

if (args.Contains("--scan-local"))
{
    using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    try
    {
        var rules = RuleResolver.Resolve(JsonFiles.Read<RuleDocument>("rules/default.rules.json"));
        var rows = new Scanner(new PathPolicy()).Scan(rules, [], null, limit.Token);
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
    return (new InstallerCleanup(temp, block ?? (() => null)),root,package,file);
}
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
