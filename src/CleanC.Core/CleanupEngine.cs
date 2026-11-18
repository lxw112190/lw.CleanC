namespace CleanC.Core;

// The UI works with rules/results; handlers own discovery and per-file checks.
// Actual deletion and durable audit logging remain in one shared pipeline.
public sealed class CleanupEngine
{
    private readonly PathPolicy policy;
    private readonly CleanupHandlers handlers;
    public CleanupEngine(PathPolicy policy) { this.policy = policy; handlers = new(policy); }
    internal CleanupEngine(PathPolicy policy, InstallerCleanup installer) { this.policy = policy; handlers = new(policy, installer); }

    public List<RuleResult> Scan(RuleDocument document, IReadOnlyList<string> exclusions,
        IProgress<ScanProgress>? progress, CancellationToken token, ScanControl? control = null, CleanupScanOptions? options = null) =>
        Scan(document, exclusions, progress, token, control, options, Scanner.CandidateLimit);

    internal List<RuleResult> Scan(RuleDocument document, IReadOnlyList<string> exclusions,
        IProgress<ScanProgress>? progress, CancellationToken token, ScanControl? control, CleanupScanOptions? options, int limit, string? scope = null)
    {
        policy.Validate(document);
        var results = new List<RuleResult>();
        int remaining = limit;
        foreach (var rule in document.Rules)
        {
            control?.Checkpoint(token); token.ThrowIfCancellationRequested();
            if (remaining == 0)
            {
                var pending = new RuleResult(rule) { ScanLimited = true };
                pending.Notes.Add("候选已达上限，此项尚未扫描");
                results.Add(pending);
                continue;
            }
            var scanned = handlers.For(rule).Scan(rule, exclusions, progress, token, control, options, remaining, scope);
            results.AddRange(scanned);
            remaining -= scanned.Sum(row => row.Count);
        }
        RefreshState(results);
        return results;
    }
    public List<RuleResult> Rescan(CleanupRule rule, string? scope, int retainedCandidates,
        IReadOnlyList<string> exclusions, IProgress<ScanProgress>? progress, CancellationToken token,
        ScanControl? control = null, CleanupScanOptions? options = null)
    {
        if (retainedCandidates < 0 || retainedCandidates >= Scanner.CandidateLimit)
            throw new InvalidOperationException("已有预览占满候选上限，请先分批处理已预览项目。");
        return Scan(new RuleDocument { Rules = [rule] }, exclusions, progress, token, control, options,
            Scanner.CandidateLimit - retainedCandidates, scope);
    }
    public void RefreshState(IReadOnlyList<RuleResult> results)
    {
        foreach (var row in results) row.ApplySafety(handlers.For(row.Rule).Check(row));
    }
    public CleanupSummary Clean(IReadOnlyList<RuleResult> selected, IReadOnlyList<string> exclusions,
        IReadOnlySet<string> confirmedRules, string logs, IProgress<ScanProgress>? progress, CancellationToken token) =>
        new Cleaner(policy, handlers).Clean(selected, exclusions, logs, progress, token, confirmedRules);
}

internal interface ICleanupHandler
{
    List<RuleResult> Scan(CleanupRule rule, IReadOnlyList<string> exclusions, IProgress<ScanProgress>? progress, CancellationToken token, ScanControl? control, CleanupScanOptions? options, int limit, string? scope);
    CleanupSafetyStatus Check(RuleResult row);
    void ValidateFile(RuleResult row, FileCandidate file);
}

internal sealed class CleanupHandlers
{
    private sealed record Definition(Func<PathPolicy, InstallerCleanup?, ICleanupHandler> Create, string RequiredConfirmation = "", bool UsesRetention = true);
    private static readonly Dictionary<string, Definition> Definitions = new(StringComparer.Ordinal)
    {
        ["files"] = new((policy, _) => new FileCleanupHandler(policy)),
        ["vs-installer-temp"] = new((policy, installer) => new VsInstallerCleanupHandler(
            installer ?? new InstallerCleanup(policy.InstallerTempRoot, roots => InstallerCleanup.GetSafetyStatus(roots).BlockReason),
            installer is null ? InstallerCleanup.GetSafetyStatus : roots => new(installer.CheckBlockReason(roots))),
            "我确认安装已完成、无需保留这些下载包；以后修复或修改 VS 可能需要重新下载。安装包不按保留天数过滤。", false)
    };
    private readonly Dictionary<string, ICleanupHandler> handlers;
    internal static bool IsKnown(string? id) => id is not null && Definitions.ContainsKey(id);
    internal static string ConfirmationFor(CleanupRule rule)
    {
        string required = Definitions.TryGetValue(rule.Handler, out var definition) ? definition.RequiredConfirmation : "";
        return string.Join("\n", new[] { required, rule.Confirmation }.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct());
    }
    internal static string RetentionFor(CleanupRule rule) => Definitions.TryGetValue(rule.Handler, out var definition) && !definition.UsesRetention
        ? "不按保留天数过滤，须核对确认条件。" : $"保留最近 {rule.MinAgeDays} 天。";
    internal CleanupHandlers(PathPolicy policy, InstallerCleanup? installer = null)
    {
        handlers = Definitions.ToDictionary(pair => pair.Key, pair => pair.Value.Create(policy, installer), StringComparer.Ordinal);
    }
    internal ICleanupHandler For(CleanupRule rule) => handlers.TryGetValue(rule.Handler, out var handler)
        ? handler : throw new InvalidDataException($"未知清理处理器：{rule.Handler}");
}

internal sealed class FileCleanupHandler(PathPolicy policy) : ICleanupHandler
{
    public List<RuleResult> Scan(CleanupRule rule, IReadOnlyList<string> exclusions, IProgress<ScanProgress>? progress, CancellationToken token, ScanControl? control, CleanupScanOptions? options, int limit, string? scope) =>
        new Scanner(policy).Scan(new() { Rules = [rule] }, exclusions, progress, token, control, options?.IncludeRecentBrowserCaches == true, limit);
    public CleanupSafetyStatus Check(RuleResult row) => new(row.Count > 0 && policy.RelatedProcessRunning(row.Rule) ? "相关程序正在运行，关闭后重新检查状态。" : null);
    public void ValidateFile(RuleResult row, FileCandidate file)
    {
        string root = PathPolicy.Normalize(file.Root), path = PathPolicy.Normalize(file.Path);
        if (!policy.IsAllowedRoot(root) || !PathPolicy.Within(path, root) ||
            !row.Rule.Paths.Any(p => PathPolicy.ExpandRoot(p).Equals(root, StringComparison.OrdinalIgnoreCase)))
            throw new IOException("不在当前规则范围内。");
        if (policy.RequiresInstallerHandler(path)) throw new IOException("安装临时包必须由安装包处理器核验和确认，不能通过普通清理删除。");
        if (!PathPolicy.MatchesFile(row.Rule, path, root)) throw new IOException("文件类型不在规则范围内。");
        if (row.IncludeRecentFiles && !policy.AllowsRecentBrowserFiles(row.Rule)) throw new IOException("此目录不允许跳过保留天数。");
        if (!row.IncludeRecentFiles && file.LastWrite > DateTime.UtcNow.AddDays(-row.Rule.MinAgeDays).ToFileTimeUtc()) throw new IOException("文件未超过保留天数。");
    }
}

internal sealed class VsInstallerCleanupHandler(InstallerCleanup installer, Func<IReadOnlyList<string>, CleanupSafetyStatus> safety) : ICleanupHandler
{
    public List<RuleResult> Scan(CleanupRule rule, IReadOnlyList<string> exclusions, IProgress<ScanProgress>? progress, CancellationToken token, ScanControl? control, CleanupScanOptions? options, int limit, string? scope)
    {
        var plans = installer.Scan(exclusions, progress, token, control, rule.Id, limit, scope, rule, out bool limited);
        var rows = new List<RuleResult>();
        foreach (var plan in plans)
        {
            var row = new RuleResult(rule) { Scope = plan.Root };
            row.Files.AddRange(plan.Files);
            row.Notes.Add(plan.Status); row.Notes.Add("不按保留天数过滤，清理前须确认安装结束");
            if (row.Count > 0) rows.Add(row);
        }
        if (rows.Count == 0) { var empty = new RuleResult(rule) { Scope = scope }; empty.Notes.Add("未发现符合目录特征和文件类型的安装临时包"); rows.Add(empty); }
        if (limited)
        {
            foreach (var row in rows)
            {
                row.ScanLimited = true;
                row.Notes.Add("候选已达上限，安装包只显示已扫描部分");
            }
        }
        return rows;
    }
    public CleanupSafetyStatus Check(RuleResult row)
    {
        if (row.Count == 0) return new(null);
        if (row.Scope is null) return new("安装包预览缺少目录范围，请重新扫描。");
        if (Processes.AnyRunning(row.Rule.Processes)) return new("相关程序正在运行，关闭后重新检查状态。");
        return safety([row.Scope]);
    }
    public void ValidateFile(RuleResult row, FileCandidate file)
    {
        if (row.Scope is null || !file.Root.Equals(row.Scope, StringComparison.OrdinalIgnoreCase) ||
            !installer.IsCandidatePath(file.Path, file.Root) || !PathPolicy.MatchesFile(row.Rule, file.Path, file.Root))
            throw new IOException("不符合安装临时包目录或文件类型限制。");
    }
}
