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
    private static readonly object NotHandled = new();

    private async Task<object?> DispatchExtra(string m, JsonElement p)
    {
        switch (m)
        {
            // ---- dialogs
            case "pick.save": return PickSave(S(p, "title"), S(p, "filter"), S(p, "name"));

            // ---- small files for the page (images, source audio) as data URLs
            case "file.dataUrl":
            {
                var path = S(p, "path");
                var root = S(p, "root");
                if (!Path.IsPathRooted(FomodService.Os(path)) && !string.IsNullOrWhiteSpace(root)) path = Path.Combine(root, FomodService.Os(path));
                if (!File.Exists(path)) return null;
                if (new FileInfo(path).Length > 25_000_000) throw new InvalidOperationException("File too large to preview.");
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
            case "patch.matchSources":
                return await Locked(() =>
                {
                    var patch = LoadPatch(S(p, "id"));
                    int found = Phase2Service.MatchSources(patch);
                    _lib.Save(patch);
                    return new { found, total = patch.Lines.Count, patch = PatchDto(patch) };
                });

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
                            proj.Json = new DbvoJsonSettings { VoicePackName = v.Name, VoicePackId = v.PackId, DisableAliasParsing = v.AliasParsing ? null : true };
                            proj.ReleaseFolder = v.Destinations.ReleaseFolder;
                        }
                    }
                    // An imported FOMOD that already installs the DBVO JSON folder: mark that option as the carrier.
                    var carrier = proj.AllOptions().FirstOrDefault(o => o.Files.Any(f => f.Destination.Equals("DragonbornVoiceOver", StringComparison.OrdinalIgnoreCase)
                                                                                    || f.Destination.StartsWith(@"SKSE\Plugins\DBVO2", StringComparison.OrdinalIgnoreCase)));
                    if (carrier != null) { proj.Json.Enabled = false; }
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
                    foreach (var pid in Strings(p, "patchIds"))
                    {
                        var patch = LoadPatch(pid);
                        var voice = voices.First(v => v.Id == patch.VoicePackId);
                        foreach (var mod in mods.Where(x => patch.ModIds.Contains(x.Id)))
                        {
                            var existing = proj.AllOptions().FirstOrDefault(o => o.PatchId == patch.Id && o.ModId == mod.Id);
                            if (existing != null)
                            {
                                existing.Files = svc.FilesFor(proj, patch, voice, mod);
                                updated++;
                                continue;
                            }
                            FomodService.GroupFor(proj, mod.Category).Options.Add(svc.OptionFor(proj, patch, voice, mod));
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
                    new FomodService(_lib, Runtime()).RefreshLinked(proj, _lib.LoadPatches(), _lib.LoadVoicePacks(), _lib.LoadMods());
                    _lib.Save(proj);
                    return FomodDto(proj);
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
    private object PatchSummary(DbvoPatch x, List<VoicePack> voices, List<Mod> mods)
    {
        var voice = voices.FirstOrDefault(v => v.Id == x.VoicePackId);
        var its = mods.Where(m => x.ModIds.Contains(m.Id)).ToList();
        var warnings = new List<string>();
        if (string.IsNullOrWhiteSpace(x.WavLipFolder)) warnings.Add("WAV + LIP folder not set");
        if (string.IsNullOrWhiteSpace(x.FinalRoot)) warnings.Add("Final folder not set");
        if (x.Kind != "vc" && string.IsNullOrWhiteSpace(x.FixFolder)) warnings.Add("DBVO Fix folder not set");
        if (x.Kind == "vc" && x.SourceFolders.Count == 0) warnings.Add("No source WAV folder");
        if (voice != null && voice.Naming == NamingMode.Legacy && !string.IsNullOrWhiteSpace(x.FinalRoot))
        {
            var fuz = its.Select(m => Path.Combine(x.FinalRoot, Phase2Service.ExpandTemplate(
                !string.IsNullOrWhiteSpace(x.FuzTemplate) ? x.FuzTemplate : voice.Destinations.FuzTemplate, m.Name, voice, x)));
            var pw = PathChecks.Check(x.Lines.Where(l => !l.IsOmitted).Select(l => l.FinalLine), voice.Naming, x.WavLipFolder, fuz, Path.Combine(_lib.PatchFolder(x.Id), "takes"));
            if (pw.Count > 0) warnings.Add("Path too long: " + string.Join(", ", pw.Select(w => w.Where).Distinct()));
        }
        var pending = x.Lines.Where(l => !l.IsOmitted && l.SelectedTakeId == null && (x.Kind != "vc" || l.SourceWav != null)).ToList();
        return new
        {
            x.Id, x.Name, x.VoicePackId, x.ModIds, x.ReferenceIds, x.UpdatedUtc, x.Kind,
            voicePack = voice?.Name ?? "(missing)",
            voiceColor = voice?.Color ?? "",
            modNames = its.Select(m => m.Name).ToList(),
            category = its.FirstOrDefault()?.Category ?? "Misc",
            total = x.Lines.Count,
            generated = x.Lines.Count(l => l.SelectedTakeId != null && !l.IsOmitted),
            failed = x.Lines.Count(l => l.Status == Phase2Status.Failed),
            omitted = x.Lines.Count(l => l.IsOmitted),
            unreviewed = its.Sum(m => m.Plugins.Sum(pl => Phase1Service.UnreviewedFlags(pl).Count)),
            charsLeft = x.Kind == "vc" ? 0 : pending.Sum(l => l.Spoken.Length),
            secondsLeft = x.Kind == "vc" ? pending.Count : 0,
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
        if (_jobCts != null) throw new InvalidOperationException("A job is already running.");
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
                Emit("wavlip", new { kind = "fatal", text = ex.Message, index = 0, total = 0 });
                Emit("jobDone", new { kind = "wavlip", ok = false, message = ex.Message });
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

    private object StartFomodBuild(JsonElement p)
    {
        if (_jobCts != null) throw new InvalidOperationException("A job is already running.");
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
                svc.RefreshLinked(proj, _lib.LoadPatches(), _lib.LoadVoicePacks(), _lib.LoadMods());
                var errors = svc.Validate(proj).Where(i => i.Level == "error" && !i.Message.StartsWith("Missing", StringComparison.Ordinal)).ToList();
                if (errors.Count > 0) throw new InvalidOperationException("Fix these first: " + string.Join(" · ", errors.Take(5).Select(e => $"{e.Where}: {e.Message}")));
                _lib.Backup("before-fomod-build", _settings.BackupsToKeep);
                var progress = new Progress<GenerationProgress>(g => Emit("progress", new { kind = "fomod", g.Done, g.Total, g.Failed, g.Current, g.Message }));
                var res = await svc.BuildAsync(proj, voice?.Naming ?? NamingMode.Legacy, package, progress, ct);
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
                Emit("jobDone", new { kind = "fomod", ok = false, message = ex.Message });
            }
            finally { _jobCts = null; }
        });
        return true;
    }
}
