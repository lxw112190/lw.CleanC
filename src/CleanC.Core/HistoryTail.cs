using System.Text;

namespace CleanC.Core;

public static class HistoryTail
{
    public static string? ReadLastLine(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (stream.Length == 0) return null;
        int size = (int)Math.Min(stream.Length, 256 * 1024);
        stream.Position = stream.Length - size;
        byte[] bytes = new byte[size]; stream.ReadExactly(bytes);
        string text = Encoding.UTF8.GetString(bytes).TrimEnd('\r', '\n');
        int newline = text.LastIndexOf('\n');
        if (newline >= 0) return text[(newline + 1)..].TrimEnd('\r');
        return stream.Length > size ? null : text;
    }
}
