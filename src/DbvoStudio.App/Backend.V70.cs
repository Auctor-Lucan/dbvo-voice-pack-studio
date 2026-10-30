using DbvoStudio.Core.Audio;
using DbvoStudio.Core.Revoice;
using DbvoStudio.Core.Text;
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
            case "patch.qa": return StartQa(p, false);
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
                    var rows = patch.Lines.Where(l => !l.IsOmitted).Select(l => new SpokenCsv.Row(l.LineId, l.FileStem ?? LineNaming.Stem(l.FinalLine, voice.Naming), l.FinalLine, l.Spoken)).ToList();
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
                    var byName = patch.Lines.GroupBy(l => l.FileStem ?? LineNaming.Stem(l.FinalLine, voice.Naming), StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
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
                if (_jobCts != null) throw new InvalidOperationException("Another task is still running (see the bar at the top). Wait for it to finish, or press Cancel there.");
                var (files, bytes) = new FuzCache(_lib.FuzCacheFolder).Clear();
                return new { files, bytes };
            }
            case "patch.mergeTakes":
                return await Locked(() =>
                {
                    NoUndoHere();
                    if (_jobCts != null) throw new InvalidOperationException("Another task is still running (see the bar at the top). Wait for it to finish, or press Cancel there.");
                    var patch = LoadPatch(S(p, "id"));
                    var other = LoadPatch(S(p, "sourceId"));
                    _lib.Backup("before merging takes");
                    var (takes, lines, onlyThere) = new Phase2Service(_lib, Runtime()).MergeTakes(patch, LoadVoice(patch.VoicePackId), other);
                    return new { takes, lines, onlyThere, patch = PatchDto(patch) };
                });
            case "patch.record":
            {
                if (_jobCts != null) throw new InvalidOperationException("Another task is still running (see the bar at the top). Wait for it to finish, or press Cancel there.");
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

    /// <summary>Check takes options, chosen in the dialog before the run.</summary>
    private sealed record QaOptions(bool Words, bool Levels, bool OnlyUnchecked, string Model, string Language, bool Quick, bool Onset, string OnsetSensitivity, bool Peaks, double PeaksDb);

    private QaOptions QaOptionsFrom(JsonElement p) => new(
        Words: !p.TryGetProperty("words", out var w) || w.ValueKind != JsonValueKind.False,
        Levels: !p.TryGetProperty("levels", out var lv) || lv.ValueKind != JsonValueKind.False,
        OnlyUnchecked: B(p, "onlyUnchecked"),
        Model: S(p, "model", "large-v3-turbo"),
        Language: S(p, "language", "en"),
        Quick: !p.TryGetProperty("quick", out var q) || q.ValueKind != JsonValueKind.False,
        Onset: !p.TryGetProperty("onset", out var on) || on.ValueKind != JsonValueKind.False,
        OnsetSensitivity: S(p, "onsetSensitivity", "normal"),
        Peaks: B(p, "peaks"),
        PeaksDb: p.TryGetProperty("peaksDb", out var pd) && pd.ValueKind == JsonValueKind.Number ? Math.Clamp(pd.GetDouble(), 8, 24) : 14);

    /// <summary>
    /// Check takes for a DBVO Patch (revoice = false) or an NPC Revoice project. Whisper transcribes the take in use of each
    /// line (with DBVO 2.0 naming in a patch, its kept variants too) and the words are compared with the text the take was
    /// made from; ffmpeg measures loudness and true peak in the same pass that prepares Whisper's 16 kHz copy. Up to six takes
    /// are prepared ahead on the CPU while Whisper works, and the project is saved every 30 seconds. Nothing is deleted.
    /// </summary>
    private object StartQa(JsonElement p, bool revoice)
    {
        if (_jobCts != null) throw new InvalidOperationException("Another task is still running (see the bar at the top). Wait for it to finish, or press Cancel there.");
        var id = S(p, "id");
        var lineIds = p.TryGetProperty("lineIds", out var li) && li.ValueKind == JsonValueKind.Array ? Strings(p, "lineIds").ToHashSet() : null;
        var o = QaOptionsFrom(p);
        if (!o.Words && !o.Levels && !o.Onset && !o.Peaks) throw new InvalidOperationException("Pick at least one check: the words, loudness and clipping, sharp peaks, or the start of each take.");
        _jobCts = NewJob();
        var ct = _jobCts.Token;
        string kind = revoice ? "revoice-qa" : "qa";
        _ = Task.Run(async () =>
        {
            int flagged = 0, done = 0, failed = 0;
            object? project = null;
            void Save() { if (project is DbvoPatch dp) _lib.Save(dp); else if (project is RevoiceProject rp) _lib.Save(rp); }
            object DoneMsg(bool ok, string message) => revoice
                ? new { kind, projectId = id, ok, flagged, checkedCount = done, message }
                : new { kind, patchId = id, ok, flagged, checkedCount = done, message };
            var temp = new List<string>();
            try
            {
                List<(Phase2Line Line, Take Take)> todo;
                string root;
                if (revoice)
                {
                    var rp = LoadRv(id); project = rp; root = _lib.RevoiceFolder(rp.Id);
                    todo = rp.Lines.Where(l => !l.Excluded && !l.Gone && l.SelectedTake != null && (lineIds == null || lineIds.Contains(l.LineId)))
                        .Select(l => ((Phase2Line)l, l.SelectedTake!)).ToList();
                }
                else
                {
                    var patch = LoadPatch(id); project = patch; root = _lib.PatchFolder(patch.Id);
                    var naming = LoadVoice(patch.VoicePackId).Naming;
                    todo = patch.Lines.Where(l => !l.IsOmitted && !l.Excluded && l.SelectedTake != null && (lineIds == null || lineIds.Contains(l.LineId)))
                        .SelectMany(l => Phase2Service.ExportTakes(l, naming).Select(x => x.Take).Append(l.SelectedTake!).Distinct().Select(t => (l, t))).ToList();
                }
                if (o.OnlyUnchecked) todo = todo.Where(x => x.Take.Qa == null).ToList();
                if (todo.Count == 0) { Emit("jobDone", DoneMsg(true, o.OnlyUnchecked ? "Every take is checked already." : "No takes to check.")); return; }

                Func<string, CancellationToken, Task<string>>? transcribe = null;
                IAsyncDisposable? engine = null;
                bool prepared16k = false;
                Func<string> device = () => "";
                if (o.Words)
                {
                    Emit("progress", new { kind, done = 0, total = todo.Count, failed = 0, message = $"Loading Whisper {o.Model}" });
                    var ws = JsonSerializer.Deserialize<WavToLipSettings>(JsonSerializer.Serialize(_settings.WavToLip))!;
                    ws.Model = o.Model; ws.Language = o.Language; ws.Quick = o.Quick;
                    var tool = new WavToLip(ws, Tools(), WhisperModelsFolder(), e =>
                    {
                        if (e.Kind == "log") App.Log("Whisper: " + e.Text);
                        else if (e.Kind == "status") Emit("progress", new { kind, done, total = todo.Count, failed, message = e.Text });
                    });
                    (transcribe, engine, prepared16k, device) = await tool.CreateCheckerAsync(ct);
                }
                await using var _e = engine;
                var tools = Tools();
                var tmpDir = Path.Combine(WavToLip.CheckTempFolder, "check");
                Directory.CreateDirectory(tmpDir);

                // Prepare ahead (ffmpeg: loudness + the 16 kHz copy) on a few CPU threads while Whisper works on the GPU.
                int ahead = Math.Clamp(Environment.ProcessorCount / 2, 2, 6);
                var prep = new Task<(string? Wav16, double? Lufs, double? Peak, string? Error, OnsetResult? Onset, double? Above)>[todo.Count];
                var cpu = new SemaphoreSlim(ahead);
                Task<(string?, double?, double?, string?, OnsetResult?, double?)> Prepare(int i) => Task.Run(async () =>
                {
                    await cpu.WaitAsync(ct);
                    try
                    {
                        var wav = Path.Combine(root, todo[i].Take.WavPath);
                        string? w16 = o.Words && prepared16k ? Path.Combine(tmpDir, Guid.NewGuid().ToString("N")[..10] + ".wav") : null;
                        if (w16 != null) lock (temp) temp.Add(w16);
                        WavFile? wf = o.Onset || o.Peaks ? WavFile.Read(wav) : null;
                        OnsetResult? onset = o.Onset ? OnsetCheck.Analyze(wf!, o.OnsetSensitivity) : null;
                        double? above = o.Peaks ? PeakSoftener.PeakAboveSpeechDb(wf!.Samples, wf.SampleRate, wf.Channels) : null;
                        if (!o.Levels && w16 == null) return ((string?)null, (double?)null, (double?)null, (string?)null, onset, above);
                        var (lufs, peak) = await tools.PrepareForCheckAsync(wav, w16, ct);
                        return (w16, lufs, peak, (string?)null, onset, above);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { return ((string?)null, (double?)null, (double?)null, FriendlyError.From(ex), (OnsetResult?)null, (double?)null); }
                    finally { cpu.Release(); }
                }, ct);
                for (int i = 0; i < Math.Min(todo.Count, ahead * 2); i++) prep[i] = Prepare(i);

                var sinceSave = System.Diagnostics.Stopwatch.StartNew();
                var rate = System.Diagnostics.Stopwatch.StartNew();
                for (int i = 0; i < todo.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    int next = i + ahead * 2;
                    if (next < todo.Count && prep[next] == null) prep[next] = Prepare(next);
                    var (line, take) = todo[i];
                    var qa = new TakeQa();
                    try
                    {
                        var (w16, lufs, peak, error, onset, above) = await prep[i];
                        if (error != null) throw new InvalidOperationException(error);
                        if (o.Peaks && above is { } ab)
                        {
                            qa.PeakAboveDb = Math.Round(ab, 1);
                            if (ab > o.PeaksDb) qa.Flags.Add("peaks");
                        }
                        if (onset is { Flagged: true })
                        {
                            qa.OnsetMs = onset.BlipMs; qa.OnsetGapMs = onset.GapMs;
                            // A take you approved in Review keeps its approval: the numbers are recorded, the flag is not raised.
                            if (!(line.Approved && take == line.SelectedTake)) qa.Flags.Add("onset");
                        }
                        if (o.Levels)
                        {
                            (qa.Lufs, qa.TruePeak) = (lufs, peak);
                            if (qa.Lufs is { } l && l < QaQuietLufs) qa.Flags.Add("quiet");
                            if (qa.TruePeak is { } tp && tp > QaPeakDbtp) qa.Flags.Add("clipping");
                        }
                        if (o.Words && transcribe != null)
                        {
                            var intended = string.IsNullOrWhiteSpace(take.SpokenText) ? line.Spoken : take.SpokenText;
                            qa.Heard = (await transcribe(w16 ?? Path.Combine(root, take.WavPath), ct)).Trim();
                            qa.Score = Math.Round(QaText.Score(intended, qa.Heard), 2);
                            if (qa.Score < QaMinScore) qa.Flags.Add("words");
                        }
                        if (w16 != null) { try { File.Delete(w16); } catch { } }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { failed++; qa.Flags.Add("error"); qa.Heard = "Could not check: " + FriendlyError.From(ex); }
                    if (qa.Flags.Count > 0) flagged++;
                    await _gate.WaitAsync(CancellationToken.None);
                    try { take.Qa = qa; } finally { _gate.Release(); }
                    done++;
                    var perSec = done / Math.Max(0.001, rate.Elapsed.TotalSeconds);
                    Emit("progress", new { kind, done, total = todo.Count, failed, current = line.FinalLine, rate = Math.Round(perSec, 1),
                        device = o.Words ? $"Whisper {o.Model} on {device()}" : "No Whisper: loudness, peaks and start of take only" });
                    if (sinceSave.Elapsed.TotalSeconds >= 30)
                    {
                        await _gate.WaitAsync(CancellationToken.None);
                        try { Save(); } finally { _gate.Release(); }
                        sinceSave.Restart();
                    }
                }
                await _gate.WaitAsync(CancellationToken.None);
                try { Save(); } finally { _gate.Release(); }
                Emit("jobDone", DoneMsg(true, flagged == 0 ? $"All {done} take(s) passed." : $"{flagged} of {done} take(s) flagged. Filter by \"QA flagged\" to listen to them."));
            }
            catch (Exception ex) when (ex is OperationCanceledException || ct.IsCancellationRequested)
            {
                // Cancelled: whatever Whisper said while stopping is not worth showing; the results so far are kept.
                App.Log("Check takes cancelled: " + ex.Message);
                try { await _gate.WaitAsync(CancellationToken.None); try { Save(); } finally { _gate.Release(); } } catch { }
                Emit("jobDone", DoneMsg(false, $"Check takes cancelled. The {done} take(s) checked so far keep their results."));
            }
            catch (Exception ex)
            {
                App.Log("Check takes failed: " + ex);
                Emit("jobDone", DoneMsg(false, FriendlyError.From(ex)));
            }
            finally
            {
                foreach (var t in temp) { try { File.Delete(t); } catch { } }
                _jobCts = null;
            }
        });
        return true;
    }

    private object StartImportLegacy(string id, string folder)
    {
        if (_jobCts != null) throw new InvalidOperationException("Another task is still running (see the bar at the top). Wait for it to finish, or press Cancel there.");
        if (!Directory.Exists(folder)) throw new InvalidOperationException("That folder was not found.");
        _jobCts = NewJob();
        var ct = _jobCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                _lib.Backup("before importing a pack");
                var patch = LoadPatch(id);
                var voice = LoadVoice(patch.VoicePackId);
                var progress = new DirectProgress<GenerationProgress>(g => Emit("progress", new { kind = "import", g.Done, g.Total, g.Failed, g.Current }));
                int n = await new Phase2Service(_lib, Runtime()).ImportLegacyAsync(patch, voice, folder, Tools(), progress, ct, _gate);
                Emit("jobDone", new { kind = "import", patchId = id, ok = true, message = $"{n} line(s) imported from the earlier pack." });
            }
            catch (OperationCanceledException) { Emit("jobDone", new { kind = "import", patchId = id, ok = false, message = "Cancelled. Lines imported so far are kept." }); }
            catch (Exception ex)
            {
                App.Log("Pack import failed: " + ex);
                Emit("jobDone", new { kind = "import", patchId = id, ok = false, message = FriendlyError.From(ex) });
            }
            finally { _jobCts = null; }
        });
        return true;
    }


}

/// <summary>
/// Reports progress on the reporting thread, in order. (Progress&lt;T&gt; posts each report to the thread pool, so a
/// report could reach the page after the job's "done" message and bring the job bar back.)
/// </summary>
internal sealed class DirectProgress<T>(Action<T> report) : IProgress<T>
{
    private readonly object _lock = new();
    public void Report(T value) { lock (_lock) report(value); }
}

public sealed partial class Backend
{
    /// <summary>Every setting a batch will use, as label/value rows for the "Check before spending credits" dialog.</summary>
    internal static List<string[]> SettingsRows(VoicePack v, bool speechToSpeech)
    {
        static string F(double x) => x.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
        static string Seed(long? s) => s?.ToString() ?? "random";
        var rows = new List<string[]>();
        void R(string k, string val) => rows.Add(new[] { k, val });
        if (speechToSpeech)
        {
            var st = v.Sts;
            R("Voice", st.VoiceName ?? st.VoiceId ?? "— none —"); R("Model", st.ModelId);
            R("Stability", F(st.Stability)); R("Similarity", F(st.Similarity)); R("Style", F(st.Style));
            R("Speaker boost", st.SpeakerBoost ? "on" : "off"); R("Remove background noise", st.RemoveBackgroundNoise ? "on" : "off");
            R("Format", st.OutputFormat); R("Seed", Seed(st.Seed)); R("Concurrent", st.Concurrency.ToString());
        }
        else if (v.Engine == "voxcpm")
        {
            var o = v.VoxCpm;
            R("Reference voice", o.Voice ?? "— none —"); R("CFG", F(o.CfgValue)); R("Timesteps", o.InferenceTimesteps.ToString());
            R("Normalize", o.Normalize ? "on" : "off"); R("Retry bad cases", o.RetryBadcase ? "on" : "off"); R("Concurrent", o.Concurrency.ToString());
        }
        else if (v.Engine == "omnivoice")
        {
            var o = v.OmniVoice;
            R("Reference voice", o.Voice ?? "— none —"); R("Steps", o.NumStep.ToString()); R("Speed", F(o.Speed)); R("Guidance", F(o.GuidanceScale)); R("Concurrent", o.Concurrency.ToString());
        }
        else if (v.Engine == "fish")
        {
            var o = v.Fish;
            R("Voice", o.VoiceName.Length > 0 ? o.VoiceName : o.VoiceId.Length > 0 ? o.VoiceId : "— none —"); R("Model", o.Model); R("Speed", F(o.Speed));
            R("Temperature", o.Temperature > 0 ? F(o.Temperature) : "default"); R("Concurrent", o.Concurrency.ToString());
        }
        else if (v.Engine == "inworld")
        {
            var o = v.Inworld;
            R("Voice", o.VoiceName.Length > 0 ? o.VoiceName : o.VoiceId.Length > 0 ? o.VoiceId : "— none —"); R("Model", o.ModelId); R("Speaking rate", F(o.SpeakingRate)); R("Concurrent", o.Concurrency.ToString());
        }
        else if (v.Engine == "cartesia")
        {
            var o = v.Cartesia;
            R("Voice", o.VoiceName.Length > 0 ? o.VoiceName : o.VoiceId.Length > 0 ? o.VoiceId : "— none —"); R("Model", o.ModelId); R("Language", o.Language);
            R("Speed", F(o.Speed)); R("Volume", F(o.Volume)); R("Emotion", o.Emotion.Length > 0 ? o.Emotion : "from the tags"); R("Concurrent", o.Concurrency.ToString());
        }
        else if (v.Engine == "minimax")
        {
            var o = v.MiniMax;
            R("Voice", o.VoiceName.Length > 0 ? o.VoiceName : o.VoiceId.Length > 0 ? o.VoiceId : "— none —"); R("Model", o.Model); R("Speed", F(o.Speed));
            R("Volume", F(o.Volume)); R("Pitch", o.Pitch.ToString()); R("Emotion", o.Emotion.Length > 0 ? o.Emotion : "from the tags"); R("Concurrent", o.Concurrency.ToString());
        }
        else if (v.Engine == "openai")
        {
            var o = v.OpenAi;
            R("Server", o.BaseUrl); R("Model", o.Model); R("Voice", o.Voice); R("Speed", F(o.Speed));
            R("Instructions", o.Instructions.Length > 0 ? o.Instructions : "— (only the tags' words)"); R("Concurrent", o.Concurrency.ToString());
        }
        else
        {
            var e = v.Eleven;
            var vs = DbvoStudio.Core.ElevenLabs.ModelCapabilities.Build(e.ModelId, e.Stability, e.Similarity, e.Style, e.SpeakerBoost, e.Speed);
            R("Voice", e.VoiceName ?? e.VoiceId ?? "— none —"); R("Model", e.ModelId);
            R("Stability", F(vs.Stability ?? e.Stability)); R("Similarity", F(vs.SimilarityBoost ?? e.Similarity));
            if (vs.Style is { } sty) R("Style", F(sty));
            if (vs.UseSpeakerBoost is { } sb) R("Speaker boost", sb ? "on" : "off");
            R("Speed", F(vs.Speed ?? 1)); R("Language", string.IsNullOrEmpty(e.LanguageCode) ? "auto" : e.LanguageCode);
            R("Read numbers", e.TextNormalization ?? "auto"); R("Format", e.OutputFormat); R("Seed", Seed(e.Seed)); R("Concurrent", e.Concurrency.ToString());
        }
        R("Loudness", v.Loudnorm ? "normalized" : "as generated"); if (Math.Abs(v.GainDb) > 0.001) R("Gain", F(v.GainDb) + " dB");
        R("Tail trim / fade", $"{v.TrimTailMs} ms / {v.FadeOutMs} ms");
        return rows;
    }
}
