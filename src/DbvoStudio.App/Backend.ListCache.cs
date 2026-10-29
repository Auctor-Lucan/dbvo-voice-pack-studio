using System.IO;
using System.Text.Json;
using DbvoStudio.Core.Projects;

namespace DbvoStudio.App;

/// <summary>
/// The patch and mod rows in the lists are computed from the full patch.json/mod.json files, which grow
/// to tens of MB each. Rows are kept here (in memory and in cache/list-rows.json) keyed on each file's
/// date and size, so startup and page refreshes only re-read the files that changed.
/// </summary>
public sealed partial class Backend
{
    private sealed class ListRow
    {
        public string Key { get; set; } = "";
        public string Name { get; set; } = "";
        public string Category { get; set; } = "";
        public int Unreviewed { get; set; }
        public JsonElement Row { get; set; }
    }

    private sealed class ListRows
    {
        public int Version { get; set; } = 1;
        public Dictionary<string, ListRow> Mods { get; set; } = new();
        public Dictionary<string, ListRow> Patches { get; set; } = new();
    }

    private readonly object _rowsLock = new();
    private ListRows? _rows;

    private string RowsPath => Path.Combine(_lib.Root, "cache", "list-rows.json");

    private static string Stamp(string file)
    {
        var fi = new FileInfo(file);
        return fi.Exists ? $"{fi.LastWriteTimeUtc.Ticks:x}.{fi.Length:x}" : "-";
    }

    /// <summary>Mod rows and patch rows for State(), re-reading only the files that changed.</summary>
    private (List<JsonElement> mods, List<JsonElement> patches) ListRowsFor(List<VoicePack> voices)
    {
        lock (_rowsLock)
        {
            if (_rows == null)
            {
                try { _rows = File.Exists(RowsPath) ? JsonSerializer.Deserialize<ListRows>(File.ReadAllText(RowsPath), JsonOptions) : null; } catch { }
                if (_rows == null || _rows.Version != 1) _rows = new ListRows();
            }
            bool changed = false;

            var modRows = new Dictionary<string, ListRow>();
            var modsDir = Path.Combine(_lib.Root, "mods");
            if (Directory.Exists(modsDir))
                foreach (var d in Directory.EnumerateDirectories(modsDir))
                {
                    var file = Path.Combine(d, "mod.json");
                    var key = Stamp(file);
                    if (key == "-") continue;
                    var id = Path.GetFileName(d);
                    if (!_rows.Mods.TryGetValue(id, out var r) || r.Key != key)
                    {
                        Mod? m;
                        try { m = Library.Load<Mod>(file); } catch { m = null; }
                        if (m == null) continue;
                        r = new ListRow
                        {
                            Key = key, Name = m.Name, Category = m.Category,
                            Unreviewed = m.Plugins.Sum(pl => Phase1Service.UnreviewedFlags(pl).Count),
                            Row = JsonSerializer.SerializeToElement(ModSummary(m), JsonOptions),
                        };
                        _rows.Mods[id] = r; changed = true;
                    }
                    modRows[id] = r;
                }

            var patchRows = new List<ListRow>();
            var seen = new HashSet<string>();
            var patchesDir = Path.Combine(_lib.Root, "patches");
            if (Directory.Exists(patchesDir))
                foreach (var d in Directory.EnumerateDirectories(patchesDir))
                {
                    var file = Path.Combine(d, "patch.json");
                    var stamp = Stamp(file);
                    if (stamp == "-") continue;
                    var id = Path.GetFileName(d);
                    seen.Add(id);
                    if (!_rows.Patches.TryGetValue(id, out var r) || !KeyMatches(r.Key, stamp))
                    {
                        DbvoPatch? x;
                        try { x = Library.Load<DbvoPatch>(file); } catch { x = null; }
                        if (x == null) continue;
                        var its = x.ModIds.Where(modRows.ContainsKey).Select(i => (id: i, row: modRows[i]))
                            .OrderBy(t => t.row.Name, StringComparer.OrdinalIgnoreCase).ToList();
                        r = new ListRow
                        {
                            Key = PatchKey(stamp, x.VoicePackId, x.ModIds),
                            Name = x.Name,
                            Row = JsonSerializer.SerializeToElement(PatchSummary(x, voices, its.Select(t => (t.id, t.row.Name, t.row.Category, t.row.Unreviewed)).ToList()), JsonOptions),
                        };
                        _rows.Patches[id] = r; changed = true;
                    }
                    patchRows.Add(r);
                }
            foreach (var gone in _rows.Patches.Keys.Where(k => !seen.Contains(k)).ToList()) { _rows.Patches.Remove(gone); changed = true; }
            foreach (var gone in _rows.Mods.Keys.Where(k => !modRows.ContainsKey(k)).ToList()) { _rows.Mods.Remove(gone); changed = true; }

            if (changed)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(RowsPath)!);
                    var tmp = RowsPath + ".tmp";
                    File.WriteAllText(tmp, JsonSerializer.Serialize(_rows, JsonOptions));
                    File.Move(tmp, RowsPath, true);
                }
                catch { }
            }
            return (modRows.Values.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).Select(r => r.Row).ToList(),
                    patchRows.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).Select(r => r.Row).ToList());
        }
    }

    /// <summary>patch stamp | voice pack id=stamp | mod id=stamp…</summary>
    private string PatchKey(string stamp, string voiceId, IEnumerable<string> modIds) =>
        stamp + "|" + voiceId + "=" + Stamp(Path.Combine(_lib.Root, "voicepacks", voiceId + ".json")) + "|" +
        string.Join(",", modIds.Select(m => m + "=" + Stamp(Path.Combine(_lib.ModFolder(m), "mod.json"))));

    private bool KeyMatches(string key, string stamp)
    {
        var parts = key.Split('|');
        if (parts.Length != 3 || parts[0] != stamp) return false;
        var v = parts[1].Split('=', 2);
        var mods = parts[2].Length == 0 ? Array.Empty<string>() : parts[2].Split(',');
        if (v.Length != 2) return false;
        return PatchKey(stamp, v[0], mods.Select(m => m.Split('=', 2)[0])) == key;
    }
}
