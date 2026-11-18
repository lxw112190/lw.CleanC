using System.Text.Json;

namespace CleanC.Core;

public sealed class Cleaner
{
    private readonly PathPolicy policy;
    private readonly CleanupHandlers handlers;
    public Cleaner(PathPolicy policy) : this(policy, new CleanupHandlers(policy)) { }
    internal Cleaner(PathPolicy policy, CleanupHandlers handlers) { this.policy = policy; this.handlers = handlers; }
    public CleanupSummary Clean(IReadOnlyList<RuleResult> selected, IReadOnlyList<string> exclusions,
        string logDirectory, IProgress<ScanProgress>? progress, CancellationToken token, IReadOnlySet<string>? confirmedRules = null)
    {
        policy.Validate(new RuleDocument { Rules = selected.Select(x => x.Rule).Distinct().ToList() });
        if (selected.Any(r => r.IncludeRecentFiles && !policy.AllowsRecentBrowserFiles(r.Rule)))
            throw new InvalidDataException("仅验证过的浏览器缓存允许包含近期文件。");
        if (selected.Any(x => x.Rule.AnalysisOnly || x.Rule.Paths.Any(p => policy.RequiresOfficialTool(PathPolicy.ExpandRoot(p)))))
            throw new InvalidDataException("此项必须使用应用官方清理工具，不支持逐文件删除。");
        foreach (var row in selected.Where(r => r.RequiresConfirmation))
        {
            if (confirmedRules?.Contains(row.Rule.Id) != true) throw new InvalidOperationException($"尚未确认清理条件：{row.Rule.Name}");
            if (handlers.For(row.Rule).Check(row).BlockReason is { } reason) throw new IOException(reason);
        }
        Directory.CreateDirectory(logDirectory);
        string logPath = Path.Combine(logDirectory, $"{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.jsonl");
        using var stream = new FileStream(logPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        using var log = new StreamWriter(stream) { AutoFlush = true };
        void Audit(object entry)
        {
            log.WriteLine(JsonSerializer.Serialize(entry));
            stream.Flush(true); // An intent is durable before each deletion.
        }
        var drives = selected.SelectMany(x => x.Files).Select(x => Path.GetPathRoot(x.Path)!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        long Free() => drives.Sum(d => { try { return new DriveInfo(d).AvailableFreeSpace; } catch (IOException) { return 0L; } });
        long before = Free(), logical = 0; int deleted = 0, skipped = 0; bool cancelled = false;
        Audit(new { kind = "start", time = DateTimeOffset.Now, mode = "permanent", count = selected.Sum(x => x.Count),
            projects = selected.Select(r => new { r.Rule.Id, r.Rule.Name, r.Rule.Handler, r.Scope, r.IncludeRecentFiles }).ToArray(),
            confirmations = selected.Where(r => r.RequiresConfirmation).Select(r => new { r.Rule.Id, Text = r.ConfirmationText }).Distinct().ToArray() });
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in selected)
        {
            var handler = handlers.For(group.Rule);
            foreach (var file in group.Files)
            {
                if (token.IsCancellationRequested) { cancelled = true; break; }
                if (!seen.Add(file.Path)) continue;
                string? reason = null;
                try
                {
                    string path = PathPolicy.Normalize(file.Path);
                    if (file.RuleId != group.Rule.Id || PathPolicy.IsExcluded(path, exclusions))
                        throw new IOException("不在当前规则范围内或已加入排除列表。");
                    if (handler.Check(group).BlockReason is { } block) throw new IOException(block);
                    handler.ValidateFile(group, file);
                    PathPolicy.EnsureNoReparsePoints(path);
                }
                catch (Exception e) when (Scanner.IsFileError(e)) { reason = e.Message; }

                if (reason is null)
                {
                    Audit(new { kind = "intent", file.Path, rule = file.RuleId, file.Size, time = DateTimeOffset.Now });
                    try { NativeFile.DeleteUnchanged(file); }
                    catch (Exception e) when (Scanner.IsFileError(e)) { reason = e.Message; }
                }
                if (reason is null) { deleted++; logical += file.Size; }
                else skipped++;
                Audit(new { kind = reason is null ? "deleted" : "skipped", file.Path, file.Size, rule = file.RuleId, reason, time = DateTimeOffset.Now });
                if ((deleted + skipped) % 32 == 0) progress?.Report(new("正在清理 · " + group.Name, deleted + skipped));
            }
            if (cancelled) break;
        }
        var summary = new CleanupSummary(deleted, skipped, logical, Free() - before, cancelled, logPath);
        Audit(new { kind = "summary", time = DateTimeOffset.Now, summary.Deleted, summary.Skipped,
            summary.LogicalBytes, summary.FreeSpaceDelta, summary.Cancelled });
        return summary;
    }
}
