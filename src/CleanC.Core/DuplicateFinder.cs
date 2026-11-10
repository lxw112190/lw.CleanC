using System.Diagnostics;
using System.Security.Cryptography;

namespace CleanC.Core;

public sealed record DuplicateGroup(string Hash, long FileSize, List<FileCandidate> Files)
{
    public int Count => Files.Count;
    public long ExtraLogicalBytes => FileSize * (Count - 1);
    public string SizeText => Display.Bytes(FileSize);
    public string ExtraText => Display.Bytes(ExtraLogicalBytes);
    public string Example => Files[0].Path;
}
public sealed record DuplicateFileVerification(string Path, long Size, string Hash);
public sealed record DuplicatePairComparison(DuplicateFileVerification Left, DuplicateFileVerification Right)
{
    public bool Identical => Left.Size == Right.Size && Left.Hash == Right.Hash;
}
public sealed record DuplicateResult(string Root, List<DuplicateGroup> Groups, long Examined, long Skipped,
    long BytesRead, bool Cancelled, bool Limited)
{
    public IReadOnlyList<string> Roots { get; init; } = [Root];
    public long Candidates { get; init; }
    public long BelowMinimum { get; init; }
    public long ExcludedEntries { get; init; }
    public IReadOnlyList<string> SkipExamples { get; init; } = [];
}

public static class DuplicateFinder
{
    public const long DefaultMinimumSize = 1;
    public static DuplicatePairComparison ComparePair(string left, string right, CancellationToken token)
    {
        left = PathPolicy.Normalize(left); right = PathPolicy.Normalize(right);
        if (left.Equals(right, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("请选择两个不同的文件。");
        var snapshots = new[] { left, right }.Select(path =>
        {
            PathPolicy.EnsureNoReparsePoints(path);
            return NativeFile.Snapshot(path, Path.GetDirectoryName(path)!, "duplicate-verification");
        }).ToArray();
        var results = new List<DuplicateFileVerification>();
        byte[] buffer = new byte[128 * 1024];
        foreach (var file in snapshots)
        {
            token.ThrowIfCancellationRequested();
            using var stream = NativeFile.OpenReadVerified(file);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            int length;
            while ((length = stream.Read(buffer)) != 0)
            { token.ThrowIfCancellationRequested(); hash.AppendData(buffer, 0, length); }
            results.Add(new(file.Path, file.Size, Convert.ToHexString(hash.GetHashAndReset())));
        }
        // Recheck both snapshots after hashing; don't report a stale file as equal.
        foreach (var file in snapshots)
            if (NativeFile.Snapshot(file.Path, file.Root, file.RuleId) != file) throw new IOException("文件在校验期间发生变化，请重试。");
        return new(results[0], results[1]);
    }
    public static DuplicateResult Find(string root, IReadOnlyList<string> exclusions, long minimumSize,
        IProgress<ScanProgress>? progress, CancellationToken token, ScanControl? control = null) =>
        Find([root], exclusions, minimumSize, progress, token, control);

    public static DuplicateResult Find(IReadOnlyList<string> roots, IReadOnlyList<string> exclusions, long minimumSize,
        IProgress<ScanProgress>? progress, CancellationToken token, ScanControl? control = null)
    {
        if (minimumSize < 1) throw new ArgumentOutOfRangeException(nameof(minimumSize));
        var normalized = roots.Select(PathPolicy.Normalize).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p.Length).ToList();
        if (normalized.Count == 0) throw new ArgumentException("请至少选择一个目录。", nameof(roots));
        var scanRoots = new List<string>();
        foreach (string path in normalized)
        {
            PathPolicy.EnsureNoReparsePoints(path);
            if (!Directory.Exists(path)) throw new DirectoryNotFoundException(path);
            if (!scanRoots.Any(parent => PathPolicy.Within(path, parent))) scanRoots.Add(path);
        }
        var bySize = new Dictionary<long, List<FileCandidate>>();
        var groups = new List<DuplicateGroup>();
        var fullHashes = new Dictionary<FileCandidate, string>();
        long examined = 0, skipped = 0, read = 0, belowMinimum = 0, excluded = 0; int candidates = 0; bool cancelled = false, limited = false;
        var skipExamples = new List<string>();
        void Skip(string message) { skipped++; if (skipExamples.Count < 8 && !skipExamples.Contains(message)) skipExamples.Add(message); }
        byte[] buffer = new byte[128 * 1024]; var timer = Stopwatch.StartNew();
        void Report(string message, bool force = false)
        {
            if (!force && timer.ElapsedMilliseconds < 150) return;
            progress?.Report(new(message, examined)); timer.Restart();
        }
        string Hash(FileCandidate file, bool sample)
        {
            control?.Checkpoint(token); token.ThrowIfCancellationRequested();
            using var stream = NativeFile.OpenReadVerified(file);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            if (sample)
            {
                int n = stream.ReadAtLeast(buffer.AsSpan(0, (int)Math.Min(4096, file.Size)), (int)Math.Min(4096, file.Size), false);
                hash.AppendData(buffer, 0, n); read += n;
                if (file.Size > 4096)
                {
                    stream.Position = Math.Max(4096, file.Size - 4096);
                    n = stream.ReadAtLeast(buffer.AsSpan(0, 4096), (int)Math.Min(4096, file.Size - stream.Position), false);
                    hash.AppendData(buffer, 0, n); read += n;
                }
            }
            else
            {
                int n;
                while ((n = stream.Read(buffer)) != 0)
                {
                    control?.Checkpoint(token); token.ThrowIfCancellationRequested();
                    hash.AppendData(buffer, 0, n); read += n;
                    Report($"完整 SHA-256 校验 · 已读取 {Display.Bytes(read)}");
                }
            }
            return Convert.ToHexString(hash.GetHashAndReset());
        }
        List<List<FileCandidate>> Partition(IEnumerable<FileCandidate> files, bool sample)
        {
            var map = new Dictionary<string, List<FileCandidate>>(StringComparer.Ordinal);
            foreach (var file in files)
            {
                try
                {
                    string hash = Hash(file, sample);
                    if (!sample) fullHashes[file] = hash;
                    if (!map.TryGetValue(hash, out var matching)) map[hash] = matching = [];
                    matching.Add(file);
                    Report(sample ? "快速筛选 · 文件头尾采样" : $"完整 SHA-256 校验 · 已读取 {Display.Bytes(read)}");
                }
                catch (Exception e) when (Scanner.IsFileError(e)) { Skip(file.Path + "：" + e.Message); }
            }
            return map.Values.Where(x => x.Count > 1).ToList();
        }
        try
        {
            Report("收集文件 · 按大小分组", true);
            foreach (string root in scanRoots)
            {
                foreach (string path in Scanner.Walk(root, exclusions, () => Skip("部分路径不可访问，或属于链接/云占位文件。"), token, control, () => excluded++))
                {
                    examined++; Report("收集文件 · 按大小分组");
                    try
                    {
                        if (new FileInfo(path).Length < minimumSize) { belowMinimum++; continue; }
                        if (candidates >= Scanner.CandidateLimit) { limited = true; break; }
                        var file = NativeFile.Snapshot(path, root, "duplicates");
                        if (file.Size < minimumSize) { belowMinimum++; continue; }
                        if (!bySize.TryGetValue(file.Size, out var sameSize)) bySize[file.Size] = sameSize = [];
                        sameSize.Add(file); candidates++;
                    }
                    catch (Exception e) when (Scanner.IsFileError(e)) { Skip(path + "：" + e.Message); }
                }
                if (limited) break;
            }
            foreach (var sizeGroup in bySize.Values.Where(x => x.Count > 1))
            {
                foreach (var sampleGroup in Partition(sizeGroup, true))
                foreach (var hashGroup in Partition(sampleGroup, false))
                {
                    var stable = new List<FileCandidate>();
                    foreach (var file in hashGroup)
                    {
                        control?.Checkpoint(token); token.ThrowIfCancellationRequested();
                        try
                        {
                            if (NativeFile.Snapshot(file.Path, file.Root, "duplicates") == file) stable.Add(file);
                            else Skip(file.Path + "：扫描期间文件发生变化。");
                        }
                        catch (Exception e) when (Scanner.IsFileError(e)) { Skip(file.Path + "：" + e.Message); }
                    }
                    if (stable.Count > 1)
                    {
                        groups.Add(new(fullHashes[stable[0]], stable[0].Size, stable));
                    }
                }
            }
        }
        catch (OperationCanceledException) { cancelled = true; }
        return new(string.Join("；", scanRoots), groups.OrderByDescending(x => x.ExtraLogicalBytes).ToList(), examined, skipped, read, cancelled, limited)
        { Roots = scanRoots, Candidates = candidates, BelowMinimum = belowMinimum, ExcludedEntries = excluded, SkipExamples = skipExamples };
    }
}
