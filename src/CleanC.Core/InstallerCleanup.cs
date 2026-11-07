using Microsoft.Win32;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CleanC.Core;

public sealed class InstallerPlan(string root) : System.ComponentModel.INotifyPropertyChanged
{
    public string Root { get; } = root;
    public List<FileCandidate> Files { get; } = [];
    private bool selected;
    public bool Selected { get => selected; set { selected = value; PropertyChanged?.Invoke(this, new(nameof(Selected))); } }
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    public long Size => Files.Sum(x => x.Size);
    public string SizeText => Display.Bytes(Size);
    public int Count => Files.Count;
    public string Status { get; set; } = "目录特征匹配，仍需确认安装已完成";
}

public sealed class InstallerCleanup
{
    private readonly string tempRoot;
    private readonly Func<string?> blocked;
    public InstallerCleanup() : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp"), GetBlockReason) { }
    internal InstallerCleanup(string root, Func<string?> blockReason) { tempRoot = PathPolicy.Normalize(root); blocked = blockReason; }
    private static readonly Regex PackageName = new(@"^(Microsoft\.|VisualStudio\.|Win(?:10|11)SDK_|Emsdk\.|Aspire\.|Android\.Manifest|Azure\.Storage\.Emulator)[A-Za-z0-9_.-]*\.[A-Fa-f0-9]{16,64}$", RegexOptions.CultureInvariant);
    private static readonly string[] Extensions = [".msi", ".cab", ".vsix", ".exe", ".zip", ".nupkg", ".msu", ".json", ".xml", ".txt", ".vsman", ".msp"];
    private bool IsCandidatePath(string path, string root)
    {
        root = PathPolicy.Normalize(root); path = PathPolicy.Normalize(path);
        if (!string.Equals(Path.GetDirectoryName(root), tempRoot, StringComparison.OrdinalIgnoreCase) || !PathPolicy.Within(path, root)) return false;
        string[] parts = Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar);
        return parts.Length >= 2 && PackageName.IsMatch(parts[0]) && Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
    }
    public List<InstallerPlan> Scan(IReadOnlyList<string> exclusions, IProgress<ScanProgress>? progress, CancellationToken token, ScanControl? control = null)
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
                        plan.Files.Add(NativeFile.Snapshot(path, root, "vs-installer-temp")); count++;
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
    public static string? GetBlockReason()
    {
        if (Processes.AnyRunning(["setup","msiexec","vs_installer","vs_installershell","vs_setup_bootstrapper","VSIXInstaller"])) return "检测到安装/更新进程，请完成安装并退出安装器。";
        try
        {
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            foreach (string path in new[] { @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending", @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired" })
            { using var key = machine.OpenSubKey(path); if (key is not null) return "系统标记需要重启，请重启后重新扫描。"; }
            using var session = machine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager");
            if (session?.GetValue("PendingFileRenameOperations") is string[] { Length: > 0 }) return "存在待重启的文件操作，请重启后重新扫描。";
        }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException) { return "无法核验待重启状态，已阻止安装包清理。"; }
        return null;
    }
    public CleanupSummary Clean(IReadOnlyList<InstallerPlan> plans, IReadOnlyList<string> exclusions, bool installationCompleted,
        string logs, IProgress<ScanProgress>? progress, CancellationToken token)
    {
        if (!installationCompleted) throw new InvalidOperationException("需要用户确认安装已完成且无需保留下载包。");
        if (blocked() is { } why) throw new IOException(why);
        Directory.CreateDirectory(logs);
        string logPath = Path.Combine(logs, $"{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.jsonl");
        using var stream = new FileStream(logPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        using var writer = new StreamWriter(stream) { AutoFlush = true };
        void Audit(object record) { writer.WriteLine(JsonSerializer.Serialize(record)); stream.Flush(true); }
        long before = new DriveInfo(Path.GetPathRoot(tempRoot)!).AvailableFreeSpace, logical = 0;
        int deleted = 0, skipped = 0; bool cancelled = false;
        Audit(new { kind = "start", time = DateTimeOffset.Now, mode = "vs-installer-temp", installationCompleted, note = "用户确认安装完成；专项不按保留天数过滤" });
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in plans.SelectMany(p => p.Files))
        {
            if (token.IsCancellationRequested) { cancelled = true; break; }
            if (!seen.Add(file.Path)) continue;
            string? reason = null;
            try
            {
                if (blocked() is { } block) throw new IOException(block);
                if (file.RuleId != "vs-installer-temp" || !plans.Any(p => p.Root.Equals(file.Root, StringComparison.OrdinalIgnoreCase)) ||
                    !IsCandidatePath(file.Path, file.Root) || PathPolicy.IsExcluded(file.Path, exclusions)) throw new IOException("不符合专项路径或排除规则。");
                PathPolicy.EnsureNoReparsePoints(file.Path);
                Audit(new { kind = "intent", time = DateTimeOffset.Now, file.Path, file.Size });
                NativeFile.DeleteUnchanged(file);
            }
            catch (Exception e) when (Scanner.IsFileError(e)) { reason = e.Message; }
            if (reason is null) { deleted++; logical += file.Size; } else skipped++;
            Audit(new { kind = reason is null ? "deleted" : "skipped", time = DateTimeOffset.Now, file.Path, file.Size, reason });
            if ((deleted + skipped) % 32 == 0) progress?.Report(new("专项清理安装临时包", deleted + skipped));
        }
        long delta = new DriveInfo(Path.GetPathRoot(tempRoot)!).AvailableFreeSpace - before;
        Audit(new { kind = "summary", time = DateTimeOffset.Now, Deleted = deleted, Skipped = skipped, LogicalBytes = logical, FreeSpaceDelta = delta, Cancelled = cancelled });
        return new(deleted, skipped, logical, delta, cancelled, logPath);
    }
}
