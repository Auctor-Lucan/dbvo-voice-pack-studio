using DbvoStudio.Core.Text;

namespace DbvoStudio.Core.Plugins;

/// <summary>
/// Localized strings for one plugin (.STRINGS, .DLSTRINGS, .ILSTRINGS).
/// .STRINGS entries are null-terminated; DL/IL entries are length-prefixed.
/// </summary>
public sealed class StringsTable
{
    private readonly Dictionary<uint, string> _strings = new();

    public int Count => _strings.Count;

    public bool TryGet(uint id, out string value) => _strings.TryGetValue(id, out value!);

    /// <summary>Load all three strings files for a plugin from a folder, e.g. Data\Strings\Skyrim_English.*.</summary>
    public static StringsTable Load(string folder, string pluginBaseName, string language = "English")
    {
        var t = new StringsTable();
        foreach (var (ext, lengthPrefixed) in new[] { ("STRINGS", false), ("DLSTRINGS", true), ("ILSTRINGS", true) })
        {
            var path = FindCaseInsensitive(folder, $"{pluginBaseName}_{language}.{ext}");
            if (path != null) t.AddFile(File.ReadAllBytes(path), lengthPrefixed);
        }
        return t;
    }

    /// <summary>Load a plugin's strings from an archive (vanilla strings live in "Skyrim - Interface.bsa"). Null when none are there.</summary>
    public static StringsTable? FromArchive(Archives.BsaArchive bsa, string pluginBaseName, string language = "English")
    {
        var t = new StringsTable();
        bool any = false;
        foreach (var (ext, lengthPrefixed) in new[] { ("STRINGS", false), ("DLSTRINGS", true), ("ILSTRINGS", true) })
        {
            var data = bsa.Read($@"strings\{pluginBaseName}_{language}.{ext}".ToLowerInvariant());
            if (data == null) continue;
            t.AddFile(data, lengthPrefixed);
            any = true;
        }
        return any ? t : null;
    }

    /// <summary>True if any strings file for the plugin exists in the folder.</summary>
    public static bool Exists(string folder, string pluginBaseName, string language = "English") =>
        new[] { "STRINGS", "DLSTRINGS", "ILSTRINGS" }
            .Any(ext => FindCaseInsensitive(folder, $"{pluginBaseName}_{language}.{ext}") != null);

    public void AddFile(byte[] b, bool lengthPrefixed)
    {
        if (b.Length < 8) return;
        uint count = BitConverter.ToUInt32(b, 0);
        int start = checked(8 + (int)count * 8);
        for (int i = 0; i < count; i++)
        {
            uint id = BitConverter.ToUInt32(b, 8 + i * 8);
            int o = start + (int)BitConverter.ToUInt32(b, 12 + i * 8);
            if (o >= b.Length) continue;
            string s;
            if (lengthPrefixed)
            {
                int len = (int)BitConverter.ToUInt32(b, o);
                int end = Math.Min(b.Length, o + 4 + len);
                int n = end - (o + 4);
                while (n > 0 && b[o + 4 + n - 1] == 0) n--;
                s = Encodings.Cp1252.GetString(b, o + 4, n);
            }
            else
            {
                int end = Array.IndexOf(b, (byte)0, o);
                if (end < 0) end = b.Length;
                s = Encodings.Cp1252.GetString(b, o, end - o);
            }
            _strings.TryAdd(id, s);
        }
    }

    private static string? FindCaseInsensitive(string folder, string name)
    {
        if (!Directory.Exists(folder)) return null;
        var direct = Path.Combine(folder, name);
        if (File.Exists(direct)) return direct;
        return Directory.EnumerateFiles(folder)
            .FirstOrDefault(f => string.Equals(Path.GetFileName(f), name, StringComparison.OrdinalIgnoreCase));
    }
}
