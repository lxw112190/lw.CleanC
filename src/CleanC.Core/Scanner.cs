using System.Diagnostics;

namespace CleanC.Core;

public static class Processes
{
    public static bool AnyRunning(IEnumerable<string> names)
    {
        foreach (string name in names)
        {
            var processes = Process.GetProcessesByName(name);
            bool running = processes.Length > 0;
            foreach (var p in processes) p.Dispose();
            if (running) return true;
        }
        return false;
    }
}

public sealed class Scanner(PathPolicy policy)
{
    public const int CandidateLimit = 100_000;
    public List<RuleResult> Scan(RuleDocument document, IReadOnlyList<string> exclusions,
        IProgress<ScanProgress>? progress, CancellationToken token, ScanControl? control = null)
    {
        policy.Validate(document);
        var results = new List<RuleResult>(); int count = 0; long examined = 0;
        var timer = Stopwatch.StartNew();
        foreach (var rule in document.Rules)
        {
            control?.Checkpoint(token); token.ThrowIfCancellationRequested();
            var effective = rule with { AnalysisOnly = rule.AnalysisOnly || rule.Paths.Any(p => policy.RequiresOfficialTool(PathPolicy.ExpandRoot(p))) };
            var result = new RuleResult(effective); results.Add(result);
            if (effective.AnalysisOnly) result.Notes.Add("仅统计 · 请使用官方清理入口");
            progress?.Report(new($"正在扫描 · {rule.Name}", count));
            if (Processes.AnyRunning(rule.Processes)) { result.Notes.Add("相关程序正在运行，关闭后重新扫描"); continue; }
            bool found = false;
            foreach (string raw in rule.Paths)
            {
                string root = PathPolicy.ExpandRoot(raw);
                if (!Directory.Exists(root)) continue;
                found = true;
                try { PathPolicy.EnsureNoReparsePoints(root); }
                catch (Exception e) when (IsFileError(e)) { result.Notes.Add("路径不可访问或包含链接"); continue; }
                foreach (string path in Walk(root, exclusions, () => Note(result, "部分文件不可访问或已跳过链接"), token, control))
                {
                    examined++;
                    if (timer.ElapsedMilliseconds >= 150)
                    {
                        progress?.Report(new($"正在扫描 · {rule.Name} · {count:N0} 个候选", examined)); timer.Restart();
                    }
                    try
                    {
                        if (count >= CandidateLimit) { result.Notes.Add("达到 10 万文件预览上限，请分批清理后重新扫描"); break; }
                        var file = NativeFile.Snapshot(path, root, rule.Id);
                        if (file.LastWrite > DateTime.UtcNow.AddDays(-rule.MinAgeDays).ToFileTimeUtc()) continue;
                        result.Files.Add(file); count++;
                        if (count % 512 == 0) progress?.Report(new($"正在扫描 · {rule.Name}", count));
                    }
                    catch (Exception e) when (IsFileError(e)) { Note(result, "部分文件不可访问或已跳过链接"); }
                }
            }
            if (!found) result.Notes.Add("未发现目录");
            else if (result.Files.Count == 0 && result.Notes.Count == 0) result.Notes.Add("没有超过保留天数的文件");
            else if (result.Notes.Count == 0) result.Notes.Add($"保留最近 {rule.MinAgeDays} 天");
        }
        return results;
    }

    private static void Note(RuleResult result, string text) { if (!result.Notes.Contains(text)) result.Notes.Add(text); }

    internal static bool IsFileError(Exception e) => e is IOException or InvalidDataException or UnauthorizedAccessException or
        System.ComponentModel.Win32Exception or System.Security.SecurityException;

    internal static IEnumerable<string> Walk(string root, IReadOnlyList<string> exclusions, Action skipped, CancellationToken token, ScanControl? control = null)
    {
        var dirs = new Stack<string>(); dirs.Push(root);
        while (dirs.TryPop(out string? dir))
        {
            control?.Checkpoint(token); token.ThrowIfCancellationRequested();
            if (PathPolicy.IsExcluded(dir, exclusions)) continue;
            IEnumerator<string> entries;
            try
            {
                PathPolicy.EnsureNoReparsePoints(dir);
                entries = Directory.EnumerateFileSystemEntries(dir).GetEnumerator();
            }
            catch (Exception e) when (IsFileError(e)) { skipped(); continue; }
            using (entries)
            while (true)
            {
                control?.Checkpoint(token); token.ThrowIfCancellationRequested();
                string path;
                try { if (!entries.MoveNext()) break; path = entries.Current; }
                catch (Exception e) when (IsFileError(e)) { skipped(); break; }
                if (PathPolicy.IsExcluded(path, exclusions)) continue;
                FileAttributes attributes;
                try { attributes = File.GetAttributes(path); }
                catch (Exception e) when (IsFileError(e)) { skipped(); continue; }
                if (((uint)attributes & (0x400u | 0x1000u | 0x40000u | 0x400000u)) != 0) { skipped(); continue; }
                if ((attributes & FileAttributes.Directory) != 0) dirs.Push(path);
                else yield return path;
            }
        }
    }

    public static AnalysisResult Analyze(string root, IReadOnlyList<string> exclusions,
        IProgress<ScanProgress>? progress, CancellationToken token, ScanControl? control = null)
    {
        root = PathPolicy.Normalize(root);
        PathPolicy.EnsureNoReparsePoints(root);
        var totals = new Dictionary<string, (long Bytes, long Count)>(StringComparer.OrdinalIgnoreCase);
        var types = new Dictionary<string, (long Bytes, long Count)>(StringComparer.OrdinalIgnoreCase);
        var largest = new PriorityQueue<UsageEntry, long>();
        long count = 0, size = 0, skipped = 0; bool cancelled = false;
        try
        {
            foreach (string path in Walk(root, exclusions, () => skipped++, token, control))
            {
                try
                {
                    var info = new FileInfo(path); long bytes = info.Length;
                    string relative = Path.GetRelativePath(root, path);
                    int separator = relative.IndexOf(Path.DirectorySeparatorChar);
                    string group = separator < 0 ? root : Path.Combine(root, relative[..separator]);
                    var previous = totals.GetValueOrDefault(group);
                    totals[group] = (previous.Bytes + bytes, previous.Count + 1);
                    string extension = Path.GetExtension(path).ToLowerInvariant();
                    if (extension.Length == 0) extension = "无扩展名";
                    // Arbitrary filenames can have distinct extensions. Bound the aggregation map on large drives.
                    if (!types.ContainsKey(extension) && types.Count >= 2048) extension = "其他类型";
                    var previousType = types.GetValueOrDefault(extension);
                    types[extension] = (previousType.Bytes + bytes, previousType.Count + 1);
                    largest.Enqueue(new(path, bytes, 1), bytes);
                    if (largest.Count > 200) largest.Dequeue();
                    count++; size += bytes;
                    if (count % 512 == 0) progress?.Report(new($"正在分析 · {path}", count));
                }
                catch (Exception e) when (IsFileError(e)) { skipped++; }
            }
        }
        catch (OperationCanceledException) { cancelled = true; }
        return new(root, totals.Select(k => new UsageEntry(k.Key, k.Value.Bytes, k.Value.Count)
            { Percent = size == 0 ? 0 : k.Value.Bytes * 100.0 / size, Label = k.Key.Equals(root, StringComparison.OrdinalIgnoreCase) ? "当前目录中的文件" : null }).OrderByDescending(x => x.Size).ToList(),
            largest.UnorderedItems.Select(x => x.Element).OrderByDescending(x => x.Size).ToList(), size, count, skipped, cancelled)
        { FileTypes = types.Select(k => new UsageEntry(k.Key, k.Value.Bytes, k.Value.Count)
            { Percent = size == 0 ? 0 : k.Value.Bytes * 100.0 / size }).OrderByDescending(x => x.Size).ToList() };
    }
}
