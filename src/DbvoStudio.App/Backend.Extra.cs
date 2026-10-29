using DbvoStudio.Core.Books;
using DbvoStudio.Core.Text;
using System.IO;
using System.Text.Json;
using DbvoStudio.App.Tools;
using DbvoStudio.Core.ElevenLabs;
using DbvoStudio.Core.Engines;
using DbvoStudio.Core.Fomod;
using DbvoStudio.Core.Naming;
using DbvoStudio.Core.Projects;
using Microsoft.Win32;

namespace DbvoStudio.App;

/// <summary>Bridge methods for pronunciation files, local engines, the Voice Changer, Other Tools and Phase 3 (FOMOD).</summary>
public sealed partial class Backend
{
    /// <summary>The page drew its first screen (the window closes the splash).</summary>
    public static event Action? PageReady;

    private static readonly object NotHandled = new();

    private async Task<object?> DispatchExtra(string m, JsonElement p)
    {
        switch (m)
        {
            // ---- dialogs
            case "pick.save": return PickSave(S(p, "title"), S(p, "filter"), S(p, "name"));
            case "patch.openTakes":
            {
                var dir = Path.Combine(_lib.PatchFolder(S(p, "id")), "takes");
                Directory.CreateDirectory(dir);
                OpenPath(dir);
                return true;
            }
            case "patch.openTakeFolder":
            {
                var patch = LoadPatch(S(p, "id"));
                var line = patch.Lines.FirstOrDefault(l => l.LineId == S(p, "lineId")) ?? throw new InvalidOperationException("Line not found.");
                var take = line.SelectedTake ?? line.Takes.LastOrDefault() ?? throw new InvalidOperationException("This line has no takes yet.");
                var wav = Path.Combine(_lib.PatchFolder(patch.Id), take.WavPath);
                if (File.Exists(wav)) OpenPath(wav); // selects the take in Explorer
                else OpenPath(Path.GetDirectoryName(wav) is { } d && Directory.Exists(d) ? d : Path.Combine(_lib.PatchFolder(patch.Id), "takes"));
                return true;
            }
            case "window.ready":
                PageReady?.Invoke();
                return true;
            case "window.theme":
                _owner.Dispatcher.Invoke(() => WindowChrome.Apply(_owner, S(p, "caption"), S(p, "text"), B(p, "dark")));
                return true;

            // ---- the background image behind every page (kept per computer, served as https://user.dbvo/)
            case "home.setBackground":
                return await Locked(() =>
                {
                    var src = S(p, "path");
                    var ext = Path.GetExtension(src).ToLowerInvariant();
                    if (ext is not (".png" or ".jpg" or ".jpeg" or ".webp" or ".gif" or ".bmp"))
                        throw new InvalidOperationException("Pick a PNG, JPG, WebP, GIF or BMP image.");
                    if (!File.Exists(src)) throw new InvalidOperationException("That image was not found.");
                    if (new FileInfo(src).Length > 30_000_000) throw new InvalidOperationException("That image is over 30 MB. Use a smaller copy.");
                    RemoveBackgroundFiles();
                    var name = "background" + ext;
                    File.Copy(src, Path.Combine(UserFilesDir, name), true);
                    _settings.Ui["homeBg"] = name;
                    _settings.Ui["homeBgV"] = DateTime.UtcNow.Ticks.ToString();
                    _lib.Save(_settings);
                    return State();
                });
            case "home.resetBackground":
                return await Locked(() =>
                {
                    RemoveBackgroundFiles();
                    _settings.Ui.Remove("homeBg");
                    _settings.Ui.Remove("homeBgV");
                    _lib.Save(_settings);
                    return State();
                });

            // ---- small files for the page (images, source audio) as data URLs
            case "file.dataUrl":
            {
                var path = S(p, "path");
                var root = S(p, "root");
                if (!Path.IsPathRooted(FomodService.Os(path)) && !string.IsNullOrWhiteSpace(root)) path = Path.Combine(root, FomodService.Os(path));
                if (!File.Exists(path)) return null;
                if (new FileInfo(path).Length > 25_000_000) throw new InvalidOperationException("This file is too large to preview here. Open it from its folder instead.");
                var ext = Path.GetExtension(path).ToLowerInvariant();
                var mime = ext switch { ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".gif" => "image/gif", ".bmp" => "image/bmp", ".webp" => "image/webp", ".wav" => "audio/wav", ".mp3" => "audio/mpeg", _ => "application/octet-stream" };
                return $"data:{mime};base64,{Convert.ToBase64String(await File.ReadAllBytesAsync(path))}";
            }

            // ---- pronunciation files
            case "pron.find27": return PronunciationIO.FindVpm27Settings();
            case "pron.import":
                return await Locked(() =>
                {
                    var incoming = PronunciationIO.Import(S(p, "path"));
                    if (incoming.Count == 0) throw new InvalidOperationException("No pronunciation entries found in that file.");
                    var target = S(p, "target", "global");
                    (int Added, int Replaced) r;
                    if (target == "global")
                    {
                        r = PronunciationIO.Merge(_settings.Pronunciations, incoming);
                        _lib.Save(_settings);
                    }
                    else
                    {
                        var v = LoadVoice(target);
                        r = PronunciationIO.Merge(v.Pronunciations, incoming);
                        _lib.Save(v);
                    }
                    return new { added = r.Added, replaced = r.Replaced, state = State() };
                });
            case "pron.export":
            {
                var target = S(p, "target", "global");
                var list = target == "global" ? _settings.Pronunciations : LoadVoice(target).Pronunciations;
                var path = S(p, "path");
                if (string.IsNullOrWhiteSpace(path)) throw new InvalidOperationException("Choose where to save.");
                PronunciationIO.Export(path, list);
                return new { path, count = list.Count };
            }

            // ---- AI auto-tagging
            case "ai.test":
            {
                var rt = Runtime();
                if (string.IsNullOrWhiteSpace(rt.AiProvider.Endpoint)) throw new InvalidOperationException("Enter an endpoint first.");
                using var tagger = new AutoTagger(rt.AiProvider.Endpoint, rt.AiProvider.ApiKey, rt.AiProvider.Model);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var r = await tagger.TagAsync("test", "I don't know if I trust any of this.", null, new[] { "[whispers]", "[nervous]", "[sighs]" });
                return new { ok = r.Error == null && r.Suggestion != null, suggestion = r.Suggestion, error = r.Error, ms = sw.ElapsedMilliseconds };
            }

            // ---- local engines (VoxCPM, OmniVoice)
            case "engine.health":
            {
                using var c = LocalClient(S(p, "engine"));
                return await c.HealthAsync();
            }
            case "credits.notices":
            {
                var f = Path.Combine(AppContext.BaseDirectory, "THIRD-PARTY-NOTICES.txt");
                if (!File.Exists(f)) throw new InvalidOperationException("THIRD-PARTY-NOTICES.txt is missing from the app folder.");
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(f) { UseShellExecute = true });
                return true;
            }
            case "reference.info":
            {
                var u = DbvoStudio.Core.Reference.UespReference.Shared;
                return u == null ? null : new
                {
                    u.Source.Name, u.Source.Url, u.Source.License, u.Source.LicenseUrl, u.Source.Pages, u.Source.RevisionsFrom, u.Source.RevisionsTo,
                    npcs = u.Npcs.Count, quotes = u.Quotes.Count, books = u.Books.Count,
                };
            }
            case "engine.voices":
            {
                using var c = LocalClient(S(p, "engine"));
                return await c.VoicesAsync();
            }
            case "engine.loadModel":
            {
                using var c = LocalClient(S(p, "engine"));
                await c.LoadModelAsync(S(p, "model"));
                return true;
            }
            case "eleven.stsModels":
            {
                using var c = Client();
                var models = await c.GetStsModelsAsync();
                return models.Select(x => new { modelId = x.ModelId, name = x.Name, canUseStyle = x.CanUseStyle, canUseSpeakerBoost = x.CanUseSpeakerBoost }).ToList();
            }

            // ---- Phase 2 extras
            case "line2.bulkMark":
                return await Locked(() =>
                {
                    var patch = LoadPatch(S(p, "id"));
                    var ids = Strings(p, "lineIds").ToHashSet();
                    bool value = B(p, "value");
                    foreach (var l in patch.Lines.Where(l => ids.Contains(l.LineId))) l.Marked = value;
                    _lib.Save(patch);
                    return PatchDto(patch);
                });
            // Find and replace in all shown: each tool sends the new texts of the lines that changed; one save.
            case "line1.replaceTexts":
                return await Locked(() =>
                {
                    var mod = LoadMod(S(p, "modId"));
                    var plugin = mod.Plugins.First(x => x.FileName == S(p, "plugin"));
                    var items = Items(p);
                    foreach (var line in plugin.Lines) if (items.TryGetValue(line.Key, out var v)) Phase1Service.SetFinal(line, v);
                    _lib.Save(mod);
                    return ModDto(mod);
                });
            case "line2.replaceTexts":
                return await Locked(() =>
                {
                    var patch = LoadPatch(S(p, "id"));
                    var items = Items(p);
                    foreach (var line in patch.Lines)
                    {
                        if (!items.TryGetValue(line.LineId, out var v) || v == line.Spoken) continue;
                        line.UndoSpoken = null;
                        line.Spoken = v;
                        line.SpokenEdited = line.Spoken != SpokenText.DefaultFor(line.FinalLine);
                        line.TagSuggestion = null;
                        line.RefreshStale();
                    }
                    _lib.Save(patch);
                    return PatchDto(patch);
                });
            case "revoice.replaceTexts":
                return await Locked(() =>
                {
                    var proj = LoadRv(S(p, "id"));
                    var items = Items(p);
                    var changed = new List<string>();
                    foreach (var l in proj.Lines)
                    {
                        if (!items.TryGetValue(l.LineId, out var v) || v == l.Spoken) continue;
                        l.UndoSpoken = null; l.Spoken = v; l.SpokenEdited = l.Spoken != l.FinalLine; l.RefreshStale();
                        changed.Add(l.LineId);
                    }
                    _lib.Save(proj);
                    return new { changed = changed.Count, lines = proj.Lines.Where(x => changed.Contains(x.LineId)).Select(RvLineDto).ToList() };
                });
            case "books.texts":
                return await Locked(() =>
                {
                    var proj = LoadBk(S(p, "id"));
                    var stems = Strings(p, "stems").ToHashSet();
                    return proj.Books.Where(b => stems.Contains(b.Stem)).Select(b => new { b.Stem, spoken = b.Spoken }).ToList();
                });
            case "books.replaceTexts":
                return await Locked(() =>
                {
                    var proj = LoadBk(S(p, "id"));
                    var items = Items(p);
                    int n = 0;
                    foreach (var b in proj.Books)
                    {
                        if (!items.TryGetValue(b.Stem, out var t) || t == b.Spoken) continue;
                        b.Spoken = t; b.SpokenEdited = true;
                        if (b.ManualBreaks) b.Breaks = b.Breaks.Where(x => x > 0 && x < t.Length).ToList();
                        n++;
                    }
                    BooksService.RebuildLines(proj);
                    _lib.Save(proj);
                    return new { changed = n, project = BkDto(proj) };
                });
            case "line2.bulkExclude":
                return await Locked(() =>
                {
                    var patch = LoadPatch(S(p, "id"));
                    var ids = Strings(p, "lineIds").ToHashSet();
                    bool value = B(p, "value");
                    foreach (var l in patch.Lines.Where(l => ids.Contains(l.LineId))) l.Excluded = value;
                    _lib.Save(patch);
                    return PatchDto(patch);
                });
            case "patch.matchSources":
                return await Locked(() =>
                {
                    var patch = LoadPatch(S(p, "id"));
                    int found = Phase2Service.MatchSources(patch);
                    _lib.Save(patch);
                    return new { found, total = patch.Lines.Count, patch = PatchDto(patch) };
                });

            case "patch.importFuz": return StartImportFuz(S(p, "id"));
            case "patch.purgeTakes":
                return await Locked(() =>
                {
                    NoUndoHere();
                    var patch = LoadPatch(S(p, "id"));
                    List<string>? ids = p.TryGetProperty("lineIds", out var li) && li.ValueKind == JsonValueKind.Array ? Strings(p, "lineIds") : null;
                    int n = new Phase2Service(_lib, Runtime()).PurgeUnusedTakes(patch, ids);
                    return new { removed = n, patch = PatchDto(patch) };
                });
            case "patch.deleteOmittedTakes":
                return await Locked(() =>
                {
                    NoUndoHere();
                    var patch = LoadPatch(S(p, "id"));
                    int n = new Phase2Service(_lib, Runtime()).DeleteOmittedTakes(patch);
                    return new { removed = n, patch = PatchDto(patch) };
                });
            case "audio.preview":
            {
                // Any WAV or .fuz as a playable data URL (.fuz audio is decoded with ffmpeg).
                var path = S(p, "path");
                if (!File.Exists(path)) return null;
                byte[] bytes = path.EndsWith(".fuz", StringComparison.OrdinalIgnoreCase)
                    ? await Phase2Service.FuzToWavAsync(path, Tools(), Path.Combine(Path.GetTempPath(), "dbvo"), CancellationToken.None)
                    : await File.ReadAllBytesAsync(path);
                return "data:audio/wav;base64," + Convert.ToBase64String(bytes);
            }

            // ---- Other Tools: WAV to LIP
            case "wavlip.scan": return ScanWavs(Strings(p, "paths"));
            case "wavlip.run": return StartWavToLip(Strings(p, "files"));
            case "wavlip.models":
            {
                var folder = WhisperModelsFolder();
                return Directory.Exists(folder) ? Directory.GetFiles(folder, "*.bin").Select(f => new { name = Path.GetFileName(f), size = new FileInfo(f).Length }).ToList<object>() : new List<object>();
            }
            case "wavlip.detect": return DetectFasterWhisper();

            // ---- Phase 3: FOMOD
            case "fomod.create":
                return await Locked(() =>
                {
                    var voice = LoadVoice(S(p, "voicePackId"));
                    var name = S(p, "name").Trim();
                    var proj = FomodService.NewProject(voice, string.IsNullOrWhiteSpace(name) ? $"{voice.Name} DBVO Patches" : name);
                    proj.RootFolder = S(p, "rootFolder");
                    proj.ReleaseFolder = voice.Destinations.ReleaseFolder;
                    _lib.Save(proj);
                    return FomodDto(proj);
                });
            case "fomod.get": return FomodDto(LoadFomod(S(p, "id")));
            case "fomod.save":
                return await Locked(() =>
                {
                    var proj = p.GetProperty("project").Deserialize<FomodProject>(JsonOptions) ?? throw new InvalidOperationException("Nothing to save.");
                    _lib.Save(proj);
                    return new { ok = true, proj.UpdatedUtc };
                });
            case "fomod.delete":
                return await Locked(() =>
                {
                    _lib.Backup("before-delete-fomod", _settings.BackupsToKeep);
                    Directory.Delete(_lib.FomodFolder(S(p, "id")), true);
                    return State();
                });
            case "fomod.import":
                return await Locked(() =>
                {
                    var path = S(p, "path");
                    string mc = path, info = "";
                    if (Directory.Exists(path))
                    {
                        var f = Directory.Exists(Path.Combine(path, "fomod")) ? Path.Combine(path, "fomod") : path;
                        mc = Path.Combine(f, "ModuleConfig.xml");
                        info = Path.Combine(f, "info.xml");
                    }
                    else info = Path.Combine(Path.GetDirectoryName(path)!, "info.xml");
                    if (!File.Exists(mc)) throw new InvalidOperationException("ModuleConfig.xml not found there.");
                    var proj = FomodXml.Read(FomodXml.ReadText(mc), File.Exists(info) ? FomodXml.ReadText(info) : null);
                    var existingId = S(p, "into");
                    // The FOMOD root is the folder above fomod\ (sources are relative to it).
                    var fomodDir = Path.GetDirectoryName(mc)!;
                    proj.RootFolder = Path.GetFileName(fomodDir).Equals("fomod", StringComparison.OrdinalIgnoreCase) ? Path.GetDirectoryName(fomodDir)! : fomodDir;
                    var bytes = File.ReadAllBytes(mc);
                    proj.Encoding = bytes.Length > 1 && (bytes[0] == 0xFF || bytes[1] == 0) ? "utf-16" : "utf-8";
                    if (!string.IsNullOrWhiteSpace(existingId))
                    {
                        var old = LoadFomod(existingId);
                        proj.Id = old.Id;
                        proj.VoicePackId = old.VoicePackId;
                        proj.ReleaseFolder = old.ReleaseFolder;
                        proj.Json = old.Json;
                        proj.ArchiveFormat = old.ArchiveFormat;
                    }
                    else
                    {
                        proj.VoicePackId = S(p, "voicePackId");
                        var v = _lib.LoadVoicePacks().FirstOrDefault(x => x.Id == proj.VoicePackId);
                        if (v != null)
                        {
                            proj.Json = new DbvoJsonSettings { VoicePackName = v.Name, VoicePackId = v.PackId };
                            proj.ReleaseFolder = v.Destinations.ReleaseFolder;
                        }
                    }
                    // An imported FOMOD that already installs the DBVO JSON folder: mark that option as the carrier.
                    var carrier = proj.AllOptions().FirstOrDefault(o => o.Files.Any(f => f.Destination.Equals("DragonbornVoiceOver", StringComparison.OrdinalIgnoreCase)
                                                                                    || f.Destination.StartsWith(@"SKSE\Plugins\DBVO2", StringComparison.OrdinalIgnoreCase)));
                    if (carrier != null) { proj.Json.Enabled = false; }
                    // A Voiced Books installer has no DBVO voice pack file to install.
                    if (B(p, "books")) proj.Json.Enabled = false;
                    _lib.Save(proj);
                    return new { project = FomodDto(proj), jsonFrom = carrier?.Name };
                });
            case "fomod.addPatches":
                return await Locked(() =>
                {
                    var proj = p.GetProperty("project").Deserialize<FomodProject>(JsonOptions)!;
                    var svc = new FomodService(_lib, Runtime());
                    var mods = _lib.LoadMods();
                    var voices = _lib.LoadVoicePacks();
                    int added = 0, updated = 0;
                    // Above or below a picked option, in its group (in the order given), instead of on the category's page.
                    var anchorId = S(p, "anchorId");
                    FomodGroup? anchorGroup = null;
                    if (anchorId.Length > 0)
                        foreach (var st in proj.Steps) foreach (var g in st.Groups) if (g.Options.Any(o => o.Id == anchorId)) anchorGroup = g;
                    bool below = S(p, "where", "below") != "above";
                    int insertAt = anchorGroup == null ? -1 : anchorGroup.Options.FindIndex(o => o.Id == anchorId) + (below ? 1 : 0);
                    foreach (var pid in Strings(p, "patchIds"))
                    {
                        var patch = LoadPatch(pid);
                        var voice = voices.First(v => v.Id == patch.VoicePackId);
                        foreach (var mod in mods.Where(x => patch.ModIds.Contains(x.Id)))
                        {
                            var existing = proj.AllOptions().FirstOrDefault(o => o.PatchId == patch.Id && o.ModId == mod.Id);
                            if (existing != null)
                            {
                                FomodService.Relink(existing, svc.FilesFor(proj, patch, voice, mod), mod);
                                updated++;
                                continue;
                            }
                            var opt = svc.OptionFor(proj, patch, voice, mod);
                            if (anchorGroup != null) anchorGroup.Options.Insert(Math.Clamp(insertAt++, 0, anchorGroup.Options.Count), opt);
                            else FomodService.GroupFor(proj, mod.Category).Options.Add(opt);
                            added++;
                        }
                    }
                    if (S(p, "sort") == "az")
                        foreach (var g in proj.Steps.SelectMany(s => s.Groups))
                            g.Options = g.Options.OrderBy(o => o.Mode == DetectMode.Required ? 0 : 1).ThenBy(o => o.Name, StringComparer.OrdinalIgnoreCase).ToList();
                    _lib.Save(proj);
                    return new { project = FomodDto(proj), added, updated };
                });
            case "fomod.refresh":
                return await Locked(() =>
                {
                    var proj = p.GetProperty("project").Deserialize<FomodProject>(JsonOptions)!;
                    var orphans = new FomodService(_lib, Runtime()).RefreshLinked(proj, _lib.LoadPatches(), _lib.LoadVoicePacks(), _lib.LoadMods());
                    _lib.Save(proj);
                    return new { project = FomodDto(proj), orphans };
                });
            case "fomod.validate":
            {
                var proj = p.GetProperty("project").Deserialize<FomodProject>(JsonOptions)!;
                return new FomodService(_lib, Runtime()).Validate(proj);
            }
            case "fomod.simulate":
            {
                var proj = p.GetProperty("project").Deserialize<FomodProject>(JsonOptions)!;
                var plugins = Strings(p, "plugins");
                Dictionary<string, bool>? choices = null;
                if (p.TryGetProperty("choices", out var ch) && ch.ValueKind == JsonValueKind.Object)
                    choices = ch.EnumerateObject().ToDictionary(x => x.Name, x => x.Value.ValueKind == JsonValueKind.True);
                return new FomodService(_lib, Runtime()).Simulate(proj, plugins, choices);
            }
            case "fomod.summary":
            {
                var proj = p.GetProperty("project").Deserialize<FomodProject>(JsonOptions)!;
                return await Task.Run(() => new FomodService(_lib, Runtime()).Summarize(proj));
            }
            case "fomod.loadOrder": return ReadLoadOrder(S(p, "path"));
            case "fomod.preview":
            {
                var proj = p.GetProperty("project").Deserialize<FomodProject>(JsonOptions)!;
                return new { moduleConfig = FomodXml.WriteModuleConfig(proj), info = FomodXml.WriteInfo(proj), json = FomodService.JsonText(proj.Json) };
            }
            case "fomod.build": return StartFomodBuild(p);
        }
        return NotHandled;
    }

    // ------------------------------------------------------------------ helpers

    private string? PickSave(string title, string filter, string name) =>
        _owner.Dispatcher.Invoke(() =>
        {
            var d = new SaveFileDialog { Title = string.IsNullOrEmpty(title) ? "Save as" : title, Filter = string.IsNullOrEmpty(filter) ? "All files|*.*" : filter, FileName = name };
            return d.ShowDialog(_owner) == true ? d.FileName : null;
        });

    private LocalEngineClient LocalClient(string engine) =>
        new(engine == LocalEngineClient.OmniVoice ? _settings.LocalEngines.OmniVoiceAddress : _settings.LocalEngines.VoxCpmAddress);

    private FomodProject LoadFomod(string id) => _lib.LoadFomod(id) ?? throw new InvalidOperationException("FOMOD project not found.");

    private object FomodDto(FomodProject p)
    {
        var voice = _lib.LoadVoicePacks().FirstOrDefault(v => v.Id == p.VoicePackId);
        return new
        {
            project = p,
            voiceName = voice?.Name ?? "",
            naming = voice?.Naming.ToString() ?? "Legacy",
            jsonPath = FomodService.JsonDestination(voice?.Naming ?? NamingMode.Legacy, string.IsNullOrWhiteSpace(p.Json.VoicePackId) ? "packid" : p.Json.VoicePackId),
        };
    }

    /// <summary>The patch row in the DBVO Patches list: Phase 1 flags, progress, characters left and warnings.</summary>
    private object PatchSummary(DbvoPatch x, List<VoicePack> voices, List<(string Id, string Name, string Category, int Unreviewed)> its)
    {
        var voice = voices.FirstOrDefault(v => v.Id == x.VoicePackId);
        var warnings = new List<string>();
        if (string.IsNullOrWhiteSpace(x.WavLipFolder)) warnings.Add("WAV + LIP folder not set");
        if (string.IsNullOrWhiteSpace(x.FinalRoot)) warnings.Add("Final folder not set");
        if (string.IsNullOrWhiteSpace(x.FixFolder)) warnings.Add("DBVO Fix folder not set");
        if (x.Kind == "vc" && x.SourceFolders.Count == 0) warnings.Add("No source WAV folder");
        if (voice != null && voice.Naming == NamingMode.Legacy && !string.IsNullOrWhiteSpace(x.FinalRoot))
        {
            var fuz = its.Select(m => Path.Combine(x.FinalRoot, Phase2Service.ExpandTemplate(
                Phase2Service.ResolveFuzTemplate(x, voice), m.Name, voice, x)));
            var pw = PathChecks.Check(x.Lines.Where(l => !l.IsOmitted).Select(l => l.FinalLine), voice.Naming, x.WavLipFolder, fuz, Path.Combine(_lib.PatchFolder(x.Id), "takes"));
            if (pw.Count > 0) warnings.Add("Path too long: " + string.Join(", ", pw.Select(w => w.Where).Distinct()));
        }
        // What "Missing" would make: lines with no take at all (edited lines have their own choice).
        var pending = Phase2Service.Eligible(x, false);
        return new
        {
            x.Id, x.Name, x.VoicePackId, x.ModIds, x.ReferenceIds, x.UpdatedUtc, x.Kind,
            voicePack = voice?.Name ?? "(missing)",
            voiceColor = voice?.Color ?? "",
            modNames = its.Select(m => m.Name).ToList(),
            folders = voice == null ? new List<string>() : Phase2Service.ExportFolders(x, voice, its.Select(m => m.Name)).ToList(),
            category = its.Count > 0 ? its[0].Category : "Misc",
            total = x.Lines.Count,
            generated = x.Lines.Count(l => l.SelectedTakeId != null && !l.IsOmitted && !l.Excluded),
            failed = x.Lines.Count(l => l.Status == Phase2Status.Failed && !l.IsOmitted && !l.Excluded),
            omitted = x.Lines.Count(l => l.IsOmitted),
            excluded = x.Lines.Count(l => l.Excluded && !l.IsOmitted),
            unreviewed = its.Sum(m => m.Unreviewed),
            charsLeft = pending.Where(l => x.Kind != "vc" || l.SourceWav == null).Sum(l => l.Spoken.Length),
            secondsLeft = x.Kind == "vc" ? pending.Count(l => l.SourceWav != null) : 0,
            noSource = x.Kind == "vc" ? x.Lines.Count(l => !l.IsOmitted && !l.Excluded && l.SelectedTakeId == null && l.SourceWav == null) : 0,
            warnings,
        };
    }

    private string WhisperModelsFolder() =>
        !string.IsNullOrWhiteSpace(_settings.WavToLip.ModelsFolder) ? _settings.WavToLip.ModelsFolder : Path.Combine(_lib.Root, "whisper-models");

    private static object ScanWavs(List<string> paths)
    {
        var files = new List<string>();
        foreach (var path in paths)
        {
            if (Directory.Exists(path)) files.AddRange(Directory.EnumerateFiles(path, "*.wav", SearchOption.AllDirectories));
            else if (File.Exists(path) && path.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)) files.Add(path);
        }
        return files.Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(f => new { path = f, name = Path.GetFileName(f), hasLip = File.Exists(Path.ChangeExtension(f, ".lip")) }).ToList();
    }

    /// <summary>Look for the WAV_to_LIP app's Python environment and CUDA folder in the usual place.</summary>
    private object DetectFasterWhisper()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "DBVO Programs", "WAV_to_LIP_App"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents", "DBVO Programs", "WAV_to_LIP_App"),
        };
        foreach (var c in candidates)
        {
            var py = Path.Combine(c, ".venv", "Scripts", "python.exe");
            if (File.Exists(py)) return new { python = py, cuda = Directory.Exists(Path.Combine(c, "cuda_libs")) ? Path.Combine(c, "cuda_libs") : "" };
        }
        return new { python = "", cuda = "" };
    }

    private object StartWavToLip(List<string> files)
    {
        if (_jobCts != null) throw new InvalidOperationException("Another task is still running (see the bar at the top). Wait for it to finish, or press Cancel there.");
        if (files.Count == 0) throw new InvalidOperationException("Add at least one WAV file first.");
        _jobCts = new CancellationTokenSource();
        var ct = _jobCts.Token;
        var settings = JsonSerializer.Deserialize<WavToLipSettings>(JsonSerializer.Serialize(_settings.WavToLip))!;
        _ = Task.Run(async () =>
        {
            try
            {
                var tool = new WavToLip(settings, Tools(), WhisperModelsFolder(), e =>
                {
                    Emit("wavlip", new { kind = e.Kind, text = e.Text, index = e.Index, total = e.Total });
                    if (e.Kind == "status" && e.Total > 0) Emit("progress", new { kind = "wavlip", done = e.Index, total = e.Total, failed = 0 });
                });
                Emit("progress", new { kind = "wavlip", done = 0, total = files.Count, failed = 0 });
                var (ok, failed) = await tool.RunAsync(files, ct);
                Emit("wavlip", new { kind = "done", text = $"Finished. Successful: {ok}; failed: {failed}.", index = files.Count, total = files.Count });
                Emit("jobDone", new { kind = "wavlip", ok = failed == 0, message = $"WAV to LIP finished: {ok} made, {failed} failed." });
            }
            catch (OperationCanceledException)
            {
                Emit("wavlip", new { kind = "done", text = "Stopped.", index = 0, total = 0 });
                Emit("jobDone", new { kind = "wavlip", ok = false, message = "Cancelled." });
            }
            catch (Exception ex)
            {
                App.Log("WAV to LIP failed: " + ex);
                Emit("wavlip", new { kind = "fatal", text = FriendlyError.From(ex), index = 0, total = 0 });
                Emit("jobDone", new { kind = "wavlip", ok = false, message = FriendlyError.From(ex) });
            }
            finally { _jobCts = null; }
        });
        return true;
    }

    /// <summary>Active plugins from a plugins.txt / loadorder.txt (MO2 profile or %LOCALAPPDATA%\Skyrim Special Edition).</summary>
    private static List<string> ReadLoadOrder(string path)
    {
        if (!File.Exists(path)) throw new InvalidOperationException("File not found.");
        var list = new List<string>();
        bool starFormat = File.ReadLines(path).Any(l => l.StartsWith('*'));
        foreach (var raw in File.ReadLines(path))
        {
            var l = raw.Trim();
            if (l.Length == 0 || l.StartsWith('#')) continue;
            if (starFormat)
            {
                if (l.StartsWith('*')) list.Add(l[1..]);
            }
            else list.Add(l);
        }
        // The base game masters are always active.
        foreach (var b in new[] { "Skyrim.esm", "Update.esm", "Dawnguard.esm", "HearthFires.esm", "Dragonborn.esm" })
            if (!list.Contains(b, StringComparer.OrdinalIgnoreCase)) list.Insert(0, b);
        return list;
    }

    private object StartImportFuz(string id)
    {
        if (_jobCts != null) throw new InvalidOperationException("Another task is still running (see the bar at the top). Wait for it to finish, or press Cancel there.");
        _jobCts = new CancellationTokenSource();
        var ct = _jobCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                var patch = LoadPatch(id);
                var voice = LoadVoice(patch.VoicePackId);
                var progress = new DirectProgress<GenerationProgress>(g => Emit("progress", new { kind = "import", g.Done, g.Total, g.Failed, g.Current }));
                int n = await new Phase2Service(_lib, Runtime()).ImportFuzAsync(patch, voice, _lib.LoadMods(), Tools(), progress, ct, _gate);
                Emit("jobDone", new { kind = "import", patchId = id, ok = true, message = $"{n} line(s) imported from .fuz files." });
            }
            catch (OperationCanceledException) { Emit("jobDone", new { kind = "import", patchId = id, ok = false, message = "Cancelled." }); }
            catch (Exception ex)
            {
                App.Log("Import .fuz failed: " + ex);
                Emit("jobDone", new { kind = "import", patchId = id, ok = false, message = FriendlyError.From(ex) });
            }
            finally { _jobCts = null; }
        });
        return true;
    }

    private object StartFomodBuild(JsonElement p)
    {
        if (_jobCts != null) throw new InvalidOperationException("Another task is still running (see the bar at the top). Wait for it to finish, or press Cancel there.");
        var proj = p.GetProperty("project").Deserialize<FomodProject>(JsonOptions)!;
        bool package = !p.TryGetProperty("package", out var pk) || pk.ValueKind != JsonValueKind.False;
        _jobCts = new CancellationTokenSource();
        var ct = _jobCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                var voice = _lib.LoadVoicePacks().FirstOrDefault(v => v.Id == proj.VoicePackId);
                var svc = new FomodService(_lib, Runtime());
                var orphans = svc.RefreshLinked(proj, _lib.LoadPatches(), _lib.LoadVoicePacks(), _lib.LoadMods());
                var errors = svc.Validate(proj).Where(i => i.Level == "error").ToList();
                if (errors.Count > 0) throw new InvalidOperationException("Fix these first: " + string.Join(" · ", errors.Take(5).Select(e => $"{e.Where}: {e.Message}")));
                _lib.Backup("before-fomod-build", _settings.BackupsToKeep, routine: true);
                var progress = new DirectProgress<GenerationProgress>(g => Emit("progress", new { kind = "fomod", g.Done, g.Total, g.Failed, g.Current, g.Message }));
                var res = await svc.BuildAsync(proj, voice?.Naming ?? NamingMode.Legacy, package, progress, ct);
                foreach (var o in orphans) res.Warnings.Add($"\"{o}\" is no longer linked to a DBVO Patch (deleted, or its mod was removed): built as it was.");
                await _gate.WaitAsync();
                try { _lib.Save(proj); } finally { _gate.Release(); }
                Emit("jobDone", new { kind = "fomod", ok = true, report = res, project = proj });
            }
            catch (OperationCanceledException)
            {
                Emit("jobDone", new { kind = "fomod", ok = false, message = "Cancelled." });
            }
            catch (Exception ex)
            {
                App.Log("FOMOD build failed: " + ex);
                Emit("jobDone", new { kind = "fomod", ok = false, message = FriendlyError.From(ex) });
            }
            finally { _jobCts = null; }
        });
        return true;
    }

    /// <summary>Files the user gives the app itself (the background image), per computer, outside the library.</summary>
    public static string UserFilesDir
    {
        get
        {
            var d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DBVOStudio", "UserFiles");
            Directory.CreateDirectory(d);
            return d;
        }
    }

    private static void RemoveBackgroundFiles()
    {
        foreach (var pattern in new[] { "background.*", "landing-background.*" }) // the second: build 6.7's name
            foreach (var f in Directory.GetFiles(UserFilesDir, pattern))
                try { File.Delete(f); } catch (IOException) { } // in use: the next pick overwrites it
    }

    private static Dictionary<string, string> Items(JsonElement p)
    {
        var d = new Dictionary<string, string>();
        if (p.TryGetProperty("items", out var a) && a.ValueKind == JsonValueKind.Array)
            foreach (var x in a.EnumerateArray()) d[x.GetProperty("k").GetString() ?? ""] = x.GetProperty("v").GetString() ?? "";
        return d;
    }
}
