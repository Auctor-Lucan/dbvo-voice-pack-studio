using System.Text;
using DbvoStudio.Core.Text;

namespace DbvoStudio.Core.Topics;

/// <summary>Topic .txt files: one in-game line per row, UTF-8, CRLF.</summary>
public static class TopicFile
{
    public static List<string> Read(string path)
    {
        var bytes = File.ReadAllBytes(path);
        string text = Decode(bytes);
        return text.Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .Where(l => l.Trim().Length > 0)
            .ToList();
    }

    public static void Write(string path, IEnumerable<string> lines)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var sb = new StringBuilder();
        foreach (var l in lines) sb.Append(l).Append("\r\n");
        File.WriteAllText(path, sb.ToString(), Encodings.Utf8NoBom);
    }

    /// <summary>UTF-8 (with or without BOM), UTF-16 with BOM, else Windows-1252.</summary>
    public static string Decode(byte[] b)
    {
        if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) return Encoding.UTF8.GetString(b, 3, b.Length - 3);
        if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE) return Encoding.Unicode.GetString(b, 2, b.Length - 2);
        if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(b, 2, b.Length - 2);
        try
        {
            return new UTF8Encoding(false, true).GetString(b);
        }
        catch (DecoderFallbackException)
        {
            return Encodings.Cp1252.GetString(b);
        }
    }
}
