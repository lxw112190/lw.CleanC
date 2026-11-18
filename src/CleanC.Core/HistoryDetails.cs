using System.Text.Json;

namespace CleanC.Core;

public sealed record HistoryBreakdown(string Rule, string Reason, int Deleted, int Skipped, IReadOnlyList<string> Samples);
public sealed record HistoryDetails(IReadOnlyList<HistoryBreakdown> Rows, bool HasSummary, int InvalidLines, bool Managed);

public static class HistoryDetailsReader
{
    private sealed class Group
    {
        public int Deleted;
        public int Skipped;
        public List<string> Samples { get; } = [];
    }

    public static HistoryDetails Read(string path, CancellationToken token)
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var groups = new Dictionary<(string Rule, string Reason), Group>();
        string? intentPath = null, intentRule = null;
        int invalid = 0;
        string? lastKind = null;
        bool managed = false;
        foreach (string line in File.ReadLines(path))
        {
            token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(line)) continue;
            lastKind = null;
            try
            {
                using var document = JsonDocument.Parse(line);
                var entry = document.RootElement;
                lastKind = Text(entry, "kind");
                if (lastKind is null) { invalid++; continue; }
                if (lastKind == "start" && entry.TryGetProperty("projects", out var projects) && projects.ValueKind == JsonValueKind.Array)
                    foreach (var project in projects.EnumerateArray())
                        if (Text(project, "Id") is { } id && Text(project, "Name") is { } name) names[id] = name;
                if (lastKind == "managed_result") managed = true;
                var file = Text(entry, "Path");
                if (lastKind == "intent")
                {
                    intentPath = file;
                    intentRule = Text(entry, "rule");
                    continue;
                }
                if (lastKind is not ("deleted" or "skipped")) continue;
                if (file is null) { invalid++; continue; }
                string rule = Text(entry, "rule") ?? (file == intentPath ? intentRule : null) ?? "未记录规则";
                intentPath = intentRule = null;
                bool skipped = lastKind == "skipped";
                string reason = skipped ? Text(entry, "reason") ?? "未记录原因" : "已删除";
                var key = (rule, reason);
                if (!groups.ContainsKey(key) && groups.Count >= 250)
                    key = ("其他项目", skipped ? "其他跳过原因" : "已删除");
                if (!groups.TryGetValue(key, out var group)) groups[key] = group = new();
                if (skipped) group.Skipped++; else group.Deleted++;
                if (group.Samples.Count < 20) group.Samples.Add(file);
            }
            catch (JsonException) { invalid++; }
        }
        var rows = groups.Select(p => new HistoryBreakdown(names.GetValueOrDefault(p.Key.Rule, p.Key.Rule), p.Key.Reason,
            p.Value.Deleted, p.Value.Skipped, p.Value.Samples)).OrderByDescending(r => r.Skipped).ThenByDescending(r => r.Deleted).ToArray();
        return new(rows, lastKind is "summary" or "managed_result", invalid, managed);
    }

    private static string? Text(JsonElement entry, string name) =>
        entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;
}
