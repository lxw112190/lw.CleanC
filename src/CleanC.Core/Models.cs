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
    public bool ReviewRequired { get; init; }
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
    public string Name => Rule.Name;
    public string Category => Rule.Category;
    public string Impact => Rule.Impact;
    public string ModeText => Rule.AnalysisOnly ? "仅统计" : Rule.ReviewRequired ? "专项确认" : "普通清理";
    public long Size => Files.Sum(f => f.Size);
    public string SizeText => Display.Bytes(Size);
    public int Count => Files.Count;
    public string Status => string.Join("；", Notes.Distinct());
    private bool selected;
    public bool Selected { get => selected; set { selected = value && !Rule.AnalysisOnly; Changed(); } }
    public void Refresh() { Changed(nameof(SizeText)); Changed(nameof(Count)); Changed(nameof(Status)); }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}

public sealed record ScanProgress(string Message, long FileCount);
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
