using System.Security.Cryptography;
using System.Text;

namespace CleanC.Core;

public sealed record AnalysisFolderTotal(string Path, int Depth, long LogicalBytes);
public sealed record AnalysisSnapshot(string Root, DateTimeOffset RecordedAt, long? AvailableBytes,
    long LogicalBytes, long Skipped, bool IndexLimited, IReadOnlyList<AnalysisFolderTotal> Folders);
public sealed record AnalysisChange(string Path, int Depth, long? Before, long? After)
{
    public long Delta => (After ?? 0) - (Before ?? 0);
}
public sealed record AnalysisComparison(AnalysisSnapshot Before, AnalysisSnapshot After, IReadOnlyList<AnalysisChange> Changes);

public sealed class AnalysisSnapshotStore(string directory)
{
    public AnalysisComparison? Record(AnalysisResult result, IReadOnlyList<string> exclusions, long? availableBytes)
    {
        // Cancelled scans never replace a valid baseline.
        if (result.Cancelled || (result.FileCount == 0 && result.Skipped > 0)) return null;
        string root = PathPolicy.Normalize(result.Root);
        var relevantExclusions = exclusions.Where(path => PathPolicy.Within(path, root) || PathPolicy.Within(root, path))
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase);
        string scope = string.Join("\n", new[] { root }.Concat(relevantExclusions)).ToUpperInvariant();
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scope)));
        string path = Path.Combine(directory, key + ".json");
        var current = new AnalysisSnapshot(root, DateTimeOffset.Now, availableBytes, result.Size, result.Skipped,
            result.SnapshotIndexLimited, result.SnapshotFolders);
        AnalysisComparison? comparison = null;
        if (File.Exists(path))
        {
            var previous = JsonFiles.Read<AnalysisSnapshot>(path);
            if (!root.Equals(previous.Root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("历史快照的目录范围与当前扫描不一致。");
            comparison = Compare(previous, current);
        }
        JsonFiles.Write(path, current);
        return comparison;
    }

    private static AnalysisComparison Compare(AnalysisSnapshot before, AnalysisSnapshot after)
    {
        var older = before.Folders.ToDictionary(item => item.Path, StringComparer.OrdinalIgnoreCase);
        var newer = after.Folders.ToDictionary(item => item.Path, StringComparer.OrdinalIgnoreCase);
        var changes = new List<AnalysisChange>();
        foreach (string path in newer.Keys.Union(older.Keys, StringComparer.OrdinalIgnoreCase))
        {
            var old = older.GetValueOrDefault(path);
            var now = newer.GetValueOrDefault(path);
            if ((before.IndexLimited || after.IndexLimited) && (old is null || now is null)) continue;
            if (old?.LogicalBytes == now?.LogicalBytes) continue;
            changes.Add(new(path, (now ?? old!).Depth, old?.LogicalBytes, now?.LogicalBytes));
        }
        return new(before, after, changes.OrderByDescending(c => c.Delta).ThenBy(c => c.Path, StringComparer.OrdinalIgnoreCase).ToList());
    }
}

internal sealed class AnalysisDirectoryIndex(int capacity = 25_000)
{
    internal const int MaxDepth = 6;
    private readonly Dictionary<string, AnalysisFolderTotal> totals = new(StringComparer.OrdinalIgnoreCase);
    internal bool Limited { get; private set; }
    internal IReadOnlyList<AnalysisFolderTotal> Folders => totals.Values.ToArray();

    internal void AddDirectory(string root, string directory, long bytes)
    {
        if (directory.Equals(root, StringComparison.OrdinalIgnoreCase))
        {
            Add(root, 1, bytes);
            return;
        }
        string[] parts = Path.GetRelativePath(root, directory).Split(Path.DirectorySeparatorChar, MaxDepth + 1);
        string path = root;
        for (int i = 0; i < Math.Min(parts.Length, MaxDepth); i++)
        {
            path = Path.Combine(path, parts[i]);
            Add(path, i + 1, bytes);
        }
    }

    private void Add(string path, int depth, long bytes)
    {
        if (totals.TryGetValue(path, out var existing))
            totals[path] = existing with { LogicalBytes = existing.LogicalBytes + bytes };
        else if (totals.Count < capacity)
            totals[path] = new(path, depth, bytes);
        else Limited = true;
    }
}
