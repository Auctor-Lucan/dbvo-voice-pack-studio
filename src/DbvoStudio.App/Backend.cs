using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using DbvoStudio.Core.ElevenLabs;
using DbvoStudio.Core.Naming;
using DbvoStudio.Core.Plugins;
using DbvoStudio.Core.Projects;
using DbvoStudio.Core.Rules;
using DbvoStudio.Core.Tools;
using Microsoft.Win32;

namespace DbvoStudio.App;

/// <summary>
/// The bridge between the web UI and the core library. Messages are {id, method, params};
/// replies are {id, result} or {id, error}; pushed events are {event, data}.
/// All state changes are saved immediately, so nothing reverts between runs.
/// </summary>
public sealed class Backend
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly Window _owner;
    private readonly Action<object> _post;
    private readonly Library _lib;
    private readonly AppSettings _settings;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _jobCts;
    private List<ElevenVoice>? _voices;
    private List<ElevenModel>? _models;

    public Backend(Window owner, Action<object> post)
    {
        _owner = owner;
        _post = post;
        var pointer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DBVOStudio", "library.txt");
        string root = File.Exists(pointer) ? File.ReadAllText(pointer).Trim() : Library.DefaultRoot;
        if (string.IsNullOrWhiteSpace(root)) root = Library.DefaultRoot;
        _lib = new Library(root);
        _settings = _lib.LoadSettings();
        AutoDetectTools();
    }

    public string LibraryRoot => _lib.Root;

    // ------------------------------------------------------------------ dispatch

    public async Task HandleAsync(string json, List<string> droppedFiles)
    {
        JsonElement msg;
        try { msg = JsonDocument.Parse(json).RootElement; }
        catch { return; }
        if (msg.ValueKind == JsonValueKind.String) msg = JsonDocument.Parse(msg.GetString()!).RootElement;
        var id = msg.TryGetProperty("id", out var idEl) ? idEl.GetInt32() : 0;
        var method = msg.GetProperty("method").GetString() ?? "";
        var p = msg.TryGetProperty("params", out var pe) ? pe : default;
        try
        {
            object? result = await Dispatch(method, p, droppedFiles);
            _post(new { id, result });
        }
        catch (Exception ex)
        {
            App.Log($"{method} failed: {ex}");
            _post(new { id, error = ex.Message });
        }
    }

    private void Emit(string name, object data) => _post(new { @event = name, data });

    private async Task<object?> Dispatch(string m, JsonElement p, List<string> dropped)
    {
        switch (m)
        {
            // ---- app / settings
            case "state": return State();
            case "settings.save": return await Locked(() => SaveSettings(p));
            case "settings.setApiKey":
                return await Locked(() =>
                {
                    _settings.ElevenLabsApiKey = Secrets.Protect(S(p, "key"));
                    _voices = null; _models = null;
                    _lib.Save(_settings);
                    return State();
                });
            case "settings.setAiKey":
                return await Locked(() => { _settings.AiProvider.ApiKey = Secrets.Protect(S(p, "key")); _lib.Save(_settings); return State(); });
            case "eleven.subscription":
                using (var c = Client()) return await c.GetSubscriptionAsync();
            case "eleven.voices":
                if (_voices == null || B(p, "refresh")) { using var c2 = Client(); _voices = await c2.GetVoicesAsync(); }
                return _voices.OrderBy(v => v.Name).Select(v => new { voiceId = v.VoiceId, name = v.Name, category = v.Category }).ToList();
            case "eleven.models":
                if (_models == null || B(p, "refresh")) { using var c3 = Client(); _models = await c3.GetModelsAsync(); }
                return _models.Select(x => new { modelId = x.ModelId, name = x.Name, canUseStyle = x.CanUseStyle, canUseSpeakerBoost = x.CanUseSpeakerBoost, maxChars = x.MaxCharsSubscribed }).ToList();
            case "eleven.subscriptionInfo":
                using (var c4 = Client())
                {
                    var sub = await c4.GetSubscriptionAsync();
                    return new { tier = sub.Tier, characterCount = sub.CharacterCount, characterLimit = sub.CharacterLimit };
                }
            case "pick.file": return PickFile(S(p, "title"), S(p, "filter"), false);
            case "pick.files": return PickFile(S(p, "title"), S(p, "filter"), true);
            case "pick.folder": return PickFolder(S(p, "title"));
            case "dropped": return dropped;
            case "open.path": OpenPath(S(p, "path")); return true;
            case "open.url": Process.Start(new ProcessStartInfo(S(p, "url")) { UseShellExecute = true }); return true;
            case "references.add":
                return await Locked(() => { _lib.AddReference(_settings, S(p, "name"), S(p, "path")); return State(); });
            case "references.remove":
                return await Locked(() =>
                {
                    var rid = S(p, "id");
                    var r = _settings.References.FirstOrDefault(x => x.Id == rid);
                    if (r != null) { _settings.References.Remove(r); try { File.Delete(r.StoredFile); } catch { } }
                    _lib.Save(_settings);
                    return State();
                });
            case "backup.now": return _lib.Backup(S(p, "reason", "manual"), _settings.BackupsToKeep);
            case "library.open": OpenPath(_lib.Root); return true;
            case "library.change": return await Locked(() => ChangeLibrary(S(p, "path")));
            case "rules.check": return CheckText(S(p, "text"));
            case "categories.save":
                return await Locked(() =>
                {
                    var cats = Strings(p, "categories").Select(c => c.Trim()).Where(c => c.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    if (cats.Count == 0) cats = AppSettings.DefaultCategories.ToList();
                    _settings.Categories = cats;
                    _lib.Save(_settings);
                    return State();
                });
            case "mod.setCategory":
                return await Locked(() =>
                {
                    var mod = LoadMod(S(p, "id"));
                    mod.Category = S(p, "category", "Misc");
                    EnsureCategory(mod.Category);
                    _lib.Save(mod);
                    return State();
                });

            // ---- voice packs
            case "voice.save":
                return await Locked(() =>
                {
                    var v = p.GetProperty("voice").Deserialize<VoicePack>(JsonOptions)!;
                    if (string.IsNullOrWhiteSpace(v.Name)) throw new InvalidOperationException("Give the voice pack a name.");
                    v.PackId = v.PackId.Trim();
                    _lib.Save(v);
                    _settings.LastVoicePackId = v.Id;
                    _lib.Save(_settings);
                    return v;
                });
            case "voice.delete":
                return await Locked(() => { _lib.DeleteVoicePack(S(p, "id")); return State(); });

            // ---- mods (Phase 1)
            case "mod.create":
                return await Locked(() =>
                {
                    var mod = new Mod { Name = S(p, "name").Trim(), Version = S(p, "version").Trim(), Category = S(p, "category", "Misc").Trim() };
                    if (mod.Name.Length == 0) throw new InvalidOperationException("Give the mod a name.");
                    EnsureCategory(mod.Category);
                    _lib.Save(mod);
                    return ModDto(mod);
                });
            case "mod.get": return ModDto(LoadMod(S(p, "id")));
            case "mod.saveMeta":
                return await Locked(() =>
                {
                    var mod = LoadMod(S(p, "id"));
                    mod.Name = S(p, "name", mod.Name);
                    mod.Version = S(p, "version", mod.Version);
                    mod.NexusUrl = S(p, "nexusUrl", mod.NexusUrl);
                    mod.Category = S(p, "category", mod.Category);
                    EnsureCategory(mod.Category);
                    _lib.Save(mod);
                    return ModDto(mod);
                });
            case "mod.delete":
                return await Locked(() =>
                {
                    _lib.Backup("before-delete-mod", _settings.BackupsToKeep);
                    Directory.Delete(_lib.ModFolder(S(p, "id")), true);
                    return State();
                });
            case "mod.import": return await Locked(() => ImportPlugins(p));
            case "mod.importFix":
                return await Locked(() =>
                {
                    var mod = LoadMod(S(p, "id"));
                    var pl = mod.Plugins.First(x => x.FileName == S(p, "plugin"));
                    var (applied, markers, unmatched) = new Phase1Service(_lib, Runtime()).ImportFixPlugin(mod, pl, S(p, "path"));
                    return new { mod = ModDto(LoadMod(mod.Id)), applied, markers, unmatched };
                });
            case "mod.exportPlayerNames":
                return await Locked(() =>
                {
                    var mod = LoadMod(S(p, "id"));
                    var pl = mod.Plugins.First(x => x.FileName == S(p, "plugin"));
                    var keys = Strings(p, "keys").ToHashSet();
                    var lines = pl.Lines.Where(l => l.IsPlayerName && l.Status != Phase1Status.Removed && !l.Excluded && (keys.Count == 0 || keys.Contains(l.Key))).ToList();
                    if (lines.Count == 0) throw new InvalidOperationException("No player-name lines to export.");
                    var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
                    Directory.CreateDirectory(downloads);
                    var file = Path.Combine(downloads, $"Player name lines - {SafeFile(mod.Name)}.txt");
                    Core.Topics.TopicFile.Write(file, lines.Select(l => (l.Final ?? l.Original).Trim()).Distinct());
                    foreach (var l in lines) { l.PlayerNameExported = true; l.Reviewed = true; }
                    _lib.Save(mod);
                    return new { file, count = lines.Count, mod = ModDto(mod) };
                });
            case "mod.addMasters":
                return await Locked(() =>
                {
                    var mod = LoadMod(S(p, "id"));
                    var svc = new Phase1Service(_lib, Runtime());
                    foreach (var f in Paths(p, "paths")) svc.AddMaster(mod, f);
                    return ModDto(mod);
                });
            case "mod.deleteMasters":
                return await Locked(() =>
                {
                    var mod = LoadMod(S(p, "id"));
                    new Phase1Service(_lib, Runtime()).DeleteUploadedMasters(mod);
                    return ModDto(mod);
                });
            case "mod.addStrings":
                return await Locked(() =>
                {
                    var mod = LoadMod(S(p, "id"));
                    var dir = Path.Combine(_lib.ModFolder(mod.Id), "strings");
                    Directory.CreateDirectory(dir);
                    foreach (var f in Paths(p, "paths")) File.Copy(f, Path.Combine(dir, Path.GetFileName(f)), true);
                    return ModDto(mod);
                });
            case "line1.update": return await Locked(() => UpdatePhase1Line(p));
            case "line1.bulk": return await Locked(() => BulkPhase1(p));
            case "mod.writeOutputs": return await Locked(() => WriteModOutputs(p));

            // ---- DBVO Patches (Phase 2)
            case "patch.create":
                return await Locked(() =>
                {
                    var voice = LoadVoice(S(p, "voicePackId"));
                    var refs = p.TryGetProperty("referenceIds", out _) ? Strings(p, "referenceIds") : null;
                    return PatchDto(CreatePatch(voice, Strings(p, "modIds"), S(p, "name"), refs));
                });
            case "patch.openFor":
                // Continue to Phase 2: the mod's patch for the given (or last used) voice pack, created if missing.
                return await Locked(() =>
                {
                    var voices = _lib.LoadVoicePacks();
                    if (voices.Count == 0) return new { needVoicePack = true, id = (string?)null };
                    var vid = S(p, "voicePackId", _settings.LastVoicePackId ?? "");
                    var voice = voices.FirstOrDefault(v => v.Id == vid) ?? voices[0];
                    var modId = S(p, "modId");
                    var existing = _lib.LoadPatches().FirstOrDefault(x => x.VoicePackId == voice.Id && x.ModIds.Count == 1 && x.ModIds[0] == modId)
                                   ?? _lib.LoadPatches().FirstOrDefault(x => x.VoicePackId == voice.Id && x.ModIds.Contains(modId));
                    var patch = existing != null ? LoadPatch(existing.Id) : CreatePatch(voice, new List<string> { modId }, "", null);
                    return new { needVoicePack = false, id = (string?)patch.Id };
                });
            case "patch.switchVoice":
                // Same mods, another voice pack: open that voice's patch, creating it if needed.
                return await Locked(() =>
                {
                    var cur = LoadPatch(S(p, "id"));
                    var voice = LoadVoice(S(p, "voicePackId"));
                    var set = cur.ModIds.OrderBy(x => x).ToList();
                    var existing = _lib.LoadPatches().FirstOrDefault(x => x.VoicePackId == voice.Id && x.ModIds.OrderBy(y => y).SequenceEqual(set));
                    var patch = existing != null ? LoadPatch(existing.Id) : CreatePatch(voice, cur.ModIds.ToList(), "", cur.ReferenceIds.ToList());
                    _settings.LastVoicePackId = voice.Id;
                    _lib.Save(_settings);
                    return new { id = patch.Id };
                });
            case "patch.get": return await Locked(() => PatchDto(LoadPatch(S(p, "id"))));
            case "patch.saveMeta":
                return await Locked(() =>
                {
                    var patch = LoadPatch(S(p, "id"));
                    patch.Name = S(p, "name", patch.Name);
                    if (p.TryGetProperty("modIds", out _)) patch.ModIds = Strings(p, "modIds");
                    if (p.TryGetProperty("referenceIds", out _)) patch.ReferenceIds = Strings(p, "referenceIds");
                    patch.WavLipFolder = S(p, "wavLipFolder", patch.WavLipFolder);
                    patch.FinalRoot = S(p, "finalRoot", patch.FinalRoot);
                    patch.FuzTemplate = S(p, "fuzTemplate", patch.FuzTemplate);
                    if (p.TryGetProperty("overwriteWavLip", out var ow) && ow.ValueKind is JsonValueKind.True or JsonValueKind.False) patch.OverwriteWavLip = ow.GetBoolean();
                    if (p.TryGetProperty("overwriteFuz", out var of) && of.ValueKind is JsonValueKind.True or JsonValueKind.False) patch.OverwriteFuz = of.GetBoolean();
                    _lib.Save(patch);
                    new Phase2Service(_lib, Runtime()).Sync(patch, LoadVoice(patch.VoicePackId), _lib.LoadMods());
                    return PatchDto(patch);
                });
            case "patch.delete":
                return await Locked(() =>
                {
                    _lib.Backup("before-delete-patch", _settings.BackupsToKeep);
                    if (_jobCts != null) throw new InvalidOperationException("Stop the running job first.");
                    ForgetPatch(S(p, "id"));
                    Directory.Delete(_lib.PatchFolder(S(p, "id")), true);
                    return State();
                });
            case "patch.sync":
                return await Locked(() =>
                {
                    var patch = LoadPatch(S(p, "id"));
                    var rep = new Phase2Service(_lib, Runtime()).Sync(patch, LoadVoice(patch.VoicePackId), _lib.LoadMods());
                    return new { patch = PatchDto(patch), report = rep };
                });
            case "line2.update": return await Locked(() => UpdatePhase2Line(p));
            case "line2.bulkTags": return await Locked(() => BulkTags(p));
            case "patch.estimate": return await Locked(() => Estimate(p));
            case "patch.generate": return StartGenerate(p);
            case "job.cancel": _jobCts?.Cancel(); return true;
            case "patch.exportWavLip":
                return await Locked(() =>
                {
                    var patch = LoadPatch(S(p, "id"));
                    var rep = new Phase2Service(_lib, Runtime()).ExportWavLip(patch, LoadVoice(patch.VoicePackId));
                    var files = WriteFixAndTopics(patch);
                    return new { rep.Written, rep.Skipped, rep.Failed, rep.Errors, files };
                });
            case "patch.buildFuz": return StartFuz(p);
            case "patch.writeFixPlugins": return await Locked(() => WritePatchFixPlugins(p));
            case "patch.checks": return Checks(S(p, "id"));
            case "patch.autotag": return await AutoTag(p);
            default: throw new InvalidOperationException("Unknown action: " + m);
        }
    }

    private async Task<object?> Locked(Func<object?> f)
    {
        await _gate.WaitAsync();
        try { return await Task.Run(f); }
        finally { _gate.Release(); }
    }

    // ------------------------------------------------------------------ state

    private object State()
    {
        var mods = _lib.LoadMods();
        var patches = _lib.LoadPatches();
        var voices = _lib.LoadVoicePacks();
        return new
        {
            libraryRoot = _lib.Root,
            settings = new
            {
                tools = new
                {
                    fonix = File.Exists(_settings.FonixDataPath), xwma = File.Exists(_settings.XwmaEncodePath),
                    ffmpeg = File.Exists(_settings.FfmpegPath), faceFx = File.Exists(_settings.FaceFxWrapperPath),
                    baseGame = Directory.Exists(_settings.BaseGameFolder), strings = Directory.Exists(_settings.StringsFolder),
                },
                categories = _settings.Categories,
                pronunciations = _settings.Pronunciations,
                defaultReferenceIds = _settings.DefaultReferenceIds,
                hasApiKey = !string.IsNullOrEmpty(Secrets.Unprotect(_settings.ElevenLabsApiKey)),
                _settings.FonixDataPath,
                _settings.XwmaEncodePath,
                _settings.FfmpegPath,
                _settings.FaceFxWrapperPath,
                _settings.BaseGameFolder,
                _settings.StringsFolder,
                _settings.StringsLanguage,
                _settings.BackupsToKeep,
                _settings.LastVoicePackId,
                aiProvider = new { _settings.AiProvider.Endpoint, _settings.AiProvider.Model, hasKey = !string.IsNullOrEmpty(_settings.AiProvider.ApiKey) },
                localEngines = _settings.LocalEngines,
                ui = _settings.Ui,
            },
            references = _settings.References,
            voicePacks = voices,
            mods = mods.Select(ModSummary).ToList(),
            patches = patches.Select(x => new
            {
                x.Id, x.Name, x.VoicePackId, x.ModIds, x.ReferenceIds, x.UpdatedUtc,
                voicePack = voices.FirstOrDefault(v => v.Id == x.VoicePackId)?.Name ?? "(missing)",
                category = mods.FirstOrDefault(m => x.ModIds.Contains(m.Id))?.Category ?? "Misc",
                total = x.Lines.Count,
                generated = x.Lines.Count(l => l.SelectedTakeId != null && !l.IsOmitted),
                failed = x.Lines.Count(l => l.Status == Phase2Status.Failed),
                omitted = x.Lines.Count(l => l.IsOmitted),
            }).ToList(),
        };
    }

    private object SaveSettings(JsonElement p)
    {
        var s = p.GetProperty("settings");
        void Set(string name, Action<string> apply) { if (s.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String) apply(v.GetString()!); }
        Set("fonixDataPath", v => _settings.FonixDataPath = v);
        Set("xwmaEncodePath", v => _settings.XwmaEncodePath = v);
        Set("ffmpegPath", v => _settings.FfmpegPath = v);
        Set("faceFxWrapperPath", v => _settings.FaceFxWrapperPath = v);
        Set("baseGameFolder", v => _settings.BaseGameFolder = v);
        Set("stringsFolder", v => _settings.StringsFolder = v);
        Set("stringsLanguage", v => _settings.StringsLanguage = v);
        Set("aiEndpoint", v => _settings.AiProvider.Endpoint = v);
        Set("aiModel", v => _settings.AiProvider.Model = v);
        Set("voxCpmAddress", v => _settings.LocalEngines.VoxCpmAddress = v);
        Set("omniVoiceAddress", v => _settings.LocalEngines.OmniVoiceAddress = v);
        if (s.TryGetProperty("backupsToKeep", out var bk) && bk.ValueKind == JsonValueKind.Number) _settings.BackupsToKeep = bk.GetInt32();
        if (s.TryGetProperty("pronunciations", out var pr) && pr.ValueKind == JsonValueKind.Array)
            _settings.Pronunciations = pr.Deserialize<List<Pronunciation>>(JsonOptions) ?? new();
        if (s.TryGetProperty("defaultReferenceIds", out var dr) && dr.ValueKind == JsonValueKind.Array)
            _settings.DefaultReferenceIds = dr.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList();
        if (s.TryGetProperty("ui", out var ui) && ui.ValueKind == JsonValueKind.Object)
            foreach (var kv in ui.EnumerateObject()) _settings.Ui[kv.Name] = kv.Value.ToString();
        _lib.Save(_settings);
        return State();
    }

    /// <summary>A copy of the settings with secrets decrypted, for the core services.</summary>
    private AppSettings Runtime()
    {
        var copy = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(_settings, Library.Json), Library.Json)!;
        copy.ElevenLabsApiKey = Secrets.Unprotect(_settings.ElevenLabsApiKey);
        copy.AiProvider.ApiKey = Secrets.Unprotect(_settings.AiProvider.ApiKey);
        return copy;
    }

    private ElevenLabsClient Client()
    {
        var key = Secrets.Unprotect(_settings.ElevenLabsApiKey);
        if (string.IsNullOrEmpty(key)) throw new InvalidOperationException("Enter your ElevenLabs API key in Settings.");
        return new ElevenLabsClient(key);
    }

    private ExternalTools Tools()
    {
        if (!File.Exists(_settings.FfmpegPath)) throw new InvalidOperationException("ffmpeg.exe was not found. Set it in Settings.");
        if (!File.Exists(_settings.FaceFxWrapperPath)) throw new InvalidOperationException("FaceFXWrapper.exe was not found. Set it in Settings.");
        return new ExternalTools
        {
            FfmpegPath = _settings.FfmpegPath,
            FaceFxWrapperPath = _settings.FaceFxWrapperPath,
            XwmaEncodePath = _settings.XwmaEncodePath,
            FonixDataPath = _settings.FonixDataPath,
        };
    }

    /// <summary>Find ffmpeg / FaceFXWrapper next to the app, or from an installed DBVO Voice Pack Maker.</summary>
    private void AutoDetectTools()
    {
        string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string la = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var dirs = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "bin"),
            AppContext.BaseDirectory,
            Path.Combine(pf, "DBVO Voice Pack Maker"),
            Path.Combine(la, "DBVO Voice Pack Maker"),
            Path.Combine(la, "Programs", "DBVO Voice Pack Maker"),
        };
        bool changed = false;
        string? Find(params string[] names) => dirs.SelectMany(d => names.Select(n => Path.Combine(d, n))).FirstOrDefault(File.Exists);
        if (!File.Exists(_settings.FfmpegPath)) { var f = Find("ffmpeg.exe", "ffmpeg-x86_64-pc-windows-msvc.exe"); if (f != null) { _settings.FfmpegPath = f; changed = true; } }
        if (!File.Exists(_settings.FaceFxWrapperPath)) { var f = Find("FaceFXWrapper.exe", "FaceFXWrapper-x86_64-pc-windows-msvc.exe"); if (f != null) { _settings.FaceFxWrapperPath = f; changed = true; } }
        if (!File.Exists(_settings.XwmaEncodePath)) { var f = Find("xWMAEncode.exe", "xwmaencode.exe", "xwmaencode-x86_64-pc-windows-msvc.exe"); if (f != null) { _settings.XwmaEncodePath = f; changed = true; } }
        if (changed) _lib.Save(_settings);
    }

    // ------------------------------------------------------------------ dialogs

    private object? PickFile(string title, string filter, bool multi)
    {
        return _owner.Dispatcher.Invoke(() =>
        {
            var d = new OpenFileDialog { Title = string.IsNullOrEmpty(title) ? "Choose file" : title, Multiselect = multi, Filter = string.IsNullOrEmpty(filter) ? "All files|*.*" : filter };
            if (d.ShowDialog(_owner) != true) return null;
            return multi ? d.FileNames : (object)d.FileName;
        });
    }

    private string? PickFolder(string title)
    {
        return _owner.Dispatcher.Invoke(() =>
        {
            var d = new OpenFolderDialog { Title = string.IsNullOrEmpty(title) ? "Choose folder" : title };
            return d.ShowDialog(_owner) == true ? d.FolderName : null;
        });
    }

    private static void OpenPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        if (File.Exists(path)) Process.Start("explorer.exe", $"/select,\"{path}\"");
        else if (Directory.Exists(path)) Process.Start("explorer.exe", $"\"{path}\"");
    }

    // ------------------------------------------------------------------ Phase 1

    private Mod LoadMod(string id) => _lib.LoadMod(id) ?? throw new InvalidOperationException("Mod not found.");
    private VoicePack LoadVoice(string id) => _lib.LoadVoicePacks().FirstOrDefault(v => v.Id == id) ?? throw new InvalidOperationException("Voice pack not found.");
    // One shared instance per patch, so a running job and edits in the grid work on the same object.
    private readonly Dictionary<string, DbvoPatch> _patches = new();

    private DbvoPatch LoadPatch(string id)
    {
        lock (_patches)
        {
            if (_patches.TryGetValue(id, out var cached)) return cached;
            var p = _lib.LoadPatch(id) ?? throw new InvalidOperationException("DBVO Patch not found.");
            // Patches from the first test build had no folders of their own: fill them from the voice pack's templates.
            if (string.IsNullOrWhiteSpace(p.WavLipFolder) && string.IsNullOrWhiteSpace(p.FinalRoot))
            {
                var v = _lib.LoadVoicePacks().FirstOrDefault(x => x.Id == p.VoicePackId);
                var modName = _lib.LoadMods().FirstOrDefault(m => p.ModIds.Contains(m.Id))?.Name ?? p.Name;
                if (v != null && (v.Destinations.WavLipFolder.Length > 0 || v.Destinations.FinalRoot.Length > 0))
                {
                    if (v.Destinations.WavLipFolder.Length > 0) p.WavLipFolder = Phase2Service.ExpandTemplate(v.Destinations.WavLipFolder, modName, v, p);
                    if (v.Destinations.FinalRoot.Length > 0) p.FinalRoot = Phase2Service.ExpandTemplate(v.Destinations.FinalRoot, modName, v, p);
                    _lib.Save(p);
                }
            }
            _patches[id] = p;
            return p;
        }
    }

    private void ForgetPatch(string id)
    {
        lock (_patches) _patches.Remove(id);
    }

    private static object ModSummary(Mod m) => new
    {
        m.Id, m.Name, m.Version, m.UpdatedUtc, m.Category,
        plugins = m.Plugins.Select(p => new
        {
            p.FileName, p.IsTopicFile,
            lines = p.Lines.Count(l => l.Status != Phase1Status.Removed),
            topicLines = Phase1Service.TopicLines(p).Count,
            fixes = Phase1Service.FixEdits(p).Count,
            flagged = p.Lines.Count(l => l.IsFlagged && l.Status != Phase1Status.Removed && !l.Excluded),
            unreviewed = Phase1Service.UnreviewedFlags(p).Count,
            playerNames = p.Lines.Count(l => l.IsPlayerName && l.Status != Phase1Status.Removed && !l.Excluded),
            needsFix = p.NeedsFixPlugin,
        }).ToList(),
    };

    private object ModDto(Mod m)
    {
        var svc = new Phase1Service(_lib, Runtime());
        var mastersDir = Path.Combine(_lib.ModFolder(m.Id), "masters");
        return new
        {
            m.Id, m.Name, m.Version, m.NexusUrl, m.UpdatedUtc, m.Category,
            uploadedMasters = Directory.Exists(mastersDir) ? Directory.GetFiles(mastersDir).Select(Path.GetFileName).ToList() : new List<string?>(),
            plugins = m.Plugins.Select(p => PluginDto(svc, m, p, true)).ToList(),
        };
    }

    private static object PluginDto(Phase1Service svc, Mod m, ModPlugin p, bool withLines) => new
    {
        p.FileName, p.FixFileName, p.Masters, p.Localized, p.Notices, p.ImportedUtc, p.FixPrefix, p.IsTopicFile, p.ImportedFixPlugin,
        missingMasters = withLines && !p.IsTopicFile ? SafeMissing(svc, m, p) : new List<MissingMaster>(),
        needsFix = p.NeedsFixPlugin,
        fixes = p.IsTopicFile ? 0 : Phase1Service.FixEdits(p).Count,
        topicLines = Phase1Service.TopicLines(p).Count,
        topicFileName = Phase1Service.TopicFileName(p),
        unreviewed = Phase1Service.UnreviewedFlags(p).Count,
        playerNames = p.Lines.Count(l => l.IsPlayerName && l.Status != Phase1Status.Removed && !l.Excluded),
        playerNamesExported = p.Lines.Count(l => l.IsPlayerName && l.PlayerNameExported && l.Status != Phase1Status.Removed && !l.Excluded),
        lines = withLines ? p.Lines.OrderBy(l => l.Status == Phase1Status.Removed).ThenBy(l => l.Order).Select(l => Line1Dto(l, p.IsTopicFile)).ToList() : null,
    };

    private static List<MissingMaster> SafeMissing(Phase1Service svc, Mod m, ModPlugin p)
    {
        try { return File.Exists(p.StoredFile) ? svc.FindMissingMasters(m, PluginFile.Read(p.StoredFile, new HashSet<string>())) : new(); }
        catch { return new(); }
    }

    private static object Line1Dto(Phase1Line l, bool topicFile) => new
    {
        key = l.Key,
        formId = topicFile ? "" : l.FormId.ToString("X8"),
        record = topicFile ? "topic line" : l.Field == LineField.TopicText ? "DIAL FULL" : "INFO RNAM",
        l.EditorId, l.Original, l.Suggested, l.Final, kind = l.Kind.ToString(), l.UserEdited, l.Reviewed,
        l.HiddenByPrompts, l.IncludeAnyway, l.Excluded, status = l.Status.ToString(), l.PreviousFinal, l.PreviousOriginal,
        l.Variants, needsOverride = !topicFile && l.NeedsOverride, flagged = l.IsFlagged,
        isPlayerName = l.IsPlayerName, l.PlayerNameExported,
        hasOtherAlias = ScriptRules.HasOtherAlias(l.Original),
        hasPlaceholder = l.Final != null && ScriptRules.HasPlaceholder(LineNaming.SpokenPart(l.Final)),
    };

    /// <summary>The script's verdict on a text the user is typing (live validation in Phase 1).</summary>
    private static object CheckText(string text)
    {
        var r = ScriptRules.Classify(text, null);
        string? problem = r.Kind switch
        {
            LineKind.Broken when ScriptRules.HasPlayerAlias(text) && !ScriptRules.HasOtherAlias(text) => null,
            LineKind.Broken when ScriptRules.HasOtherAlias(text) => "Still contains an alias placeholder, so it can't be voiced as is.",
            LineKind.Broken => "The script would still mark this BROKEN (for example because of * or a misplaced < or parenthesis).",
            LineKind.TooLong => $"Still {text.Trim().Length} characters: lines over {ScriptRules.MaxLength} crash DBVO.",
            LineKind.Fixed => "The script would still reformat this: " + r.Final,
            _ => null,
        };
        return new { kind = r.Kind.ToString(), length = text.Trim().Length, max = ScriptRules.MaxLength, problem, isPlayerName = ScriptRules.HasPlayerAlias(text) };
    }

    private object ImportPlugins(JsonElement p)
    {
        var mod = LoadMod(S(p, "id"));
        var paths = Paths(p, "paths").Where(f => new[] { ".esp", ".esm", ".esl", ".txt" }.Contains(Path.GetExtension(f).ToLowerInvariant())).ToList();
        if (paths.Count == 0) throw new InvalidOperationException("Choose .esp, .esm or .esl plugins, or topic .txt files.");
        _lib.Backup("before-import-" + mod.Name, _settings.BackupsToKeep);
        var svc = new Phase1Service(_lib, Runtime());
        var reports = new List<object>();
        foreach (var f in paths)
        {
            var r = Path.GetExtension(f).Equals(".txt", StringComparison.OrdinalIgnoreCase) ? svc.ImportTopicFile(mod, f) : svc.Import(mod, f);
            reports.Add(new { plugin = r.Plugin.FileName, r.Added, r.Changed, r.Removed, r.Kept, r.UnresolvedStrings, missingMasters = r.MissingMasters });
        }
        return new { mod = ModDto(LoadMod(mod.Id)), reports };
    }

    private object UpdatePhase1Line(JsonElement p)
    {
        var mod = LoadMod(S(p, "modId"));
        var plugin = mod.Plugins.First(x => x.FileName == S(p, "plugin"));
        var line = plugin.Lines.First(x => x.Key == S(p, "key"));
        switch (S(p, "action"))
        {
            case "setFinal": Phase1Service.SetFinal(line, S(p, "value")); break;
            case "accept": Phase1Service.AcceptSuggestion(line); break;
            case "revert": Phase1Service.RevertToOriginal(line); break;
            case "review": line.Reviewed = true; break;
            case "exclude": line.Excluded = B(p, "value"); line.Reviewed = true; break;
            case "includeHidden": line.IncludeAnyway = B(p, "value"); break;
            case "variants":
                line.Variants = Strings(p, "value").Select(v => v.Trim()).Where(v => v.Length > 0).ToList();
                line.Reviewed = true;
                break;
            case "keepPrevious":
                if (line.PreviousFinal != null) Phase1Service.SetFinal(line, line.PreviousFinal);
                line.Status = Phase1Status.Active;
                break;
            case "acceptChange": line.Status = Phase1Status.Active; line.Reviewed = true; break;
            default: throw new InvalidOperationException("Unknown line action.");
        }
        _lib.Save(mod);
        return new { line = Line1Dto(line, plugin.IsTopicFile), plugin = PluginDto(new Phase1Service(_lib, Runtime()), mod, plugin, false) };
    }

    private object BulkPhase1(JsonElement p)
    {
        var mod = LoadMod(S(p, "modId"));
        var plugin = mod.Plugins.First(x => x.FileName == S(p, "plugin"));
        var keys = Strings(p, "keys").ToHashSet();
        foreach (var line in plugin.Lines.Where(l => keys.Contains(l.Key)))
        {
            switch (S(p, "action"))
            {
                case "accept": Phase1Service.AcceptSuggestion(line); break;
                case "revert": Phase1Service.RevertToOriginal(line); break;
                case "review": line.Reviewed = true; break;
                case "exclude": line.Excluded = true; line.Reviewed = true; break;
                case "include": line.Excluded = false; break;
            }
        }
        _lib.Save(mod);
        return ModDto(mod);
    }

    private object WriteModOutputs(JsonElement p)
    {
        var mod = LoadMod(S(p, "id"));
        var svc = new Phase1Service(_lib, Runtime());
        var folders = Strings(p, "fixFolders");
        var topicFolder = S(p, "topicFolder");
        if (string.IsNullOrWhiteSpace(topicFolder)) topicFolder = folders.FirstOrDefault() ?? throw new InvalidOperationException("Choose a folder.");
        var written = mod.Plugins.SelectMany(pl => svc.WriteOutputs(mod, pl, folders, topicFolder)).ToList();
        return written;
    }

    // ------------------------------------------------------------------ Phase 2

    private object PatchDto(DbvoPatch patch)
    {
        var voice = _lib.LoadVoicePacks().FirstOrDefault(v => v.Id == patch.VoicePackId);
        var mods = _lib.LoadMods();
        return new
        {
            patch.Id, patch.Name, patch.VoicePackId, patch.ModIds, patch.ReferenceIds, patch.WavLipFolder, patch.FinalRoot, patch.FuzTemplate,
            patch.OverwriteWavLip, patch.OverwriteFuz,
            category = mods.FirstOrDefault(m => patch.ModIds.Contains(m.Id))?.Category ?? "Misc",
            mediaBase = "https://media.dbvo/" + Path.GetRelativePath(_lib.Root, _lib.PatchFolder(patch.Id)).Replace('\\', '/') + "/",
            modNames = mods.Where(m => patch.ModIds.Contains(m.Id)).ToDictionary(m => m.Id, m => m.Name),
            lines = patch.Lines.Select(l => Line2Dto(l, voice)).ToList(),
        };
    }

    private static object Line2Dto(Phase2Line l, VoicePack? v) => new
    {
        l.LineId, l.FinalLine, l.OriginalLine, l.Spoken, l.SpokenEdited, status = l.Status.ToString(), l.OmittedBy, l.OverrideOmit,
        omitted = l.IsOmitted, l.LastError, l.TagSuggestion, l.SelectedTakeId,
        stem = v == null ? "" : LineNaming.Stem(l.FinalLine, v.Naming),
        legacyName = LineNaming.Legacy(l.FinalLine),
        differs = l.SpokenDiffersFromFinal, editedInPhase1 = l.EditedInPhase1,
        stale = l.SelectedTake != null && l.SelectedTake.SpokenText != l.Spoken,
        sources = l.Sources.Select(s => new { s.ModId, s.Plugin, formId = s.FormId.ToString("X8"), field = s.Field.ToString() }),
        takes = l.Takes.Select(t => new { t.Id, t.Slot, t.CreatedUtc, t.SpokenText, t.Settings, wav = t.WavPath.Replace('\\', '/'), t.DurationSeconds }),
    };

    private object UpdatePhase2Line(JsonElement p)
    {
        var patch = LoadPatch(S(p, "id"));
        var voice = LoadVoice(patch.VoicePackId);
        var line = patch.Lines.First(l => l.LineId == S(p, "lineId"));
        switch (S(p, "action"))
        {
            case "spoken":
                line.Spoken = S(p, "value");
                line.SpokenEdited = line.Spoken != SpokenText.DefaultFor(line.FinalLine);
                line.TagSuggestion = null;
                if (line.SelectedTake != null && line.Status == Phase2Status.Generated && line.SelectedTake.SpokenText != line.Spoken) line.Status = Phase2Status.Stale;
                break;
            case "resetSpoken":
                line.Spoken = SpokenText.DefaultFor(line.FinalLine);
                line.SpokenEdited = false;
                break;
            case "selectTake":
                line.SelectedTakeId = S(p, "value");
                line.Status = Phase2Status.Generated;
                break;
            case "deleteTake":
                var t = line.Takes.FirstOrDefault(x => x.Id == S(p, "value"));
                if (t != null)
                {
                    line.Takes.Remove(t);
                    var dir = Path.GetDirectoryName(Path.Combine(_lib.PatchFolder(patch.Id), t.WavPath));
                    try { if (dir != null && Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
                    if (line.SelectedTakeId == t.Id) line.SelectedTakeId = line.Takes.LastOrDefault()?.Id;
                    if (line.Takes.Count == 0) line.Status = line.IsOmitted ? Phase2Status.Omitted : Phase2Status.Pending;
                }
                break;
            case "overrideOmit":
                line.OverrideOmit = B(p, "value");
                if (!line.IsOmitted && line.Status == Phase2Status.Omitted) line.Status = Phase2Status.Pending;
                if (line.IsOmitted && line.Takes.Count == 0) line.Status = Phase2Status.Omitted;
                break;
            case "acceptTags":
                if (line.TagSuggestion != null) { line.Spoken = line.TagSuggestion; line.SpokenEdited = true; }
                line.TagSuggestion = null;
                break;
            case "rejectTags":
                line.TagSuggestion = null;
                break;
            default: throw new InvalidOperationException("Unknown line action.");
        }
        _lib.Save(patch);
        return Line2Dto(line, voice);
    }

    private object BulkTags(JsonElement p)
    {
        var patch = LoadPatch(S(p, "id"));
        var voice = LoadVoice(patch.VoicePackId);
        var ids = p.TryGetProperty("lineIds", out var li) && li.ValueKind == JsonValueKind.Array ? Strings(p, "lineIds").ToHashSet() : null;
        var tags = Strings(p, "tags");
        bool remove = S(p, "mode") == "remove";
        int n = 0;
        foreach (var line in patch.Lines.Where(l => !l.IsOmitted && (ids == null || ids.Contains(l.LineId))))
        {
            var before = line.Spoken;
            line.Spoken = remove ? SpokenText.RemoveTags(line.Spoken, tags) : SpokenText.AddPrefixTags(line.Spoken, tags);
            if (line.Spoken != before)
            {
                n++;
                line.SpokenEdited = true;
                if (line.SelectedTake != null && line.Status == Phase2Status.Generated) line.Status = Phase2Status.Stale;
            }
        }
        _lib.Save(patch);
        return new { changed = n, patch = PatchDto(patch) };
    }

    private object Estimate(JsonElement p)
    {
        var patch = LoadPatch(S(p, "id"));
        var voice = LoadVoice(patch.VoicePackId);
        var lines = SelectLines(patch, p);
        var e = voice.Eleven;
        var vs = ModelCapabilities.Build(e.ModelId, e.Stability, e.Similarity, e.Style, e.SpeakerBoost, e.Speed);
        return new
        {
            count = lines.Count,
            characters = lines.Sum(l => l.Spoken.Length),
            voice = e.VoiceName, voiceId = e.VoiceId, model = e.ModelId, e.OutputFormat, e.Concurrency, e.Seed, e.LanguageCode, e.TextNormalization,
            settings = new { stability = vs.Stability, similarityBoost = vs.SimilarityBoost, style = vs.Style, useSpeakerBoost = vs.UseSpeakerBoost, speed = vs.Speed },
            voice.Loudnorm, voice.GainDb, voice.TrimTailMs, voice.FadeOutMs, naming = voice.Naming.ToString(),
            pronunciations = voice.Pronunciations.Count,
            dictionaries = e.PronunciationDictionaryIds.Count,
        };
    }

    private static List<Phase2Line> SelectLines(DbvoPatch patch, JsonElement p)
    {
        var mode = S(p, "mode", "missing");
        List<string>? ids = p.TryGetProperty("lineIds", out var li) && li.ValueKind == JsonValueKind.Array ? Strings(p, "lineIds") : null;
        return mode switch
        {
            "selected" => Phase2Service.Eligible(patch, true, ids ?? new List<string>()),
            "all" => Phase2Service.Eligible(patch, true),
            _ => Phase2Service.Eligible(patch, false),
        };
    }

    private object StartGenerate(JsonElement p)
    {
        if (_jobCts != null) throw new InvalidOperationException("A job is already running.");
        var id = S(p, "id");
        var mode = S(p, "mode", "missing");
        List<string>? ids = p.TryGetProperty("lineIds", out var li) && li.ValueKind == JsonValueKind.Array ? Strings(p, "lineIds") : null;
        _jobCts = new CancellationTokenSource();
        var ct = _jobCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                var patch = LoadPatch(id);
                var voice = LoadVoice(patch.VoicePackId);
                _lib.Backup("before-generate-" + patch.Name, _settings.BackupsToKeep);
                var lines = mode switch
                {
                    "selected" => Phase2Service.Eligible(patch, true, ids ?? new List<string>()),
                    "all" => Phase2Service.Eligible(patch, true),
                    _ => Phase2Service.Eligible(patch, false),
                };
                var svc = new Phase2Service(_lib, Runtime());
                var progress = new Progress<GenerationProgress>(g => Emit("progress", new { kind = "generate", g.Done, g.Total, g.Failed, g.Current, g.Message }));
                Emit("progress", new { kind = "generate", done = 0, total = lines.Count, failed = 0, current = (string?)null, message = (string?)null });
                await svc.GenerateAsync(patch, voice, lines, Tools(), progress, l => { Emit("line2", new { patchId = patch.Id, line = Line2Dto(l, voice) }); return Task.CompletedTask; }, ct, _gate);
                Emit("jobDone", new { kind = "generate", patchId = patch.Id, ok = true });
            }
            catch (OperationCanceledException)
            {
                Emit("jobDone", new { kind = "generate", patchId = id, ok = false, message = "Cancelled." });
            }
            catch (Exception ex)
            {
                App.Log("Generate failed: " + ex);
                Emit("jobDone", new { kind = "generate", patchId = id, ok = false, message = ex.Message });
            }
            finally
            {
                _jobCts = null;
            }
        });
        return true;
    }

    private object StartFuz(JsonElement p)
    {
        if (_jobCts != null) throw new InvalidOperationException("A job is already running.");
        var id = S(p, "id");
        var custom = S(p, "customFolder");
        _jobCts = new CancellationTokenSource();
        var ct = _jobCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                var patch = LoadPatch(id);
                var voice = LoadVoice(patch.VoicePackId);
                var svc = new Phase2Service(_lib, Runtime());
                List<string> files;
                await _gate.WaitAsync();
                try { files = string.IsNullOrWhiteSpace(custom) ? WriteFixAndTopics(patch) : new List<string>(); }
                finally { _gate.Release(); }
                var progress = new Progress<GenerationProgress>(g => Emit("progress", new { kind = "fuz", g.Done, g.Total, g.Failed, g.Current }));
                var rep = await svc.ExportFuzAsync(patch, voice, _lib.LoadMods(), Tools(), string.IsNullOrWhiteSpace(custom) ? null : custom, progress, ct);
                Emit("jobDone", new { kind = "fuz", patchId = id, ok = rep.Failed == 0, report = rep, files, reminders = Phase2Service.EndOfPhaseReminders(patch, voice, _lib.LoadMods()) });
            }
            catch (OperationCanceledException)
            {
                Emit("jobDone", new { kind = "fuz", patchId = id, ok = false, message = "Cancelled." });
            }
            catch (Exception ex)
            {
                App.Log("Fuz failed: " + ex);
                Emit("jobDone", new { kind = "fuz", patchId = id, ok = false, message = ex.Message });
            }
            finally
            {
                _jobCts = null;
            }
        });
        return true;
    }

    /// <summary>Write each mod's DBVO Fix plugin (when needed) to the WAV+LIP folder and the mod's final folder.</summary>
    private object WritePatchFixPlugins(JsonElement p) => WriteFixAndTopics(LoadPatch(S(p, "id")));

    /// <summary>
    /// Write each mod's DBVO Fix plugin (when needed) and topic file to the patch's WAV + LIP folder and its
    /// final mod folder. Runs automatically with "Copy WAV + LIP" and "Build .fuz".
    /// </summary>
    private List<string> WriteFixAndTopics(DbvoPatch patch)
    {
        var svc1 = new Phase1Service(_lib, Runtime());
        var folders = new[] { patch.WavLipFolder, patch.FinalRoot }.Where(f => !string.IsNullOrWhiteSpace(f)).ToList();
        if (folders.Count == 0) throw new InvalidOperationException("Set this patch's WAV + LIP folder or final folder first (Folders & options).");
        var written = new List<string>();
        foreach (var mod in _lib.LoadMods().Where(m => patch.ModIds.Contains(m.Id)))
            foreach (var pl in mod.Plugins) written.AddRange(svc1.WriteOutputs(mod, pl, folders, folders[0]));
        return written;
    }

    /// <summary>A new DBVO Patch: folders filled in from the voice pack's templates ({mod} = mod name).</summary>
    private DbvoPatch CreatePatch(VoicePack voice, List<string> modIds, string name, List<string>? referenceIds)
    {
        var mods = _lib.LoadMods().Where(x => modIds.Contains(x.Id)).ToList();
        if (mods.Count == 0) throw new InvalidOperationException("Pick at least one mod.");
        string modName = mods.Count == 1 ? mods[0].Name : (string.IsNullOrWhiteSpace(name) ? string.Join(" + ", mods.Select(x => x.Name)) : name.Trim());
        var patch = new DbvoPatch
        {
            Name = string.IsNullOrWhiteSpace(name) ? modName : name.Trim(),
            VoicePackId = voice.Id,
            ModIds = modIds.ToList(),
            ReferenceIds = referenceIds ?? _settings.DefaultReferenceIds.ToList(),
        };
        string Expand(string t) => string.IsNullOrWhiteSpace(t) ? "" : Phase2Service.ExpandTemplate(t, modName, voice, patch);
        patch.WavLipFolder = Expand(voice.Destinations.WavLipFolder);
        patch.FinalRoot = Expand(voice.Destinations.FinalRoot);
        patch.FuzTemplate = string.IsNullOrWhiteSpace(voice.Destinations.FuzTemplate) ? @"Sound\DBVO\{packId}" : voice.Destinations.FuzTemplate;
        _lib.Save(patch);
        lock (_patches) _patches[patch.Id] = patch;
        new Phase2Service(_lib, Runtime()).Sync(patch, voice, _lib.LoadMods());
        _settings.LastVoicePackId = voice.Id;
        _lib.Save(_settings);
        return patch;
    }

    private void EnsureCategory(string category)
    {
        if (string.IsNullOrWhiteSpace(category)) return;
        if (!_settings.Categories.Contains(category, StringComparer.OrdinalIgnoreCase))
        {
            _settings.Categories.Add(category.Trim());
            _lib.Save(_settings);
        }
    }

    private static string SafeFile(string s) => string.Concat(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();

    /// <summary>
    /// Point the app at another library folder. An empty (or new) folder gets a copy of the current library;
    /// a folder that already holds a library is used as is. The app restarts afterwards.
    /// </summary>
    private object ChangeLibrary(string target)
    {
        if (string.IsNullOrWhiteSpace(target)) throw new InvalidOperationException("Choose a folder.");
        if (_jobCts != null) throw new InvalidOperationException("Stop the running job first.");
        target = Path.GetFullPath(target);
        if (string.Equals(target.TrimEnd('\\'), _lib.Root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return new { changed = false };
        bool existingLibrary = File.Exists(Path.Combine(target, "settings.json"));
        if (!existingLibrary)
        {
            if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
                throw new InvalidOperationException("That folder is not empty and holds no library. Choose an empty folder or an existing DBVO Studio library.");
            CopyDirectory(_lib.Root, target);
        }
        var pointer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DBVOStudio", "library.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(pointer)!);
        File.WriteAllText(pointer, target);
        _owner.Dispatcher.BeginInvoke(() =>
        {
            var exe = Environment.ProcessPath;
            if (exe != null) Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
            Application.Current.Shutdown();
        });
        return new { changed = true, copied = !existingLibrary, path = target };
    }

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)), true);
    }

    private object Checks(string id)
    {
        var patch = LoadPatch(id);
        var voice = LoadVoice(patch.VoicePackId);
        var mods = _lib.LoadMods();
        var svc = new Phase2Service(_lib, Runtime());
        var root = svc.FinalRoot(patch, voice);
        var fuzFolders = string.IsNullOrWhiteSpace(root) ? new List<string>()
            : patch.ModIds.Select(mid => mods.FirstOrDefault(m => m.Id == mid)?.Name ?? "Mod")
                .Select(n => Path.Combine(root, Phase2Service.ExpandTemplate(svc.FuzTemplate(patch, voice), n, voice, patch))).ToList();
        var warnings = PathChecks.Check(patch.Lines.Where(l => !l.IsOmitted).Select(l => l.FinalLine), voice.Naming, svc.WavLipFolder(patch, voice), fuzFolders, svc.TakesFolder(patch));
        var unreviewed = mods.Where(m => patch.ModIds.Contains(m.Id))
            .SelectMany(m => m.Plugins.Select(pl => new { mod = m.Name, plugin = pl.FileName, count = Phase1Service.UnreviewedFlags(pl).Count }))
            .Where(x => x.count > 0).ToList();
        return new
        {
            pathWarnings = warnings,
            reminders = Phase2Service.EndOfPhaseReminders(patch, voice, mods),
            unreviewed,
            fuzFolders,
            wavLipFolder = svc.WavLipFolder(patch, voice),
            finalRoot = root,
            tools = new
            {
                ffmpeg = File.Exists(_settings.FfmpegPath), faceFx = File.Exists(_settings.FaceFxWrapperPath),
                xwma = File.Exists(_settings.XwmaEncodePath), fonix = File.Exists(_settings.FonixDataPath),
                apiKey = !string.IsNullOrEmpty(Secrets.Unprotect(_settings.ElevenLabsApiKey)),
            },
        };
    }

    private async Task<object> AutoTag(JsonElement p)
    {
        var patch = LoadPatch(S(p, "id"));
        var voice = LoadVoice(patch.VoicePackId);
        var ids = Strings(p, "lineIds").ToHashSet();
        var rt = Runtime();
        using var tagger = new AutoTagger(rt.AiProvider.Endpoint, rt.AiProvider.ApiKey, rt.AiProvider.Model);
        var lines = patch.Lines.Where(l => ids.Contains(l.LineId)).ToList();
        var sem = new SemaphoreSlim(4);
        var results = await Task.WhenAll(lines.Select(async l =>
        {
            await sem.WaitAsync();
            try { return await tagger.TagAsync(l.LineId, l.Spoken, null, voice.Tags); }
            finally { sem.Release(); }
        }));
        await _gate.WaitAsync();
        try
        {
            foreach (var r in results.Where(r => r.Suggestion != null))
            {
                var l = patch.Lines.FirstOrDefault(x => x.LineId == r.LineId);
                if (l != null && r.Suggestion != l.Spoken) l.TagSuggestion = r.Suggestion;
            }
            _lib.Save(patch);
        }
        finally { _gate.Release(); }
        return new { results, patch = PatchDto(patch) };
    }

    // ------------------------------------------------------------------ json helpers

    private static string S(JsonElement p, string name, string fallback = "") =>
        p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : fallback;

    private static bool B(JsonElement p, string name) =>
        p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static List<string> Strings(JsonElement p, string name) =>
        p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList()
            : new List<string>();

    private static List<string> Paths(JsonElement p, string name) => Strings(p, name).Where(File.Exists).ToList();
}
