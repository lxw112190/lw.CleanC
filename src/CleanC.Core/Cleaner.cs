using System.Text.Json;

namespace CleanC.Core;

public sealed class Cleaner(PathPolicy policy)
{
    public CleanupSummary Clean(IReadOnlyList<RuleResult> selected, IReadOnlyList<string> exclusions,
        string logDirectory, IProgress<ScanProgress>? progress, CancellationToken token)
    {
        policy.Validate(new RuleDocument { Rules = selected.Select(x => x.Rule).ToList() });
        if (selected.Any(x => x.Rule.AnalysisOnly || x.Rule.Paths.Any(p => policy.RequiresOfficialTool(PathPolicy.ExpandRoot(p)))))
            throw new InvalidDataException("此项必须使用应用官方清理工具，不支持逐文件删除。");
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
        Audit(new { kind = "start", time = DateTimeOffset.Now, mode = "permanent", count = selected.Sum(x => x.Count) });
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in selected)
        {
            foreach (var file in group.Files)
            {
                if (token.IsCancellationRequested) { cancelled = true; break; }
                if (!seen.Add(file.Path)) continue;
                string? reason = null;
                try
                {
                    string root = PathPolicy.Normalize(file.Root), path = PathPolicy.Normalize(file.Path);
                    if (file.RuleId != group.Rule.Id || !policy.IsAllowedRoot(root) || !PathPolicy.Within(path, root) ||
                        !group.Rule.Paths.Any(p => PathPolicy.ExpandRoot(p).Equals(root, StringComparison.OrdinalIgnoreCase)) ||
                        PathPolicy.IsExcluded(path, exclusions))
                        throw new IOException("不在当前规则范围内或已加入排除列表。");
                    PathPolicy.EnsureNoReparsePoints(path);
                    if (file.LastWrite > DateTime.UtcNow.AddDays(-group.Rule.MinAgeDays).ToFileTimeUtc()) throw new IOException("文件未超过保留天数。");
                    if (Processes.AnyRunning(group.Rule.Processes)) throw new IOException("相关程序正在运行。");
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
                Audit(new { kind = reason is null ? "deleted" : "skipped", file.Path, file.Size, reason, time = DateTimeOffset.Now });
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
