namespace CleanC.Core;

public sealed class ScanControl : IDisposable
{
    private readonly ManualResetEventSlim gate = new(true);
    public bool IsPaused => !gate.IsSet;
    public void Pause() => gate.Reset();
    public void Resume() => gate.Set();
    public void Checkpoint(CancellationToken token) { token.ThrowIfCancellationRequested(); gate.Wait(token); }
    public void Dispose() => gate.Dispose();
}

public static class RuleResolver
{
    // Deliberately restricted discovery: numbered Chromium profiles and the same explicit cache leaf only.
    public static RuleDocument Resolve(RuleDocument document)
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string[] browsers = [Path.Combine(local, "Google", "Chrome", "User Data"), Path.Combine(local, "Microsoft", "Edge", "User Data")];
        return document with { Rules = document.Rules.Select(rule => rule with
        {
            Paths = rule.Paths.SelectMany(raw =>
            {
                string root = PathPolicy.ExpandRoot(raw);
                foreach (string browser in browsers)
                {
                    string parent = Path.GetDirectoryName(root) ?? "";
                    if (!parent.Equals(Path.Combine(browser, "Default"), StringComparison.OrdinalIgnoreCase)) continue;
                    string leaf = Path.GetFileName(root);
                    if (leaf is not ("Cache" or "Code Cache" or "GPUCache")) continue;
                    return DiscoverProfiles(browser, leaf).Prepend(root);
                }
                return new[] { root };
            }).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
        }).ToList() };
    }
    internal static IEnumerable<string> DiscoverProfiles(string browser, string leaf)
    {
        try
        {
            if (!Directory.Exists(browser)) return [];
            PathPolicy.EnsureNoReparsePoints(browser);
            return Directory.EnumerateDirectories(browser, "Profile *").Where(p =>
                int.TryParse(Path.GetFileName(p).AsSpan(8), out int number) && number >= 0 &&
                (File.GetAttributes(p) & FileAttributes.ReparsePoint) == 0)
                .Order(StringComparer.OrdinalIgnoreCase).Take(200).Select(p => Path.Combine(p, leaf)).ToArray();
        }
        catch (Exception e) when (Scanner.IsFileError(e)) { return []; }
    }
}
