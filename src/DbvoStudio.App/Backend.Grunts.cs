using System.IO;
using System.Text.Json;
using DbvoStudio.Core.ElevenLabs;
using DbvoStudio.Core.Engines;
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
                // raw: without the trim and fades (the trim dialog plays the whole file)
                if (B(p, "raw")) line = new GruntLine { Id = line.Id, Source = line.Source };
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
            case "grunts.openFolder":
            {
                var proj = LoadGr(S(p, "id"));
                var which = S(p, "which");
                var path = which == "output" ? proj.OutputFolder : which == "mod" ? (GrModFolder(proj.Target) ?? "") : GrFolder(proj);
                if (path.Length == 0 || !Directory.Exists(path)) throw new InvalidOperationException("That folder doesn't exist yet.");
                OpenPath(path);
                return true;
            }
            case "grunts.reverb": return await GrReverb(p);
            case "grunts.effect": return await GrEffect(p);
            case "grunts.fxPreview": return await GrFxPreview(p);
            case "grunts.trim": return await Locked(() => GrTrim(p));
            case "grunts.variants": return await GrVariants(p);
            case "grunts.measure": return StartGrMeasure(p);
            case "grunts.whisperAll": return StartGrWhisper(p);
            case "grunts.transcribe": return await GrTranscribe(p);
            case "grunts.subtitles": return await Locked(() => GrSubtitles(p));
            default: return await DispatchGruntDrafts(m, p);
        }
    }

    private (string Sig, object Dto)? _grPathsCache;

    /// <summary>The installed-mod folders and their state (cached until a folder or its plugin changes: reading the plugin is slow).</summary>
    private object GrPaths()
    {
        var sig = _settings.PchtFolder + "|" + _settings.SpvsFolder + "|" + FilesStamp(GrModFolder(GruntTargets.Pcht), GruntTargets.Pcht) + "|" + FilesStamp(GrModFolder(GruntTargets.Spvs), GruntTargets.Spvs);
        if (_grPathsCache is { } c && c.Sig == sig) return c.Dto;
        var dto = GrPathsRead();
        _grPathsCache = (sig, dto);
        return dto;
    }

    private object GrPathsRead()
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
                drafts = proj.Drafts.Values.Where(d => d.EventId == e.Id && d.Tries.Count > 0).Select(d => new { d.Slot, lineId = d.ReplaceLineId, tries = d.Tries.Count }),
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
                effects = GruntService.EffectsOf(proj), proj.DefaultEffect, builtInEffects = Core.Audio.Effects.BuiltIn(), packLufs = GruntService.PackLoudness(proj),
            },
            catalog = new { cat.Source, cat.SourceInfo, notes, customs = cat.Events.Count(e => e.Origin == "custom") },
            paths = GrPaths(),
            tools = GrTools(),
            events,
            orphans,
            voices = voices.Select(v => new { v.Id, v.Name, v.Color }),
            others = GrOthers(proj.Id),
        };
    }

    private (DateTime At, List<(string Id, string Name, string Target, int Lines)> List)? _grOthers;

    /// <summary>The other projects (for "Copy from another project"): read from disk at most every 20 seconds, the open ones from memory.</summary>
    private object GrOthers(string id)
    {
        if (_grOthers is not { } c || (DateTime.UtcNow - c.At).TotalSeconds > 20)
        {
            c = (DateTime.UtcNow, _lib.LoadGruntProjects().Select(x => (x.Id, x.Name, x.Target, x.Events.Values.Sum(s => s.Lines.Count))).ToList());
            _grOthers = c;
        }
        return c.List.Where(x => x.Id != id).Select(x =>
        {
            GruntProject? live; lock (_gr) _gr.TryGetValue(x.Id, out live);
            return live != null ? new { id = live.Id, name = live.Name, target = live.Target, lines = live.Events.Values.Sum(s => s.Lines.Count) } : new { id = x.Id, name = x.Name, target = x.Target, lines = x.Lines };
        }).ToList();
    }

    private static object GrLineDto(GruntLine l, string folder)
    {
        var path = GruntService.SourcePath(l, folder);
        return new { l.Id, l.Source, path, file = Path.GetFileName(path), exists = File.Exists(path), l.Origin, l.Text, l.Spoken, l.FileName, l.Slot, l.DurationSeconds, l.SampleRate, l.Channels, l.Lufs, l.AddedUtc, l.Reverb, effectId = l.Reverb ? (l.EffectId.Length > 0 ? l.EffectId : "inner") : "", l.PeakDb, l.TrimStart, l.TrimEnd, l.FadeInMs, l.FadeOutMs, wav = path.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) };
    }

    /// <summary>Reads length, rate and channels of new lines (outside the page lock; quick per file).</summary>
    private async Task GrProbe(GruntProject proj, List<GruntLine> lines)
    {
        if (lines.Count == 0 || !File.Exists(_settings.FfmpegPath)) return;
        var tools = GrExternal();
        var folder = GrFolder(proj);
        // WAV headers are read directly; other formats run ffmpeg, several at a time.
        await Parallel.ForEachAsync(lines, new ParallelOptions { MaxDegreeOfParallelism = 6 }, async (l, ct) =>
        {
            try
            {
                var info = await GruntService.ProbeAsync(tools, GruntService.SourcePath(l, folder), ct);
                if (info != null) { l.DurationSeconds = info.Seconds; l.SampleRate = info.SampleRate; l.Channels = info.Channels; }
            }
            catch { /* shown as unknown length */ }
        });
        // Loudness of the new lines, in the background (the line rows show it once measured).
        _ = Task.Run(async () =>
        {
            try
            {
                var tmp = new GruntProject();
                GruntService.State(tmp, "x").Lines.AddRange(lines);
                await GruntService.MeasureAsync(tmp, folder, tools, true, null, CancellationToken.None);
                await Locked(() => { _lib.Save(proj); return null; });
            }
            catch { /* measured on Check or Measure loudness */ }
        });
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
        if (p.TryGetProperty("effects", out var fxs) && fxs.ValueKind == JsonValueKind.Array)
            proj.Effects = fxs.Deserialize<List<Core.Audio.EffectPreset>>(JsonOptions) ?? proj.Effects;
        Str("defaultEffect", v => { if (Core.Audio.Effects.Find(GruntService.EffectsOf(proj), v) != null) proj.DefaultEffect = v; });
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
            GruntService.ReleaseReverb(proj, line, GrFolder(proj));
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
                GruntService.ReleaseReverb(proj, line, GrFolder(proj));
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
                foreach (var l in st.Lines.ToList()) GruntService.ReleaseReverb(proj, l, GrFolder(proj));
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
        plan.Issues.AddRange(GruntService.LoudnessIssues(proj, cat));
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
        var line = await Locked(() => GrSaveTake(proj, ev, slot, wav, convert ? "recording (Voice Changer)" : "recording", S(p, "subtitle").Trim()));
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

    // ------------------------------------------------------------------ reverb, Whisper subtitles, subtitles .toml

    private async Task<object?> GrReverb(JsonElement p)
    {
        GruntProject proj = null!; GruntLine line = null!;
        await Locked(() =>
        {
            proj = LoadGr(S(p, "id"));
            line = proj.Events.Values.SelectMany(s => s.Lines).FirstOrDefault(l => l.Id == S(p, "lineId")) ?? throw new InvalidOperationException("Line not found.");
            return null;
        });
        var tools = File.Exists(_settings.FfmpegPath) ? GrExternal() : null;
        await _gate.WaitAsync(); // the file and the project change together
        try
        {
            try { await GruntService.SetReverbAsync(proj, line, GrFolder(proj), B(p, "on"), tools, B(p, "force")); }
            catch (GruntService.FileChangedException ex) { return new { changed = true, message = ex.Message }; }
            _lib.Save(proj);
            return new { changed = false, project = GrDto(proj) };
        }
        finally { _gate.Release(); }
    }

    /// <summary>An effect preset on several lines at once (or off with effect ""): the line menu and "all lines of this event".</summary>
    private async Task<object?> GrEffect(JsonElement p)
    {
        GruntProject proj = null!; List<GruntLine> lines = null!;
        await Locked(() =>
        {
            proj = LoadGr(S(p, "id"));
            var ids = Strings(p, "lineIds").ToHashSet();
            lines = proj.Events.Values.SelectMany(s => s.Lines).Where(l => ids.Contains(l.Id)).ToList();
            return null;
        });
        var fx = S(p, "effect");
        var tools = File.Exists(_settings.FfmpegPath) ? GrExternal() : null;
        var folder = GrFolder(proj);
        int done = 0; var problems = new List<string>();
        await _gate.WaitAsync();
        try
        {
            // Lines sharing a file follow the first one; each file once.
            foreach (var l in lines.GroupBy(x => GruntService.SourcePath(x, folder), StringComparer.OrdinalIgnoreCase).Select(g => g.First()))
            {
                try { await GruntService.SetEffectAsync(proj, l, folder, fx.Length == 0 ? null : fx, tools, B(p, "force")); done++; }
                catch (GruntService.FileChangedException ex) { if (lines.Count == 1) return new { changed = true, message = ex.Message }; problems.Add(ex.Message); }
                catch (Exception ex) { problems.Add(Path.GetFileName(GruntService.SourcePath(l, folder)) + ": " + FriendlyError.From(ex)); }
            }
            _lib.Save(proj);
            if (lines.Count == 1 && problems.Count == 1) throw new InvalidOperationException(problems[0]);
            return new { changed = false, done, problems, project = GrDto(proj) };
        }
        finally { _gate.Release(); }
    }

    /// <summary>How a line sounds with an effect (or without any: effect ""), for listening in the effect picker; nothing is changed.</summary>
    private async Task<object?> GrFxPreview(JsonElement p)
    {
        var proj = LoadGr(S(p, "id"));
        var line = proj.Events.Values.SelectMany(s => s.Lines).FirstOrDefault(l => l.Id == S(p, "lineId")) ?? throw new InvalidOperationException("Line not found.");
        var folder = GrFolder(proj);
        var src = GruntService.SourcePath(line, folder);
        // Always from the dry file: the original when an effect is on.
        var dry = line.Reverb && File.Exists(GruntService.ReverbOriginal(folder, src)) ? GruntService.ReverbOriginal(folder, src) : src;
        if (!File.Exists(dry)) throw new InvalidOperationException("The audio file is missing.");
        var fxId = S(p, "effect");
        var fx = fxId.Length > 0 ? Core.Audio.Effects.Find(GruntService.EffectsOf(proj), fxId) ?? throw new InvalidOperationException("Unknown effect.") : null;
        var cache = Path.Combine(folder, "cache", "fx");
        Directory.CreateDirectory(cache);
        var key = GruntService.Hash(GruntService.Stamp(dry) + "|" + dry + "|" + (fx == null ? "" : JsonSerializer.Serialize(fx, JsonOptions)));
        var outPath = Path.Combine(cache, key + ".wav");
        if (!File.Exists(outPath))
        {
            var tools = File.Exists(_settings.FfmpegPath) ? GrExternal() : null;
            if (fx != null) await Core.Audio.Effects.ApplyAsync(dry, outPath, fx, tools);
            else
            {
                // the line as it is, in a format the page plays
                var tmp = new GruntLine { Id = line.Id, Source = dry };
                var wav = await GruntService.PreviewAsync(GrExternal(), tmp, folder);
                File.Copy(wav, outPath, true);
            }
        }
        return new { url = "https://media.dbvo/" + Path.GetRelativePath(_lib.Root, outPath).Replace('\\', '/') + "?t=" + File.GetLastWriteTimeUtc(outPath).Ticks };
    }

    private object GrTrim(JsonElement p)
    {
        var proj = LoadGr(S(p, "id"));
        var line = proj.Events.Values.SelectMany(s => s.Lines).FirstOrDefault(l => l.Id == S(p, "lineId")) ?? throw new InvalidOperationException("Line not found.");
        double D(string n) => p.TryGetProperty(n, out var x) && x.ValueKind == JsonValueKind.Number ? Math.Max(0, x.GetDouble()) : 0;
        line.TrimStart = D("start"); line.TrimEnd = D("end"); line.FadeInMs = D("fadeIn"); line.FadeOutMs = D("fadeOut");
        if (line.DurationSeconds > 0 && line.TrimEnd >= line.DurationSeconds - 0.005) line.TrimEnd = 0; // the whole length = no end trim
        if (line.TrimEnd > 0 && line.TrimEnd <= line.TrimStart + 0.02) throw new InvalidOperationException("The end has to come after the start.");
        line.Lufs = null; line.PeakDb = null;
        _lib.Save(proj);
        return GrDto(proj);
    }

    private async Task<object?> GrVariants(JsonElement p)
    {
        var proj = LoadGr(S(p, "id"));
        var (cat, _) = GrCatalog(proj.Target);
        var ev = cat.Events.FirstOrDefault(e => e.Id == S(p, "eventId")) ?? throw new InvalidOperationException("Event not found.");
        if (ev.Kind != "pool") throw new InvalidOperationException("Variants go into pool events (a shout part has one file).");
        var line = GruntService.State(proj, ev.Id).Lines.FirstOrDefault(l => l.Id == S(p, "lineId")) ?? throw new InvalidOperationException("Line not found.");
        double D(string n, double d) => p.TryGetProperty(n, out var x) && x.ValueKind == JsonValueKind.Number ? x.GetDouble() : d;
        var made = await GruntService.MakeVariantsAsync(proj, ev, line, GrFolder(proj), (int)D("count", 3), Math.Clamp(D("pitch", 3), 0, 25), Math.Clamp(D("speed", 3), 0, 25), GrExternal());
        return await Locked(() =>
        {
            var st = GruntService.State(proj, ev.Id);
            int at = st.Lines.IndexOf(line) + 1;
            foreach (var (rel, pitch, speed) in made)
            {
                var w = GruntService.ProbeWavHeader(Path.Combine(GrFolder(proj), rel));
                var nl = new GruntLine { Source = rel, Origin = "generated", Text = line.Text, Spoken = line.Spoken, DurationSeconds = w?.Seconds ?? 0, SampleRate = w?.SampleRate ?? 0, Channels = w?.Channels ?? 1 };
                st.Lines.Insert(Math.Min(at++, st.Lines.Count), nl);
            }
            st.Skipped = false;
            _lib.Save(proj);
            return new { added = made.Count, project = GrDto(proj) };
        });
    }

    private object StartGrMeasure(JsonElement p)
    {
        if (_jobCts != null) throw new InvalidOperationException("Another task is still running (see the bar at the top). Wait for it to finish, or press Cancel there.");
        var id = S(p, "id");
        var proj = LoadGr(id);
        var tools = GrExternal();
        bool all = B(p, "all");
        _jobCts = new CancellationTokenSource();
        var ct = _jobCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                Emit("progress", new { kind = "grunts-measure", done = 0, total = 1, message = "Measuring loudness" });
                int n = await GruntService.MeasureAsync(proj, GrFolder(proj), tools, !all, new DirectProgress<(int Done, int Total)>(x => Emit("progress", new { kind = "grunts-measure", done = x.Done, total = x.Total })), ct);
                await _gate.WaitAsync(); try { _lib.Save(proj); } finally { _gate.Release(); }
                Emit("jobDone", new { kind = "grunts-measure", id, ok = true, message = $"Loudness measured for {n} line(s)." });
            }
            catch (OperationCanceledException) { Emit("jobDone", new { kind = "grunts-measure", id, ok = false, message = "Cancelled." }); }
            catch (Exception ex) { Emit("jobDone", new { kind = "grunts-measure", id, ok = false, message = FriendlyError.From(ex) }); }
            finally { _jobCts = null; }
        });
        return new { started = true };
    }

    /// <summary>Whisper fills in every empty subtitle of an event, or of the whole project (one model load for all).</summary>
    private object StartGrWhisper(JsonElement p)
    {
        if (_jobCts != null) throw new InvalidOperationException("Another task is still running (see the bar at the top). Wait for it to finish, or press Cancel there.");
        var id = S(p, "id");
        var proj = LoadGr(id);
        var tools = GrExternal();
        var eventId = S(p, "eventId");
        var folder = GrFolder(proj);
        var todo = proj.Events.Where(kv => eventId.Length == 0 || kv.Key == eventId).SelectMany(kv => kv.Value.Lines)
            .Where(l => l.Text.Trim().Length == 0 && File.Exists(GruntService.SourcePath(l, folder))).ToList();
        if (todo.Count == 0) throw new InvalidOperationException("Every line there has a subtitle already.");
        _jobCts = new CancellationTokenSource();
        var ct = _jobCts.Token;
        _ = Task.Run(async () =>
        {
            int filled = 0;
            try
            {
                Emit("progress", new { kind = "grunts-whisper", done = 0, total = todo.Count, message = "Loading Whisper" });
                var tool = new Tools.WavToLip(_settings.WavToLip, Tools(), WhisperModelsFolder(), e => { if (e.Kind == "log") App.Log("Whisper: " + e.Text); });
                var (transcribe, engine) = await tool.CreateTranscriberAsync(ct);
                var workRoot = Path.Combine(Path.GetTempPath(), "dbvo");
                Directory.CreateDirectory(workRoot);
                using var work = new WorkFolder(workRoot);
                await using (engine)
                {
                    for (int i = 0; i < todo.Count; i++)
                    {
                        ct.ThrowIfCancellationRequested();
                        var l = todo[i];
                        Emit("progress", new { kind = "grunts-whisper", done = i, total = todo.Count, current = Path.GetFileName(GruntService.SourcePath(l, folder)) });
                        try
                        {
                            var src = GruntService.SourcePath(l, folder);
                            var input = src;
                            if (src.EndsWith(".fuz", StringComparison.OrdinalIgnoreCase)) { input = work.File($"f{i}.xwm"); File.WriteAllBytes(input, Core.Audio.FuzFile.Split(File.ReadAllBytes(src)).Xwm); }
                            var wav = work.File($"w{i}.wav");
                            var r = await ExternalTools.RunAsync(tools.FfmpegPath, new[] { "-y", "-hide_banner", "-loglevel", "error", "-i", input, "-ac", "1", "-ar", "16000", "-acodec", "pcm_s16le", wav }, ct);
                            if (r.ExitCode != 0) continue;
                            var text = (await transcribe(wav, ct)).Trim();
                            if (text.Length > 0 && l.Text.Trim().Length == 0) { l.Text = text; filled++; }
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex) { App.Log("Grunts Whisper: " + ex.Message); }
                    }
                }
                await _gate.WaitAsync(); try { _lib.Save(proj); } finally { _gate.Release(); }
                Emit("jobDone", new { kind = "grunts-whisper", id, ok = true, message = $"Whisper filled in {filled} of {todo.Count} empty subtitle(s). Check them before exporting." });
            }
            catch (OperationCanceledException) { await SaveQuiet(); Emit("jobDone", new { kind = "grunts-whisper", id, ok = false, message = $"Cancelled ({filled} filled in)." }); }
            catch (Exception ex) { await SaveQuiet(); Emit("jobDone", new { kind = "grunts-whisper", id, ok = false, message = FriendlyError.From(ex) }); }
            finally { _jobCts = null; }
            async Task SaveQuiet() { await _gate.WaitAsync(); try { _lib.Save(proj); } catch { } finally { _gate.Release(); } }
        });
        return new { started = true, lines = todo.Count };
    }

    /// <summary>Whisper's transcript of a line's audio or of a recording (the WAV to LIP Tool's Whisper settings): a subtitle suggestion.</summary>
    private async Task<object?> GrTranscribe(JsonElement p)
    {
        var proj = LoadGr(S(p, "id"));
        var tools = GrExternal();
        var workRoot = Path.Combine(Path.GetTempPath(), "dbvo");
        Directory.CreateDirectory(workRoot);
        using var work = new WorkFolder(workRoot);
        string input;
        if (S(p, "data") is { Length: > 0 } data)
        {
            var ext = S(p, "ext", ".webm");
            input = work.File("in" + (ext.StartsWith('.') ? ext : "." + ext));
            await File.WriteAllBytesAsync(input, Convert.FromBase64String(data));
        }
        else
        {
            var line = proj.Events.Values.SelectMany(s => s.Lines).FirstOrDefault(l => l.Id == S(p, "lineId")) ?? throw new InvalidOperationException("Line not found.");
            input = GruntService.SourcePath(line, GrFolder(proj));
            if (!File.Exists(input)) throw new InvalidOperationException("The audio file is missing.");
            if (input.EndsWith(".fuz", StringComparison.OrdinalIgnoreCase)) { var x = work.File("in.xwm"); File.WriteAllBytes(x, Core.Audio.FuzFile.Split(File.ReadAllBytes(input)).Xwm); input = x; }
        }
        var wav = work.File("w.wav");
        double D(string n) => p.TryGetProperty(n, out var x) && x.ValueKind == JsonValueKind.Number ? x.GetDouble() : 0;
        var args = new List<string> { "-y", "-hide_banner", "-loglevel", "error", "-i", input };
        double start = D("start"), end = D("end");
        if (start > 0.001) { args.Add("-ss"); args.Add(start.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)); }
        if (end > start + 0.05) { args.Add("-to"); args.Add(end.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)); }
        args.AddRange(new[] { "-ac", "1", "-ar", "16000", "-acodec", "pcm_s16le", wav });
        var r = await ExternalTools.RunAsync(tools.FfmpegPath, args);
        if (r.ExitCode != 0 || !File.Exists(wav)) throw new ToolException("The audio couldn't be read for Whisper.");
        var tool = new Tools.WavToLip(_settings.WavToLip, Tools(), WhisperModelsFolder(), e => { if (e.Kind == "log") App.Log("Whisper: " + e.Text); });
        var (transcribe, engine) = await tool.CreateTranscriberAsync(CancellationToken.None);
        string text;
        await using (engine) text = (await transcribe(wav, CancellationToken.None)).Trim();
        return new { text };
    }

    /// <summary>open: writes the project's subtitles .toml and opens it in the default editor. sync: reads it back if it was saved since.</summary>
    private object GrSubtitles(JsonElement p)
    {
        var proj = LoadGr(S(p, "id"));
        var folder = GrFolder(proj);
        var path = GruntService.SubtitlesTomlPath(folder);
        if (S(p, "action") == "open")
        {
            // A newer file (edited, not read yet) is read first so nothing typed is lost.
            if (File.Exists(path) && File.GetLastWriteTimeUtc(path).Ticks > proj.SubtitlesTomlTicks && proj.SubtitlesTomlTicks > 0)
                GruntService.ReadSubtitlesToml(proj, File.ReadAllText(path));
            Directory.CreateDirectory(folder);
            File.WriteAllText(path, GruntService.BuildSubtitlesToml(proj, GrCatalog(proj.Target).Cat, folder), new System.Text.UTF8Encoding(false));
            proj.SubtitlesTomlTicks = File.GetLastWriteTimeUtc(path).Ticks;
            _lib.Save(proj);
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }); }
            catch { try { System.Diagnostics.Process.Start("notepad.exe", $"\"{path}\""); } catch { /* the path is shown */ } } // no program set for .toml
            return new { path, changed = 0 };
        }
        if (!File.Exists(path) || proj.SubtitlesTomlTicks == 0 || File.GetLastWriteTimeUtc(path).Ticks <= proj.SubtitlesTomlTicks) return new { changed = 0 };
        var (changed, unknown) = GruntService.ReadSubtitlesToml(proj, File.ReadAllText(path));
        proj.SubtitlesTomlTicks = File.GetLastWriteTimeUtc(path).Ticks;
        _lib.Save(proj);
        return new { changed, unknown = unknown.Count, project = GrDto(proj) };
    }

    // ------------------------------------------------------------------ text-to-speech workspace

    private static string DraftKey(string eventId, string slot, string lineId) => $"{eventId}|{slot}|{lineId}";

    private (GruntProject Proj, GruntEventDef Ev, string Key, GruntDraft Draft) GrDraft(JsonElement p, bool create)
    {
        var proj = LoadGr(S(p, "id"));
        var (cat, _) = GrCatalog(proj.Target);
        var ev = cat.Events.FirstOrDefault(e => e.Id == S(p, "eventId")) ?? throw new InvalidOperationException("Event not found.");
        string slot = S(p, "slot"), lineId = S(p, "lineId");
        var key = DraftKey(ev.Id, slot, lineId);
        if (!proj.Drafts.TryGetValue(key, out var d))
        {
            d = new GruntDraft { EventId = ev.Id, Slot = slot, ReplaceLineId = lineId };
            // A line being redone starts from its own texts; a shout part from its words.
            var line = lineId.Length > 0 ? GruntService.State(proj, ev.Id).Lines.FirstOrDefault(l => l.Id == lineId) : null;
            var s = ev.Slots.FirstOrDefault(x => x.Id == slot);
            d.Subtitle = line?.Text ?? s?.Text ?? "";
            d.Spoken = line != null ? (line.Spoken.Length > 0 ? line.Spoken : line.Text) : s?.Text ?? "";
            if (create) proj.Drafts[key] = d;
        }
        return (proj, ev, key, d);
    }

    private object GrDraftDto(GruntProject proj, GruntEventDef ev, GruntDraft d)
    {
        var folder = GrFolder(proj);
        var v = _lib.LoadVoicePacks().FirstOrDefault(x => x.Id == proj.VoicePackId);
        var engine = v?.Engine ?? "elevenlabs";
        string voiceName = v == null ? "" : engine == "voxcpm" ? v.VoxCpm.Voice : engine == "omnivoice" ? v.OmniVoice.Voice : v.Eleven.VoiceName;
        string model = v == null ? "" : engine == "voxcpm" ? "VoxCPM" : engine == "omnivoice" ? v.OmniVoice.Model : v.Eleven.ModelId;
        // ElevenLabs v3/v4 and OmniVoice read [tags]; VoxCPM would say them, so they are left out for it by default.
        var tags = engine == "omnivoice" ? new List<string> { "[laughter]", "[sigh]", "[gasp]" } : v?.Tags ?? new List<string>();
        return new
        {
            eventId = ev.Id, d.Slot, lineId = d.ReplaceLineId, d.Subtitle, d.Spoken, d.SelectedId,
            tries = d.Tries.Select(t => new
            {
                t.Id, t.Spoken, t.Seconds, t.Settings, t.CreatedUtc,
                url = "https://media.dbvo/" + Path.GetRelativePath(_lib.Root, Path.Combine(folder, t.File)).Replace('\\', '/'),
                exists = File.Exists(Path.Combine(folder, t.File)),
            }),
            voice = new
            {
                ready = v != null && voiceName.Length > 0, voicePack = v?.Name ?? "", engine, voiceName, model, tags,
                tagModel = engine == "elevenlabs" && ModelCapabilities.IsTagModel(v?.Eleven.ModelId ?? ""),
                stability = v?.Eleven.Stability ?? 0.5, speed = engine == "omnivoice" ? v?.OmniVoice.Speed ?? 1 : v?.Eleven.Speed ?? 1,
                seed = v?.Eleven.Seed, discrete = ModelCapabilities.HasDiscreteStability(v?.Eleven.ModelId ?? ""),
                hasKey = !string.IsNullOrWhiteSpace(Secrets.Unprotect(_settings.ElevenLabsApiKey)),
            },
            label = ev.Label, slotLabel = ev.Slots.FirstOrDefault(x => x.Id == d.Slot)?.Label ?? "", words = ev.Slots.FirstOrDefault(x => x.Id == d.Slot)?.Text ?? "",
            target = proj.Target,
        };
    }

    private void GrDeleteTry(GruntProject proj, GruntTry t)
    {
        try { File.Delete(Path.Combine(GrFolder(proj), t.File)); } catch { }
    }

    private async Task<object?> DispatchGruntDrafts(string m, JsonElement p)
    {
        switch (m)
        {
            case "grunts.draft":
                return await Locked(() => { var (proj, ev, _, d) = GrDraft(p, false); return GrDraftDto(proj, ev, d); });
            case "grunts.draftText":
                return await Locked(() =>
                {
                    var (proj, ev, _, d) = GrDraft(p, true);
                    if (p.TryGetProperty("subtitle", out var su) && su.ValueKind == JsonValueKind.String) d.Subtitle = su.GetString()!;
                    if (p.TryGetProperty("spoken", out var sp) && sp.ValueKind == JsonValueKind.String) d.Spoken = sp.GetString()!;
                    if (p.TryGetProperty("selectedId", out var se) && se.ValueKind == JsonValueKind.String) d.SelectedId = se.GetString()!;
                    d.UpdatedUtc = DateTime.UtcNow;
                    _lib.Save(proj);
                    return GrDraftDto(proj, ev, d);
                });
            case "grunts.try":
            {
                (GruntProject proj, GruntEventDef ev, string key, GruntDraft d) = (null!, null!, "", null!);
                await Locked(() => { (proj, ev, key, d) = GrDraft(p, true); return null; });
                var spoken = S(p, "spoken").Trim();
                if (spoken.Length == 0) throw new InvalidOperationException("Write what the voice says first (the spoken text).");
                var (wav, seconds, settings) = await GrSynth(proj, spoken, p);
                return await Locked(() =>
                {
                    var t = new GruntTry { Spoken = spoken, Seconds = seconds, Settings = settings };
                    t.File = Path.Combine("drafts", GruntService.Hash(key), t.Id + ".wav");
                    var dest = Path.Combine(GrFolder(proj), t.File);
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    File.Copy(wav, dest, true);
                    try { File.Delete(wav); } catch { }
                    d.Spoken = S(p, "spoken");
                    if (p.TryGetProperty("subtitle", out var su) && su.ValueKind == JsonValueKind.String) d.Subtitle = su.GetString()!;
                    d.Tries.Add(t);
                    d.SelectedId = t.Id;
                    d.UpdatedUtc = DateTime.UtcNow;
                    proj.Drafts[key] = d;
                    _lib.Save(proj);
                    return new { tryId = t.Id, draft = GrDraftDto(proj, ev, d) };
                });
            }
            case "grunts.tryDelete":
                return await Locked(() =>
                {
                    var (proj, ev, _, d) = GrDraft(p, true);
                    var t = d.Tries.FirstOrDefault(x => x.Id == S(p, "tryId"));
                    if (t != null) { d.Tries.Remove(t); GrDeleteTry(proj, t); if (d.SelectedId == t.Id) d.SelectedId = d.Tries.LastOrDefault()?.Id ?? ""; }
                    _lib.Save(proj);
                    return GrDraftDto(proj, ev, d);
                });
            case "grunts.discard":
                return await Locked(() =>
                {
                    var (proj, ev, key, d) = GrDraft(p, false);
                    foreach (var t in d.Tries) GrDeleteTry(proj, t);
                    proj.Drafts.Remove(key);
                    _lib.Save(proj);
                    return GrDto(proj);
                });
            case "grunts.commit":
                return await Locked(() =>
                {
                    var (proj, ev, key, d) = GrDraft(p, false);
                    var t = d.Tries.FirstOrDefault(x => x.Id == S(p, "tryId", d.SelectedId)) ?? throw new InvalidOperationException("Pick the try to keep first.");
                    var folder = GrFolder(proj);
                    var src = Path.Combine(folder, t.File);
                    if (!File.Exists(src)) throw new InvalidOperationException("That try's audio file is gone; make a new try.");
                    if (p.TryGetProperty("subtitle", out var su) && su.ValueKind == JsonValueKind.String) d.Subtitle = su.GetString()!;
                    var rel = Path.Combine("audio", System.Text.RegularExpressions.Regex.Replace(ev.Id, @"[^A-Za-z0-9]+", "_").Trim('_'), $"tts-{DateTime.Now:yyyyMMdd-HHmmss}-{NewId.Short(4)}.wav");
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(folder, rel))!);
                    File.Move(src, Path.Combine(folder, rel), true);
                    var st = GruntService.State(proj, ev.Id);
                    var line = d.ReplaceLineId.Length > 0 ? st.Lines.FirstOrDefault(l => l.Id == d.ReplaceLineId) : null;
                    if (line != null)
                    {
                        // Redoing a line: same place, name and slot; the project's own old audio goes.
                        GruntService.ReleaseReverb(proj, line, folder);
                        if (!Path.IsPathRooted(line.Source) && !proj.Events.Values.SelectMany(s => s.Lines).Any(l => l != line && l.Source == line.Source))
                            try { File.Delete(GruntService.SourcePath(line, folder)); } catch { }
                        line.Source = rel; line.Origin = "generated";
                    }
                    else
                    {
                        line = new GruntLine { Source = rel, Origin = "generated" };
                        if (ev.Kind == "slots")
                        {
                            var sl = ev.Slots.FirstOrDefault(x => x.Id == d.Slot) ?? ev.Slots.FirstOrDefault(x => !st.Lines.Any(l => l.Slot == x.Id)) ?? ev.Slots.First();
                            st.Lines.RemoveAll(l => l.Slot == sl.Id);
                            line.Slot = sl.Id;
                        }
                        st.Lines.Add(line);
                    }
                    line.Text = d.Subtitle.Trim();
                    line.Spoken = t.Spoken;
                    line.DurationSeconds = t.Seconds; line.SampleRate = 44100; line.Channels = 1; line.Lufs = null;
                    st.Skipped = false;
                    foreach (var other in d.Tries.Where(x => x != t)) GrDeleteTry(proj, other);
                    proj.Drafts.Remove(key);
                    _lib.Save(proj);
                    return new { line = GrLineDto(line, folder), project = GrDto(proj) };
                });
            default: return NotHandled;
        }
    }

    /// <summary>One text-to-speech try with the project's voice pack (ElevenLabs, VoxCPM or OmniVoice). Returns a temporary WAV.</summary>
    private async Task<(string Wav, double Seconds, string Settings)> GrSynth(GruntProject proj, string spoken, JsonElement p)
    {
        var v = _lib.LoadVoicePacks().FirstOrDefault(x => x.Id == proj.VoicePackId) ?? throw new InvalidOperationException("Pick a DBVO voice pack for this project first (Setup & export → DBVO voice pack).");
        var rt = Runtime();
        var engine = v.Engine ?? "elevenlabs";
        double? D(string n) => p.TryGetProperty(n, out var x) && x.ValueKind == JsonValueKind.Number ? x.GetDouble() : null;
        bool sendTags = !p.TryGetProperty("sendTags", out var stg) || stg.ValueKind != JsonValueKind.False;
        var prons = Phase2Service.MergePronunciations(_settings.Pronunciations, v.Pronunciations);
        var tools = GrExternal();
        var workRoot = Path.Combine(Path.GetTempPath(), "dbvo");
        Directory.CreateDirectory(workRoot);
        using var work = new WorkFolder(workRoot);
        byte[] audio; string format; string settings;
        switch (engine)
        {
            case "voxcpm":
            case "omnivoice":
            {
                var text = sendTags ? spoken : Core.Naming.SpokenText.StripTags(spoken);
                text = Phase2Service.ApplyPronunciations(text, prons, "local");
                using var local = new LocalEngineClient(engine == "voxcpm" ? _settings.LocalEngines.VoxCpmAddress : _settings.LocalEngines.OmniVoiceAddress);
                if (engine == "voxcpm")
                {
                    if (v.VoxCpm.Voice.Length == 0) throw new InvalidOperationException("The voice pack has no VoxCPM reference voice.");
                    audio = await local.VoxCpmAsync(text, v.VoxCpm);
                    settings = $"VoxCPM · {v.VoxCpm.Voice} · cfg {v.VoxCpm.CfgValue:0.##}{(sendTags ? "" : " · tags left out")}";
                }
                else
                {
                    if (v.OmniVoice.Voice.Length == 0) throw new InvalidOperationException("The voice pack has no OmniVoice reference voice.");
                    var os = JsonSerializer.Deserialize<OmniVoiceSettings>(JsonSerializer.Serialize(v.OmniVoice))!;
                    if (D("speed") is { } sp) os.Speed = Math.Clamp(sp, 0.5, 2);
                    audio = await local.OmniVoiceAsync(text, os);
                    settings = $"OmniVoice · {os.Voice} · speed {os.Speed:0.##}{(sendTags ? "" : " · tags left out")}";
                }
                format = "wav";
                break;
            }
            default:
            {
                if (string.IsNullOrWhiteSpace(rt.ElevenLabsApiKey)) throw new InvalidOperationException("Text-to-speech needs your ElevenLabs API key (Global Settings → General & tools → ElevenLabs).");
                var es = v.Eleven;
                if (es.VoiceId.Length == 0) throw new InvalidOperationException("The voice pack has no ElevenLabs voice.");
                double stability = D("stability") ?? es.Stability, speed = D("speed") ?? es.Speed;
                // A fixed seed gives the same take every time; trial and error wants a new one per try unless asked.
                long? seed = B(p, "keepSeed") ? es.Seed : null;
                var text = Phase2Service.ApplyPronunciations(sendTags ? spoken : Core.Naming.SpokenText.StripTags(spoken), prons, es.ModelId);
                var req = new TtsRequest
                {
                    VoiceId = es.VoiceId, ModelId = es.ModelId, Text = text, OutputFormat = es.OutputFormat, Seed = seed,
                    LanguageCode = string.IsNullOrWhiteSpace(es.LanguageCode) ? null : es.LanguageCode,
                    ApplyTextNormalization = es.TextNormalization == "auto" ? null : es.TextNormalization,
                    VoiceSettings = ModelCapabilities.Build(es.ModelId, stability, es.Similarity, es.Style, es.SpeakerBoost, speed),
                    PronunciationDictionaries = es.PronunciationDictionaryIds.Count == 0 ? null : es.PronunciationDictionaryIds.Take(3).Select(id => new PronunciationLocator { DictionaryId = id }).ToList(),
                };
                using var client = new ElevenLabsClient(rt.ElevenLabsApiKey);
                audio = await client.TextToSpeechAsync(req, CancellationToken.None);
                format = es.OutputFormat;
                settings = $"{es.ModelId} · stability {req.VoiceSettings?.Stability ?? stability:0.##} · speed {speed:0.##}{(seed is { } sd ? " · seed " + sd : "")}";
                break;
            }
        }
        var wav = await GrDecode(tools, work, audio, format);
        var w = Core.Audio.WavFile.Read(wav);
        w.TrimTailAndFade(v.TrimTailMs, v.FadeOutMs);
        var keep = Path.Combine(workRoot, "try-" + Guid.NewGuid().ToString("N")[..10] + ".wav");
        w.Write(keep);
        return (keep, w.DurationSeconds, settings);
    }
}
