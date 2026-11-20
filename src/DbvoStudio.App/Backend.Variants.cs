using System.IO;
using System.Text.Json;
using DbvoStudio.Core.ElevenLabs;
using DbvoStudio.Core.Fomod;
using DbvoStudio.Core.Naming;
using DbvoStudio.Core.Projects;
using DbvoStudio.Core.Text;
using DbvoStudio.Core.Variants;

namespace DbvoStudio.App;

/// <summary>
/// 12.0.0: Phase 2 → Variants. One view per voice pack: which lines play a random take in game, making more of them with
/// the Variant settings (prefix tags, stability, speed, seed, context), lines added by hand, the Variants Pack (one BSA)
/// and its pre-ticked FOMOD option. The prefix and rows only ever apply to variant takes made here.
/// </summary>
public sealed partial class Backend
{
    private readonly Dictionary<string, VariantsConfig> _variants = new();

    /// <summary>Mod names by ID, from the list rows the app keeps anyway (no mod file is read).</summary>
    private Dictionary<string, string> ModNames()
    {
        var (mods, _) = ListRowsFor(_lib.LoadVoicePacks());
        var d = new Dictionary<string, string>();
        foreach (var m in mods)
            if (m.TryGetProperty("id", out var id) && m.TryGetProperty("name", out var n)) d[id.GetString() ?? ""] = n.GetString() ?? "";
        return d;
    }

    private VariantsConfig LoadVariantsCfg(string voiceId)
    {
        lock (_variants)
        {
            if (_variants.TryGetValue(voiceId, out var c)) return c;
            c = _lib.LoadVariants(voiceId);
            foreach (var l in c.AddedLines.Where(l => l.Status == Phase2Status.Generating)) l.Status = l.Takes.Count > 0 ? Phase2Status.Generated : Phase2Status.Pending;
            _variants[voiceId] = c;
            return c;
        }
    }

    private void SaveVariantsCfg(VariantsConfig c)
    {
        c.UpdatedUtc = DateTime.UtcNow;
        _lib.Save(c);
    }

    /// <summary>One line of a patch, as much as the Variants list needs (patch files run to tens of MB each).</summary>
    private sealed record VEntry(string PatchId, string PatchName, string LineId, string Key, string Text, string Stem, string Words, string[] ModIds, int Takes, int Kept, int Pending, bool Exported);
    /// <summary>Per patch file (date + size + naming): its voice pack and lines, so the list never reloads an unchanged patch.</summary>
    private readonly Dictionary<string, (DateTime Write, long Len, string Naming, string VoiceId, List<VEntry> Entries)> _vIndex = new();

    private static List<VEntry> EntriesOf(string patchId, string patchName, IEnumerable<Phase2Line> lines, VoicePack v) =>
        lines.Where(l => !string.IsNullOrWhiteSpace(l.FinalLine)).Select(l =>
        {
            var stem = l.FileStem ?? LineNaming.Stem(l.FinalLine, v.Naming);
            // Omitted (a reference covers it) or excluded: the line is never exported, so its takes don't count (VariantGroup.Exported).
            bool ex = !l.IsOmitted && !l.Excluded;
            return new VEntry(patchId, patchName, l.LineId, v.Naming == NamingMode.Legacy ? stem.ToUpperInvariant() : stem, l.FinalLine, stem,
                SpokenText.StripTags(l.Spoken), l.Sources.Select(s => s.ModId).ToArray(),
                ex ? l.Takes.Count : 0, ex ? l.Takes.Count(t => t.Kept) : 0, ex ? l.Takes.Count(t => t.VariantPending && !t.Kept) : 0, ex);
        }).ToList();

    /// <summary>
    /// Every line of the voice pack's patches. Patches open in the app are read as they are; the rest from an index kept
    /// per file, refreshed only when a file changed. Nothing here adds patches to the app's cache.
    /// </summary>
    private List<VEntry> VariantEntries(VoicePack v)
    {
        var all = new List<VEntry>();
        // Which patches are this voice pack's: from the list rows the app keeps anyway, so other voice packs' files are never read.
        var (_, rows) = ListRowsFor(_lib.LoadVoicePacks());
        var mine = rows.Where(r => r.TryGetProperty("voicePackId", out var vp) && vp.GetString() == v.Id && r.TryGetProperty("id", out _))
            .Select(r => r.GetProperty("id").GetString() ?? "").ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in Directory.EnumerateDirectories(Path.Combine(_lib.Root, "patches")))
        {
            var id = Path.GetFileName(dir);
            if (!mine.Contains(id)) continue;
            DbvoPatch? cached;
            lock (_patches) _patches.TryGetValue(id, out cached);
            if (cached != null)
            {
                if (cached.VoicePackId == v.Id) all.AddRange(EntriesOf(cached.Id, cached.Name, cached.Lines, v));
                continue;
            }
            var file = Path.Combine(dir, "patch.json");
            if (!File.Exists(file)) continue;
            var fi = new FileInfo(file);
            var naming = v.Naming.ToString();
            lock (_vIndex)
            {
                if (_vIndex.TryGetValue(id, out var ix) && ix.Write == fi.LastWriteTimeUtc && ix.Len == fi.Length && (ix.VoiceId != v.Id || ix.Naming == naming))
                {
                    if (ix.VoiceId == v.Id) all.AddRange(ix.Entries);
                    continue;
                }
            }
            DbvoPatch? p;
            try { p = _lib.LoadPatch(id); } catch { continue; }
            if (p == null) continue;
            var entries = p.VoicePackId == v.Id ? EntriesOf(p.Id, p.Name, p.Lines, v) : new List<VEntry>();
            lock (_vIndex) _vIndex[id] = (fi.LastWriteTimeUtc, fi.Length, naming, p.VoicePackId, entries);
            all.AddRange(entries);
        }
        return all;
    }

    /// <summary>The patches (the app's own objects) that have any of these lines: only those are loaded.</summary>
    private List<DbvoPatch> PatchesWith(VoicePack v, IEnumerable<string> keys)
    {
        var set = keys.ToHashSet(StringComparer.Ordinal);
        return VariantEntries(v).Where(e => set.Contains(e.Key)).Select(e => e.PatchId).Distinct().Select(LoadPatch).ToList();
    }

    /// <summary>Whether the prefix goes as context when the settings don't say: the app's default for ElevenLabs.</summary>
    private bool DefaultContext(VoicePack v) => _settings.LeadingTagsAsContext && v.Engine == "elevenlabs";

    private async Task<object?> DispatchVariants(string m, JsonElement p)
    {
        switch (m)
        {
            case "variants.get":
                {
                    object? dto = await Locked(() => VariantsDto(S(p, "voiceId")));
                    var voice = LoadVoice(S(p, "voiceId"));
                    double? rate = voice.Engine == "elevenlabs" && !string.IsNullOrWhiteSpace(Secrets.Unprotect(_settings.ElevenLabsApiKey)) ? await CreditRate(voice.Eleven.ModelId) : null;
                    return new { data = dto, creditRate = rate };
                }
            case "variants.line": return await Locked(() => VariantLineDto(S(p, "voiceId"), S(p, "key")));
            case "variants.saveSettings": return await Locked(() => SaveVariantSettings(p));
            case "variants.keep": return await Locked(() => KeepVariant(p));
            case "variants.checkLine": return await Locked(() => CheckTypedLine(S(p, "voiceId"), S(p, "text")));
            case "variants.search": return await Locked(() => SearchLines(S(p, "voiceId"), S(p, "q")));
            case "variants.addLine": return await Locked(() => AddVariantLine(p));
            case "variants.removeLine": return await Locked(() => RemoveVariantLine(S(p, "voiceId"), S(p, "key")));
            case "variants.generate": return StartVariants(p);
            case "variants.export": return StartVariantsExport(p);
            case "variants.fomods": return await Locked(() => _lib.LoadFomods().Where(f => f.VoicePackId == S(p, "voiceId")).Select(f => new { f.Id, f.Name }).ToList());
            case "variants.addToFomod": return await Locked(() => AddVariantsToFomod(S(p, "voiceId"), S(p, "fomodId")));
            default: return NotHandled;
        }
    }

    private object VariantsDto(string voiceId)
    {
        var voice = LoadVoice(voiceId);
        var cfg = LoadVariantsCfg(voiceId);
        var entries = VariantEntries(voice).Concat(EntriesOf("", "Added here", cfg.AddedLines, voice)).ToList();
        var lines = entries.GroupBy(e => e.Key).Where(g => g.Any(e => e.Exported)).Select(g =>
        {
            // the line new variants go to: the exported one with the most ★ already, else the first patch by name (as VariantGroup.Home)
            var home = g.Where(e => e.Exported).OrderByDescending(e => e.Kept).ThenBy(e => e.PatchId.Length == 0 ? 1 : 0).ThenBy(e => e.PatchName, StringComparer.OrdinalIgnoreCase).First();
            return new
            {
                key = g.Key,
                text = home.Text,
                stem = home.Stem,
                // the words as you voiced them (no tags): what a variant says after its prefix
                words = home.Words,
                mods = g.SelectMany(e => e.ModIds).Distinct().Count(),
                patches = g.Where(e => e.PatchId.Length > 0 && e.Exported).Select(e => e.PatchId).Distinct().Count(),
                takes = g.Sum(e => e.Takes),
                kept = g.Sum(e => e.Kept),
                pending = g.Sum(e => e.Pending),
                added = g.All(e => e.PatchId.Length == 0),
            };
        }).ToList();
        bool ctx = cfg.TagsAsContext ?? DefaultContext(voice);
        return new
        {
            voice = new { voice.Id, voice.Name, voice.Color, naming = voice.Naming.ToString(), voice.PackId, voice.Engine, modelId = voice.Eleven.ModelId,
                discrete = ModelCapabilities.HasDiscreteStability(voice.Eleven.ModelId), releaseFolder = voice.Destinations.ReleaseFolder },
            settings = new
            {
                prefix = cfg.Prefix,
                tagsAsContext = ctx,
                contextIsDefault = cfg.TagsAsContext == null,
                appContextDefault = DefaultContext(voice),
                preset = cfg.Preset,
                rows = cfg.Rows,
                presets = VariantsConfig.BuiltInPresets().Select(x => new { x.Name, x.Rows, builtIn = true })
                    .Concat(cfg.Presets.Select(x => new { x.Name, x.Rows, builtIn = false })).ToList(),
                packName = cfg.PackNameFor(voice),
                outputFolder = cfg.OutputFolderFor(voice),
                lastExport = cfg.LastExportUtc,
            },
            lines,
        };
    }

    private object VariantLineDto(string voiceId, string key)
    {
        var voice = LoadVoice(voiceId);
        var cfg = LoadVariantsCfg(voiceId);
        var g = VariantsService.Groups(voice, PatchesWith(voice, new[] { key }), cfg).FirstOrDefault(x => x.Key == key)
            ?? throw new InvalidOperationException("That line isn't in this voice pack any more.");
        var mods = ModNames();
        var builder = new VariantsPackBuilder(_lib);
        var home = g.Home;
        return new
        {
            key = g.Key,
            text = g.Text,
            stem = g.Stem,
            spoken = home.Line.Spoken,
            added = g.Added,
            modNames = g.Members.SelectMany(x => x.Line.Sources.Select(s => mods.TryGetValue(s.ModId, out var n) ? n : "")).Where(n => n.Length > 0).Distinct().OrderBy(n => n).ToList(),
            patchNames = g.Exported.Where(x => x.Patch != null).Select(x => x.Patch!.Name).Distinct().ToList(),
            takes = g.Exported.SelectMany(x => x.Line.Takes.Select(t => new
            {
                patchId = x.Patch?.Id,
                patch = x.Patch?.Name ?? "Added here",
                lineId = x.Line.LineId,
                takeId = t.Id,
                t.Slot,
                duration = t.DurationSeconds,
                model = t.Settings.ModelId,
                engine = t.Settings.Engine,
                stability = t.Settings.Stability,
                speed = t.Settings.Speed,
                seed = t.Settings.Seed,
                spoken = t.SpokenText,
                kept = t.Kept,
                pending = t.VariantPending && !t.Kept,
                inUse = x.Line.SelectedTakeId == t.Id,
                qaFlags = t.Qa?.Flags,
                url = MediaUrl(Path.Combine(builder.RootOf(voice, x.Patch), t.WavPath)),
                created = t.CreatedUtc,
            })).OrderBy(t => t.created).ToList(),
        };
    }

    private object SaveVariantSettings(JsonElement p)
    {
        var voice = LoadVoice(S(p, "voiceId"));
        var cfg = LoadVariantsCfg(voice.Id);
        if (p.TryGetProperty("prefix", out var pre) && pre.ValueKind == JsonValueKind.String) cfg.Prefix = pre.GetString()!.Trim();
        if (p.TryGetProperty("tagsAsContext", out var tc)) cfg.TagsAsContext = tc.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null };
        if (p.TryGetProperty("preset", out var ps) && ps.ValueKind == JsonValueKind.String) cfg.Preset = ps.GetString()!;
        if (p.TryGetProperty("rows", out var rows) && rows.ValueKind == JsonValueKind.Array)
            cfg.Rows = ReadRows(rows);
        if (p.TryGetProperty("packName", out var pn) && pn.ValueKind == JsonValueKind.String) cfg.PackName = pn.GetString()!.Trim();
        if (p.TryGetProperty("outputFolder", out var of) && of.ValueKind == JsonValueKind.String) cfg.OutputFolder = of.GetString()!.Trim();
        if (p.TryGetProperty("savePreset", out var sp) && sp.ValueKind == JsonValueKind.String && sp.GetString()!.Trim().Length > 0)
        {
            var name = sp.GetString()!.Trim();
            if (VariantsConfig.BuiltInPresets().Any(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"\"{name}\" is a built-in preset. Pick another name.");
            cfg.Presets.RemoveAll(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            cfg.Presets.Add(new VariantPreset { Name = name, Rows = cfg.Rows.Select(r => r.Clone()).ToList() });
            cfg.Preset = name;
        }
        if (p.TryGetProperty("deletePreset", out var dp) && dp.ValueKind == JsonValueKind.String)
            cfg.Presets.RemoveAll(x => x.Name.Equals(dp.GetString(), StringComparison.OrdinalIgnoreCase));
        SaveVariantsCfg(cfg);
        return true;
    }

    private static List<VariantRow> ReadRows(JsonElement rows)
    {
        var list = new List<VariantRow>();
        foreach (var r in rows.EnumerateArray())
        {
            double Num(string n, double d) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : d;
            var row = new VariantRow
            {
                Stability = Math.Clamp(Num("stability", 0.5), 0, 1),
                Speed = Math.Round(Math.Clamp(Num("speed", 1.0), 0.7, 1.2), 2),
                Word = r.TryGetProperty("word", out var w) && w.ValueKind == JsonValueKind.String ? w.GetString()!.Trim().Trim('[', ']').Trim() : "",
                FixedSeed = r.TryGetProperty("fixedSeed", out var f) && f.ValueKind == JsonValueKind.True,
            };
            if (r.TryGetProperty("seed", out var s) && s.ValueKind == JsonValueKind.Number && s.TryGetInt64(out var seed)) row.Seed = Math.Clamp(seed, 0, VariantsService.MaxSeed);
            if (row.FixedSeed && row.Seed == null) row.Seed = VariantsService.NewSeed();
            list.Add(row);
        }
        if (list.Count == 0) throw new InvalidOperationException("Keep at least one variant row.");
        if (list.Count > 10) throw new InvalidOperationException("Ten variants per line at most.");
        return list;
    }

    /// <summary>Star or un-star a take as a variant. Listening to a pending (Creative) take and starring it clears its wait.</summary>
    private object KeepVariant(JsonElement p)
    {
        var voiceId = S(p, "voiceId");
        var patchId = S(p, "patchId");
        var cfg = LoadVariantsCfg(voiceId);
        var lines = patchId.Length > 0 ? LoadPatch(patchId).Lines : cfg.AddedLines;
        var line = lines.FirstOrDefault(l => l.LineId == S(p, "lineId")) ?? throw new InvalidOperationException("Line not found.");
        var take = line.Takes.FirstOrDefault(t => t.Id == S(p, "takeId")) ?? throw new InvalidOperationException("Take not found.");
        take.Kept = B(p, "kept");
        take.VariantPending = false;
        if (patchId.Length > 0) _lib.Save(LoadPatch(patchId)); else SaveVariantsCfg(cfg);
        return true;
    }

    private object CheckTypedLine(string voiceId, string text)
    {
        var voice = LoadVoice(voiceId);
        var t = text.Trim();
        if (t.Length == 0) return new { ok = false, problems = new List<string>(), stem = "", files = Array.Empty<string>(), existing = new List<string>() };
        var problems = VariantsService.CheckTypedLine(text);
        if (voice.Naming == NamingMode.Dbrev) problems.Insert(0, "DBReV packs find lines by their ID, not their text, so a typed line can't be added to them.");
        var stem = LineNaming.Stem(t, voice.Naming);
        var key = voice.Naming == NamingMode.Legacy ? stem.ToUpperInvariant() : stem;
        var g = VariantsService.Groups(voice, PatchesWith(voice, new[] { key }), LoadVariantsCfg(voiceId)).FirstOrDefault(x => x.Key == key);
        return new
        {
            ok = problems.Count == 0,
            problems,
            stem,
            files = new[] { stem + ".fuz", stem + @"\1.fuz …", voice.Naming == NamingMode.Legacy ? stem + "(1).fuz …" : "" }.Where(x => x.Length > 0).ToArray(),
            existing = g == null ? new List<string>() : g.Exported.Select(x => x.Patch?.Name ?? "Added here").Distinct().ToList(),
            key = g?.Key,
        };
    }

    private List<(string Text, string Where)>? _lineIndex;
    private DateTime _lineIndexAt;

    /// <summary>Lines from your Phase 1 mods (all voice packs share them) that contain the typed words.</summary>
    private object SearchLines(string voiceId, string q)
    {
        q = q.Trim();
        if (q.Length < 3) return new List<object>();
        if (_lineIndex == null || DateTime.UtcNow - _lineIndexAt > TimeSpan.FromMinutes(5))
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var idx = new List<(string, string)>();
            foreach (var mod in _lib.LoadMods())
                foreach (var pl in mod.Plugins)
                    foreach (var l in pl.Lines)
                    {
                        var text = l.Final;
                        if (string.IsNullOrWhiteSpace(text) || !seen.Add(text)) continue;
                        idx.Add((text, mod.Name));
                    }
            _lineIndex = idx;
            _lineIndexAt = DateTime.UtcNow;
        }
        return _lineIndex.Where(x => x.Text.Contains(q, StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.Text.StartsWith(q, StringComparison.OrdinalIgnoreCase) ? 0 : 1).ThenBy(x => x.Text.Length)
            .Take(12).Select(x => new { text = x.Text, where = x.Where }).ToList();
    }

    /// <summary>A typed line: added here when no patch has it; a line the voice pack already has is simply selected.</summary>
    private object AddVariantLine(JsonElement p)
    {
        var voice = LoadVoice(S(p, "voiceId"));
        if (voice.Naming == NamingMode.Dbrev) throw new InvalidOperationException("DBReV packs find lines by their ID, not their text, so a typed line can't be added to them.");
        var text = S(p, "text").Trim();
        var problems = VariantsService.CheckTypedLine(text);
        if (problems.Count > 0) throw new InvalidOperationException(string.Join(" ", problems));
        var cfg = LoadVariantsCfg(voice.Id);
        var stem = LineNaming.Stem(text, voice.Naming);
        var key = voice.Naming == NamingMode.Legacy ? stem.ToUpperInvariant() : stem;
        var g = VariantsService.Groups(voice, PatchesWith(voice, new[] { key }), cfg).FirstOrDefault(x => x.Key == key);
        if (g != null) return new { key = g.Key, existed = true };
        var line = VariantsService.NewAddedLine(voice, text, S(p, "spoken"));
        cfg.AddedLines.Add(line);
        SaveVariantsCfg(cfg);
        return new { key = line.Key, existed = false };
    }

    private object RemoveVariantLine(string voiceId, string key)
    {
        if (_jobCts != null && _generating == "variants:" + voiceId) throw new InvalidOperationException("Variants are being made right now. Wait for that to finish first.");
        var cfg = LoadVariantsCfg(voiceId);
        var line = cfg.AddedLines.FirstOrDefault(l => l.Key == key) ?? throw new InvalidOperationException("Only lines you added here can be removed here.");
        cfg.AddedLines.Remove(line);
        SaveVariantsCfg(cfg);
        try { Directory.Delete(Path.Combine(_lib.VariantsFolder(voiceId), "takes", line.LineId), true); } catch (DirectoryNotFoundException) { }
        return true;
    }

    /// <summary>
    /// Make variant takes for the ticked lines: each row of the Variant settings once per line, one row after another.
    /// Lines in several patches get them in one (the one with the most ★ already), so they share them. A line without any ★
    /// yet gets its take in use starred first, so the take it has stays in the rotation.
    /// </summary>
    private object StartVariants(JsonElement p)
    {
        var voiceId = S(p, "voiceId");
        var keys = Strings(p, "keys").ToHashSet(StringComparer.Ordinal);
        if (keys.Count == 0) throw new InvalidOperationException("Tick one or more lines first.");
        int? oneRow = p.TryGetProperty("row", out var ri) && ri.ValueKind == JsonValueKind.Number ? ri.GetInt32() : null;
        _jobCts = NewJob("variants:" + voiceId);
        var ct = _jobCts.Token;
        _ = Task.Run(async () =>
        {
            const string kind = "variants";
            try
            {
                var voice = LoadVoice(voiceId);
                VariantsConfig cfg;
                List<VariantGroup> groups;
                List<VariantRow> rows;
                bool ctx;
                await _gate.WaitAsync();
                try
                {
                    cfg = LoadVariantsCfg(voiceId);
                    rows = cfg.Rows.Select(r => r.Clone()).ToList();
                    // "+ One more take": one row only.
                    if (oneRow is int i && i >= 0 && i < rows.Count) rows = new() { rows[i] };
                    ctx = cfg.TagsAsContext ?? DefaultContext(voice);
                    groups = VariantsService.Groups(voice, PatchesWith(voice, keys), cfg).Where(g => keys.Contains(g.Key)).ToList();
                    // The take in use gets a ★ first (when the line has none), so it stays in the rotation. With DBVO 2.0 names
                    // this matters for the patch's own export too: one ★ take would otherwise replace the take in use.
                    {
                        var touched = new HashSet<DbvoPatch>();
                        foreach (var g in groups.Where(g => g.KeptCount == 0))
                        {
                            var (hp, hl) = g.Home;
                            if (hl.SelectedTake is { } t) { t.Kept = true; t.VariantPending = false; if (hp != null) touched.Add(hp); }
                        }
                        foreach (var tp in touched) _lib.Save(tp);
                        SaveVariantsCfg(cfg);
                    }
                }
                finally { _gate.Release(); }
                int total = groups.Count * rows.Count, before = 0, failed = 0;
                if (total == 0) { Emit("jobDone", new { kind, voiceId, ok = true, made = 0 }); return; }
                if (total > 5) _lib.Backup("before-variants-" + voice.Name, _settings.BackupsToKeep, routine: true);
                var svc = new Phase2Service(_lib, Runtime());
                Emit("progress", new { kind, done = 0, total, failed = 0, current = (string?)null, message = (string?)null });
                bool discrete = ModelCapabilities.HasDiscreteStability(voice.Eleven.ModelId);
                for (int r = 0; r < rows.Count; r++)
                {
                    var row = rows[r];
                    double stab = discrete ? ModelCapabilities.SnapStability(row.Stability) : row.Stability;
                    bool creative = stab < 0.25;
                    VariantSpec Spec(Phase2Line l) => new(VariantsService.VariantText(l.Spoken, cfg.Prefix, row.Word), stab, row.Speed,
                        row.FixedSeed ? row.Seed : VariantsService.NewSeed(), ctx, Keep: !creative);
                    int offset = before;
                    foreach (var byHome in groups.GroupBy(g => g.Home.Patch))
                    {
                        ct.ThrowIfCancellationRequested();
                        var lines = byHome.Select(g => g.Home.Line).ToList();
                        int callFailed = 0, callOffset = offset, rowNo = r + 1;
                        var progress = new DirectProgress<GenerationProgress>(g =>
                        {
                            callFailed = g.Failed;
                            Emit("progress", new { kind, done = callOffset + g.Done, total, failed = failed + g.Failed, current = g.Current, message = $"Variant {rowNo} of {rows.Count}" + (g.Message != null ? ": " + g.Message : "") });
                        });
                        if (byHome.Key is { } patch)
                            await svc.GenerateAsync(patch, voice, lines, Tools(), progress, null, ct, _gate, engineOverride: voice.Engine, variant: Spec);
                        else
                        {
                            var vp = new DbvoPatch { Id = "variants-" + voiceId, Name = "Variants", VoicePackId = voiceId, Kind = "tts", TagsAsContext = ctx, Lines = cfg.AddedLines };
                            await svc.GenerateAsync(vp, voice, lines, Tools(), progress, null, ct, _gate, engineOverride: voice.Engine,
                                folder: _lib.VariantsFolder(voiceId), save: () => SaveVariantsCfg(cfg), variant: Spec);
                        }
                        failed += callFailed;
                        offset += lines.Count;
                    }
                    before += groups.Count;
                }
                Emit("jobDone", new { kind, voiceId, ok = true, made = total - failed, failed });
            }
            catch (OperationCanceledException) { Emit("jobDone", new { kind, voiceId, ok = false, message = "Cancelled. Variants made so far are kept." }); }
            catch (Exception ex)
            {
                App.Log("Variants failed: " + ex);
                Emit("jobDone", new { kind, voiceId, ok = false, message = FriendlyError.From(ex) });
            }
            finally { _jobCts = null; _generating = null; }
        });
        return true;
    }

    private object StartVariantsExport(JsonElement p)
    {
        var voiceId = S(p, "voiceId");
        _jobCts = NewJob();
        var ct = _jobCts.Token;
        _ = Task.Run(async () =>
        {
            const string kind = "variantsPack";
            try
            {
                var voice = LoadVoice(voiceId);
                VariantsConfig cfg;
                List<VariantGroup> groups;
                await _gate.WaitAsync();
                try
                {
                    cfg = LoadVariantsCfg(voiceId);
                    // A snapshot of the kept takes: generation can't run meanwhile (one job at a time), edits wait for the gate.
                    // Only lines with two or more ★ go in: load just the patches that have them.
                    var withVariants = VariantEntries(voice).Concat(EntriesOf("", "", cfg.AddedLines, voice))
                        .GroupBy(e => e.Key).Where(g => g.Sum(e => e.Kept) >= 2).Select(g => g.Key).ToList(); // Kept counts exported lines only
                    groups = VariantsService.Groups(voice, PatchesWith(voice, withVariants), cfg);
                }
                finally { _gate.Release(); }
                var progress = new DirectProgress<GenerationProgress>(g => Emit("progress", new { kind, g.Done, g.Total, g.Failed, g.Current, g.Message }));
                var rep = await new VariantsPackBuilder(_lib).BuildAsync(voice, groups, cfg, Tools(), progress, ct);
                await Locked(() => { SaveVariantsCfg(cfg); return null; });
                Emit("jobDone", new { kind, voiceId, ok = true, report = rep });
            }
            catch (OperationCanceledException) { Emit("jobDone", new { kind, voiceId, ok = false, message = "Cancelled. The earlier Variants Pack, if any, is unchanged." }); }
            catch (Exception ex)
            {
                App.Log("Variants Pack failed: " + ex);
                Emit("jobDone", new { kind, voiceId, ok = false, message = FriendlyError.From(ex) });
            }
            finally { _jobCts = null; }
        });
        return true;
    }

    /// <summary>
    /// The Variants Pack as a pre-ticked (Recommended) option in the voice pack's FOMOD project: its BSA and plugin,
    /// in a "Variants Pack" group on the first page. Added once; later calls point the same option at the current files.
    /// </summary>
    private object AddVariantsToFomod(string voiceId, string fomodId)
    {
        var voice = LoadVoice(voiceId);
        var cfg = LoadVariantsCfg(voiceId);
        var f = LoadFomod(fomodId);
        if (f.VoicePackId != voiceId) throw new InvalidOperationException("That FOMOD project belongs to another voice pack.");
        var name = cfg.PackNameFor(voice);
        var folder = cfg.OutputFolderFor(voice);
        var bsa = Path.Combine(folder, name + ".bsa");
        var esp = Path.Combine(folder, name + ".esp");
        if (!File.Exists(bsa) || !File.Exists(esp)) throw new InvalidOperationException("Export the Variants Pack first: its BSA and plugin aren't in " + folder + ".");
        string Src(string full)
        {
            if (string.IsNullOrWhiteSpace(f.RootFolder)) return full;
            var rel = Path.GetRelativePath(f.RootFolder, full);
            return rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel) ? full : rel;
        }
        if (f.Steps.Count == 0) f.Steps.Add(new FomodStep { Name = "Voice Pack" });
        var opt = f.Steps.SelectMany(s => s.Groups).SelectMany(g => g.Options).FirstOrDefault(o => o.Files.Any(x => x.Destination.Equals(name + ".bsa", StringComparison.OrdinalIgnoreCase)));
        bool added = opt == null;
        if (opt == null)
        {
            var group = f.Steps[0].Groups.FirstOrDefault(g => g.Name.Equals("Variants Pack", StringComparison.OrdinalIgnoreCase));
            if (group == null) { group = new FomodGroup { Name = "Variants Pack", Type = "SelectAny" }; f.Steps[0].Groups.Add(group); }
            opt = new FomodOption { Name = name };
            group.Options.Add(opt);
        }
        opt.Mode = DetectMode.Recommended;
        opt.Plugins.Clear();
        if (string.IsNullOrWhiteSpace(opt.Description))
            opt.Description = "Lines that play a different take each time you say them (Dragonborn Voice Over 2 and Dragonborn ReVoiced). "
                + "Keep this BSA as it is: unpacking it, or repacking the mod with Cathedral Assets Optimizer, breaks the variants.";
        opt.Files.RemoveAll(x => x.Destination.Equals(name + ".bsa", StringComparison.OrdinalIgnoreCase) || x.Destination.Equals(name + ".esp", StringComparison.OrdinalIgnoreCase));
        opt.Files.Add(new FomodFile { Kind = "file", Source = Src(bsa), Destination = name + ".bsa" });
        opt.Files.Add(new FomodFile { Kind = "file", Source = Src(esp), Destination = name + ".esp" });
        _lib.Save(f);
        return new { added, fomod = f.Name, option = opt.Name };
    }
}
