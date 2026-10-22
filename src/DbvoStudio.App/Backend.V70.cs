using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using DbvoStudio.App.Tools;
using DbvoStudio.Core.Naming;
using DbvoStudio.Core.Projects;

namespace DbvoStudio.App;

/// <summary>Build 7.0: legacy pack import, take quality checks, review, CSV editing, the .fuz cache, recording and merging takes.</summary>
public sealed partial class Backend
{
    /// <summary>A take is flagged when the transcript scores below this, or it is quieter than <see cref="QaQuietLufs"/> or peaks above <see cref="QaPeakDbtp"/>.</summary>
    public const double QaMinScore = 0.75, QaQuietLufs = -30, QaPeakDbtp = -0.5;

    private async Task<object?> DispatchV70(string m, JsonElement p)
    {
        switch (m)
        {
            case "patch.legacyScan": return await Locked(() => new Phase2Service(_lib, Runtime()).ScanLegacy(LoadPatch(S(p, "id")), S(p, "folder")));
            case "patch.importLegacy": return StartImportLegacy(S(p, "id"), S(p, "folder"));
            case "patch.qa": return StartQa(S(p, "id"), p.TryGetProperty("lineIds", out var li) && li.ValueKind == JsonValueKind.Array ? Strings(p, "lineIds") : null);
            case "patch.clearQa":
                return await Locked(() =>
                {
                    var patch = LoadPatch(S(p, "id"));
                    foreach (var t in patch.Lines.SelectMany(l => l.Takes)) t.Qa = null;
                    _lib.Save(patch);
                    return PatchDto(patch);
                });
            case "patch.approve":
                return await Locked(() =>
                {
                    var patch = LoadPatch(S(p, "id"));
                    var ids = Strings(p, "lineIds").ToHashSet();
                    bool value = B(p, "value");
                    foreach (var l in patch.Lines.Where(l => ids.Contains(l.LineId))) l.Approved = value && l.Takes.Count > 0;
                    _lib.Save(patch);
                    return PatchDto(patch);
                });
            case "patch.exportCsv":
                return await Locked(() =>
                {
                    var patch = LoadPatch(S(p, "id"));
                    var voice = LoadVoice(patch.VoicePackId);
                    var rows = patch.Lines.Where(l => !l.IsOmitted).Select(l => new SpokenCsv.Row(l.LineId, LineNaming.Stem(l.FinalLine, voice.Naming), l.FinalLine, l.Spoken)).ToList();
                    var path = S(p, "path");
                    File.WriteAllText(path, SpokenCsv.Write(rows), new UTF8Encoding(true));
                    return new { lines = rows.Count, path };
                });
            case "patch.readCsv":
                return await Locked(() =>
                {
                    var patch = LoadPatch(S(p, "id"));
                    var voice = LoadVoice(patch.VoicePackId);
                    var rows = SpokenCsv.Read(File.ReadAllText(S(p, "path")));
                    var byId = patch.Lines.ToDictionary(l => l.LineId);
                    var byName = patch.Lines.GroupBy(l => LineNaming.Stem(l.FinalLine, voice.Naming), StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
                    var changes = new List<object>();
                    int unmatched = 0, same = 0;
                    foreach (var r in rows)
                    {
                        var line = r.LineId.Length > 0 && byId.TryGetValue(r.LineId, out var a) ? a : r.FileName.Length > 0 && byName.TryGetValue(r.FileName, out var b) ? b : null;
                        if (line == null) { unmatched++; continue; }
                        var incoming = r.Spoken.Replace("\r\n", "\n").Trim();
                        if (incoming.Length == 0 || incoming == line.Spoken) { same++; continue; }
                        changes.Add(new { line.LineId, line.FinalLine, current = line.Spoken, incoming });
                    }
                    return new { changes, unmatched, same, rows = rows.Count };
                });
            case "patch.applyCsv":
                return await Locked(() =>
                {
                    var patch = LoadPatch(S(p, "id"));
                    int n = 0;
                    foreach (var c in p.GetProperty("changes").EnumerateArray())
                    {
                        var line = patch.Lines.FirstOrDefault(l => l.LineId == S(c, "lineId"));
                        var text = S(c, "spoken");
                        if (line == null || text.Length == 0 || text == line.Spoken) continue;
                        line.UndoSpoken = null;
                        line.Spoken = text;
                        line.SpokenEdited = line.Spoken != SpokenText.DefaultFor(line.FinalLine);
                        line.TagSuggestion = null;
                        line.RefreshStale();
                        n++;
                    }
                    _lib.Save(patch);
                    return new { applied = n, patch = PatchDto(patch) };
                });
            case "cache.fuzInfo":
            {
                var (files, bytes) = new FuzCache(_lib.FuzCacheFolder).Size();
                return new { files, bytes };
            }
            case "cache.fuzClear":
            {
                if (_jobCts != null) throw new InvalidOperationException("Wait for the running job to finish.");
                var (files, bytes) = new FuzCache(_lib.FuzCacheFolder).Clear();
                return new { files, bytes };
            }
            case "patch.mergeTakes":
                return await Locked(() =>
                {
                    if (_jobCts != null) throw new InvalidOperationException("Wait for the running job to finish.");
                    var patch = LoadPatch(S(p, "id"));
                    var other = LoadPatch(S(p, "sourceId"));
                    _lib.Backup("before merging takes");
                    var (takes, lines, onlyThere) = new Phase2Service(_lib, Runtime()).MergeTakes(patch, LoadVoice(patch.VoicePackId), other);
                    return new { takes, lines, onlyThere, patch = PatchDto(patch) };
                });
            case "patch.record":
            {
                if (_jobCts != null) throw new InvalidOperationException("Wait for the running job to finish.");
                var patch = LoadPatch(S(p, "id"));
                var voice = LoadVoice(patch.VoicePackId);
                var line = patch.Lines.FirstOrDefault(l => l.LineId == S(p, "lineId")) ?? throw new InvalidOperationException("Line not found.");
                var data = Convert.FromBase64String(S(p, "data"));
                double D(string n) => p.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
                var take = await new Phase2Service(_lib, Runtime()).AddRecordingAsync(patch, voice, line, data, S(p, "ext", ".webm"), D("start"), D("end"), B(p, "convert"), Tools(), CancellationToken.None, _gate);
                return new { takeId = take.Id, line = Line2Dto(line, voice) };
            }
            case "patch.recordTarget":
            {
                var voice = LoadVoice(LoadPatch(S(p, "id")).VoicePackId);
                var (voiceId, voiceName) = Phase2Service.RecordingTarget(voice);
                bool ready = !string.IsNullOrWhiteSpace(_settings.ElevenLabsApiKey) && !string.IsNullOrWhiteSpace(voiceId);
                return new { ready, voiceName = string.IsNullOrWhiteSpace(voiceName) ? voice.Name : voiceName,
                    why = string.IsNullOrWhiteSpace(_settings.ElevenLabsApiKey) ? "Add your ElevenLabs API key in Global Settings to convert recordings." : string.IsNullOrWhiteSpace(voiceId) ? "Pick an ElevenLabs voice for this voice pack to convert recordings." : "" };
            }
            default: return NotHandled;
        }
    }

    private object StartImportLegacy(string id, string folder)
    {
        if (_jobCts != null) throw new InvalidOperationException("A job is already running.");
        if (!Directory.Exists(folder)) throw new InvalidOperationException("That folder was not found.");
        _jobCts = new CancellationTokenSource();
        var ct = _jobCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                _lib.Backup("before importing a pack");
                var patch = LoadPatch(id);
                var voice = LoadVoice(patch.VoicePackId);
                var progress = new Progress<GenerationProgress>(g => Emit("progress", new { kind = "import", g.Done, g.Total, g.Failed, g.Current }));
                int n = await new Phase2Service(_lib, Runtime()).ImportLegacyAsync(patch, voice, folder, Tools(), progress, ct, _gate);
                Emit("jobDone", new { kind = "import", patchId = id, ok = true, message = $"{n} line(s) imported from the earlier pack." });
            }
            catch (OperationCanceledException) { Emit("jobDone", new { kind = "import", patchId = id, ok = false, message = "Cancelled. Lines imported so far are kept." }); }
            catch (Exception ex)
            {
                App.Log("Pack import failed: " + ex);
                Emit("jobDone", new { kind = "import", patchId = id, ok = false, message = ex.Message });
            }
            finally { _jobCts = null; }
        });
        return true;
    }

    /// <summary>
    /// Check takes: Whisper transcribes the selected take of each line and the transcript is compared with the text the
    /// take was made from; ffmpeg measures loudness and true peak. Results are stored on the take. Nothing is deleted.
    /// </summary>
    private object StartQa(string id, List<string>? lineIds)
    {
        if (_jobCts != null) throw new InvalidOperationException("A job is already running.");
        _jobCts = new CancellationTokenSource();
        var ct = _jobCts.Token;
        _ = Task.Run(async () =>
        {
            int flagged = 0, done = 0, failed = 0;
            try
            {
                var patch = LoadPatch(id);
                var only = lineIds?.ToHashSet();
                var todo = patch.Lines.Where(l => !l.IsOmitted && !l.Excluded && l.SelectedTake != null && (only == null || only.Contains(l.LineId)))
                    .Select(l => (Line: l, Take: l.SelectedTake!)).ToList();
                if (todo.Count == 0) { Emit("jobDone", new { kind = "qa", patchId = id, ok = true, message = "No takes to check." }); return; }
                Emit("progress", new { kind = "qa", done = 0, total = todo.Count, failed = 0, message = "Loading Whisper" });
                var tool = new WavToLip(_settings.WavToLip, Tools(), WhisperModelsFolder(), e =>
                {
                    if (e.Kind == "log") App.Log("Whisper: " + e.Text);
                    else if (e.Kind == "status") Emit("progress", new { kind = "qa", done, total = todo.Count, failed, message = e.Text });
                });
                var (transcribe, engine) = await tool.CreateTranscriberAsync(ct);
                await using var _e = engine;
                var tools = Tools();
                var root = _lib.PatchFolder(patch.Id);
                foreach (var (line, take) in todo)
                {
                    ct.ThrowIfCancellationRequested();
                    var qa = new TakeQa();
                    try
                    {
                        var wav = Path.Combine(root, take.WavPath);
                        var intended = string.IsNullOrWhiteSpace(take.SpokenText) ? line.Spoken : take.SpokenText;
                        qa.Heard = (await transcribe(wav, ct)).Trim();
                        qa.Score = Math.Round(QaText.Score(intended, qa.Heard), 2);
                        (qa.Lufs, qa.TruePeak) = await tools.MeasureAsync(wav, ct);
                        if (qa.Score < QaMinScore) qa.Flags.Add("words");
                        if (qa.Lufs is { } l && l < QaQuietLufs) qa.Flags.Add("quiet");
                        if (qa.TruePeak is { } tp && tp > QaPeakDbtp) qa.Flags.Add("clipping");
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { failed++; qa.Flags.Add("error"); qa.Heard = "Could not check: " + ex.Message; }
                    if (qa.Flags.Count > 0) flagged++;
                    await _gate.WaitAsync(CancellationToken.None);
                    try { take.Qa = qa; } finally { _gate.Release(); }
                    done++;
                    Emit("progress", new { kind = "qa", done, total = todo.Count, failed, current = line.FinalLine });
                    if (done % 25 == 0) { await _gate.WaitAsync(CancellationToken.None); try { _lib.Save(patch); } finally { _gate.Release(); } }
                }
                await _gate.WaitAsync(CancellationToken.None);
                try { _lib.Save(patch); } finally { _gate.Release(); }
                Emit("jobDone", new { kind = "qa", patchId = id, ok = true, flagged, checkedCount = done,
                    message = flagged == 0 ? $"All {done} take(s) passed." : $"{flagged} of {done} take(s) flagged. Filter by \"QA flagged\" to listen to them." });
            }
            catch (OperationCanceledException)
            {
                try { await _gate.WaitAsync(CancellationToken.None); try { _lib.Save(LoadPatch(id)); } finally { _gate.Release(); } } catch { }
                Emit("jobDone", new { kind = "qa", patchId = id, ok = false, message = $"Cancelled after {done} take(s); their results are kept." });
            }
            catch (Exception ex)
            {
                App.Log("Check takes failed: " + ex);
                Emit("jobDone", new { kind = "qa", patchId = id, ok = false, message = ex.Message });
            }
            finally { _jobCts = null; }
        });
        return true;
    }
}
