using System.IO;
using System.Text.Json;
using DbvoStudio.Core.ElevenLabs;
using DbvoStudio.Core.Grunts;
using DbvoStudio.Core.Plugins;
using DbvoStudio.Core.Projects;
using DbvoStudio.Core.Text;
using DbvoStudio.Core.Tools;

namespace DbvoStudio.App;

/// <summary>Bridge methods for the Player Grunts Tool (Other Tools): PCHT / MPCHT and SPVS player voice packs.</summary>
public sealed partial class Backend
{
    private readonly Dictionary<string, GruntProject> _gr = new();
    /// <summary>Catalogs read from the installed mods, by target and the files' write times (so a mod update is picked up).</summary>
    private readonly Dictionary<string, (string Sig, GruntCatalog Cat, List<string> Notes)> _grCats = new();

    private GruntProject LoadGr(string id)
    {
        lock (_gr)
        {
            if (_gr.TryGetValue(id, out var p)) return p;
            p = _lib.LoadGruntProject(id) ?? throw new InvalidOperationException("Player Grunts project not found.");
            _gr[id] = p;
            return p;
        }
    }

    private string GrFolder(GruntProject p) => _lib.GruntsFolder(p.Id);

    private string? GrModFolder(string target)
    {
        var f = target == GruntTargets.Spvs ? _settings.SpvsFolder : _settings.PchtFolder;
        return !string.IsNullOrWhiteSpace(f) && Directory.Exists(f) ? f : null;
    }

    /// <summary>The event list for a target: the installed mod's (when its folder is set), plus custom events.</summary>
    private (GruntCatalog Cat, List<string> Notes) GrCatalog(string target)
    {
        var folder = GrModFolder(target);
        var custom = _lib.LoadGruntCustomEvents();
        string sig = target + "|" + (folder ?? "") + "|" + FilesStamp(folder, target) + "|" + (File.Exists(_lib.GruntCustomEventsPath) ? File.GetLastWriteTimeUtc(_lib.GruntCustomEventsPath).Ticks : 0);
        lock (_grCats) if (_grCats.TryGetValue(target, out var c) && c.Sig == sig) return (c.Cat, c.Notes);
        var notes = new List<string>();
        var cat = GruntCatalogs.Resolve(target, folder, custom.Events, notes);
        lock (_grCats) _grCats[target] = (sig, cat, notes);
        return (cat, notes);
    }

    private static string FilesStamp(string? folder, string target)
    {
        if (folder == null) return "";
        var files = target == GruntTargets.Spvs
            ? new[] { Path.Combine(folder, GruntCatalogs.SpvsPlugin), Path.Combine(folder, GruntCatalogs.SpvsShoutPlugin), Path.Combine(folder, "SKSE", "Plugins", "AudioUtil", "config", "SPVS_FemaleCustom01.toml") }
            : new[] { Path.Combine(folder, GruntCatalogs.PchtPlugin), Path.Combine(folder, GruntCatalogs.PchtPatchPlugin) };
        return string.Join(",", files.Select(f => File.Exists(f) ? File.GetLastWriteTimeUtc(f).Ticks.ToString() : "-"));
    }

    private GruntService.AppTools GrTools() => new(
        File.Exists(_settings.FfmpegPath),
        !string.IsNullOrWhiteSpace(_settings.XwmaEncodePath) && File.Exists(_settings.XwmaEncodePath),
        File.Exists(_settings.FaceFxWrapperPath) && File.Exists(_settings.FonixDataPath));

    private ExternalTools GrExternal()
    {
        if (!File.Exists(_settings.FfmpegPath)) throw new InvalidOperationException("ffmpeg.exe wasn't found. Pick it in Global Settings → General & tools → Tools.");
        return new ExternalTools { FfmpegPath = _settings.FfmpegPath, FaceFxWrapperPath = _settings.FaceFxWrapperPath ?? "", XwmaEncodePath = _settings.XwmaEncodePath, FonixDataPath = _settings.FonixDataPath };
    }

    /// <summary>The installed PC Head Tracking - MCM.esp, read for building plugins (null when the folder isn't set).</summary>
    private PluginFile? GrPchtSource()
    {
        var f = GrModFolder(GruntTargets.Pcht);
        if (f == null) return null;
        var esp = Path.Combine(f, GruntCatalogs.PchtPlugin);
        return File.Exists(esp) ? PchtPlugins.ReadSource(esp) : null;
    }

    private async Task<object?> DispatchGrunts(string m, JsonElement p, List<string> dropped)
    {
        switch (m)
        {
            case "grunts.list":
                return await Locked(() => _lib.LoadGruntProjects().Select(x => new
                {
                    x.Id, x.Name, x.Target, x.Gender, x.UpdatedUtc, x.LastExportUtc, multi = x.Pcht.Multi, prefix = x.Pcht.Prefix, slot = x.Spvs.Slot,
                    events = x.Events.Count(kv => kv.Value.Lines.Count > 0), lines = x.Events.Values.Sum(s => s.Lines.Count),
                    total = GrCatalog(x.Target).Cat.Events.Count,
                }).ToList());
            case "grunts.create":
                return await Locked(() =>
                {
                    var target = S(p, "target") == GruntTargets.Spvs ? GruntTargets.Spvs : GruntTargets.Pcht;
                    var proj = new GruntProject { Name = S(p, "name", "Player voice").Trim(), Target = target, Gender = S(p, "gender", "female"), VoicePackId = S(p, "voicePackId") };
                    if (proj.Name.Length == 0) proj.Name = "Player voice";
                    if (target == GruntTargets.Pcht)
                    {
                        proj.Pcht.Multi = B(p, "multi");
                        proj.Pcht.FullName = proj.Name;
                        proj.Pcht.Prefix = new string(proj.Name.Where(char.IsAsciiLetterOrDigit).ToArray());
                    }
                    else proj.Spvs.Slot = S(p, "slot", proj.Gender == "male" ? "MaleCustom01" : "FemaleCustom01");
                    _lib.Save(proj);
                    lock (_gr) _gr[proj.Id] = proj;
                    return new { id = proj.Id };
                });
            case "grunts.delete":
                return await Locked(() =>
                {
                    if (_jobCts != null) throw new InvalidOperationException("Another task is still running (see the bar at the top). Press Cancel there first.");
                    var id = S(p, "id");
                    _lib.Backup("before-delete-grunts", _settings.BackupsToKeep);
                    lock (_gr) _gr.Remove(id);
                    var dir = _lib.GruntsFolder(id);
                    if (Directory.Exists(dir)) Directory.Delete(dir, true); // the project's own recordings and caches; imported files stay where they are
                    return true;
                });
            case "grunts.get": return await Locked(() => GrDto(LoadGr(S(p, "id"))));
            case "grunts.save": return await Locked(() => GrSave(p));
            case "grunts.paths":
                return await Locked(() =>
                {
                    if (p.TryGetProperty("pchtFolder", out var pf) && pf.ValueKind == JsonValueKind.String) _settings.PchtFolder = pf.GetString()!.Trim();
                    if (p.TryGetProperty("spvsFolder", out var sf) && sf.ValueKind == JsonValueKind.String) _settings.SpvsFolder = sf.GetString()!.Trim();
                    _lib.Save(_settings);
                    lock (_grCats) _grCats.Clear();
                    return S(p, "id") is { Length: > 0 } id ? GrDto(LoadGr(id)) : GrPaths();
                });
            case "grunts.addFiles":
            {
                var proj = LoadGr(S(p, "id"));
                var (cat, _) = GrCatalog(proj.Target);
                var ev = cat.Events.FirstOrDefault(e => e.Id == S(p, "eventId")) ?? throw new InvalidOperationException("Event not found.");
                var paths = Strings(p, "paths");
                if (B(p, "dropped")) paths.AddRange(dropped);
                var files = GruntService.CollectAudio(paths, recursive: false);
                if (files.Count == 0) throw new InvalidOperationException("No audio files there (WAV, XWM, FUZ, MP3, OGG, FLAC, M4A, OPUS).");
                List<GruntLine> added = new();
                await Locked(() => { added = GruntService.AddFiles(proj, ev, files, S(p, "slot") is { Length: > 0 } sl ? sl : null); _lib.Save(proj); return null; });
                await GrProbe(proj, added);
                return await Locked(() => new { added = added.Count, skipped = files.Count - added.Count, project = GrDto(proj) });
            }
            case "grunts.line": return await Locked(() => GrLine(p));
            case "grunts.event": return await Locked(() => GrEvent(p));
            case "grunts.audio":
            {
                var proj = LoadGr(S(p, "id"));
                var line = proj.Events.Values.SelectMany(s => s.Lines).FirstOrDefault(l => l.Id == S(p, "lineId")) ?? throw new InvalidOperationException("Line not found.");
                var wav = await GruntService.PreviewAsync(GrExternal(), line, GrFolder(proj));
                // The page can only load files from the library; a WAV elsewhere is copied into the project's cache.
                if (!Path.GetFullPath(wav).StartsWith(Path.GetFullPath(_lib.Root), StringComparison.OrdinalIgnoreCase))
                {
                    var cache = Path.Combine(GrFolder(proj), "cache", "preview");
                    Directory.CreateDirectory(cache);
                    var copy = Path.Combine(cache, GruntService.Hash(wav + File.GetLastWriteTimeUtc(wav).Ticks) + ".wav");
                    if (!File.Exists(copy)) File.Copy(wav, copy, true);
                    wav = copy;
                }
                return "https://media.dbvo/" + Path.GetRelativePath(_lib.Root, wav).Replace('\\', '/') + "?t=" + File.GetLastWriteTimeUtc(wav).Ticks;
            }
            case "grunts.importPack":
            {
                var proj = LoadGr(S(p, "id"));
                var (cat, _) = GrCatalog(proj.Target);
                var folder = S(p, "path");
                if (!Directory.Exists(folder)) throw new InvalidOperationException("That folder doesn't exist.");
                (int Files, int Events, List<string> Notes) r = default;
                var before = proj.Events.Values.SelectMany(s => s.Lines).Select(l => l.Id).ToHashSet();
                await Locked(() => { r = GruntService.ImportPackFolder(proj, cat, folder); _lib.Save(proj); return null; });
                await GrProbe(proj, proj.Events.Values.SelectMany(s => s.Lines).Where(l => !before.Contains(l.Id)).ToList());
                return await Locked(() => new { files = r.Files, events = r.Events, notes = r.Notes, project = GrDto(proj) });
            }
            case "grunts.importProject":
                return await Locked(() =>
                {
                    var proj = LoadGr(S(p, "id"));
                    var from = LoadGr(S(p, "fromId"));
                    int n = GruntService.ImportProject(proj, GrCatalog(proj.Target).Cat, from, GrCatalog(from.Target).Cat, GrFolder(from), GrFolder(proj));
                    _lib.Save(proj);
                    return new { added = n, project = GrDto(proj) };
                });
            case "grunts.relink":
                return await Locked(() =>
                {
                    var proj = LoadGr(S(p, "id"));
                    var folder = S(p, "path");
                    if (!Directory.Exists(folder)) throw new InvalidOperationException("That folder doesn't exist.");
                    int n = GruntService.Relink(proj, GrFolder(proj), folder);
                    _lib.Save(proj);
                    return new { found = n, project = GrDto(proj) };
                });
            case "grunts.check":
                return await Locked(() =>
                {
                    var proj = LoadGr(S(p, "id"));
                    var plan = GrPlan(proj);
                    return GrPlanDto(proj, plan);
                });
            case "grunts.export": return StartGrExport(p);
            case "grunts.custom":
                return await Locked(() => GrCustom(p));
            case "grunts.recordTarget":
            {
                var proj = LoadGr(S(p, "id"));
                var v = _lib.LoadVoicePacks().FirstOrDefault(x => x.Id == proj.VoicePackId);
                var (voiceId, voiceName) = v == null ? ("", "") : Phase2Service.RecordingTarget(v);
                bool hasKey = !string.IsNullOrWhiteSpace(Secrets.Unprotect(_settings.ElevenLabsApiKey));
                bool ready = hasKey && voiceId.Length > 0 && (v?.Engine ?? "elevenlabs") == "elevenlabs";
                return new { ready, voiceName, why = !hasKey ? "Add your ElevenLabs API key in Global Settings to convert recordings." : v == null ? "Pick a DBVO voice pack for this project (Export settings → Voice) to convert recordings." : voiceId.Length == 0 ? "The voice pack has no ElevenLabs voice." : "" };
            }
            case "grunts.record": return await GrRecord(p);
            case "grunts.tts": return await GrTts(p);
            case "grunts.openFolder":
            {
                var proj = LoadGr(S(p, "id"));
                var which = S(p, "which");
                var path = which == "output" ? proj.OutputFolder : which == "mod" ? (GrModFolder(proj.Target) ?? "") : GrFolder(proj);
                if (path.Length == 0 || !Directory.Exists(path)) throw new InvalidOperationException("That folder doesn't exist yet.");
                OpenPath(path);
                return true;
            }
            default: return NotHandled;
        }
    }

    private object GrPaths()
    {
        var pcht = GrModFolder(GruntTargets.Pcht);
        var spvs = GrModFolder(GruntTargets.Spvs);
        string pchtState = "", spvsState = "";
        bool pchtMulti = false;
        if (!string.IsNullOrWhiteSpace(_settings.PchtFolder))
        {
            var esp = Path.Combine(_settings.PchtFolder, GruntCatalogs.PchtPlugin);
            if (!File.Exists(esp)) pchtState = $"{GruntCatalogs.PchtPlugin} isn't in that folder.";
            else
            {
                try { pchtMulti = PchtPlugins.IsMulti(PchtPlugins.ReadSource(esp)); pchtState = pchtMulti ? "PC Head Tracking with Multi Voice Packs support" : "PC Head Tracking (without Multi Voice Packs support)"; }
                catch (Exception ex) { pchtState = "Can't be read: " + ex.Message; }
            }
        }
        if (!string.IsNullOrWhiteSpace(_settings.SpvsFolder))
        {
            var (cat, notes) = GrCatalog(GruntTargets.Spvs);
            spvsState = cat.Source == "installed" ? cat.SourceInfo.Split(" · ")[0] : notes.FirstOrDefault() ?? "Not an SPVS folder (no SKSE\\Plugins\\AudioUtil\\config\\SPVS_FemaleCustom01.toml).";
        }
        return new { pchtFolder = _settings.PchtFolder, spvsFolder = _settings.SpvsFolder, pchtOk = pcht != null && pchtState.StartsWith("PC Head"), pchtMulti, pchtState, spvsOk = spvs != null && spvsState.StartsWith("SPVS"), spvsState };
    }

    // ------------------------------------------------------------------ dto

    private object GrDto(GruntProject proj)
    {
        var (cat, notes) = GrCatalog(proj.Target);
        var folder = GrFolder(proj);
        var voices = _lib.LoadVoicePacks();
        var events = cat.Events.Select(e =>
        {
            var st = proj.Events.TryGetValue(e.Id, out var s) ? s : null;
            var lines = st?.Lines ?? new List<GruntLine>();
            int missing = lines.Count(l => !File.Exists(GruntService.SourcePath(l, folder)));
            int need = e.Kind == "slots" ? e.Slots.Count : 0;
            string status = st?.Skipped == true ? "skipped" : missing > 0 ? "broken" : lines.Count == 0 ? "empty" : e.Kind == "slots" ? (lines.Count >= need ? "full" : "partial") : "full";
            return new
            {
                e.Id, e.Category, e.Label, e.Description, e.Kind, e.Folder, e.Sndr, e.BaseSlots, e.Key, vanilla = e.VanillaNames.Count, e.Spoken, e.Origin, e.Notes,
                pcht = e.PchtFolders,
                slots = e.Slots.Select(x => new { x.Id, x.Label, x.Text, files = x.Files.Count, patch = x.Files.Any(f => f.Contains(GruntCatalogs.PchtPatchPlugin)) }),
                skipped = st?.Skipped ?? false, note = st?.Note ?? "", status, missing,
                lines = lines.Select(l => GrLineDto(l, folder)),
            };
        }).ToList();
        // Lines of events the current list doesn't have (a removed custom event, an older mod version): shown so they aren't lost.
        var orphans = proj.Events.Where(kv => kv.Value.Lines.Count > 0 && cat.Events.All(e => e.Id != kv.Key)).Select(kv => new { id = kv.Key, lines = kv.Value.Lines.Count }).ToList();
        return new
        {
            project = new
            {
                proj.Id, proj.Name, proj.Target, proj.Gender, proj.VoicePackId, voicePack = voices.FirstOrDefault(v => v.Id == proj.VoicePackId)?.Name ?? "",
                pcht = proj.Pcht, spvs = proj.Spvs, audio = proj.Audio, proj.OutputFolder, proj.LastExportUtc, proj.LastReport, exported = proj.ExportedFiles.Count,
                defaultPlugin = GruntService.DefaultPluginName(proj),
            },
            catalog = new { cat.Source, cat.SourceInfo, notes, customs = cat.Events.Count(e => e.Origin == "custom") },
            paths = GrPaths(),
            tools = GrTools(),
            events,
            orphans,
            voices = voices.Select(v => new { v.Id, v.Name, v.Color }),
            others = _lib.LoadGruntProjects().Where(x => x.Id != proj.Id).Select(x => new { x.Id, x.Name, x.Target, lines = x.Events.Values.Sum(s => s.Lines.Count) }),
        };
    }

    private static object GrLineDto(GruntLine l, string folder)
    {
        var path = GruntService.SourcePath(l, folder);
        return new { l.Id, l.Source, path, file = Path.GetFileName(path), exists = File.Exists(path), l.Origin, l.Text, l.FileName, l.Slot, l.DurationSeconds, l.SampleRate, l.Channels, l.Lufs, l.AddedUtc };
    }

    /// <summary>Reads length, rate and channels of new lines (outside the page lock; quick per file).</summary>
    private async Task GrProbe(GruntProject proj, List<GruntLine> lines)
    {
        if (lines.Count == 0 || !File.Exists(_settings.FfmpegPath)) return;
        var tools = GrExternal();
        var folder = GrFolder(proj);
        foreach (var l in lines)
        {
            try
            {
                var info = await GruntService.ProbeAsync(tools, GruntService.SourcePath(l, folder));
                if (info != null) { l.DurationSeconds = info.Seconds; l.SampleRate = info.SampleRate; l.Channels = info.Channels; }
            }
            catch { /* shown as unknown length */ }
        }
        await Locked(() => { _lib.Save(proj); return null; });
    }

    private object GrSave(JsonElement p)
    {
        var proj = LoadGr(S(p, "id"));
        void Str(string name, Action<string> set) { if (p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String) set(v.GetString()!); }
        Str("name", v => { if (v.Trim().Length > 0) proj.Name = v.Trim(); });
        Str("gender", v => { if (v is "female" or "male" or "both") proj.Gender = v; });
        Str("voicePackId", v => proj.VoicePackId = v);
        Str("outputFolder", v => proj.OutputFolder = v.Trim());
        if (p.TryGetProperty("pcht", out var pc) && pc.ValueKind == JsonValueKind.Object)
            proj.Pcht = MergeJson(proj.Pcht, pc);
        if (p.TryGetProperty("spvs", out var sp) && sp.ValueKind == JsonValueKind.Object)
            proj.Spvs = MergeJson(proj.Spvs, sp);
        if (p.TryGetProperty("audio", out var au) && au.ValueKind == JsonValueKind.Object)
        {
            proj.Audio = MergeJson(proj.Audio, au);
            if (proj.Audio.Format is not ("wav" or "xwm" or "fuz")) proj.Audio.Format = "wav";
            proj.Audio.SampleRate = proj.Audio.SampleRate is 22050 or 44100 or 48000 ? proj.Audio.SampleRate : 44100;
        }
        proj.Pcht.Prefix = proj.Pcht.Prefix.Trim();
        if (proj.Target == GruntTargets.Spvs && proj.Gender == "both") proj.Gender = proj.Spvs.Slot.StartsWith("Male") ? "male" : "female";
        _lib.Save(proj);
        return GrDto(proj);
    }

    /// <summary>The object with the given JSON (camelCase) fields applied over it.</summary>
    private static T MergeJson<T>(T current, JsonElement patch) where T : class
    {
        var node = JsonSerializer.SerializeToNode(current, JsonOptions)!.AsObject();
        foreach (var prop in patch.EnumerateObject()) node[prop.Name] = System.Text.Json.Nodes.JsonNode.Parse(prop.Value.GetRawText());
        return node.Deserialize<T>(JsonOptions) ?? current;
    }

    private object GrLine(JsonElement p)
    {
        var proj = LoadGr(S(p, "id"));
        var evId = S(p, "eventId");
        var st = GruntService.State(proj, evId);
        var line = st.Lines.FirstOrDefault(l => l.Id == S(p, "lineId")) ?? throw new InvalidOperationException("Line not found.");
        if (B(p, "remove"))
        {
            st.Lines.Remove(line);
            // A recording or generated take the project owns goes too; the user's own imported files are never touched.
            if (!Path.IsPathRooted(line.Source) && !proj.Events.Values.SelectMany(s => s.Lines).Any(l => l.Source == line.Source))
                try { File.Delete(GruntService.SourcePath(line, GrFolder(proj))); } catch { }
        }
        else
        {
            if (p.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String) line.Text = t.GetString()!;
            if (p.TryGetProperty("fileName", out var fn) && fn.ValueKind == JsonValueKind.String) line.FileName = Path.GetFileNameWithoutExtension(fn.GetString()!.Trim());
            if (p.TryGetProperty("slot", out var sl) && sl.ValueKind == JsonValueKind.String)
            {
                var slot = sl.GetString()!;
                var other = st.Lines.FirstOrDefault(l => l.Slot == slot && l != line);
                if (other != null) other.Slot = line.Slot; // swap
                line.Slot = slot;
            }
            if (p.TryGetProperty("to", out var to) && to.ValueKind == JsonValueKind.Number)
            {
                int i = Math.Clamp(to.GetInt32(), 0, st.Lines.Count - 1);
                st.Lines.Remove(line);
                st.Lines.Insert(i, line);
            }
            if (p.TryGetProperty("moveTo", out var mt) && mt.ValueKind == JsonValueKind.String && mt.GetString() is { Length: > 0 } target && target != evId)
            {
                var (cat, _) = GrCatalog(proj.Target);
                var dest = cat.Events.FirstOrDefault(e => e.Id == target) ?? throw new InvalidOperationException("Event not found.");
                if (dest.Kind != "pool") throw new InvalidOperationException("Lines can only be moved into a pool event; drop files on a shout part instead.");
                st.Lines.Remove(line);
                line.Slot = "";
                GruntService.State(proj, dest.Id).Lines.Add(line);
            }
            if (p.TryGetProperty("replace", out var rp) && rp.ValueKind == JsonValueKind.String && rp.GetString() is { Length: > 0 } path)
            {
                if (!File.Exists(path) || !GruntService.IsAudio(path)) throw new InvalidOperationException("Pick an audio file.");
                line.Source = path; line.Origin = "import"; line.DurationSeconds = 0; line.Lufs = null;
                _ = Task.Run(() => GrProbe(proj, new List<GruntLine> { line }));
            }
        }
        _lib.Save(proj);
        return GrDto(proj);
    }

    private object GrEvent(JsonElement p)
    {
        var proj = LoadGr(S(p, "id"));
        var ids = Strings(p, "eventIds");
        if (ids.Count == 0) ids.Add(S(p, "eventId"));
        foreach (var id in ids)
        {
            var st = GruntService.State(proj, id);
            if (NB(p, "skipped") is { } sk) st.Skipped = sk;
            if (p.TryGetProperty("note", out var n) && n.ValueKind == JsonValueKind.String) st.Note = n.GetString()!;
            if (B(p, "clear"))
            {
                var owned = st.Lines.Where(l => !Path.IsPathRooted(l.Source)).ToList();
                st.Lines.Clear();
                foreach (var l in owned) if (!proj.Events.Values.SelectMany(s => s.Lines).Any(x => x.Source == l.Source)) try { File.Delete(GruntService.SourcePath(l, GrFolder(proj))); } catch { }
            }
            if (B(p, "forget")) proj.Events.Remove(id);
        }
        _lib.Save(proj);
        return GrDto(proj);
    }

    // ------------------------------------------------------------------ custom events

    private object GrCustom(JsonElement p)
    {
        var target = S(p, "target") == GruntTargets.Spvs ? GruntTargets.Spvs : GruntTargets.Pcht;
        var store = _lib.LoadGruntCustomEvents();
        var action = S(p, "action", "list");
        var installed = GrModFolder(target) is { } mf ? GruntCatalogs.Resolve(target, mf, Array.Empty<GruntEventDef>(), new List<string>()) : GruntCatalogs.BuiltIn(target);
        object List() => new { events = store.Events.Where(e => e.Target == target).ToList(), path = _lib.GruntCustomEventsPath };
        switch (action)
        {
            case "list": return List();
            case "save":
            {
                var e = p.GetProperty("event").Deserialize<GruntEventDef>(JsonOptions) ?? throw new InvalidOperationException("No event.");
                e.Target = target;
                e.Kind = "pool";
                e.Origin = "custom";
                var oldId = S(p, "oldId");
                if (oldId.Length > 0) store.Events.RemoveAll(x => x.Id == oldId && x.Target == target);
                if (string.IsNullOrWhiteSpace(e.Id) || oldId.Length == 0) e.Id = "";
                var why = GruntCatalogs.Validate(e, installed);
                if (why != null) throw new InvalidOperationException(why[..1].ToUpperInvariant() + why[1..] + ".");
                if (store.Events.Any(x => x.Target == target && x.Id.Equals(e.Id, StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("A custom event with this key or folder exists already.");
                if (store.Events.Any(x => x.Target == target && x.Folder.Equals(e.Folder, StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("Another custom event uses this folder.");
                // Keep the project lines of a renamed event.
                if (oldId.Length > 0 && oldId != e.Id)
                    foreach (var pr in _lib.LoadGruntProjects().Where(x => x.Target == target && x.Events.ContainsKey(oldId)))
                    {
                        var live = LoadGr(pr.Id);
                        if (live.Events.Remove(oldId, out var stt)) { live.Events[e.Id] = stt; _lib.Save(live); }
                    }
                store.Events.Add(e);
                _lib.Save(store);
                lock (_grCats) _grCats.Remove(target);
                return List();
            }
            case "delete":
                store.Events.RemoveAll(x => x.Id == S(p, "eventId") && x.Target == target);
                _lib.Save(store);
                lock (_grCats) _grCats.Remove(target);
                return List();
            case "import":
            {
                var path = S(p, "path");
                if (!File.Exists(path)) throw new InvalidOperationException("File not found.");
                var current = GruntCatalogs.Resolve(target, GrModFolder(target), store.Events, new List<string>());
                var (events, problems) = GruntCatalogs.ParseImport(Path.GetFileName(path), File.ReadAllText(path), target, current);
                int added = 0;
                foreach (var e in events)
                {
                    if (store.Events.Any(x => x.Target == target && (x.Id.Equals(e.Id, StringComparison.OrdinalIgnoreCase) || x.Folder.Equals(e.Folder, StringComparison.OrdinalIgnoreCase))))
                    { problems.Add($"{e.Label}: a custom event with the same key or folder exists; kept yours."); continue; }
                    store.Events.Add(e);
                    added++;
                }
                if (added > 0) { _lib.Save(store); lock (_grCats) _grCats.Remove(target); }
                return new { added, problems, list = List() };
            }
            case "export":
            {
                var path = S(p, "path");
                var file = new GruntCustomEvents { Events = store.Events.Where(e => e.Target == target).ToList() };
                Library.SaveJson(path, file);
                return new { written = file.Events.Count, path };
            }
            default: throw new InvalidOperationException("Unknown custom event action.");
        }
    }

    // ------------------------------------------------------------------ check and export

    private GruntExportPlan GrPlan(GruntProject proj)
    {
        var (cat, notes) = GrCatalog(proj.Target);
        PluginFile? src = null;
        if (proj.Target == GruntTargets.Pcht) try { src = GrPchtSource(); } catch (Exception ex) { notes = notes.Append("PC Head Tracking - MCM.esp can't be read: " + ex.Message).ToList(); }
        var installed = proj.Target == GruntTargets.Spvs && GrModFolder(GruntTargets.Spvs) is { } f ? GruntCatalogs.Resolve(GruntTargets.Spvs, f, Array.Empty<GruntEventDef>(), new List<string>()) : null;
        var plan = GruntService.Plan(proj, cat, GrFolder(proj), src, GrTools(), installed);
        foreach (var n in notes) plan.Issues.Add(new GruntIssue("info", n));
        if (proj.Target == GruntTargets.Pcht && proj.Pcht.Multi && src != null && !PchtPlugins.IsMulti(src))
            plan.Issues.Add(new GruntIssue("error", $"The {GruntCatalogs.PchtPlugin} in the PC Head Tracking folder is the plain one. Point the folder at \"Multi Voice Packs support for PC Head Tracking\" (its version of the plugin), or turn Multi Voice Packs off."));
        return plan;
    }

    private static object GrPlanDto(GruntProject proj, GruntExportPlan plan) => new
    {
        issues = plan.Issues.Select(i => new { i.Severity, i.Text, i.EventId, i.LineId }),
        errors = plan.Issues.Count(i => i.Severity == "error"),
        warnings = plan.Issues.Count(i => i.Severity == "warning"),
        files = plan.Files.Count,
        audio = plan.Files.Count(f => f.Kind == "audio"),
        plugins = plan.Plugins.Select(x => new { x.FileName, x.Overrides, x.NewRecords, x.Masters }),
        notes = plan.Notes,
        overwrite = plan.WouldOverwrite.Take(30),
        overwriteCount = plan.WouldOverwrite.Count,
        output = proj.OutputFolder,
    };

    private object StartGrExport(JsonElement p)
    {
        if (_jobCts != null) throw new InvalidOperationException("Another task is still running (see the bar at the top). Wait for it to finish, or press Cancel there.");
        var id = S(p, "id");
        var proj = LoadGr(id);
        var tools = GrExternal();
        GruntExportPlan plan;
        _gate.Wait();
        try { plan = GrPlan(proj); } finally { _gate.Release(); }
        if (plan.HasErrors) return new { started = false, check = GrPlanDto(proj, plan) };
        if (plan.WouldOverwrite.Count > 0 && !B(p, "overwrite")) return new { started = false, confirm = true, check = GrPlanDto(proj, plan) };
        var (cat, _) = GrCatalog(proj.Target);
        _jobCts = new CancellationTokenSource();
        var ct = _jobCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                Emit("progress", new { kind = "grunts-export", done = 0, total = plan.Files.Count, message = "Exporting the voice pack" });
                var progress = new DirectProgress<(int Done, int Total, string Text)>(x => Emit("progress", new { kind = "grunts-export", done = x.Done, total = x.Total, current = x.Text, message = $"Exporting {x.Done}/{x.Total}" }));
                var r = await GruntService.ExportAsync(proj, cat, plan, GrFolder(proj), tools, progress, ct);
                await _gate.WaitAsync();
                try { _lib.Save(proj); } finally { _gate.Release(); }
                Emit("jobDone", new { kind = "grunts-export", id, ok = true, report = r.Report, written = r.Written, folder = proj.OutputFolder });
            }
            catch (OperationCanceledException) { Emit("jobDone", new { kind = "grunts-export", id, ok = false, message = "Export cancelled. The output folder wasn't changed." }); }
            catch (Exception ex)
            {
                App.Log("Player Grunts export failed: " + ex);
                Emit("jobDone", new { kind = "grunts-export", id, ok = false, message = FriendlyError.From(ex) + " The output folder wasn't changed." });
            }
            finally { _jobCts = null; }
        });
        return new { started = true };
    }

    // ------------------------------------------------------------------ recording and text-to-speech

    private (GruntProject Proj, GruntEventDef Ev, string Slot) GrTarget(JsonElement p)
    {
        var proj = LoadGr(S(p, "id"));
        var (cat, _) = GrCatalog(proj.Target);
        // The recorder passes "eventId|slot" as its line id.
        var key = S(p, "lineId", S(p, "eventId"));
        var parts = key.Split('|');
        var ev = cat.Events.FirstOrDefault(e => e.Id == parts[0]) ?? throw new InvalidOperationException("Event not found.");
        var slot = parts.Length > 1 ? parts[1] : S(p, "slot");
        return (proj, ev, slot);
    }

    private async Task<object?> GrRecord(JsonElement p)
    {
        if (_jobCts != null) throw new InvalidOperationException("Another task is still running (see the bar at the top). Wait for it to finish, or press Cancel there.");
        var (proj, ev, slot) = GrTarget(p);
        var data = Convert.FromBase64String(S(p, "data"));
        double D(string n) => p.TryGetProperty(n, out var x) && x.ValueKind == JsonValueKind.Number ? x.GetDouble() : 0;
        var tools = GrExternal();
        var workRoot = Path.Combine(Path.GetTempPath(), "dbvo");
        Directory.CreateDirectory(workRoot);
        using var work = new WorkFolder(workRoot);
        var ext = S(p, "ext", ".webm");
        var input = work.File("in" + (ext.StartsWith('.') ? ext : "." + ext));
        await File.WriteAllBytesAsync(input, data);
        var cut = work.File("cut.wav");
        var args = new List<string> { "-y", "-hide_banner", "-loglevel", "error", "-i", input };
        double start = D("start"), end = D("end");
        if (start > 0.001) { args.Add("-ss"); args.Add(start.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)); }
        if (end > start + 0.05) { args.Add("-to"); args.Add(end.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)); }
        args.AddRange(new[] { "-af", "highpass=f=70,afade=t=in:d=0.015", "-ac", "1", "-ar", "44100", "-acodec", "pcm_s16le", "-map_metadata", "-1", cut });
        var r = await ExternalTools.RunAsync(tools.FfmpegPath, args);
        if (r.ExitCode != 0 || !File.Exists(cut)) throw new ToolException("The recording couldn't be read. Try recording again; if it keeps failing, check the microphone in Windows sound settings.");
        string wav = cut;
        bool convert = B(p, "convert");
        if (convert)
        {
            var rt = Runtime();
            var v = _lib.LoadVoicePacks().FirstOrDefault(x => x.Id == proj.VoicePackId) ?? throw new InvalidOperationException("Pick a DBVO voice pack for this project first.");
            var (voiceId, _) = Phase2Service.RecordingTarget(v);
            if (string.IsNullOrWhiteSpace(rt.ElevenLabsApiKey) || voiceId.Length == 0) throw new InvalidOperationException("Converting needs your ElevenLabs API key and a voice in the voice pack.");
            var sts = v.Sts;
            var vs = ModelCapabilities.Build(sts.ModelId, sts.Stability, sts.Similarity, sts.Style, sts.SpeakerBoost, 1.0);
            if (vs != null) vs.Speed = null;
            using var client = new ElevenLabsClient(rt.ElevenLabsApiKey);
            var outBytes = await client.SpeechToSpeechAsync(voiceId, sts.ModelId, await File.ReadAllBytesAsync(cut), "recording.wav", vs, sts.Seed, sts.RemoveBackgroundNoise, sts.OutputFormat, CancellationToken.None);
            wav = await GrDecode(tools, work, outBytes, sts.OutputFormat);
        }
        var line = await Locked(() => GrSaveTake(proj, ev, slot, wav, convert ? "recording (Voice Changer)" : "recording", ""));
        return new { line, project = await Locked(() => GrDto(proj)) };
    }

    private async Task<string> GrDecode(ExternalTools tools, WorkFolder work, byte[] audio, string format)
    {
        var wav = work.File("out.wav");
        if (format.StartsWith("pcm_", StringComparison.OrdinalIgnoreCase)) { ExternalTools.PcmToWav(audio, int.Parse(format.Split('_')[1]), wav); return wav; }
        var src = work.File(format.StartsWith("wav", StringComparison.OrdinalIgnoreCase) ? "src.wav" : "src.mp3");
        await File.WriteAllBytesAsync(src, audio);
        await tools.ToWavAsync(src, wav, 44100);
        return wav;
    }

    /// <summary>Stores a recorded or generated WAV in the project folder and adds it to the event (into the slot for shouts).</summary>
    private object GrSaveTake(GruntProject proj, GruntEventDef ev, string slot, string wav, string origin, string text)
    {
        var folder = GrFolder(proj);
        var rel = Path.Combine("audio", System.Text.RegularExpressions.Regex.Replace(ev.Id, @"[^A-Za-z0-9]+", "_").Trim('_'), $"{(origin.StartsWith("rec") ? "rec" : "tts")}-{DateTime.Now:yyyyMMdd-HHmmss}-{NewId.Short(4)}.wav");
        var dest = Path.Combine(folder, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        File.Copy(wav, dest, true);
        var st = GruntService.State(proj, ev.Id);
        var w = Core.Audio.WavFile.Read(dest);
        var line = new GruntLine { Source = rel, Origin = origin.StartsWith("rec") ? "recording" : "generated", Text = text, DurationSeconds = w.DurationSeconds, SampleRate = w.SampleRate, Channels = w.Channels };
        if (ev.Kind == "slots")
        {
            var s = ev.Slots.FirstOrDefault(x => x.Id == slot) ?? ev.Slots.FirstOrDefault(x => !st.Lines.Any(l => l.Slot == x.Id)) ?? ev.Slots.First();
            st.Lines.RemoveAll(l => l.Slot == s.Id);
            line.Slot = s.Id;
            if (line.Text.Length == 0) line.Text = s.Text;
        }
        st.Lines.Add(line);
        st.Skipped = false;
        _lib.Save(proj);
        return GrLineDto(line, folder);
    }

    private async Task<object?> GrTts(JsonElement p)
    {
        var (proj, ev, slot) = GrTarget(p);
        var text = S(p, "text").Trim();
        if (text.Length == 0) throw new InvalidOperationException("Write what the line says first.");
        var rt = Runtime();
        if (string.IsNullOrWhiteSpace(rt.ElevenLabsApiKey)) throw new InvalidOperationException("Text-to-speech needs your ElevenLabs API key (Global Settings → General & tools → ElevenLabs).");
        var v = _lib.LoadVoicePacks().FirstOrDefault(x => x.Id == proj.VoicePackId) ?? throw new InvalidOperationException("Pick a DBVO voice pack for this project first (Export settings → Voice).");
        if ((v.Engine ?? "elevenlabs") != "elevenlabs" || v.Eleven.VoiceId.Length == 0) throw new InvalidOperationException("The voice pack needs an ElevenLabs voice for text-to-speech here.");
        var es = v.Eleven;
        var req = new TtsRequest
        {
            VoiceId = es.VoiceId, ModelId = es.ModelId, Text = text, OutputFormat = es.OutputFormat, Seed = es.Seed,
            LanguageCode = string.IsNullOrWhiteSpace(es.LanguageCode) ? null : es.LanguageCode,
            ApplyTextNormalization = es.TextNormalization == "auto" ? null : es.TextNormalization,
            VoiceSettings = ModelCapabilities.Build(es.ModelId, es.Stability, es.Similarity, es.Style, es.SpeakerBoost, es.Speed),
        };
        using var client = new ElevenLabsClient(rt.ElevenLabsApiKey);
        var audio = await client.TextToSpeechAsync(req, CancellationToken.None);
        var tools = GrExternal();
        var workRoot = Path.Combine(Path.GetTempPath(), "dbvo");
        Directory.CreateDirectory(workRoot);
        using var work = new WorkFolder(workRoot);
        var wav = await GrDecode(tools, work, audio, es.OutputFormat);
        var w = Core.Audio.WavFile.Read(wav);
        w.TrimTailAndFade(v.TrimTailMs, v.FadeOutMs);
        w.Write(wav);
        var spoken = Core.Naming.SpokenText.StripTags(text);
        var line = await Locked(() => GrSaveTake(proj, ev, slot, wav, "generated", spoken.Length > 0 ? spoken : text));
        return new { line, project = await Locked(() => GrDto(proj)) };
    }
}
