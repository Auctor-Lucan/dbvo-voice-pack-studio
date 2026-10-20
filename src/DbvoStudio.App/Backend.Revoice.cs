using System.IO;
using System.Text.Json;
using DbvoStudio.App.Tools;
using DbvoStudio.Core.Projects;
using DbvoStudio.Core.Naming;
using DbvoStudio.Core.Revoice;

namespace DbvoStudio.App;

/// <summary>Bridge methods for the NPC Revoice Tool (Other Tools).</summary>
public sealed partial class Backend
{
    private readonly Dictionary<string, RevoiceProject> _rv = new();
    private readonly Dictionary<string, RevoiceSession> _rvSessions = new();

    private RevoiceProject LoadRv(string id)
    {
        lock (_rv)
        {
            if (_rv.TryGetValue(id, out var p)) return p;
            p = _lib.LoadRevoiceProject(id) ?? throw new InvalidOperationException("NPC Revoice project not found.");
            _rv[id] = p;
            return p;
        }
    }

    private RevoiceService RvService() => new(_lib, Runtime());

    /// <summary>The loaded plugins and voice files for a project, reused while its plugins and sources are unchanged.</summary>
    private RevoiceSession RvSession(RevoiceProject p, IProgress<string>? progress = null)
    {
        var sig = RevoiceService.SessionSignature(p);
        lock (_rvSessions)
        {
            if (_rvSessions.TryGetValue(p.Id, out var s) && s.Signature == sig) return s;
        }
        var fresh = RvService().Open(p, progress);
        lock (_rvSessions)
        {
            if (_rvSessions.TryGetValue(p.Id, out var old)) old.Dispose();
            _rvSessions[p.Id] = fresh;
        }
        return fresh;
    }

    private RevoiceSession? RvSessionIfOpen(RevoiceProject p)
    {
        lock (_rvSessions) return _rvSessions.TryGetValue(p.Id, out var s) && s.Signature == RevoiceService.SessionSignature(p) ? s : null;
    }

    private async Task<object?> DispatchRevoice(string m, JsonElement p, List<string> dropped)
    {
        switch (m)
        {
            case "revoice.list":
                return await Locked(() => _lib.LoadRevoiceProjects().Select(x => new
                {
                    x.Id, x.Name, x.UpdatedUtc, lines = x.Lines.Count, generated = x.Lines.Count(l => l.SelectedTakeId != null),
                    targets = x.Plugins.Where(t => t.Target).Select(t => t.FileName).ToList(), speakers = x.Lines.Select(l => l.SpeakerKey).Distinct().Count(),
                    newVoiceTypes = x.NewVoiceTypes.Count, x.LastScan?.ScannedUtc,
                }).ToList());
            case "revoice.create":
                return await Locked(() =>
                {
                    var proj = new RevoiceProject { Name = S(p, "name", "New revoice").Trim() };
                    RvService().ApplyBaseGame(proj);
                    _lib.Save(proj);
                    lock (_rv) _rv[proj.Id] = proj;
                    return new { id = proj.Id };
                });
            case "revoice.delete":
                return await Locked(() =>
                {
                    if (_jobCts != null) throw new InvalidOperationException("Stop the running job first.");
                    var id = S(p, "id");
                    _lib.Backup("before-delete-revoice", _settings.BackupsToKeep);
                    lock (_rv) _rv.Remove(id);
                    lock (_rvSessions) { if (_rvSessions.Remove(id, out var s)) s.Dispose(); }
                    var dir = _lib.RevoiceFolder(id);
                    if (Directory.Exists(dir)) Directory.Delete(dir, true);
                    return true;
                });
            case "revoice.get": return await Locked(() => RvDto(LoadRv(S(p, "id"))));
            case "revoice.save":
                return await Locked(() =>
                {
                    var proj = LoadRv(S(p, "id"));
                    proj.Name = S(p, "name", proj.Name);
                    proj.OutputFolder = S(p, "outputFolder", proj.OutputFolder);
                    proj.Language = S(p, "language", proj.Language);
                    proj.VoiceTypePlugin = S(p, "voiceTypePlugin", proj.VoiceTypePlugin);
                    if (NB(p, "emotionTags") is { } et) proj.EmotionTags = et;
                    if (NB(p, "referenceByEditorId") is { } re) proj.ReferenceByEditorId = re;
                    if (NB(p, "voiceChangerWarningAccepted") is { } wa) proj.VoiceChangerWarningAccepted = wa;
                    if (NB(p, "voiceChanger") is { } vc)
                    {
                        if (vc && !proj.VoiceChangerWarningAccepted) throw new InvalidOperationException("Read and accept the Voice Changer warning first.");
                        proj.VoiceChanger = vc;
                    }
                    if (NB(p, "useBaseGame") is { } bg && bg != proj.UseBaseGame) { proj.UseBaseGame = bg; RvService().ApplyBaseGame(proj); }
                    if (B(p, "refreshBaseGame")) RvService().ApplyBaseGame(proj);
                    if (p.TryGetProperty("voice", out var vs) && vs.ValueKind == JsonValueKind.Object)
                    {
                        var v = vs.Deserialize<VoicePack>(JsonOptions)!;
                        v.Name = "NPC Revoice"; v.Naming = NamingMode.Legacy;
                        proj.Voice = v;
                    }
                    _lib.Save(proj);
                    return RvDto(proj);
                });
            case "revoice.addFiles":
                return await Locked(() =>
                {
                    var proj = LoadRv(S(p, "id"));
                    var paths = Strings(p, "paths");
                    if (B(p, "dropped")) paths.AddRange(dropped);
                    var added = RvAdd(proj, paths);
                    _lib.Save(proj);
                    return new { added, project = RvDto(proj) };
                });
            case "revoice.plugin":
                return await Locked(() =>
                {
                    var proj = LoadRv(S(p, "id"));
                    var pl = proj.Plugins.FirstOrDefault(x => x.Path == S(p, "path")) ?? throw new InvalidOperationException("Plugin not found.");
                    if (B(p, "remove")) proj.Plugins.Remove(pl);
                    else if (NB(p, "target") is { } t) pl.Target = t;
                    _lib.Save(proj);
                    return RvDto(proj);
                });
            case "revoice.source":
                return await Locked(() =>
                {
                    var proj = LoadRv(S(p, "id"));
                    var src = proj.Sources.FirstOrDefault(x => x.Id == S(p, "sourceId")) ?? throw new InvalidOperationException("Source not found.");
                    if (B(p, "remove")) proj.Sources.Remove(src);
                    else if (p.TryGetProperty("priority", out var pr) && pr.ValueKind == JsonValueKind.Number) src.Priority = pr.GetInt32();
                    _lib.Save(proj);
                    return RvDto(proj);
                });
            case "revoice.scan": return StartRvScan(S(p, "id"));
            case "revoice.speaker":
                return await Locked(() =>
                {
                    var proj = LoadRv(S(p, "id"));
                    foreach (var key in Strings(p, "keys").DefaultIfEmpty(S(p, "key")).Where(k => k.Length > 0))
                    {
                        if (!proj.Speakers.TryGetValue(key, out var a)) proj.Speakers[key] = a = new SpeakerAssignment();
                        if (p.TryGetProperty("voicePackId", out var vp)) a.VoicePackId = vp.ValueKind == JsonValueKind.String && vp.GetString()!.Length > 0 ? vp.GetString() : null;
                        if (p.TryGetProperty("engine", out var en)) a.Engine = en.ValueKind == JsonValueKind.String && en.GetString()!.Length > 0 ? en.GetString() : null;
                        if (p.TryGetProperty("voiceId", out var vi))
                        {
                            a.VoiceId = vi.ValueKind == JsonValueKind.String && vi.GetString()!.Length > 0 ? vi.GetString() : null;
                            a.VoiceName = a.VoiceId == null ? null : S(p, "voiceName", a.VoiceId);
                            if (a.VoiceId != null) a.VoicePackId = null;
                        }
                        if (NB(p, "skip") is { } sk) a.Skip = sk;
                    }
                    _lib.Save(proj);
                    return RvSpeakers(proj);
                });
            case "revoice.line": return await Locked(() => RvLine(p));
            case "revoice.bulk": return await Locked(() => RvBulk(p));
            case "revoice.original":
            {
                var proj = LoadRv(S(p, "id"));
                var line = proj.Lines.FirstOrDefault(l => l.LineId == S(p, "lineId")) ?? throw new InvalidOperationException("Line not found.");
                var session = await Task.Run(() => RvSession(proj));
                var wav = await RevoiceService.OriginalWavAsync(proj, line, session, Tools(), CancellationToken.None);
                return wav == null ? null : "data:audio/wav;base64," + Convert.ToBase64String(wav);
            }
            case "revoice.estimate": return await Locked(() => RvEstimate(p));
            case "revoice.generate": return StartRvGenerate(p);
            case "revoice.export": return StartRvExport(p);
            case "revoice.npcs":
            {
                var proj = LoadRv(S(p, "id"));
                var s = await Task.Run(() => RvSession(proj));
                var q = S(p, "q").Trim();
                var d = s.Dialogue;
                return d.Npcs.Values
                    .Where(n => q.Length == 0 || n.Display.Contains(q, StringComparison.OrdinalIgnoreCase) || n.EditorId.Contains(q, StringComparison.OrdinalIgnoreCase))
                    .Where(n => n.Name.Length > 0)
                    .OrderBy(n => n.Display).Take(60)
                    .Select(n => new { key = n.Key.ToString(), name = n.Display, editorId = n.EditorId, plugin = d.Spelling.GetValueOrDefault(n.Key.Plugin, n.Key.Plugin),
                        voiceType = string.Join(", ", d.VoiceTypesOf(n.Key).Select(d.VoiceTypeFolder)) })
                    .ToList();
            }
            case "revoice.planCreate":
            {
                var proj = LoadRv(S(p, "id"));
                var s = await Task.Run(() => RvSession(proj));
                return await Locked(() =>
                {
                    var d = s.Dialogue;
                    var npc = RevoiceService.ParseKey(S(p, "npc"));
                    var vts = d.VoiceTypesOf(npc);
                    if (vts.Count == 0) throw new InvalidOperationException("This NPC has no voice type.");
                    var n = d.Npcs[npc];
                    var plan = new NewVoiceTypePlan
                    {
                        EditorId = "_" + new string(n.EditorId.Where(char.IsLetterOrDigit).ToArray()) + "Voice",
                        BaseVoiceType = vts.First().ToString(),
                        Npcs = RevoiceService.NpcCopies(d, npc).Select(c => c.Key).ToList(),
                    };
                    plan.Lists = RevoiceService.SuggestLists(d, plan).Where(l => l.ContainsBase).Select(l => l.Key).ToList();
                    proj.NewVoiceTypes.Add(plan);
                    if (string.IsNullOrWhiteSpace(proj.VoiceTypePlugin)) proj.VoiceTypePlugin = proj.Name + " Voices.esp";
                    _lib.Save(proj);
                    return new { planId = plan.Id, project = RvDto(proj) };
                });
            }
            case "revoice.planSave":
                return await Locked(() =>
                {
                    var proj = LoadRv(S(p, "id"));
                    var plan = proj.NewVoiceTypes.FirstOrDefault(x => x.Id == S(p, "planId")) ?? throw new InvalidOperationException("Plan not found.");
                    var ed = S(p, "editorId", plan.EditorId).Trim();
                    if (ed.Length == 0 || ed.Any(c => !(char.IsLetterOrDigit(c) || c == '_'))) throw new InvalidOperationException("Editor IDs use letters, digits and _ only.");
                    plan.EditorId = ed;
                    if (p.TryGetProperty("npcs", out _)) plan.Npcs = Strings(p, "npcs");
                    if (p.TryGetProperty("lists", out _)) plan.Lists = Strings(p, "lists");
                    if (p.TryGetProperty("extraNpcs", out _)) plan.ExtraNpcs = Strings(p, "extraNpcs").Select(x => x.Trim()).Where(x => x.Contains('|')).ToList();
                    plan.LineSet = S(p, "lineSet", plan.LineSet);
                    _lib.Save(proj);
                    return RvDto(proj);
                });
            case "revoice.planDelete":
                return await Locked(() =>
                {
                    var proj = LoadRv(S(p, "id"));
                    var pid = S(p, "planId");
                    proj.NewVoiceTypes.RemoveAll(x => x.Id == pid);
                    proj.Lines.RemoveAll(l => l.PlanId == pid && l.Takes.Count == 0);
                    foreach (var l in proj.Lines.Where(l => l.PlanId == pid)) l.Gone = true;
                    _lib.Save(proj);
                    return RvDto(proj);
                });
            case "revoice.planInfo":
            {
                var proj = LoadRv(S(p, "id"));
                var s = await Task.Run(() => RvSession(proj));
                var plan = proj.NewVoiceTypes.FirstOrDefault(x => x.Id == S(p, "planId")) ?? throw new InvalidOperationException("Plan not found.");
                return await Task.Run(() =>
                {
                    var d = s.Dialogue;
                    var npcKeys = plan.Npcs.Select(RevoiceService.ParseKey).ToList();
                    var first = npcKeys.FirstOrDefault();
                    var copies = npcKeys.Count == 0 ? new List<NpcCopy>() : RevoiceService.NpcCopies(d, first);
                    foreach (var k in npcKeys.Where(k => copies.All(c => c.Key != k.ToString())))
                        if (d.Npcs.TryGetValue(k, out var n)) copies.Add(new NpcCopy(k.ToString(), n.EditorId, n.Display, d.Spelling.GetValueOrDefault(k.Plugin, k.Plugin), string.Join(",", d.VoiceTypesOf(k).Select(d.VoiceTypeFolder))));
                    var baseVt = RevoiceService.ParseKey(plan.BaseVoiceType);
                    var sharing = d.NpcsWithVoiceType(baseVt).Where(n => n.Name.Length > 0).Select(n => n.Display).Distinct().OrderBy(x => x).ToList();
                    return new
                    {
                        plan, baseVoiceType = d.VoiceTypeFolder(baseVt), sharing, copies,
                        lists = RevoiceService.SuggestLists(d, plan),
                        coverage = RevoiceService.Coverage(d, plan, RvService().PluginName(proj)),
                    };
                });
            }
            case "revoice.writePack":
            {
                var proj = LoadRv(S(p, "id"));
                if (string.IsNullOrWhiteSpace(proj.OutputFolder)) throw new InvalidOperationException("Set the output folder first.");
                var s = await Task.Run(() => RvSession(proj));
                return await Locked(() => RvService().WriteVoiceTypePack(proj, s));
            }
            case "revoice.orphans":
            {
                var proj = LoadRv(S(p, "id"));
                var s = await Task.Run(() => RvSession(proj));
                return await Locked(() =>
                {
                    var orphans = RevoiceService.FindOrphans(proj, s);
                    proj.OrphanProposals = RevoiceService.MatchByName(proj, orphans);
                    _lib.Save(proj);
                    return RvOrphansDto(proj);
                });
            }
            case "revoice.orphansWhisper": return StartRvWhisper(S(p, "id"));
            case "revoice.orphanAudio":
            {
                var proj = LoadRv(S(p, "id"));
                var src = proj.Sources.FirstOrDefault(x => x.Id == S(p, "sourceId")) ?? throw new InvalidOperationException("Source not found.");
                var s = await Task.Run(() => RvSession(proj));
                var wav = await RevoiceService.DecodeAsync(s.Files, VoiceFileIndex.Make(src, S(p, "inner")), Tools(), CancellationToken.None);
                return "data:audio/wav;base64," + Convert.ToBase64String(wav);
            }
            case "revoice.orphansApply":
                return await Locked(() =>
                {
                    var proj = LoadRv(S(p, "id"));
                    var approved = new List<OrphanMatch>();
                    if (p.TryGetProperty("approved", out var ap) && ap.ValueKind == JsonValueKind.Array)
                        foreach (var a in ap.EnumerateArray())
                            approved.Add(new OrphanMatch { SourceId = S(a, "sourceId"), Inner = S(a, "inner"), LineId = S(a, "lineId"), Method = S(a, "method", "by hand") });
                    int n = RevoiceService.ApplyOrphanMatches(proj, approved);
                    var done = approved.Select(x => (x.SourceId, x.Inner)).ToHashSet();
                    proj.OrphanProposals.RemoveAll(x => done.Contains((x.SourceId, x.Inner)));
                    _lib.Save(proj);
                    return new { linked = n, project = RvDto(proj) };
                });
            default: return NotHandled;
        }
    }

    // ------------------------------------------------------------------ files and DTOs

    private List<string> RvAdd(RevoiceProject proj, List<string> paths)
    {
        var added = new List<string>();
        int pri = proj.Sources.Where(x => !x.BaseGame).Select(x => x.Priority).DefaultIfEmpty(0).Max() + 1;
        void Source(string kind, string path)
        {
            if (proj.Sources.Any(x => string.Equals(x.Path, path, StringComparison.OrdinalIgnoreCase))) return;
            proj.Sources.Add(new RevoiceSource { Kind = kind, Path = path, Priority = pri++ });
            added.Add(path);
        }
        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (Directory.Exists(path))
            {
                Source("folder", path);
                foreach (var f in Directory.EnumerateFiles(path).Where(f => Path.GetExtension(f).ToLowerInvariant() is ".esp" or ".esm" or ".esl")) AddPlugin(f);
                foreach (var b in Directory.EnumerateFiles(path, "*.bsa")) Source("bsa", b);
                continue;
            }
            if (!File.Exists(path)) continue;
            switch (Path.GetExtension(path).ToLowerInvariant())
            {
                case ".esp" or ".esm" or ".esl":
                    AddPlugin(path);
                    // Its own archives and loose voice files next to it.
                    var dir = Path.GetDirectoryName(path)!;
                    var baseName = Path.GetFileNameWithoutExtension(path);
                    foreach (var b in Directory.EnumerateFiles(dir, "*.bsa").Where(b => Path.GetFileNameWithoutExtension(b).StartsWith(baseName, StringComparison.OrdinalIgnoreCase))) Source("bsa", b);
                    if (Directory.Exists(Path.Combine(dir, "Sound")) || Directory.Exists(Path.Combine(dir, "sound"))) Source("folder", dir);
                    break;
                case ".bsa": Source("bsa", path); break;
                case ".zip": Source("zip", path); break;
                case ".7z": Source("7z", path); break;
            }
        }
        void AddPlugin(string f)
        {
            var name = Path.GetFileName(f);
            var existing = proj.Plugins.FirstOrDefault(x => x.FileName.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (existing != null) { existing.Path = f; existing.BaseGame = false; return; }
            bool isBase = RevoiceService.BaseMasters.Any(m => m.Equals(name, StringComparison.OrdinalIgnoreCase));
            proj.Plugins.Add(new RevoicePlugin { Path = f, FileName = name, Target = !isBase });
            added.Add(f);
        }
        return added;
    }

    private object RvSpeakers(RevoiceProject proj)
    {
        var voices = _lib.LoadVoicePacks().ToDictionary(v => v.Id);
        return proj.Lines.GroupBy(l => l.SpeakerKey).Select(g =>
        {
            proj.Speakers.TryGetValue(g.Key, out var a);
            var vp = a?.VoiceId == null && a?.VoicePackId != null && voices.TryGetValue(a.VoicePackId, out var v) ? v : null;
            return new
            {
                key = g.Key, name = g.First().SpeakerName, voiceType = g.First().VoiceType, lines = g.Count(),
                generated = g.Count(l => l.SelectedTakeId != null), original = g.Count(l => l.Original != null),
                characters = g.Where(l => !l.Excluded).Sum(l => l.Spoken.Length),
                voicePackId = vp?.Id, voicePack = vp?.Name, voiceColor = vp?.Color, skip = a?.Skip ?? false,
                engine = a?.Engine ?? "elevenlabs", voiceId = a?.VoiceId, voiceName = a?.VoiceName ?? vp?.Name,
                plan = g.First().PlanId,
            };
        }).OrderByDescending(x => x.lines).ToList();
    }

    private object RvDto(RevoiceProject proj)
    {
        var s = RvSessionIfOpen(proj);
        return new
        {
            proj.Id, proj.Name, proj.OutputFolder, proj.UseBaseGame, proj.Language, proj.EmotionTags, proj.ReferenceByEditorId,
            proj.VoiceChanger, proj.VoiceChangerWarningAccepted, voice = proj.Voice, voiceTypePlugin = RvService().PluginName(proj),
            plugins = proj.Plugins.Select(x => new { x.Path, x.FileName, x.Target, x.BaseGame, exists = File.Exists(x.Path) }),
            sources = proj.Sources.OrderByDescending(x => x.Priority).Select(x => new { x.Id, x.Kind, x.Path, x.Priority, x.BaseGame, exists = x.Kind == "folder" ? Directory.Exists(x.Path) : File.Exists(x.Path) }),
            baseGameFolder = RevoiceService.DataFolder(_settings.BaseGameFolder),
            proj.LastScan, proj.LastExportUtc, sessionOpen = s != null,
            plans = proj.NewVoiceTypes,
            speakers = RvSpeakers(proj),
            mediaBase = "https://media.dbvo/" + Path.GetRelativePath(_lib.Root, _lib.RevoiceFolder(proj.Id)).Replace('\\', '/') + "/",
            lines = proj.Lines.Select(RvLineDto),
        };
    }

    private static object RvLineDto(RevoiceLine l) => new
    {
        id = l.LineId, l.Info, resp = l.Response, pf = l.PluginFolder, vt = l.VoiceType, stem = l.FileStem, text = l.FinalLine, spoken = l.Spoken,
        l.SpokenEdited, tag = l.TagSuggestion, speaker = l.SpeakerKey, speakerName = l.SpeakerName, l.Npcs, cert = l.Certainty, l.Via,
        emo = ResponseInfo.EmotionNames.ElementAtOrDefault(l.Emotion) ?? "Neutral", emoV = l.EmotionValue, l.Notes, l.Quest, l.Topic, l.Plugin,
        l.Original, excl = l.Excluded, undo = l.UndoSpoken != null, plan = l.PlanId, status = l.Status.ToString(), err = l.LastError, sel = l.SelectedTakeId, l.Marked, l.Gone,
        takes = l.Takes.Select(t => new { t.Id, t.Slot, t.CreatedUtc, t.SpokenText, engine = t.Settings.Engine, model = t.Settings.ModelId, wav = t.WavPath.Replace('\\', '/'), t.DurationSeconds }),
    };

    private object RvLine(JsonElement p)
    {
        var proj = LoadRv(S(p, "id"));
        var l = proj.Lines.FirstOrDefault(x => x.LineId == S(p, "lineId")) ?? throw new InvalidOperationException("Line not found.");
        RvApply(proj, l, S(p, "action"), p);
        _lib.Save(proj);
        return RvLineDto(l);
    }

    private object RvBulk(JsonElement p)
    {
        var proj = LoadRv(S(p, "id"));
        var ids = Strings(p, "lineIds").ToHashSet();
        int n = 0;
        foreach (var l in proj.Lines.Where(x => ids.Contains(x.LineId))) { RvApply(proj, l, S(p, "action"), p); n++; }
        _lib.Save(proj);
        return new { changed = n, lines = proj.Lines.Where(x => ids.Contains(x.LineId)).Select(RvLineDto).ToList() };
    }

    private void RvApply(RevoiceProject proj, RevoiceLine l, string action, JsonElement p)
    {
        switch (action)
        {
            case "spoken":
                l.UndoSpoken = null;
                l.Spoken = S(p, "value");
                l.SpokenEdited = l.Spoken != l.FinalLine;
                if (l.SelectedTake != null && l.Status == Phase2Status.Generated && l.SelectedTake.SpokenText != l.Spoken) l.Status = Phase2Status.Stale;
                break;
            case "resetSpoken": l.UndoSpoken = l.Spoken; l.Spoken = l.FinalLine; l.SpokenEdited = false; break;
            case "undoReset": if (l.UndoSpoken != null) { l.Spoken = l.UndoSpoken; l.SpokenEdited = l.Spoken != l.FinalLine; } l.UndoSpoken = null; break;
            case "acceptTags": if (l.TagSuggestion != null) { l.Spoken = l.TagSuggestion; l.SpokenEdited = true; l.TagSuggestion = null; } break;
            case "rejectTags": l.TagSuggestion = null; break;
            case "exclude": l.Excluded = true; break;
            case "include": l.Excluded = false; break;
            case "mark": l.Marked = !l.Marked; break;
            case "markOn": l.Marked = true; break;
            case "markOff": l.Marked = false; break;
            case "selectTake":
                if (l.Takes.Any(t => t.Id == S(p, "value"))) { l.SelectedTakeId = S(p, "value"); l.Status = Phase2Status.Generated; }
                break;
            case "deleteTake":
            {
                var t = l.Takes.FirstOrDefault(x => x.Id == S(p, "value"));
                if (t == null) break;
                var root = _lib.RevoiceFolder(proj.Id);
                try { File.Delete(Path.Combine(root, t.WavPath)); File.Delete(Path.Combine(root, t.LipPath)); } catch { }
                l.Takes.Remove(t);
                if (l.SelectedTakeId == t.Id) l.SelectedTakeId = l.Takes.LastOrDefault()?.Id;
                l.Status = l.SelectedTakeId != null ? Phase2Status.Generated : Phase2Status.Pending;
                break;
            }
            case "keepUsedTake":
            {
                var root = _lib.RevoiceFolder(proj.Id);
                foreach (var t in l.Takes.Where(t => t.Id != l.SelectedTakeId).ToList())
                {
                    try { File.Delete(Path.Combine(root, t.WavPath)); File.Delete(Path.Combine(root, t.LipPath)); } catch { }
                    l.Takes.Remove(t);
                }
                break;
            }
        }
    }

    // ------------------------------------------------------------------ jobs

    private List<RevoiceLine> RvSelect(RevoiceProject proj, JsonElement p)
    {
        var mode = S(p, "mode", "missing");
        var ids = Strings(p, "lineIds").ToHashSet();
        bool Usable(RevoiceLine l) => !l.Excluded && !l.Gone && !(proj.Speakers.TryGetValue(l.SpeakerKey, out var a) && a.Skip) && (!proj.VoiceChanger || l.Original != null);
        return mode switch
        {
            "selected" => proj.Lines.Where(l => ids.Contains(l.LineId) && Usable(l)).ToList(),
            "marked" => proj.Lines.Where(l => l.Marked && Usable(l)).ToList(),
            "all" => proj.Lines.Where(Usable).ToList(),
            _ => proj.Lines.Where(l => Usable(l) && l.Status is Phase2Status.Pending or Phase2Status.Failed or Phase2Status.Stale).ToList(),
        };
    }

    private object RvEstimate(JsonElement p)
    {
        var proj = LoadRv(S(p, "id"));
        var lines = RvSelect(proj, p);
        var voices = _lib.LoadVoicePacks().ToDictionary(v => v.Id);
        var resolved = lines.Select(l => (Line: l, Voice: RvVoiceFor(proj, l, voices))).ToList();
        var unassigned = resolved.Where(x => x.Voice == null).Select(x => x.Line.SpeakerName).Distinct().ToList();
        var groups = resolved.Where(x => x.Voice != null).GroupBy(x => x.Voice!.Id)
            .Select(g => { var v = g.First().Voice!; return new { voicePack = v.Name, color = v.Color, engine = proj.VoiceChanger ? "elevenlabs speech-to-speech" : v.Engine, lines = g.Count(), characters = g.Sum(x => x.Line.Spoken.Length) }; })
            .ToList();
        return new { count = groups.Sum(g => g.lines), characters = groups.Sum(g => g.characters), groups, unassigned, voiceChanger = proj.VoiceChanger };
    }

    private object StartRvScan(string id)
    {
        if (_jobCts != null) throw new InvalidOperationException("A job is already running.");
        _jobCts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            try
            {
                var proj = LoadRv(id);
                Emit("progress", new { kind = "revoice", done = 0, total = 1, failed = 0, message = "Reading plugins" });
                var prog = new Progress<string>(msg => Emit("progress", new { kind = "revoice", done = 0, total = 1, failed = 0, message = msg }));
                var s = RvSession(proj, prog);
                RevoiceScanReport rep = null!;
                await _gate.WaitAsync();
                try
                {
                    rep = RvService().Scan(proj, s);
                    _lib.Save(proj);
                }
                finally { _gate.Release(); }
                Emit("jobDone", new { kind = "revoice-scan", id, ok = true, report = rep });
            }
            catch (Exception ex)
            {
                App.Log("Revoice scan failed: " + ex);
                Emit("jobDone", new { kind = "revoice-scan", id, ok = false, message = ex.Message });
            }
            finally { _jobCts = null; }
        });
        return true;
    }

    private object RvOrphansDto(RevoiceProject proj) => new
    {
        proposals = proj.OrphanProposals,
        silent = proj.Lines.Where(l => l.Original == null && l.PlanId == null && !l.Gone).Select(l => new { id = l.LineId, text = l.FinalLine, vt = l.VoiceType, stem = l.FileStem, l.SpeakerName }).ToList(),
    };

    /// <summary>Transcribe orphaned files that found no line by name, with the WAV to LIP Whisper settings, and match them to silent lines.</summary>
    private object StartRvWhisper(string id)
    {
        if (_jobCts != null) throw new InvalidOperationException("A job is already running.");
        _jobCts = new CancellationTokenSource();
        var ct = _jobCts.Token;
        _ = Task.Run(async () =>
        {
            var temp = new List<string>();
            try
            {
                var proj = LoadRv(id);
                var s = RvSession(proj);
                var orphans = RevoiceService.FindOrphans(proj, s);
                var named = proj.OrphanProposals.Where(x => x.LineId != null).Select(x => (x.SourceId, x.Inner)).ToHashSet();
                var todo = orphans.Where(o => !named.Contains((o.Source.Id, o.Inner))).ToList();
                var tool = new WavToLip(_settings.WavToLip, Tools(), WhisperModelsFolder(), e => { if (e.Kind == "log") App.Log("Whisper: " + e.Text); });
                var (transcribe, engine) = await tool.CreateTranscriberAsync(ct);
                var items = new List<(VoiceFile, string)>();
                await using (engine)
                {
                    var work = Path.Combine(Path.GetTempPath(), "dbvo", "rv-orphans");
                    Directory.CreateDirectory(work);
                    for (int i = 0; i < todo.Count; i++)
                    {
                        ct.ThrowIfCancellationRequested();
                        Emit("progress", new { kind = "wavlip", done = i, total = todo.Count, failed = 0, current = todo[i].Inner, message = "Transcribing orphaned files" });
                        try
                        {
                            var wav = await RevoiceService.DecodeAsync(s.Files, todo[i], Tools(), ct);
                            var path = Path.Combine(work, $"o{i:00000}.wav");
                            await File.WriteAllBytesAsync(path, wav, ct);
                            temp.Add(path);
                            items.Add((todo[i], (await transcribe(path, ct)).Trim()));
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex) { App.Log($"Orphan {todo[i].Inner}: {ex.Message}"); }
                    }
                }
                await _gate.WaitAsync();
                try
                {
                    var matched = RevoiceService.MatchByTranscript(proj, items);
                    var keys = matched.Select(m => (m.SourceId, m.Inner)).ToHashSet();
                    proj.OrphanProposals.RemoveAll(x => keys.Contains((x.SourceId, x.Inner)));
                    proj.OrphanProposals.AddRange(matched);
                    _lib.Save(proj);
                }
                finally { _gate.Release(); }
                Emit("jobDone", new { kind = "revoice-orphans", id, ok = true, message = $"{items.Count} file(s) transcribed." });
            }
            catch (OperationCanceledException) { Emit("jobDone", new { kind = "revoice-orphans", id, ok = false, message = "Cancelled." }); }
            catch (Exception ex)
            {
                App.Log("Orphan transcription failed: " + ex);
                Emit("jobDone", new { kind = "revoice-orphans", id, ok = false, message = ex.Message });
            }
            finally
            {
                foreach (var t in temp) { try { File.Delete(t); } catch { } }
                _jobCts = null;
            }
        });
        return true;
    }

    /// <summary>The voice a line is spoken with: the speaker's engine + voice on top of the project's voice settings (or an older project's voice pack).</summary>
    private static VoicePack? RvVoiceFor(RevoiceProject proj, RevoiceLine l, Dictionary<string, VoicePack> voicePacks)
    {
        if (!proj.Speakers.TryGetValue(l.SpeakerKey, out var a)) return null;
        if (a.VoiceId != null)
        {
            var engine = a.Engine ?? "elevenlabs";
            if (proj.VoiceChanger && engine != "elevenlabs") return null;
            var v = JsonSerializer.Deserialize<VoicePack>(JsonSerializer.Serialize(proj.Voice, Library.Json), Library.Json)!;
            v.Id = "rv-" + engine + "-" + a.VoiceId;
            v.Name = a.VoiceName ?? a.VoiceId;
            v.Engine = engine;
            v.SettingsLocked = false;
            switch (engine)
            {
                case "voxcpm": v.VoxCpm.Voice = a.VoiceId; break;
                case "omnivoice": v.OmniVoice.Voice = a.VoiceId; break;
                default:
                    v.Eleven.VoiceId = a.VoiceId; v.Eleven.VoiceName = v.Name;
                    v.Sts.VoiceId = a.VoiceId; v.Sts.VoiceName = v.Name;
                    break;
            }
            return v;
        }
        return a.VoicePackId != null && voicePacks.TryGetValue(a.VoicePackId, out var vp) ? vp : null;
    }

    private object StartRvGenerate(JsonElement p)
    {
        if (_jobCts != null) throw new InvalidOperationException("A job is already running.");
        var id = S(p, "id");
        var proj = LoadRv(id);
        var all = RvSelect(proj, p);
        var voicePacks = _lib.LoadVoicePacks().ToDictionary(v => v.Id);
        var voices = new Dictionary<string, VoicePack>();
        var groups = all.Select(l => (Line: l, Voice: RvVoiceFor(proj, l, voicePacks))).Where(x => x.Voice != null)
            .GroupBy(x => { voices[x.Voice!.Id] = x.Voice!; return x.Voice!.Id; }, x => x.Line).ToList();
        if (groups.Count == 0) throw new InvalidOperationException("Pick a voice for the speakers first (Speakers tab).");
        _jobCts = new CancellationTokenSource();
        var ct = _jobCts.Token;
        _ = Task.Run(async () =>
        {
            var temp = new List<string>();
            try
            {
                _lib.Backup("before-revoice-" + proj.Name, _settings.BackupsToKeep);
                var svc = new Phase2Service(_lib, Runtime());
                int total = groups.Sum(g => g.Count()), offset = 0;
                var session = proj.VoiceChanger ? RvSession(proj) : null;
                foreach (var g in groups)
                {
                    var voice = voices[g.Key];
                    var lines = g.Cast<Phase2Line>().ToList();
                    if (session != null)
                    {
                        var work = Path.Combine(Path.GetTempPath(), "dbvo", "rv-src");
                        Directory.CreateDirectory(work);
                        foreach (var l in g)
                        {
                            var wav = await RevoiceService.OriginalWavAsync(proj, l, session, Tools(), ct);
                            if (wav == null) { l.SourceWav = null; continue; }
                            var path = Path.Combine(work, l.LineId + ".wav");
                            await File.WriteAllBytesAsync(path, wav, ct);
                            l.SourceWav = path;
                            temp.Add(path);
                        }
                    }
                    var shim = new DbvoPatch { Id = proj.Id, Name = proj.Name, VoicePackId = voice.Id, Kind = proj.VoiceChanger ? "vc" : "tts" };
                    int baseDone = offset;
                    var progress = new Progress<Core.Projects.GenerationProgress>(x => Emit("progress", new { kind = "generate", done = baseDone + x.Done, total, x.Failed, x.Current, message = (string?)null }));
                    await svc.GenerateAsync(shim, voice, lines, Tools(), progress,
                        l => { Emit("revoiceLine", new { projectId = proj.Id, line = RvLineDto((RevoiceLine)l) }); return Task.CompletedTask; },
                        ct, _gate, proj.VoiceChanger ? "sts" : null, _lib.RevoiceFolder(proj.Id), () => _lib.Save(proj));
                    offset += lines.Count;
                }
                Emit("jobDone", new { kind = "generate", revoiceId = proj.Id, ok = true });
            }
            catch (OperationCanceledException) { Emit("jobDone", new { kind = "generate", revoiceId = id, ok = false, message = "Cancelled." }); }
            catch (Exception ex)
            {
                App.Log("Revoice generate failed: " + ex);
                Emit("jobDone", new { kind = "generate", revoiceId = id, ok = false, message = ex.Message });
            }
            finally
            {
                foreach (var t in temp) { try { File.Delete(t); } catch { } }
                foreach (var l in proj.Lines) l.SourceWav = null;
                _jobCts = null;
            }
        });
        return true;
    }

    private object StartRvExport(JsonElement p)
    {
        if (_jobCts != null) throw new InvalidOperationException("A job is already running.");
        var id = S(p, "id");
        var proj = LoadRv(id);
        bool overwrite = !p.TryGetProperty("overwrite", out var ow) || ow.ValueKind != JsonValueKind.False;
        _jobCts = new CancellationTokenSource();
        var ct = _jobCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                var session = proj.NewVoiceTypes.Count > 0 ? RvSession(proj) : null;
                var progress = new Progress<Core.Projects.GenerationProgress>(x => Emit("progress", new { kind = "fuz", x.Done, x.Total, x.Failed, x.Current, message = "Writing .fuz files" }));
                var rep = await RvService().ExportAsync(proj, Tools(), overwrite, progress, ct, session);
                await _gate.WaitAsync();
                try { _lib.Save(proj); } finally { _gate.Release(); }
                Emit("jobDone", new { kind = "revoice-export", id, ok = rep.Failed == 0, report = rep, folder = proj.OutputFolder });
            }
            catch (OperationCanceledException) { Emit("jobDone", new { kind = "revoice-export", id, ok = false, message = "Cancelled." }); }
            catch (Exception ex)
            {
                App.Log("Revoice export failed: " + ex);
                Emit("jobDone", new { kind = "revoice-export", id, ok = false, message = ex.Message });
            }
            finally { _jobCts = null; }
        });
        return true;
    }
}
