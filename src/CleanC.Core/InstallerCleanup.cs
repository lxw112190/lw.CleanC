using Microsoft.Win32;
using System.Text.RegularExpressions;

namespace CleanC.Core;

public sealed class InstallerPlan(string root)
{
    public string Root { get; } = root;
    public List<FileCandidate> Files { get; } = [];
    public long Size => Files.Sum(x => x.Size);
    public string SizeText => Display.Bytes(Size);
    public int Count => Files.Count;
    public string Status { get; set; } = "目录特征匹配，仍需确认安装已完成";
}

public sealed class InstallerCleanup
{
    private readonly string tempRoot;
    private readonly Func<IReadOnlyList<string>, string?> blocked;
    public static string DefaultTempRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp");
    public InstallerCleanup() : this(DefaultTempRoot, roots => GetSafetyStatus(roots).BlockReason) { }
    internal InstallerCleanup(string root, Func<IReadOnlyList<string>, string?> blockReason) { tempRoot = PathPolicy.Normalize(root); blocked = blockReason; }
    private static readonly Regex PackageName = new(@"^(Microsoft\.|VisualStudio\.|Win(?:10|11)SDK_|Emsdk\.|Aspire\.|Android\.Manifest|Azure\.Storage\.Emulator)[A-Za-z0-9_.-]*\.[A-Fa-f0-9]{16,64}$", RegexOptions.CultureInvariant);
    private static readonly string[] Extensions = [".msi", ".cab", ".vsix", ".exe", ".zip", ".nupkg", ".msu", ".json", ".xml", ".txt", ".vsman", ".msp"];
    internal bool IsCandidatePath(string path, string root)
    {
        root = PathPolicy.Normalize(root); path = PathPolicy.Normalize(path);
        if (!string.Equals(Path.GetDirectoryName(root), tempRoot, StringComparison.OrdinalIgnoreCase) || !PathPolicy.Within(path, root)) return false;
        string[] parts = Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar);
        return parts.Length >= 2 && PackageName.IsMatch(parts[0]) && Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
    }
    internal static bool IsManagedPayload(string path, string temp)
    {
        path = PathPolicy.Normalize(path); temp = PathPolicy.Normalize(temp);
        if (!PathPolicy.Within(path, temp)) return false;
        string[] parts = Path.GetRelativePath(temp, path).Split(Path.DirectorySeparatorChar);
        return parts.Length >= 3 && PackageName.IsMatch(parts[1]) && Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
    }
    internal string? CheckBlockReason(IReadOnlyList<string> roots) => blocked(roots);
    public List<InstallerPlan> Scan(IReadOnlyList<string> exclusions, IProgress<ScanProgress>? progress, CancellationToken token, ScanControl? control = null, string ruleId = "vs-installer-temp")
    {
        var plans = new List<InstallerPlan>();
        if (!Directory.Exists(tempRoot)) return plans;
        PathPolicy.EnsureNoReparsePoints(tempRoot);
        int count = 0;
        foreach (string root in Directory.EnumerateDirectories(tempRoot))
        {
            control?.Checkpoint(token); token.ThrowIfCancellationRequested();
            if (PathPolicy.IsExcluded(root, exclusions)) continue;
            try
            {
                PathPolicy.EnsureNoReparsePoints(root);
                if (!Directory.EnumerateDirectories(root).Any(p => PackageName.IsMatch(Path.GetFileName(p)))) continue;
                var plan = new InstallerPlan(root);
                foreach (string path in Scanner.Walk(root, exclusions, () => { plan.Status = "部分文件不可访问，仍需确认安装已完成"; }, token, control))
                {
                    if (!IsCandidatePath(path, root)) continue;
                    try
                    {
                        if (count >= Scanner.CandidateLimit) throw new InvalidDataException("安装包预览超过 10 万文件，请缩小范围后重试。");
                        plan.Files.Add(NativeFile.Snapshot(path, root, ruleId)); count++;
                        if (count % 64 == 0) progress?.Report(new("识别 VS 安装临时包 · " + Path.GetFileName(root), count));
                    }
                    catch (Exception e) when (Scanner.IsFileError(e) && e is not InvalidDataException) { plan.Status = "部分文件已跳过，仍需确认安装已完成"; }
                }
                if (plan.Count > 0) plans.Add(plan);
            }
            catch (Exception e) when (Scanner.IsFileError(e) && e is not InvalidDataException) { }
        }
        return plans.OrderByDescending(x => x.Size).ToList();
    }
    public static CleanupSafetyStatus GetSafetyStatus(IReadOnlyList<string> roots)
    {
        if (Processes.AnyRunning(["setup","msiexec","vs_installer","vs_installershell","vs_setup_bootstrapper","VSIXInstaller"])) return new("检测到安装/更新进程，请完成安装并退出安装器，再点击重新检查状态。");
        try
        {
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            foreach (string path in new[] { @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending", @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired" })
            { using var key = machine.OpenSubKey(path); if (key is not null) return new("Windows 系统更新标记需要重启，请重启后重新扫描。"); }
            using var session = machine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager");
            if (session is null) return new(InstallerSafety.Unverifiable);
            string? advisory = null;
            foreach (string name in new[] { "PendingFileRenameOperations", "PendingFileRenameOperations2" })
            {
                var status = InstallerSafety.CheckPendingOperations(session.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames), roots);
                if (status.BlockReason is not null) return status;
                advisory ??= status.Advisory;
            }
            return new(null, advisory);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException) { return new("无法核验待重启状态，已阻止安装包清理。"); }
    }
    public CleanupSummary Clean(IReadOnlyList<InstallerPlan> plans, IReadOnlyList<string> exclusions, bool installationCompleted,
        string logs, IProgress<ScanProgress>? progress, CancellationToken token)
    {
        if (!installationCompleted) throw new InvalidOperationException("需要用户确认安装已完成且无需保留下载包。");
        // Compatibility entry point; use the same checks, deletion and audit as the UI.
        var rule = new CleanupRule { Id = "vs-installer-temp", Name = "VS 安装临时包", Handler = "vs-installer-temp",
            Paths = [tempRoot], MinAgeDays = 0, Impact = "以后修复或修改 VS 可能需要重新下载。" };
        var rows = plans.Select(plan =>
        {
            var row = new RuleResult(rule) { Scope = plan.Root }; row.Files.AddRange(plan.Files); return row;
        }).ToList();
        var policy = tempRoot.Equals(PathPolicy.Normalize(DefaultTempRoot), StringComparison.OrdinalIgnoreCase)
            ? new PathPolicy() : new PathPolicy(Path.GetDirectoryName(tempRoot)!, tempRoot);
        return new CleanupEngine(policy, this).Clean(rows, exclusions, new HashSet<string> { rule.Id }, logs, progress, token);
    }
}
