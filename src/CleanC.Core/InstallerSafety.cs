namespace CleanC.Core;

internal static class InstallerSafety
{
    internal const string Unverifiable = "无法核验待重启文件操作，已阻止安装包清理；请重启后重新检查。";

    // MoveFileEx stores source/destination pairs. An empty destination means deletion.
    // Read both sides: a pending move into a package matters just as much as a move out.
    internal static CleanupSafetyStatus CheckPendingOperations(object? value, IReadOnlyList<string> roots)
    {
        if (value is null) return new(null);
        if (value is not string[] entries || entries.Length % 2 != 0) return new(Unverifiable);
        if (entries.Length == 0) return new(null);
        try
        {
            string[] normalizedRoots = roots.Select(PathPolicy.Normalize).ToArray();
            for (int i = 0; i < entries.Length; i += 2)
            {
                if (string.IsNullOrEmpty(entries[i]) || entries[i + 1] is null) return new(Unverifiable);
                foreach (string entry in new[] { entries[i], entries[i + 1] })
                {
                    if (entry.Length == 0) continue;
                    string path = NormalizePendingPath(entry);
                    if (normalizedRoots.Any(root => PathPolicy.Within(path, root) || PathPolicy.Within(root, path)))
                        return new("待重启文件操作涉及所选安装包目录，请重启后重新扫描。");
                }
            }
        }
        catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException or NotSupportedException)
        {
            return new(Unverifiable);
        }
        return new(null, "检测到其他目录的待重启文件操作，与当前核验目录无路径重叠；仍建议适时重启。");
    }

    private static string NormalizePendingPath(string path)
    {
        // Replacement marker and the *1 deletion marker observed in installer queues.
        if (path.StartsWith('!')) path = path[1..];
        if (path.StartsWith("*1", StringComparison.Ordinal)) path = path[2..];
        if (path.StartsWith(@"\??\", StringComparison.Ordinal)) path = path[4..];
        else if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) path = path[4..];
        path = PathPolicy.Normalize(path);
        // An 8.3 alias could refer to a package despite a different textual path.
        // Keep it blocked until reboot rather than claiming it is unrelated.
        if (System.Text.RegularExpressions.Regex.IsMatch(path, @"(?:^|\\)[^\\]*~[0-9]"))
            throw new InvalidDataException("无法核验待重启操作中的短文件名路径。");
        return path;
    }
}
