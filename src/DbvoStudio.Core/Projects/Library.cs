using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DbvoStudio.Core.Projects;

/// <summary>
/// The on-disk library: settings, voice packs, mods (Phase 1) and DBVO Patches (Phase 2).
/// Layout:
///   settings.json
///   voicepacks\&lt;id&gt;.json
///   references\&lt;id&gt;.txt
///   mods\&lt;id&gt;\mod.json, mods\&lt;id&gt;\plugins\*.esp, mods\&lt;id&gt;\masters\*
///   patches\&lt;id&gt;\patch.json, patches\&lt;id&gt;\takes\&lt;lineId&gt;\NN\&lt;stem&gt;.wav|.lip
///   backups\&lt;timestamp&gt;-&lt;reason&gt;.zip
/// Every save is atomic (write to a temp file, then replace).
/// </summary>
public sealed class Library
{
    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public Library(string root)
    {
        Root = Path.GetFullPath(root);
        Directory.CreateDirectory(Root);
        foreach (var d in new[] { "voicepacks", "references", "mods", "patches", "fomods", "revoice", "backups" })
            Directory.CreateDirectory(Path.Combine(Root, d));
    }

    public string Root { get; }
    public string SettingsPath => Path.Combine(Root, "settings.json");
    public string ModFolder(string modId) => Path.Combine(Root, "mods", modId);
    public string PatchFolder(string patchId) => Path.Combine(Root, "patches", patchId);
    public string ReferencePath(string id) => Path.Combine(Root, "references", id + ".txt");

    public static string DefaultRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "DBVO Studio");

    // ---------- settings ----------
    public AppSettings LoadSettings() => Load<AppSettings>(SettingsPath) ?? new AppSettings();
    public void Save(AppSettings s) => SaveJson(SettingsPath, s);

    // ---------- voice packs ----------
    public List<VoicePack> LoadVoicePacks() =>
        Directory.EnumerateFiles(Path.Combine(Root, "voicepacks"), "*.json")
            .Select(Load<VoicePack>).Where(v => v != null).Select(v => v!).OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase).ToList();
    public void Save(VoicePack v) => SaveJson(Path.Combine(Root, "voicepacks", v.Id + ".json"), v);
    public void DeleteVoicePack(string id) => File.Delete(Path.Combine(Root, "voicepacks", id + ".json"));

    // ---------- mods ----------
    public List<Mod> LoadMods() =>
        Directory.EnumerateDirectories(Path.Combine(Root, "mods"))
            .Select(d => Load<Mod>(Path.Combine(d, "mod.json"))).Where(m => m != null).Select(m => m!)
            .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();
    public Mod? LoadMod(string id) => Load<Mod>(Path.Combine(ModFolder(id), "mod.json"));
    public void Save(Mod m)
    {
        m.UpdatedUtc = DateTime.UtcNow;
        SaveJson(Path.Combine(ModFolder(m.Id), "mod.json"), m);
    }

    // ---------- patches ----------
    public List<DbvoPatch> LoadPatches() =>
        Directory.EnumerateDirectories(Path.Combine(Root, "patches"))
            .Select(d => Load<DbvoPatch>(Path.Combine(d, "patch.json"))).Where(p => p != null).Select(p => p!)
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
    public DbvoPatch? LoadPatch(string id) => Load<DbvoPatch>(Path.Combine(PatchFolder(id), "patch.json"));
    public void Save(DbvoPatch p)
    {
        p.UpdatedUtc = DateTime.UtcNow;
        SaveJson(Path.Combine(PatchFolder(p.Id), "patch.json"), p);
    }

    // ---------- FOMOD projects (Phase 3) ----------
    public string FomodFolder(string id) => Path.Combine(Root, "fomods", id);
    public List<Fomod.FomodProject> LoadFomods() =>
        Directory.EnumerateDirectories(Path.Combine(Root, "fomods"))
            .Select(d => Load<Fomod.FomodProject>(Path.Combine(d, "fomod.json"))).Where(p => p != null).Select(p => p!)
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
    public Fomod.FomodProject? LoadFomod(string id) => Load<Fomod.FomodProject>(Path.Combine(FomodFolder(id), "fomod.json"));
    public void Save(Fomod.FomodProject p)
    {
        p.UpdatedUtc = DateTime.UtcNow;
        SaveJson(Path.Combine(FomodFolder(p.Id), "fomod.json"), p);
    }

    // ---------- NPC Revoice projects (Other Tools) ----------
    public string RevoiceFolder(string id) => Path.Combine(Root, "revoice", id);
    public List<Revoice.RevoiceProject> LoadRevoiceProjects() =>
        Directory.EnumerateDirectories(Path.Combine(Root, "revoice"))
            .Select(d => Load<Revoice.RevoiceProject>(Path.Combine(d, "project.json"))).Where(p => p != null).Select(p => p!)
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
    public Revoice.RevoiceProject? LoadRevoiceProject(string id) => Load<Revoice.RevoiceProject>(Path.Combine(RevoiceFolder(id), "project.json"));
    public void Save(Revoice.RevoiceProject p)
    {
        p.UpdatedUtc = DateTime.UtcNow;
        SaveJson(Path.Combine(RevoiceFolder(p.Id), "project.json"), p);
    }

    // ---------- references ----------
    public ReferenceSet AddReference(AppSettings settings, string name, string topicFile)
    {
        var lines = Topics.TopicFile.Read(topicFile);
        var r = new ReferenceSet { Name = name.Trim(), LineCount = lines.Count };
        r.StoredFile = ReferencePath(r.Id);
        Topics.TopicFile.Write(r.StoredFile, lines);
        settings.References.Add(r);
        Save(settings);
        return r;
    }

    // ---------- backups ----------
    /// <summary>Zip every JSON file (not audio) into backups\. Keeps the newest <paramref name="keep"/>.</summary>
    public string Backup(string reason, int keep = 30)
    {
        string safe = string.Concat(reason.Select(c => char.IsLetterOrDigit(c) ? c : '-')).Trim('-');
        if (safe.Length > 40) safe = safe[..40];
        var path = Path.Combine(Root, "backups", $"{DateTime.Now:yyyyMMdd-HHmmss}-{safe}.zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            foreach (var f in Directory.EnumerateFiles(Root, "*.json", SearchOption.AllDirectories))
            {
                if (f.StartsWith(Path.Combine(Root, "backups"), StringComparison.OrdinalIgnoreCase)) continue;
                zip.CreateEntryFromFile(f, Path.GetRelativePath(Root, f), CompressionLevel.Fastest);
            }
        }
        var old = Directory.EnumerateFiles(Path.Combine(Root, "backups"), "*.zip").OrderByDescending(f => f).Skip(Math.Max(1, keep));
        foreach (var o in old) { try { File.Delete(o); } catch { } }
        return path;
    }

    // ---------- json ----------
    public static T? Load<T>(string path) where T : class
    {
        if (!File.Exists(path)) return null;
        using var s = File.OpenRead(path);
        return JsonSerializer.Deserialize<T>(s, Json);
    }

    public static void SaveJson<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        using (var s = File.Create(tmp))
            JsonSerializer.Serialize(s, value, Json);
        File.Move(tmp, path, true);
    }
}
