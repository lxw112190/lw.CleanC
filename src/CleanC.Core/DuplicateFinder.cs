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
public sealed record DuplicateResult(string Root, List<DuplicateGroup> Groups, long Examined, long Skipped,
    long BytesRead, bool Cancelled, bool Limited);

public static class DuplicateFinder
{
    public static DuplicateResult Find(string root, IReadOnlyList<string> exclusions, long minimumSize,
        IProgress<ScanProgress>? progress, CancellationToken token, ScanControl? control = null)
    {
        if (minimumSize < 1) throw new ArgumentOutOfRangeException(nameof(minimumSize));
        root = PathPolicy.Normalize(root); PathPolicy.EnsureNoReparsePoints(root);
        var bySize = new Dictionary<long, List<FileCandidate>>();
        var groups = new List<DuplicateGroup>();
        var fullHashes = new Dictionary<FileCandidate, string>();
        long examined = 0, skipped = 0, read = 0; int candidates = 0; bool cancelled = false, limited = false;
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
                catch (Exception e) when (Scanner.IsFileError(e)) { skipped++; }
            }
            return map.Values.Where(x => x.Count > 1).ToList();
        }
        try
        {
            Report("收集文件 · 按大小分组", true);
            foreach (string path in Scanner.Walk(root, exclusions, () => skipped++, token, control))
            {
                examined++; Report("收集文件 · 按大小分组");
                try
                {
                    if (new FileInfo(path).Length < minimumSize) continue;
                    if (candidates >= Scanner.CandidateLimit) { limited = true; break; }
                    var file = NativeFile.Snapshot(path, root, "duplicates");
                    if (!bySize.TryGetValue(file.Size, out var sameSize)) bySize[file.Size] = sameSize = [];
                    sameSize.Add(file); candidates++;
                }
                catch (Exception e) when (Scanner.IsFileError(e)) { skipped++; }
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
                            if (NativeFile.Snapshot(file.Path, root, "duplicates") == file) stable.Add(file);
                            else skipped++;
                        }
                        catch (Exception e) when (Scanner.IsFileError(e)) { skipped++; }
                    }
                    if (stable.Count > 1)
                    {
                        groups.Add(new(fullHashes[stable[0]], stable[0].Size, stable));
                    }
                }
            }
        }
        catch (OperationCanceledException) { cancelled = true; }
        return new(root, groups.OrderByDescending(x => x.ExtraLogicalBytes).ToList(), examined, skipped, read, cancelled, limited);
    }
}
