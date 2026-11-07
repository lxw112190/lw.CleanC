namespace CleanC.Core;

public sealed class PathPolicy
{
    private readonly string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private readonly string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    private readonly string? fixtureRoot;
    public PathPolicy() { }
    internal PathPolicy(string fixtureRoot) => this.fixtureRoot = Normalize(fixtureRoot);

    public static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('*') || path.Contains('?') ||
            path.StartsWith(@"\\", StringComparison.Ordinal) || !Path.IsPathFullyQualified(path) ||
            path.AsSpan(2).Contains(':'))
            throw new InvalidDataException("路径必须是本地绝对路径，不能包含通配符或数据流。");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }
    public static string ExpandRoot(string raw)
    {
        string expanded = Environment.ExpandEnvironmentVariables(raw);
        if (expanded.Contains('%')) throw new InvalidDataException("规则根目录含未展开的变量或不支持的百分号。");
        return Normalize(expanded);
    }
    public static bool Within(string path, string root) => path.Equals(root, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    public bool IsAllowedRoot(string root)
    {
        root = Normalize(root);
        if (fixtureRoot is not null) return Within(root, fixtureRoot) && !root.Equals(fixtureRoot, StringComparison.OrdinalIgnoreCase);
        string[] exact = [Path.Combine(local, "Temp"), Path.Combine(local, "CrashDumps"),
            Path.Combine(local, "npm-cache", "_cacache"), Path.Combine(roaming, "npm-cache", "_cacache"),
            Path.Combine(local, "pip", "Cache"), Path.Combine(local, "Yarn", "Cache"),
            Path.Combine(local, "NuGet", "v3-cache"), Path.Combine(local, "NuGet", "plugins-cache"),
            Path.Combine(local, "D3DSCache"), Path.Combine(local, "uv", "cache"),
            Path.Combine(roaming, "Code", "Cache"), Path.Combine(roaming, "Code", "Code Cache"),
            Path.Combine(roaming, "Code", "GPUCache"), Path.Combine(roaming, "Code", "CachedData"),
            Path.Combine(roaming, "Code", "CachedExtensionVSIXs")];
        if (root.Equals(Path.Combine(local, "Microsoft", "vscode-cpptools", "ipch"), StringComparison.OrdinalIgnoreCase) ||
            root.Equals(Path.Combine(local, "NVIDIA", "GLCache"), StringComparison.OrdinalIgnoreCase) ||
            root.Equals(Path.Combine(local, "NVIDIA", "DXCache"), StringComparison.OrdinalIgnoreCase)) return true;
        if (exact.Any(x => root.Equals(x, StringComparison.OrdinalIgnoreCase))) return true;
        // Personal additions must name an explicit cache directory beneath LocalAppData.
        if (!Within(root, local)) return false;
        string[] parts = Path.GetRelativePath(local, root).Split(Path.DirectorySeparatorChar);
        string[] cacheNames = ["Cache", "Caches", "Code Cache", "GPUCache", "CachedData", "CachedExtensionVSIXs"];
        return parts.Length >= 3 && cacheNames.Contains(parts[^1], StringComparer.OrdinalIgnoreCase) &&
            !parts.Any(p => p.Equals("Packages", StringComparison.OrdinalIgnoreCase) ||
                            p.Equals("MicrosoftEdge", StringComparison.OrdinalIgnoreCase));
    }

    public bool RequiresOfficialTool(string root) => Within(Normalize(root), Path.Combine(local, "uv"));

    public static bool MatchesFile(CleanupRule rule, string path, string root)
    {
        string extension = Path.GetExtension(path);
        if (root.EndsWith(@"\Microsoft\vscode-cpptools\ipch", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".ipch", StringComparison.OrdinalIgnoreCase)) return false;
        return rule.FileExtensions.Length == 0 || rule.FileExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }

    public static void EnsureNoReparsePoints(string path)
    {
        for (string? p = Normalize(path); p is not null; p = Path.GetDirectoryName(p))
            if ((File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("跳过目录联接、符号链接或云占位文件。");
    }

    public List<string> Validate(RuleDocument document)
    {
        if (document.Version != 1 || document.Rules is null || document.Rules.Count == 0 || document.Rules.Count > 200)
            throw new InvalidDataException("规则版本应为 1，规则数量应在 1–200 之间。");
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var roots = new List<string>();
        foreach (var r in document.Rules)
        {
            if (r is null) throw new InvalidDataException("规则数组不能包含 null。");
            if (string.IsNullOrWhiteSpace(r.Id) || !ids.Add(r.Id) || string.IsNullOrWhiteSpace(r.Name) ||
                string.IsNullOrWhiteSpace(r.Impact) || r.MinAgeDays < 1 || r.MinAgeDays > 3650 ||
                r.Paths is null || r.Paths.Length == 0 || r.Processes is null || r.FileExtensions is null ||
                r.FileExtensions.Any(x => string.IsNullOrWhiteSpace(x) || !x.StartsWith('.') || x.IndexOfAny(['*','?','\\','/',':']) >= 0))
                throw new InvalidDataException($"规则 {r.Id} 缺少必要内容、ID 重复或保留天数不在 1–3650 之间。");
            foreach (var path in r.Paths)
            {
                string root = ExpandRoot(path);
                if (!IsAllowedRoot(root)) throw new InvalidDataException($"规则 {r.Name} 超出允许的缓存范围：{root}");
                if (roots.Any(x => Within(root, x) || Within(x, root)))
                    throw new InvalidDataException($"规则路径重复或相互包含：{root}");
                roots.Add(root);
            }
        }
        return roots;
    }

    public static bool IsExcluded(string path, IReadOnlyList<string> exclusions) =>
        exclusions.Any(x => Within(path, x));
}
