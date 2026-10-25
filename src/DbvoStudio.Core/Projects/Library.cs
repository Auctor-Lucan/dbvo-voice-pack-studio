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
        foreach (var d in new[] { "voicepacks", "references", "mods", "patches", "fomods", "revoice", "books", "grunts", "backups" })
            Directory.CreateDirectory(Path.Combine(Root, d));
    }

    public string Root { get; }
    public string SettingsPath => Path.Combine(Root, "settings.json");
    /// <summary>Built .fuz files by content (audio + LIP + encoder), so a rebuild only encodes what changed. Not backed up.</summary>
    public string FuzCacheFolder => Path.Combine(Root, "cache", "fuz");
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

    // ---------- Voiced Books projects (Other Tools) ----------
    public string BooksFolder(string id) => Path.Combine(Root, "books", id);
    public List<Books.BooksProject> LoadBooksProjects() =>
        Directory.EnumerateDirectories(Path.Combine(Root, "books"))
            .Select(d => Load<Books.BooksProject>(Path.Combine(d, "project.json"))).Where(p => p != null).Select(p => p!)
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
    public Books.BooksProject? LoadBooksProject(string id) => Load<Books.BooksProject>(Path.Combine(BooksFolder(id), "project.json"));
    public void Save(Books.BooksProject p)
    {
        p.UpdatedUtc = DateTime.UtcNow;
        SaveJson(Path.Combine(BooksFolder(p.Id), "project.json"), p);
    }

    // ---------- Player Grunts projects (Other Tools) ----------
    public string GruntsFolder(string id) => Path.Combine(Root, "grunts", id);
    /// <summary>The user's custom Player Grunts event definitions (shared by all projects).</summary>
    public string GruntCustomEventsPath => Path.Combine(Root, "grunts", "custom-events.json");
    public List<Grunts.GruntProject> LoadGruntProjects() =>
        Directory.EnumerateDirectories(Path.Combine(Root, "grunts"))
            .Select(d => Load<Grunts.GruntProject>(Path.Combine(d, "project.json"))).Where(p => p != null).Select(p => p!)
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
    public Grunts.GruntProject? LoadGruntProject(string id) => Load<Grunts.GruntProject>(Path.Combine(GruntsFolder(id), "project.json"));
    public void Save(Grunts.GruntProject p)
    {
        p.UpdatedUtc = DateTime.UtcNow;
        SaveJson(Path.Combine(GruntsFolder(p.Id), "project.json"), p);
    }
    public Grunts.GruntCustomEvents LoadGruntCustomEvents() => Load<Grunts.GruntCustomEvents>(GruntCustomEventsPath) ?? new Grunts.GruntCustomEvents();
    public void Save(Grunts.GruntCustomEvents c) => SaveJson(GruntCustomEventsPath, c);

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
    /// <summary>
    /// Zip every project file into backups\. Routine backups (before a generate or build) are rotated on their own
    /// and made at most every <paramref name="routineMinutes"/> minutes, so they never push out the backups made
    /// before an import or a delete.
    /// </summary>
    public string? Backup(string reason, int keep = 30, bool routine = false, int routineMinutes = 10)
    {
        string safe = string.Concat(reason.Select(c => char.IsLetterOrDigit(c) ? c : '-')).Trim('-');
        if (safe.Length > 40) safe = safe[..40];
        var dir = Path.Combine(Root, "backups");
        Directory.CreateDirectory(dir);
        static bool IsRoutine(string f) => Path.GetFileName(f).Contains("-auto-", StringComparison.Ordinal);
        if (routine)
        {
            var last = Directory.EnumerateFiles(dir, "*.zip").Where(IsRoutine).Select(File.GetLastWriteTimeUtc).DefaultIfEmpty(DateTime.MinValue).Max();
            if (DateTime.UtcNow - last < TimeSpan.FromMinutes(routineMinutes)) return null;
        }
        var stamp = $"{DateTime.Now:yyyyMMdd-HHmmss}{(routine ? "-auto" : "")}-{safe}";
        var path = Path.Combine(dir, stamp + ".zip");
        for (int i = 2; File.Exists(path); i++) path = Path.Combine(dir, $"{stamp}-{i}.zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            foreach (var f in Directory.EnumerateFiles(Root, "*.json", SearchOption.AllDirectories))
            {
                if (f.StartsWith(dir, StringComparison.OrdinalIgnoreCase)) continue;
                zip.CreateEntryFromFile(f, Path.GetRelativePath(Root, f), CompressionLevel.Fastest);
            }
        }
        var old = Directory.EnumerateFiles(dir, "*.zip").Where(f => IsRoutine(f) == routine).OrderByDescending(f => Path.GetFileName(f), StringComparer.Ordinal).Skip(Math.Max(1, keep));
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

    /// <summary>Called before a JSON file is written (the app keeps the old content for Undo).</summary>
    public static Action<string>? BeforeSave;

    public static void SaveJson<T>(string path, T value)
    {
        BeforeSave?.Invoke(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        using (var s = File.Create(tmp))
            JsonSerializer.Serialize(s, value, Json);
        File.Move(tmp, path, true);
    }
}
