using DbvoStudio.Core.Audio;
using DbvoStudio.Core.ElevenLabs;
using DbvoStudio.Core.Engines;
using DbvoStudio.Core.Naming;
using DbvoStudio.Core.Plugins;
using DbvoStudio.Core.Rules;
using DbvoStudio.Core.Tools;
using DbvoStudio.Core.Topics;

namespace DbvoStudio.Core.Projects;

public sealed class SyncReport
{
    public int Total { get; set; }
    public int Added { get; set; }
    public int Removed { get; set; }
    public int Omitted { get; set; }
    public int Stale { get; set; }
}

public sealed record GenerationProgress(int Done, int Total, int Failed, string? Current, string? Message);

public sealed class ExportReport
{
    public int Written { get; set; }
    public int Skipped { get; set; }
    public int Failed { get; set; }
    public List<string> Errors { get; } = new();
}

/// <summary>Phase 2: build the line list for a DBVO Patch, generate takes, and export WAV + LIP and .fuz.</summary>
public sealed class Phase2Service
{
    private readonly Library _lib;
    private readonly AppSettings _settings;

    public Phase2Service(Library lib, AppSettings settings)
    {
        _lib = lib;
        _settings = settings;
    }

    public string TakesFolder(DbvoPatch p) => Path.Combine(_lib.PatchFolder(p.Id), "takes");

    /// <summary>The patch's own WAV + LIP folder (filled from the voice pack template when the patch was made).</summary>
    public string WavLipFolder(DbvoPatch p, VoicePack v) => p.WavLipFolder;

    /// <summary>The patch's final mod folder: receives the DBVO Fix plugin; .fuz files go below it.</summary>
    public string FinalRoot(DbvoPatch p, VoicePack v) => p.FinalRoot;

    /// <summary>Sub-folder for .fuz files under the final folder, e.g. Sound\DBVO\{packId}.</summary>
    public string FuzTemplate(DbvoPatch p, VoicePack v) =>
        !string.IsNullOrWhiteSpace(p.FuzTemplate) ? p.FuzTemplate
        : !string.IsNullOrWhiteSpace(v.Destinations.FuzTemplate) ? v.Destinations.FuzTemplate : @"Sound\DBVO\{packId}";

    /// <summary>Expand a folder template: {mod}, {packId}, {voicePack}, {patch}.</summary>
    public static string ExpandTemplate(string template, string modName, VoicePack v, DbvoPatch p) =>
        template.Replace("{mod}", Safe(modName), StringComparison.OrdinalIgnoreCase)
            .Replace("{packId}", Safe(v.PackId), StringComparison.OrdinalIgnoreCase)
            .Replace("{voicePack}", Safe(v.Name), StringComparison.OrdinalIgnoreCase)
            .Replace("{patch}", Safe(p.Name), StringComparison.OrdinalIgnoreCase);

    private static string Safe(string s) => string.Concat(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();

    /// <summary>Keys of every line in the given reference sets, by naming mode.</summary>
    public Dictionary<string, string> ReferenceKeys(IEnumerable<string> referenceIds, NamingMode mode)
    {
        var keys = new Dictionary<string, string>();
        foreach (var id in referenceIds)
        {
            var r = _settings.References.FirstOrDefault(x => x.Id == id);
            if (r == null || !File.Exists(r.StoredFile)) continue;
            foreach (var line in TopicFile.Read(r.StoredFile))
                keys.TryAdd(LineNaming.Key(line, mode), r.Name);
        }
        return keys;
    }

    /// <summary>
    /// Rebuild the patch's lines from its mods' Phase 1 results. Existing lines keep their spoken text,
    /// takes and status (matched by naming key). Lines found in active reference sets are marked omitted.
    /// </summary>
    public SyncReport Sync(DbvoPatch patch, VoicePack voice, IReadOnlyList<Mod> mods)
    {
        var report = new SyncReport();
        var refKeys = ReferenceKeys(patch.ReferenceIds, voice.Naming);
        var old = patch.Lines.GroupBy(l => l.Key).ToDictionary(g => g.Key, g => g.First());
        var result = new Dictionary<string, Phase2Line>();

        foreach (var mod in mods.Where(m => patch.ModIds.Contains(m.Id)))
        {
            foreach (var plugin in mod.Plugins)
            {
                foreach (var l in plugin.Lines.OrderBy(x => x.Order))
                {
                    foreach (var final in l.TopicLines())
                    {
                        var key = LineNaming.Key(final, voice.Naming);
                        if (!result.TryGetValue(key, out var line))
                        {
                            if (old.TryGetValue(key, out var prev))
                            {
                                line = prev;
                                line.FinalLine = final;
                                line.Sources.Clear();
                            }
                            else
                            {
                                line = new Phase2Line
                                {
                                    Key = key,
                                    LineId = NewId.FromKey(key),
                                    FinalLine = final,
                                    Spoken = SpokenText.DefaultFor(final),
                                };
                                report.Added++;
                            }
                            line.OriginalLine = l.Variants.Contains(final) ? final : l.Original;
                            result[key] = line;
                        }
                        line.Sources.Add(new LineSource { ModId = mod.Id, Plugin = plugin.FileName, FormId = l.FormId, Field = l.Field });
                    }
                }
            }
        }

        foreach (var line in result.Values)
        {
            line.OmittedBy = refKeys.TryGetValue(line.Key, out var name) ? name : null;
            if (line.IsOmitted) report.Omitted++;
            if (line.Status == Phase2Status.Omitted && !line.IsOmitted) line.Status = line.Takes.Count > 0 ? Phase2Status.Generated : Phase2Status.Pending;
            if (line.IsOmitted && line.Takes.Count == 0) line.Status = Phase2Status.Omitted;
            if (line.SelectedTake != null && line.SelectedTake.SpokenText != line.Spoken && line.Status == Phase2Status.Generated)
            {
                line.Status = Phase2Status.Stale;
                report.Stale++;
            }
        }
        report.Removed = old.Keys.Count(k => !result.ContainsKey(k));
        patch.Lines = result.Values.ToList();
        report.Total = patch.Lines.Count;
        if (patch.Kind == "vc") MatchSources(patch);
        _lib.Save(patch);
        return report;
    }

    /// <summary>Lines eligible for generation: not omitted, and (unless <paramref name="regenerate"/>) without a usable take.</summary>
    public static List<Phase2Line> Eligible(DbvoPatch patch, bool regenerate, IEnumerable<string>? onlyLineIds = null)
    {
        var only = onlyLineIds?.ToHashSet();
        return patch.Lines.Where(l => !l.IsOmitted)
            .Where(l => patch.Kind != "vc" || l.SourceWav != null)
            .Where(l => only == null || only.Contains(l.LineId))
            .Where(l => regenerate || only != null || l.Status is Phase2Status.Pending or Phase2Status.Failed or Phase2Status.Stale)
            .ToList();
    }

    /// <summary>
    /// The global DBVO dictionary plus the voice pack's own; for the same word the voice pack wins.
    /// Longer words come first, so "Alduin's" is handled before "Alduin".
    /// </summary>
    public static List<Pronunciation> MergePronunciations(IEnumerable<Pronunciation> global, IEnumerable<Pronunciation> voice)
    {
        var map = new Dictionary<string, Pronunciation>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in global.Where(p => !string.IsNullOrWhiteSpace(p.Word))) map[p.Word.Trim()] = p;
        foreach (var p in voice.Where(p => !string.IsNullOrWhiteSpace(p.Word))) map[p.Word.Trim()] = p;
        return map.Values.OrderByDescending(p => p.Word.Trim().Length).ToList();
    }

    /// <summary>Apply the pronunciation dictionary (local layer) to the spoken text before sending.</summary>
    public static string ApplyPronunciations(string text, IEnumerable<Pronunciation> entries, string modelId)
    {
        var t = text;
        foreach (var e in entries.Where(e => !string.IsNullOrWhiteSpace(e.Word)))
        {
            string replacement = e.Kind == "phoneme"
                ? (ModelCapabilities.SupportsPhonemeRules(modelId) ? "/" + e.Replacement.Trim('/') + "/" : e.Word)
                : e.Replacement;
            var pattern = System.Text.RegularExpressions.Regex.Escape(e.Word);
            if (e.WholeWord) pattern = @"(?<![\p{L}\p{N}_])" + pattern + @"(?![\p{L}\p{N}_])";
            var opts = e.CaseSensitive ? System.Text.RegularExpressions.RegexOptions.None : System.Text.RegularExpressions.RegexOptions.IgnoreCase;
            t = System.Text.RegularExpressions.Regex.Replace(t, pattern, replacement.Replace("$", "$$"), opts);
        }
        return t;
    }

    /// <summary>Audio for one line from the voice pack's engine, before conversion.</summary>
    private sealed record EngineAudio(byte[] Audio, string Format, TakeSettings Settings);

    /// <summary>
    /// Generate one take for each line: engine (ElevenLabs, VoxCPM, OmniVoice, or ElevenLabs speech-to-speech for
    /// Voice Changer patches) -> WAV (44.1 kHz mono 16-bit) -> trim 15 ms + fade 10 ms -> LIP from the trimmed WAV
    /// with tags removed. Tools only see short work-folder paths; finished files are saved as
    /// takes\&lt;lineId&gt;\NN\&lt;stem&gt;.wav/.lip and the new take is selected.
    /// </summary>
    /// <param name="stateGate">Lock shared with any other code that edits or saves the same patch object.</param>
    public async Task GenerateAsync(DbvoPatch patch, VoicePack voice, IReadOnlyList<Phase2Line> lines, ExternalTools tools,
        IProgress<GenerationProgress>? progress = null, Func<Phase2Line, Task>? onLineDone = null, CancellationToken ct = default,
        SemaphoreSlim? stateGate = null)
    {
        bool vc = patch.Kind == "vc";
        string engine = vc ? "sts" : voice.Engine;
        var es = voice.Eleven.Clone();
        var sts = voice.Sts;
        var pronunciations = MergePronunciations(_settings.Pronunciations, voice.Pronunciations);
        ElevenLabsClient? client = null;
        LocalEngineClient? local = null;
        int concurrency;
        switch (engine)
        {
            case "sts":
                if (string.IsNullOrWhiteSpace(_settings.ElevenLabsApiKey)) throw new InvalidOperationException("Enter your ElevenLabs API key in Settings.");
                if (string.IsNullOrWhiteSpace(sts.VoiceId)) throw new InvalidOperationException("Pick the target voice for the Voice Changer.");
                client = new ElevenLabsClient(_settings.ElevenLabsApiKey);
                concurrency = sts.Concurrency;
                break;
            case LocalEngineClient.VoxCpm:
                local = new LocalEngineClient(_settings.LocalEngines.VoxCpmAddress);
                if (string.IsNullOrWhiteSpace(voice.VoxCpm.Voice)) throw new InvalidOperationException("Pick a VoxCPM reference voice for this voice pack.");
                concurrency = voice.VoxCpm.Concurrency;
                break;
            case LocalEngineClient.OmniVoice:
                local = new LocalEngineClient(_settings.LocalEngines.OmniVoiceAddress);
                if (string.IsNullOrWhiteSpace(voice.OmniVoice.Voice)) throw new InvalidOperationException("Pick an OmniVoice reference voice for this voice pack.");
                concurrency = voice.OmniVoice.Concurrency;
                break;
            default:
                if (string.IsNullOrWhiteSpace(_settings.ElevenLabsApiKey)) throw new InvalidOperationException("Enter your ElevenLabs API key in Settings.");
                if (string.IsNullOrWhiteSpace(es.VoiceId)) throw new InvalidOperationException("Pick a voice for this voice pack.");
                client = new ElevenLabsClient(_settings.ElevenLabsApiKey);
                concurrency = es.Concurrency;
                break;
        }
        using var _c = client;
        using var _l = local;
        var limiter = new AdaptiveLimiter(Math.Max(1, concurrency));
        var workRoot = Path.Combine(Path.GetTempPath(), "dbvo");
        Directory.CreateDirectory(workRoot);
        int done = 0, failed = 0, total = lines.Count;
        var saveLock = stateGate ?? new SemaphoreSlim(1, 1);
        var convertGate = new SemaphoreSlim(Math.Max(2, Environment.ProcessorCount / 2));

        async Task<EngineAudio> Synthesize(Phase2Line line, CancellationToken c)
        {
            switch (engine)
            {
                case "sts":
                {
                    if (line.SourceWav == null || !File.Exists(line.SourceWav)) throw new InvalidOperationException("No source WAV found for this line in the source folders.");
                    var bytes = await File.ReadAllBytesAsync(line.SourceWav, c);
                    var vs = ModelCapabilities.Build(sts.ModelId, sts.Stability, sts.Similarity, sts.Style, sts.SpeakerBoost, 1.0);
                    if (vs != null) vs.Speed = null;
                    var audio = await limiter.RunAsync(x => client!.SpeechToSpeechAsync(sts.VoiceId, sts.ModelId, bytes, "source.wav", vs, sts.Seed, sts.RemoveBackgroundNoise, sts.OutputFormat, x),
                        ex => ex is ElevenLabsException { Retryable: true }, ct: c);
                    return new EngineAudio(audio, sts.OutputFormat, new TakeSettings
                    {
                        Engine = "elevenlabs-sts", VoiceId = sts.VoiceId, ModelId = sts.ModelId, Stability = sts.Stability, Similarity = sts.Similarity,
                        Style = sts.Style, SpeakerBoost = sts.SpeakerBoost, Speed = 1, Seed = sts.Seed, OutputFormat = sts.OutputFormat,
                    });
                }
                case LocalEngineClient.VoxCpm:
                {
                    var text = ApplyPronunciations(SpokenText.StripTags(line.Spoken), pronunciations, "local");
                    var audio = await limiter.RunAsync(x => local!.VoxCpmAsync(text, voice.VoxCpm, x), ex => ex is HttpRequestException, ct: c);
                    return new EngineAudio(audio, "wav", new TakeSettings { Engine = "voxcpm", VoiceId = voice.VoxCpm.Voice, ModelId = "voxcpm", Stability = voice.VoxCpm.CfgValue, OutputFormat = "wav" });
                }
                case LocalEngineClient.OmniVoice:
                {
                    var text = ApplyPronunciations(SpokenText.StripTags(line.Spoken), pronunciations, "local");
                    var audio = await limiter.RunAsync(x => local!.OmniVoiceAsync(text, voice.OmniVoice, x), ex => ex is HttpRequestException, ct: c);
                    return new EngineAudio(audio, "wav", new TakeSettings { Engine = "omnivoice", VoiceId = voice.OmniVoice.Voice, ModelId = voice.OmniVoice.Model, Speed = voice.OmniVoice.Speed, OutputFormat = "wav" });
                }
                default:
                {
                    var text = ApplyPronunciations(line.Spoken, pronunciations, es.ModelId);
                    var req = new TtsRequest
                    {
                        VoiceId = es.VoiceId,
                        ModelId = es.ModelId,
                        Text = text,
                        OutputFormat = es.OutputFormat,
                        Seed = es.Seed,
                        LanguageCode = string.IsNullOrWhiteSpace(es.LanguageCode) ? null : es.LanguageCode,
                        ApplyTextNormalization = es.TextNormalization == "auto" ? null : es.TextNormalization,
                        VoiceSettings = ModelCapabilities.Build(es.ModelId, es.Stability, es.Similarity, es.Style, es.SpeakerBoost, es.Speed),
                        PronunciationDictionaries = es.PronunciationDictionaryIds.Count == 0 ? null
                            : es.PronunciationDictionaryIds.Take(3).Select(id => new PronunciationLocator { DictionaryId = id }).ToList(),
                    };
                    var audio = await limiter.RunAsync(x => client!.TextToSpeechAsync(req, x), ex => ex is ElevenLabsException { Retryable: true }, ct: c);
                    return new EngineAudio(audio, es.OutputFormat, new TakeSettings
                    {
                        Engine = "elevenlabs", VoiceId = es.VoiceId, ModelId = es.ModelId, Stability = req.VoiceSettings?.Stability ?? 0,
                        Similarity = es.Similarity, Style = es.Style, SpeakerBoost = es.SpeakerBoost, Speed = es.Speed, Seed = es.Seed, OutputFormat = es.OutputFormat,
                    });
                }
            }
        }

        async Task One(Phase2Line line)
        {
            ct.ThrowIfCancellationRequested();
            line.Status = Phase2Status.Generating;
            line.LastError = null;
            Take? newTake = null;
            try
            {
                var got = await Synthesize(line, ct);
                await convertGate.WaitAsync(ct);
                try
                {
                    using var work = new WorkFolder(workRoot);
                    var wav = work.File("a.wav");
                    if (got.Format.StartsWith("pcm_", StringComparison.OrdinalIgnoreCase))
                    {
                        int rate = int.Parse(got.Format.Split('_')[1]);
                        var raw = work.File("raw.wav");
                        ExternalTools.PcmToWav(got.Audio, rate, raw);
                        await tools.ToWavAsync(raw, wav, 44100, voice.Loudnorm, voice.GainDb, ct);
                    }
                    else
                    {
                        var src = work.File(got.Format == "wav" ? "src.wav" : "a.mp3");
                        await File.WriteAllBytesAsync(src, got.Audio, ct);
                        await tools.ToWavAsync(src, wav, 44100, voice.Loudnorm, voice.GainDb, ct);
                    }

                    var w = WavFile.Read(wav);
                    w.TrimTailAndFade(voice.TrimTailMs, voice.FadeOutMs);
                    w.Write(wav);

                    var lip = work.File("a.lip");
                    await tools.MakeLipAsync(wav, work.File("r.wav"), lip, SpokenText.StripTags(line.Spoken), ct);

                    var take = new Take
                    {
                        Slot = line.Takes.Count == 0 ? 1 : line.Takes.ToList().Max(t => t.Slot) + 1,
                        SpokenText = line.Spoken,
                        DurationSeconds = w.DurationSeconds,
                        Settings = got.Settings,
                    };
                    var stem = LineNaming.Stem(line.FinalLine, voice.Naming);
                    var rel = Path.Combine("takes", line.LineId, take.Slot.ToString("00"));
                    var dir = Path.Combine(_lib.PatchFolder(patch.Id), rel);
                    Directory.CreateDirectory(dir);
                    take.WavPath = Path.Combine(rel, stem + ".wav");
                    take.LipPath = Path.Combine(rel, stem + ".lip");
                    File.Copy(wav, Path.Combine(_lib.PatchFolder(patch.Id), take.WavPath), true);
                    File.Copy(lip, Path.Combine(_lib.PatchFolder(patch.Id), take.LipPath), true);
                    newTake = take;
                }
                finally { convertGate.Release(); }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                line.Status = line.Takes.Count > 0 ? Phase2Status.Generated : Phase2Status.Pending;
                throw;
            }
            catch (Exception ex)
            {
                line.Status = Phase2Status.Failed;
                line.LastError = ex.Message;
                Interlocked.Increment(ref failed);
            }
            finally
            {
                int d = Interlocked.Increment(ref done);
                progress?.Report(new GenerationProgress(d, total, failed, line.FinalLine, line.LastError));
                await saveLock.WaitAsync(CancellationToken.None);
                try
                {
                    if (newTake != null)
                    {
                        line.Takes.Add(newTake);
                        line.SelectedTakeId = newTake.Id;
                        line.Status = Phase2Status.Generated;
                    }
                    _lib.Save(patch);
                    if (onLineDone != null) await onLineDone(line);
                }
                finally { saveLock.Release(); }
            }
        }

        // Network concurrency is enforced by the limiter; convertGate only bounds the CPU-bound conversion stage.
        await Task.WhenAll(lines.Select(One));
    }

    /// <summary>
    /// Voice Changer: find each line's source WAV in the source folders (with sub-folders). Files are matched by
    /// both naming modes, so a Legacy source pack works for a DBVO 2.0 target and the other way round.
    /// Returns how many lines have a source.
    /// </summary>
    public static int MatchSources(DbvoPatch patch)
    {
        var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in patch.SourceFolders.Where(Directory.Exists))
        {
            foreach (var f in Directory.EnumerateFiles(folder, "*.wav", SearchOption.AllDirectories))
                index.TryAdd(Path.GetFileNameWithoutExtension(f), f);
        }
        int found = 0;
        foreach (var line in patch.Lines)
        {
            line.SourceWav = index.TryGetValue(LineNaming.Stem(line.FinalLine, NamingMode.Legacy), out var a) ? a
                : index.TryGetValue(LineNaming.Stem(line.FinalLine, NamingMode.Dbvo2Hash), out var b) ? b : null;
            if (line.SourceWav != null) found++;
        }
        return found;
    }

    /// <summary>Seconds of audio in a WAV file, from its header (0 if unreadable).</summary>
    public static double WavSeconds(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            using var br = new BinaryReader(fs);
            if (new string(br.ReadChars(4)) != "RIFF") return 0;
            br.ReadInt32();
            if (new string(br.ReadChars(4)) != "WAVE") return 0;
            int byteRate = 0;
            while (fs.Position + 8 <= fs.Length)
            {
                var id = new string(br.ReadChars(4));
                int size = br.ReadInt32();
                if (id == "fmt ")
                {
                    var fmt = br.ReadBytes(size);
                    byteRate = BitConverter.ToInt32(fmt, 8);
                }
                else if (id == "data") return byteRate > 0 ? (double)size / byteRate : 0;
                else fs.Seek(size + (size & 1), SeekOrigin.Current);
            }
        }
        catch { }
        return 0;
    }

    /// <summary>Copy (hardlink) each selected take into the WAV + LIP folder. Existing files are skipped unless overwrite is on.</summary>
    public ExportReport ExportWavLip(DbvoPatch patch, VoicePack voice)
    {
        var rep = new ExportReport();
        var folder = WavLipFolder(patch, voice);
        if (string.IsNullOrWhiteSpace(folder)) throw new InvalidOperationException("Set the WAV + LIP folder first.");
        foreach (var line in patch.Lines.Where(l => !l.IsOmitted && l.SelectedTake != null))
        {
            var t = line.SelectedTake!;
            var stem = LineNaming.Stem(line.FinalLine, voice.Naming);
            try
            {
                bool any = false;
                foreach (var (srcRel, ext) in new[] { (t.WavPath, ".wav"), (t.LipPath, ".lip") })
                {
                    var target = Path.Combine(folder, stem + ext);
                    if (File.Exists(target) && !patch.OverwriteWavLip) continue;
                    FileLinks.LinkOrCopy(Path.Combine(_lib.PatchFolder(patch.Id), srcRel), target, true);
                    any = true;
                }
                if (any) rep.Written++; else rep.Skipped++;
            }
            catch (Exception ex)
            {
                rep.Failed++;
                rep.Errors.Add($"{stem}: {ex.Message}");
            }
        }
        return rep;
    }

    /// <summary>
    /// Build .fuz files from the selected takes into each source mod's template folder (or one custom folder).
    /// A line shared by several mods is encoded once and hardlinked into each folder.
    /// </summary>
    public async Task<ExportReport> ExportFuzAsync(DbvoPatch patch, VoicePack voice, IReadOnlyList<Mod> mods, ExternalTools tools,
        string? customFolder = null, IProgress<GenerationProgress>? progress = null, CancellationToken ct = default)
    {
        var rep = new ExportReport();
        var root = FinalRoot(patch, voice);
        var template = FuzTemplate(patch, voice);
        if (customFolder == null && string.IsNullOrWhiteSpace(root)) throw new InvalidOperationException("Set the final folder first.");
        var workRoot = Path.Combine(Path.GetTempPath(), "dbvo");
        var lines = patch.Lines.Where(l => !l.IsOmitted && l.SelectedTake != null).ToList();
        int done = 0;
        var gate = new SemaphoreSlim(Math.Max(2, Environment.ProcessorCount / 2));

        await Task.WhenAll(lines.Select(async line =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var stem = LineNaming.Stem(line.FinalLine, voice.Naming);
                var folders = customFolder != null
                    ? new List<string> { customFolder }
                    : line.Sources.Select(s => mods.FirstOrDefault(m => m.Id == s.ModId)?.Name ?? "Mod").Distinct()
                        .Select(name => Path.Combine(root, ExpandTemplate(template, name, voice, patch))).ToList();
                var targets = folders.Select(f => Path.Combine(f, stem + ".fuz")).ToList();
                var needed = targets.Where(t => patch.OverwriteFuz || !File.Exists(t)).ToList();
                if (needed.Count == 0)
                {
                    lock (rep) rep.Skipped++;
                    return;
                }
                using var work = new WorkFolder(workRoot);
                var t = line.SelectedTake!;
                var wav = work.File("a.wav");
                File.Copy(Path.Combine(_lib.PatchFolder(patch.Id), t.WavPath), wav, true);
                var xwm = work.File("a.xwm");
                await tools.ToXwmaAsync(wav, xwm, 48000, ct);
                var fuz = FuzFile.Build(await File.ReadAllBytesAsync(Path.Combine(_lib.PatchFolder(patch.Id), t.LipPath), ct), await File.ReadAllBytesAsync(xwm, ct));
                var first = needed[0];
                Directory.CreateDirectory(Path.GetDirectoryName(first)!);
                await File.WriteAllBytesAsync(first, fuz, ct);
                foreach (var other in needed.Skip(1)) FileLinks.LinkOrCopy(first, other, true);
                lock (rep) rep.Written++;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                lock (rep)
                {
                    rep.Failed++;
                    rep.Errors.Add($"{line.FinalLine}: {ex.Message}");
                }
            }
            finally
            {
                gate.Release();
                int d = Interlocked.Increment(ref done);
                progress?.Report(new GenerationProgress(d, lines.Count, rep.Failed, line.FinalLine, null));
            }
        }));
        return rep;
    }

    /// <summary>Reminders shown at the end of Phase 2: unfixed TOO LONG lines (Legacy) and unreviewed BROKEN lines.</summary>
    public static List<string> EndOfPhaseReminders(DbvoPatch patch, VoicePack voice, IReadOnlyList<Mod> mods)
    {
        var list = new List<string>();
        foreach (var mod in mods.Where(m => patch.ModIds.Contains(m.Id)))
        foreach (var p in mod.Plugins)
        {
            var active = p.Lines.Where(l => l.Status != Phase1Status.Removed && !l.Excluded).ToList();
            if (voice.Naming == NamingMode.Legacy)
            {
                int tooLong = active.Count(l => l.Kind == LineKind.TooLong && !l.UserEdited);
                if (tooLong > 0) list.Add($"{p.FileName}: {tooLong} TOO LONG WILL CTD line(s) were not fixed in {p.FixFileName}.");
            }
            int broken = active.Count(l => l.Kind == LineKind.Broken && !l.Reviewed);
            if (broken > 0) list.Add($"{p.FileName}: {broken} BROKEN LINE row(s) were never reviewed.");
        }
        return list;
    }
}
