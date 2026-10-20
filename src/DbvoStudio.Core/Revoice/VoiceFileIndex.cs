using System.IO.Compression;
using DbvoStudio.Core.Archives;

namespace DbvoStudio.Core.Revoice;

/// <summary>Where voice files come from. Higher priority wins; loose files beat archives at the same priority, as in game.</summary>
public sealed class RevoiceSource
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    /// <summary>"folder" (a Data folder, a mod folder or any folder with Sound\Voice inside), "bsa" or "zip".</summary>
    public string Kind { get; set; } = "folder";
    public string Path { get; set; } = "";
    public int Priority { get; set; }
    /// <summary>Added automatically from the base game folder.</summary>
    public bool BaseGame { get; set; }
}

/// <summary>One voice file found in a source.</summary>
public sealed class VoiceFile
{
    public required string PluginFolder { get; init; }
    public required string VoiceType { get; init; }
    public required string Stem { get; init; }
    public required string Extension { get; init; }
    public required RevoiceSource Source { get; init; }
    /// <summary>Path inside the source (relative for folders, entry path for archives).</summary>
    public required string Inner { get; init; }
    public uint FormId { get; init; }
    public int Response { get; init; }
    public bool Loose => Source.Kind == "folder";
    public string Display => $"{System.IO.Path.GetFileName(Source.Path)}: {Inner}";
}

/// <summary>
/// Every voice file under Sound\Voice\&lt;plugin&gt;\&lt;voice type&gt;\ in the sources, with the copy the game would use.
/// Lines are matched by plugin folder + voice type + FormID + response number, so they still match when editor IDs
/// were renamed or names were cut differently.
/// </summary>
public sealed class VoiceFileIndex : IDisposable
{
    private readonly Dictionary<(string Plugin, string Vt, uint Id, int Resp), VoiceFile> _winners = new();
    private readonly Dictionary<string, BsaArchive> _bsas = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Errors { get; } = new();
    public int FileCount { get; private set; }

    public IEnumerable<VoiceFile> Files => _winners.Values;

    public static VoiceFileIndex Build(IEnumerable<RevoiceSource> sources)
    {
        var idx = new VoiceFileIndex();
        // Lowest priority first, archives before loose files, so later ones overwrite.
        foreach (var s in sources.OrderBy(s => s.Priority).ThenBy(s => s.Kind == "folder" ? 1 : 0))
        {
            try { idx.Scan(s); }
            catch (Exception ex) { idx.Errors.Add($"{s.Path}: {ex.Message}"); }
        }
        return idx;
    }

    private void Scan(RevoiceSource s)
    {
        switch (s.Kind)
        {
            case "bsa":
            {
                var bsa = BsaArchive.Open(s.Path);
                _bsas[s.Path] = bsa;
                foreach (var e in bsa.Entries) Add(s, e.Path);
                break;
            }
            case "zip":
            {
                using var z = ZipFile.OpenRead(s.Path);
                foreach (var e in z.Entries) Add(s, e.FullName);
                break;
            }
            default:
            {
                if (!Directory.Exists(s.Path)) throw new DirectoryNotFoundException("Folder not found.");
                foreach (var voiceRoot in FindVoiceRoots(s.Path))
                    foreach (var f in Directory.EnumerateFiles(voiceRoot, "*", SearchOption.AllDirectories))
                        Add(s, System.IO.Path.GetRelativePath(s.Path, f));
                break;
            }
        }
    }

    /// <summary>Sound\Voice folders at the top of the folder, one level down (a Data folder inside a mod) or the folder itself.</summary>
    private static IEnumerable<string> FindVoiceRoots(string folder)
    {
        string? Find(string parent, string name) => Directory.Exists(parent)
            ? Directory.EnumerateDirectories(parent).FirstOrDefault(d => string.Equals(System.IO.Path.GetFileName(d), name, StringComparison.OrdinalIgnoreCase)) : null;
        var sound = Find(folder, "sound");
        if (sound != null && Find(sound, "voice") is { } v) { yield return v; yield break; }
        if (string.Equals(System.IO.Path.GetFileName(folder), "voice", StringComparison.OrdinalIgnoreCase)) { yield return folder; yield break; }
        var data = Find(folder, "data");
        if (data != null && Find(data, "sound") is { } s2 && Find(s2, "voice") is { } v2) yield return v2;
    }

    private void Add(RevoiceSource s, string inner)
    {
        var p = inner.Replace('/', '\\');
        int at = p.IndexOf(@"sound\voice\", StringComparison.OrdinalIgnoreCase);
        string rest = at >= 0 ? p[(at + 12)..] : p;
        var parts = rest.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3) return;
        string ext = System.IO.Path.GetExtension(parts[2]).ToLowerInvariant();
        if (ext is not (".fuz" or ".xwm" or ".wav")) return;
        string stem = System.IO.Path.GetFileNameWithoutExtension(parts[2]);
        var bits = stem.Split('_');
        if (bits.Length < 3 || !uint.TryParse(bits[^2], System.Globalization.NumberStyles.HexNumber, null, out uint id) || !int.TryParse(bits[^1], out int resp)) return;
        var vf = new VoiceFile
        {
            PluginFolder = parts[0], VoiceType = parts[1], Stem = stem, Extension = ext, Source = s, Inner = inner,
            FormId = id & 0xFFFFFF, Response = resp,
        };
        var key = (parts[0].ToLowerInvariant(), parts[1].ToLowerInvariant(), vf.FormId, resp);
        // Within one source, .fuz beats .xwm beats .wav (the game prefers .fuz).
        if (_winners.TryGetValue(key, out var old) && old.Source == s && Rank(old.Extension) >= Rank(ext)) return;
        _winners[key] = vf;
        FileCount++;
    }

    private static int Rank(string ext) => ext == ".fuz" ? 3 : ext == ".xwm" ? 2 : 1;

    public VoiceFile? Find(string pluginFolder, string voiceType, uint formId, int response) =>
        _winners.TryGetValue((pluginFolder.ToLowerInvariant(), voiceType.ToLowerInvariant(), formId & 0xFFFFFF, response), out var f) ? f : null;

    /// <summary>Voice type folders that have a file for this line.</summary>
    public IEnumerable<VoiceFile> ForLine(string pluginFolder, uint formId, int response)
    {
        var pf = pluginFolder.ToLowerInvariant();
        return _winners.Where(kv => kv.Key.Plugin == pf && kv.Key.Id == (formId & 0xFFFFFF) && kv.Key.Resp == response).Select(kv => kv.Value);
    }

    /// <summary>Lookup by line for all voice types at once (built on demand).</summary>
    public ILookup<(string Plugin, uint Id, int Resp), VoiceFile> ByLine() =>
        _winners.ToLookup(kv => (kv.Key.Plugin, kv.Key.Id, kv.Key.Resp), kv => kv.Value);

    /// <summary>The file's bytes; for a .wav also its .lip when present.</summary>
    public byte[] Read(VoiceFile f)
    {
        switch (f.Source.Kind)
        {
            case "bsa":
                if (!_bsas.TryGetValue(f.Source.Path, out var bsa)) _bsas[f.Source.Path] = bsa = BsaArchive.Open(f.Source.Path);
                return bsa.Read(f.Inner) ?? throw new FileNotFoundException(f.Inner);
            case "zip":
            {
                using var z = ZipFile.OpenRead(f.Source.Path);
                var e = z.GetEntry(f.Inner) ?? z.Entries.First(x => x.FullName.Equals(f.Inner, StringComparison.OrdinalIgnoreCase));
                using var st = e.Open();
                using var ms = new MemoryStream();
                st.CopyTo(ms);
                return ms.ToArray();
            }
            default:
                return File.ReadAllBytes(System.IO.Path.Combine(f.Source.Path, f.Inner));
        }
    }

    public void Dispose()
    {
        foreach (var b in _bsas.Values) b.Dispose();
        _bsas.Clear();
    }
}
