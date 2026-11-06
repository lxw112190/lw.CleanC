using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace CleanC.Core;

internal static class NativeFile
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct Info
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
        public readonly long Size => ((long)SizeHigh << 32) | SizeLow;
        public readonly long WriteTime => Time(Write);
        public readonly long CreationTime => Time(Creation);
        public readonly ulong Id => ((ulong)IndexHigh << 32) | IndexLow;
        private static long Time(System.Runtime.InteropServices.ComTypes.FILETIME t) => ((long)t.dwHighDateTime << 32) | (uint)t.dwLowDateTime;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security,
        uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out Info info);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder path, uint size, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int kind, ref int info, uint size);

    private static SafeFileHandle Open(string path, bool delete)
    {
        var handle = CreateFileW(path, delete ? 0x10080u : 0x80u, delete ? 0u : 7u,
            IntPtr.Zero, 3, 0x00200000, IntPtr.Zero); // OPEN_REPARSE_POINT; exclusive DELETE handle when cleaning.
        if (handle.IsInvalid) { handle.Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error()); }
        return handle;
    }
    private static Info Inspect(SafeFileHandle handle, string expected)
    {
        if (!GetFileInformationByHandle(handle, out Info info)) throw new Win32Exception(Marshal.GetLastWin32Error());
        if ((info.Attributes & (uint)(FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0 || info.Links != 1)
            throw new IOException("跳过链接、目录或具有多个硬链接的文件。");
        var buffer = new StringBuilder(32768);
        uint length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
        if (length == 0 || length >= buffer.Capacity) throw new IOException("无法核验实际文件路径。");
        string actual = buffer.ToString();
        if (actual.StartsWith(@"\\?\", StringComparison.Ordinal)) actual = actual[4..];
        if (!PathPolicy.Normalize(actual).Equals(PathPolicy.Normalize(expected), StringComparison.OrdinalIgnoreCase))
            throw new IOException("实际路径与预览路径不一致。");
        return info;
    }
    internal static FileCandidate Snapshot(string path, string root, string rule)
    {
        using var handle = Open(path, false);
        Info i = Inspect(handle, path);
        return new(path, root, rule, i.Size, i.WriteTime, i.CreationTime, i.Volume, i.Id);
    }
    internal static FileStream OpenReadVerified(FileCandidate file)
    {
        var handle = CreateFileW(file.Path, 0x80000080, 1, IntPtr.Zero, 3, 0x08200000, IntPtr.Zero);
        try
        {
            if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            Info i = Inspect(handle, file.Path);
            if (i.Id != file.FileId || i.Volume != file.Volume || i.Size != file.Size ||
                i.WriteTime != file.LastWrite || i.CreationTime != file.Creation)
                throw new IOException("文件已变化。");
            return new FileStream(handle, FileAccess.Read, 128 * 1024);
        }
        catch { handle.Dispose(); throw; }
    }
    internal static void DeleteUnchanged(FileCandidate file)
    {
        using var handle = Open(file.Path, true);
        Info i = Inspect(handle, file.Path);
        if (i.Id != file.FileId || i.Volume != file.Volume || i.Size != file.Size ||
            i.WriteTime != file.LastWrite || i.CreationTime != file.Creation)
            throw new IOException("文件自预览后已改变，请重新扫描。");
        int disposition = 1;
        if (!SetFileInformationByHandle(handle, 4, ref disposition, 4))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }
}
