using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace CleanC.Core;

public static class Display
{
    public static string Bytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double n = bytes; int i = 0;
        while (n >= 1024 && i < units.Length - 1) { n /= 1024; i++; }
        return $"{n:0.##} {units[i]}";
    }
}

public sealed record CleanupRule
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Category { get; init; } = "";
    public string Description { get; init; } = "";
    public string Impact { get; init; } = "";
    public string[] Paths { get; init; } = [];
    public string[] Processes { get; init; } = [];
    public int MinAgeDays { get; init; } = 7;
    public bool AnalysisOnly { get; init; }
    public string[] FileExtensions { get; init; } = [];
    public string[] FileNamePatterns { get; init; } = [];
    public bool ReviewRequired { get; init; }
    public string Handler { get; init; } = "files";
    public string Confirmation { get; init; } = "";
}

public sealed record RuleDocument
{
    public int Version { get; init; } = 1;
    public List<CleanupRule> Rules { get; init; } = [];
}

public sealed record FileCandidate(string Path, string Root, string RuleId, long Size,
    long LastWrite, long Creation, uint Volume, ulong FileId)
{
    public string SizeText => Display.Bytes(Size);
    public string ModifiedText => DateTime.FromFileTimeUtc(LastWrite).ToLocalTime().ToString("yyyy-MM-dd HH:mm");
}

public sealed class RuleResult(CleanupRule rule) : INotifyPropertyChanged
{
    public CleanupRule Rule { get; } = rule;
    public List<FileCandidate> Files { get; } = [];
    public List<string> Notes { get; } = [];
    public string? Scope { get; init; }
    public string Name => Scope is null ? Rule.Name : $"{Rule.Name} · {Path.GetFileName(Scope)}";
    public string Category => Rule.Category;
    public string Impact => Rule.Impact;
    public bool RequiresConfirmation => ConfirmationText.Length > 0;
    public bool IncludeRecentFiles { get; init; }
    public bool ScanLimited { get; internal set; }
    public string ConfirmationText => string.Join("\n", new[] { CleanupHandlers.ConfirmationFor(Rule), IncludeRecentFiles ?
        "我确认相关浏览器已关闭；此项包含近期可重建缓存，不按保留天数过滤。网页资源和着色器可能重新下载或编译。" : "" }.Where(s => s.Length > 0));
    public string RetentionText => IncludeRecentFiles ? "已包含近期浏览器缓存，不按保留天数过滤。" : CleanupHandlers.RetentionFor(Rule);
    public string ModeText => Rule.AnalysisOnly ? "仅统计" : RequiresConfirmation ? "需确认" : Rule.ReviewRequired ? "需核对" : "普通清理";
    public long Size => Files.Sum(f => f.Size);
    public string SizeText => Display.Bytes(Size);
    public int Count => Files.Count;
    public string? BlockReason { get; private set; }
    public string? Advisory { get; private set; }
    public string Status => string.Join("；", Notes.Concat(new[] { BlockReason, Advisory }.OfType<string>()).Distinct());
    public void ApplySafety(CleanupSafetyStatus safety) { BlockReason = safety.BlockReason; Advisory = safety.Advisory; Changed(nameof(Status)); }
    private bool selected;
    public bool Selected { get => selected; set { selected = value && !Rule.AnalysisOnly; Changed(); } }
    public void Refresh() { Changed(nameof(SizeText)); Changed(nameof(Count)); Changed(nameof(Status)); }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}

public sealed record ScanProgress(string Message, long FileCount);
public sealed record CleanupScanOptions(bool IncludeRecentBrowserCaches = false);
public sealed record CleanupSafetyStatus(string? BlockReason, string? Advisory = null);
public sealed record UsageEntry(string Path, long Size, long FileCount)
{
    public string? Label { get; init; }
    public string Name => Label ?? (System.IO.Path.GetFileName(Path.TrimEnd('\\')) is { Length: > 0 } n ? n : Path);
    public string SizeText => Display.Bytes(Size);
    public double Percent { get; init; }
}
public sealed record AnalysisResult(string Root, List<UsageEntry> Folders, List<UsageEntry> LargestFiles,
    long Size, long FileCount, long Skipped, bool Cancelled)
{
    public List<UsageEntry> FileTypes { get; init; } = [];
    public IReadOnlyList<AnalysisFolderTotal> SnapshotFolders { get; init; } = [];
    public bool SnapshotIndexLimited { get; init; }
}
public sealed record CleanupSummary(int Deleted, int Skipped, long LogicalBytes, long FreeSpaceDelta,
    bool Cancelled, string LogPath);

public sealed class Preferences
{
    public List<string> Exclusions { get; set; } = [];
}

public static class JsonFiles
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true, WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    public static T Read<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options)
        ?? throw new InvalidDataException("JSON 内容为空。");
    public static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, Options));
        File.Move(temp, path, true);
    }
}
