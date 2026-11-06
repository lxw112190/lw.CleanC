using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace CleanC.Core;

public sealed record ToolResult(int ExitCode, bool Cancelled, long FreeSpaceDelta, string Output, string LogPath);

public static class UvMaintenance
{
    public static string CacheRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "uv", "cache");
    public static string? FindExecutable()
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var dirs = new[] { Path.Combine(profile, ".local", "bin"), Path.Combine(profile, ".cargo", "bin") }
            .Concat((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator));
        foreach (string directory in dirs)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory)) continue;
                string exe = PathPolicy.Normalize(Path.Combine(directory.Trim('"'), "uv.exe"));
                if (!File.Exists(exe)) continue;
                PathPolicy.EnsureNoReparsePoints(exe); return exe;
            }
            catch (Exception e) when (Scanner.IsFileError(e) || e is ArgumentException) { }
        }
        return null;
    }

    public static void Validate(IReadOnlyList<string> exclusions)
    {
        PathPolicy.EnsureNoReparsePoints(CacheRoot);
        ValidateExclusions(CacheRoot, exclusions);
        if (Processes.AnyRunning(["uv", "python", "pythonw"]))
            throw new IOException("请先关闭 uv 和 Python 进程，再清理 uv 缓存。");
    }
    internal static void ValidateExclusions(string root, IReadOnlyList<string> exclusions)
    {
        if (exclusions.Any(p => PathPolicy.Within(root, p) || PathPolicy.Within(p, root)))
            throw new InvalidDataException("uv 缓存存在排除项。官方清理会清理整个缓存，无法保留这些项目，因此已阻止操作。");
    }

    internal static ProcessStartInfo BuildStartInfo(string executable)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(executable)!
        };
        foreach (string arg in new[] { "cache", "clean", "--cache-dir", CacheRoot }) start.ArgumentList.Add(arg);
        start.Environment["UV_LOCK_TIMEOUT"] = "5";
        start.Environment["UV_NO_PROGRESS"] = "1";
        return start;
    }

    public static async Task<ToolResult> CleanAsync(string executable, IReadOnlyList<string> exclusions,
        string logDirectory, CancellationToken token)
    {
        // The UI confirms the exact executable, fixed operation and cache scope. Rules cannot supply commands.
        if (!string.Equals(executable, FindExecutable(), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("uv 路径已变化，请重试。");
        Validate(exclusions); token.ThrowIfCancellationRequested();
        Directory.CreateDirectory(logDirectory);
        string path = Path.Combine(logDirectory, $"{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.jsonl");
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        using var writer = new StreamWriter(stream) { AutoFlush = true };
        void Log(object entry) { writer.WriteLine(JsonSerializer.Serialize(entry)); stream.Flush(true); }
        string drive = Path.GetPathRoot(CacheRoot)!;
        long before = new DriveInfo(drive).AvailableFreeSpace;
        Log(new { kind = "managed_start", time = DateTimeOffset.Now, executable, operation = "uv cache clean", cacheRoot = CacheRoot,
            note = "官方工具处理整个缓存；无逐文件删除清单，保留天数不适用。" });
        var start = BuildStartInfo(executable);
        using var process = new Process { StartInfo = start };
        process.Start();
        static async Task<string> Drain(StreamReader reader)
        {
            var text = new StringBuilder(); char[] buffer = new char[2048]; int n;
            while ((n = await reader.ReadAsync(buffer)) != 0)
                if (text.Length < 16384) text.Append(buffer, 0, Math.Min(n, 16384 - text.Length));
            return text.ToString();
        }
        Task<string> stdout = Drain(process.StandardOutput), stderr = Drain(process.StandardError);
        bool cancelled = false;
        try { await process.WaitForExitAsync(token); }
        catch (OperationCanceledException)
        {
            cancelled = true;
            try { if (!process.HasExited) process.Kill(); } catch (InvalidOperationException) { }
            await process.WaitForExitAsync();
        }
        string output = (await stdout + "\n" + await stderr).Trim();
        long delta = new DriveInfo(drive).AvailableFreeSpace - before;
        Log(new { kind = "managed_result", time = DateTimeOffset.Now, exitCode = process.ExitCode, cancelled, delta, output });
        return new(process.ExitCode, cancelled, delta, output, path);
    }
}
