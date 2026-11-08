using System.Text.RegularExpressions;

namespace CleanC.Core;

internal static class BrowserCaches
{
    internal static IEnumerable<(string Root, string Process)> Bases(string local)
    {
        yield return (Path.Combine(local, "Google", "Chrome", "User Data"), "chrome");
        yield return (Path.Combine(local, "Microsoft", "Edge", "User Data"), "msedge");
        yield return (Path.Combine(local, "360ChromeX", "Chrome", "User Data"), "360ChromeX");
    }
    internal static string? Owner(string root, string local)
    {
        foreach (var browser in Bases(local))
        {
            if (!PathPolicy.Within(root, browser.Root)) continue;
            string[] parts = Path.GetRelativePath(browser.Root, root).Split(Path.DirectorySeparatorChar);
            if (parts.Length == 1 && parts[0].ToUpperInvariant() is "SHADERCACHE" or "GRSHADERCACHE") return browser.Process;
            if (parts.Length == 2 && (parts[0].Equals("Default", StringComparison.OrdinalIgnoreCase) || Regex.IsMatch(parts[0], @"^Profile [0-9]+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) &&
                new[] { "Cache", "Code Cache", "GPUCache" }.Contains(parts[1], StringComparer.OrdinalIgnoreCase)) return browser.Process;
        }
        return null;
    }
}

internal static class DiagnosticLogs
{
    internal static string? Owner(string root)
    {
        if (root.EndsWith(@"\Tencent\Logs", StringComparison.OrdinalIgnoreCase)) return "QQ";
        if (root.EndsWith(@"\Tencent\xwechat\log", StringComparison.OrdinalIgnoreCase)) return "Weixin";
        if (root.EndsWith(@"\Tencent\WeMeet\Global\Logs", StringComparison.OrdinalIgnoreCase)) return "wemeetapp";
        return null;
    }
    internal static bool Matches(string path, string root)
    {
        string relative = Path.GetRelativePath(root, path);
        if (root.EndsWith(@"\Tencent\Logs", StringComparison.OrdinalIgnoreCase))
            return Regex.IsMatch(relative, @"^QQ\.tlg(?:\.[0-9]{1,3})?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (root.EndsWith(@"\Tencent\xwechat\log", StringComparison.OrdinalIgnoreCase))
            return Regex.IsMatch(relative, @"^(?:mm_[0-9]{8}\.xlog|radium\\host_[0-9]{8}\.xlog)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (root.EndsWith(@"\Tencent\WeMeet\Global\Logs", StringComparison.OrdinalIgnoreCase))
            return !relative.Contains(Path.DirectorySeparatorChar) && Path.GetExtension(relative).Equals(".log", StringComparison.OrdinalIgnoreCase);
        return true;
    }
}
