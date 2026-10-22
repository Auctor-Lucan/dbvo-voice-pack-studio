using DbvoStudio.Core.Text;
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
    /// <summary>Removed lines whose takes are kept (hidden) in case the line comes back.</summary>
    public int Parked { get; set; }
    public int Omitted { get; set; }
    public int Stale { get; set; }
    /// <summary>Lines that got a take from WAV + LIP files already in the patch's WAV + LIP folder.</summary>
    public int Adopted { get; set; }
    /// <summary>Lines without a take that have a .fuz in the final folder (can be imported).</summary>
    public int FuzOnly { get; set; }
}

public sealed record GenerationProgress(int Done, int Total, int Failed, string? Current, string? Message);

public sealed class ExportReport
{
    public int Written { get; set; }
    public int Skipped { get; set; }
    public int Failed { get; set; }
    /// <summary>.fuz files taken unchanged from an imported pack (no second encode).</summary>
    public int Reused { get; set; }
    /// <summary>.fuz files taken from the encode cache (same audio and LIP as an earlier build).</summary>
    public int Cached { get; set; }
    public List<string> Errors { get; } = new();
}

/// <summary>Phase 2: build the line list for a DBVO Patch, generate takes, and export WAV + LIP and .fuz.</summary>
public sealed partial class Phase2Service
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
    public string FuzTemplate(DbvoPatch p, VoicePack v) => ResolveFuzTemplate(p, v);

    public const string DefaultFuzTemplate = @"Sound\DBVO\{packId}";

    /// <summary>The patch's .fuz sub-folder (or the voice pack's), made valid: see <see cref="NormalizeFuzTemplate"/>.</summary>
    public static string ResolveFuzTemplate(DbvoPatch p, VoicePack v) =>
        NormalizeFuzTemplate(!string.IsNullOrWhiteSpace(p.FuzTemplate) ? p.FuzTemplate : v.Destinations.FuzTemplate, p.FinalRoot, v.Destinations.FinalRoot);

    /// <summary>
    /// A .fuz sub-folder is relative to the final folder and ends in DBVO's own Sound\DBVO\&lt;voice pack ID&gt; folder.
    /// Empty becomes Sound\DBVO\{packId}; a full path that starts with the final folder loses that part
    /// (otherwise the final folder was doubled, or the Sound\DBVO part was missing); a folder without a
    /// Sound\ part gets \Sound\DBVO\{packId} added ("Vamp - {mod}" → "Vamp - {mod}\Sound\DBVO\{packId}").
    /// </summary>
    public static string NormalizeFuzTemplate(string? template, params string?[] finalRoots)
    {
        var t = (template ?? "").Trim().Replace('/', '\\');
        if (Path.IsPathRooted(t) || (t.Length > 1 && t[1] == ':'))
        {
            string? root = finalRoots.Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => r!.Trim().Replace('/', '\\').TrimEnd('\\'))
                .FirstOrDefault(r => t.StartsWith(r + "\\", StringComparison.OrdinalIgnoreCase) || t.Equals(r, StringComparison.OrdinalIgnoreCase));
            t = root != null ? t[root.Length..] : "";
        }
        t = t.Trim('\\', ' ');
        if (t.Length == 0) return DefaultFuzTemplate;
        var parts = t.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (!parts.Any(x => x.Equals("Sound", StringComparison.OrdinalIgnoreCase))) t += "\\" + DefaultFuzTemplate;
        return t;
    }

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
        // Parked lines (left Phase 1 earlier, with takes) come back with their takes when the line returns.
        var old = patch.Lines.Concat(patch.Parked).GroupBy(l => l.Key).ToDictionary(g => g.Key, g => g.First());
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
        report.Removed = patch.Lines.Count(l => !result.ContainsKey(l.Key));
        // Lines that left Phase 1 keep their takes out of sight: toggling a line off in Phase 1 (or editing it and
        // changing it back) must not lose them.
        patch.Parked = old.Values.Where(l => !result.ContainsKey(l.Key) && l.Takes.Count > 0).ToList();
        foreach (var l in patch.Parked) l.Sources.Clear();
        report.Parked = patch.Lines.Count(l => !result.ContainsKey(l.Key) && l.Takes.Count > 0);
        patch.Lines = result.Values.ToList();
        report.Total = patch.Lines.Count;
        if (patch.Kind == "vc") MatchSources(patch);
        report.Adopted = AdoptExisting(patch, voice);
        report.FuzOnly = FindFuzOnly(patch, voice, mods).Count;
        _lib.Save(patch);
        return report;
    }

    /// <summary>Stems a line's files may have: the voice pack's naming first, then the other one.</summary>
    private static IEnumerable<string> Stems(Phase2Line l, VoicePack v)
    {
        yield return LineNaming.Stem(l.FinalLine, v.Naming);
        yield return LineNaming.Stem(l.FinalLine, v.Naming == NamingMode.Legacy ? NamingMode.Dbvo2Hash : NamingMode.Legacy);
    }

    /// <summary>
    /// Lines without a take whose WAV (and LIP) already exist in the patch's WAV + LIP folder get that pair as
    /// take #1, so earlier work counts and can be played, exported and built into .fuz. The files are hardlinked
    /// into the take folder (copied across drives). Returns how many lines were adopted.
    /// </summary>
    public int AdoptExisting(DbvoPatch patch, VoicePack voice)
    {
        if (string.IsNullOrWhiteSpace(patch.WavLipFolder) || !Directory.Exists(patch.WavLipFolder)) return 0;
        var wavs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in Directory.EnumerateFiles(patch.WavLipFolder, "*.wav", SearchOption.AllDirectories)) wavs.TryAdd(Path.GetFileNameWithoutExtension(f), f);
        if (wavs.Count == 0) return 0;
        int n = 0;
        foreach (var line in patch.Lines.Where(l => l.Takes.Count == 0 && !l.NoAdopt))
        {
            string? wav = null;
            foreach (var stem in Stems(line, voice)) if (wavs.TryGetValue(stem, out wav)) break;
            if (wav == null) continue;
            var lip = Path.ChangeExtension(wav, ".lip");
            if (!File.Exists(lip)) continue; // a WAV without its LIP is not a finished line
            AddExistingTake(patch, voice, line, wav, lip, "existing");
            n++;
        }
        return n;
    }

    private Take AddExistingTake(DbvoPatch patch, VoicePack voice, Phase2Line line, string wav, string lip, string source, string? originalFuz = null, string? label = null)
    {
        var stem = LineNaming.Stem(line.FinalLine, voice.Naming);
        var take = new Take
        {
            Slot = line.Takes.Count == 0 ? 1 : line.Takes.Max(t => t.Slot) + 1,
            SpokenText = line.Spoken,
            DurationSeconds = WavSeconds(wav),
            Settings = new TakeSettings { Engine = source, ModelId = label ?? (source == "fuz" ? "imported from .fuz" : "imported WAV + LIP") },
        };
        var rel = Path.Combine("takes", line.LineId, take.Slot.ToString("00"));
        take.WavPath = Path.Combine(rel, stem + ".wav");
        take.LipPath = Path.Combine(rel, stem + ".lip");
        FileLinks.LinkOrCopy(wav, Path.Combine(_lib.PatchFolder(patch.Id), take.WavPath), true);
        FileLinks.LinkOrCopy(lip, Path.Combine(_lib.PatchFolder(patch.Id), take.LipPath), true);
        if (originalFuz != null && File.Exists(originalFuz))
        {
            take.OriginalFuz = Path.Combine(rel, stem + ".fuz");
            FileLinks.LinkOrCopy(originalFuz, Path.Combine(_lib.PatchFolder(patch.Id), take.OriginalFuz), true);
        }
        line.Takes.Add(take);
        line.SelectedTakeId = take.Id;
        line.Status = Phase2Status.Generated;
        line.LastError = null;
        return take;
    }

    /// <summary>A finished WAV + LIP made outside generation (a microphone recording, converted or not) becomes a new take.</summary>
    public Take AddRecordedTake(DbvoPatch patch, VoicePack voice, Phase2Line line, string wav, string lip, bool converted)
    {
        var t = AddExistingTake(patch, voice, line, wav, lip, converted ? "microphone+vc" : "microphone",
            label: converted ? "microphone, converted with the Voice Changer" : "microphone recording");
        t.SpokenText = line.Spoken;
        return t;
    }

    /// <summary>Lines without a take that have a .fuz (in either naming) in the patch's .fuz folders.</summary>
    public List<(Phase2Line Line, string Fuz)> FindFuzOnly(DbvoPatch patch, VoicePack voice, IReadOnlyList<Mod> mods)
    {
        var list = new List<(Phase2Line, string)>();
        var root = FinalRoot(patch, voice);
        if (string.IsNullOrWhiteSpace(root)) return list;
        var folders = patch.ModIds.Select(id => mods.FirstOrDefault(m => m.Id == id)?.Name ?? "Mod")
            .Select(n => Path.Combine(root, ExpandTemplate(FuzTemplate(patch, voice), n, voice, patch))).Where(Directory.Exists).Distinct().ToList();
        if (folders.Count == 0) return list;
        var fuz = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in folders)
            foreach (var f in Directory.EnumerateFiles(d, "*.fuz")) fuz.TryAdd(Path.GetFileNameWithoutExtension(f), f);
        foreach (var line in patch.Lines.Where(l => l.Takes.Count == 0 && !l.IsOmitted && !l.NoAdopt))
            foreach (var stem in Stems(line, voice))
                if (fuz.TryGetValue(stem, out var f)) { list.Add((line, f)); break; }
        return list;
    }

    /// <summary>Turn .fuz files from the final folder back into WAV + LIP takes (ffmpeg decodes the xWMA).</summary>
    public async Task<int> ImportFuzAsync(DbvoPatch patch, VoicePack voice, IReadOnlyList<Mod> mods, ExternalTools tools,
        IProgress<GenerationProgress>? progress, CancellationToken ct, SemaphoreSlim? stateGate = null)
    {
        var todo = FindFuzOnly(patch, voice, mods);
        var workRoot = Path.Combine(Path.GetTempPath(), "dbvo");
        Directory.CreateDirectory(workRoot);
        int done = 0, ok = 0, failed = 0;
        var gate = stateGate ?? new SemaphoreSlim(1, 1);
        foreach (var (line, fuzPath) in todo)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var work = new WorkFolder(workRoot);
                var (lipBytes, xwm) = FuzFile.Split(await File.ReadAllBytesAsync(fuzPath, ct));
                var x = work.File("a.xwm");
                await File.WriteAllBytesAsync(x, xwm, ct);
                var wav = work.File("a.wav");
                await tools.ToWavAsync(x, wav, 44100, false, 0, ct);
                var lip = work.File("a.lip");
                await File.WriteAllBytesAsync(lip, lipBytes, ct);
                await gate.WaitAsync(ct);
                try { if (line.Takes.Count == 0) { AddExistingTake(patch, voice, line, wav, lip, "fuz", fuzPath); ok++; } }
                finally { gate.Release(); }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { failed++; line.LastError = "Could not import the .fuz: " + FriendlyError.From(ex); }
            progress?.Report(new GenerationProgress(++done, todo.Count, failed, line.FinalLine, null));
        }
        await gate.WaitAsync(CancellationToken.None);
        try { _lib.Save(patch); } finally { gate.Release(); }
        return ok;
    }

    /// <summary>Lines eligible for generation: not omitted, and (unless <paramref name="regenerate"/>) without a usable take.</summary>
    /// <summary>
    /// The lines a generation would make. regenerate: every line (or every listed one). Otherwise "missing" (no take at all)
    /// or, with edited, the lines that have takes but whose spoken text changed since (or whose last attempt failed).
    /// </summary>
    public static List<Phase2Line> Eligible(DbvoPatch patch, bool regenerate, IEnumerable<string>? onlyLineIds = null, bool edited = false)
    {
        var only = onlyLineIds?.ToHashSet();
        return patch.Lines.Where(l => !l.IsOmitted)
            .Where(l => !l.Excluded)
            .Where(l => patch.Kind != "vc" || l.SourceWav != null || patch.TtsForMissing)
            .Where(l => only == null || only.Contains(l.LineId))
            .Where(l => regenerate || only != null || (edited ? l.IsEdited : l.IsMissing))
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
        SemaphoreSlim? stateGate = null, string? engineOverride = null, string? folder = null, Action? save = null,
        bool makeLip = true, Func<Phase2Line, string>? takeFolder = null, Func<Phase2Line, (string? Previous, string? Next)>? context = null)
    {
        string root = folder ?? _lib.PatchFolder(patch.Id);
        bool vc = patch.Kind == "vc";
        string engine = engineOverride ?? (vc ? "sts" : voice.Engine);
        var es = voice.Eleven.Clone();
        var sts = voice.Sts;
        var pronunciations = MergePronunciations(_settings.Pronunciations, voice.Pronunciations);
        ElevenLabsClient? client = null;
        LocalEngineClient? local = null;
        int concurrency;
        switch (engine)
        {
            case "sts":
                if (string.IsNullOrWhiteSpace(_settings.ElevenLabsApiKey)) throw new InvalidOperationException("ElevenLabs needs your API key, and none is saved. Add it in Global Settings → General & tools → ElevenLabs.");
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
                if (string.IsNullOrWhiteSpace(_settings.ElevenLabsApiKey)) throw new InvalidOperationException("ElevenLabs needs your API key, and none is saved. Add it in Global Settings → General & tools → ElevenLabs.");
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

        async Task<EngineAudio> Synthesize(Phase2Line line, string spoken, CancellationToken c)
        {
            switch (engine)
            {
                case "sts":
                {
                    if (line.SourceWav == null || !File.Exists(line.SourceWav)) throw new InvalidOperationException("No source WAV or .fuz found for this line in the source folders.");
                    var bytes = line.SourceWav.EndsWith(".fuz", StringComparison.OrdinalIgnoreCase)
                        ? await FuzToWavAsync(line.SourceWav, tools, workRoot, c)
                        : await File.ReadAllBytesAsync(line.SourceWav, c);
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
                    var text = ApplyPronunciations(SpokenText.StripTags(spoken), pronunciations, "local");
                    var audio = await limiter.RunAsync(x => local!.VoxCpmAsync(text, voice.VoxCpm, x), ex => ex is HttpRequestException, ct: c);
                    return new EngineAudio(audio, "wav", new TakeSettings { Engine = "voxcpm", VoiceId = voice.VoxCpm.Voice, ModelId = "voxcpm", Stability = voice.VoxCpm.CfgValue, OutputFormat = "wav" });
                }
                case LocalEngineClient.OmniVoice:
                {
                    var text = ApplyPronunciations(SpokenText.StripTags(spoken), pronunciations, "local");
                    var audio = await limiter.RunAsync(x => local!.OmniVoiceAsync(text, voice.OmniVoice, x), ex => ex is HttpRequestException, ct: c);
                    return new EngineAudio(audio, "wav", new TakeSettings { Engine = "omnivoice", VoiceId = voice.OmniVoice.Voice, ModelId = voice.OmniVoice.Model, Speed = voice.OmniVoice.Speed, OutputFormat = "wav" });
                }
                default:
                {
                    var text = ApplyPronunciations(spoken, pronunciations, es.ModelId);
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
                    if (context != null && !es.ModelId.Contains("v3", StringComparison.OrdinalIgnoreCase))
                    {
                        // Request stitching: the neighbouring text keeps the voice's flow across parts (not available on eleven_v3).
                        var (prevText, nextText) = context(line);
                        req.PreviousText = string.IsNullOrWhiteSpace(prevText) ? null : ApplyPronunciations(prevText, pronunciations, es.ModelId);
                        req.NextText = string.IsNullOrWhiteSpace(nextText) ? null : ApplyPronunciations(nextText, pronunciations, es.ModelId);
                    }
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
            // The text as it is now: an edit made while this line generates must leave the new take marked as changed.
            var spoken = line.Spoken;
            try
            {
                var got = await Synthesize(line, spoken, ct);
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
                    if (makeLip) await tools.MakeLipAsync(wav, work.File("r.wav"), lip, SpokenText.StripTags(spoken), ct);

                    var take = new Take
                    {
                        Slot = line.Takes.Count == 0 ? 1 : line.Takes.ToList().Max(t => t.Slot) + 1,
                        SpokenText = spoken,
                        DurationSeconds = w.DurationSeconds,
                        Settings = got.Settings,
                    };
                    var stem = line.FileStem ?? LineNaming.Stem(line.FinalLine, voice.Naming);
                    var rel = Path.Combine(takeFolder?.Invoke(line) ?? Path.Combine("takes", line.LineId), take.Slot.ToString("00"));
                    var dir = Path.Combine(root, rel);
                    Directory.CreateDirectory(dir);
                    take.WavPath = Path.Combine(rel, stem + ".wav");
                    take.LipPath = makeLip ? Path.Combine(rel, stem + ".lip") : "";
                    File.Copy(wav, Path.Combine(root, take.WavPath), true);
                    if (makeLip) File.Copy(lip, Path.Combine(root, take.LipPath), true);
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
                line.LastError = FriendlyError.From(ex);
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
                        line.RefreshStale();
                    }
                    if (save != null) save(); else _lib.Save(patch);
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
        // WAV first; a .fuz (as in a released voice pack) is used when there is no WAV of that name.
        foreach (var pattern in new[] { "*.wav", "*.fuz" })
            foreach (var folder in patch.SourceFolders.Where(Directory.Exists))
                foreach (var f in Directory.EnumerateFiles(folder, pattern, SearchOption.AllDirectories))
                    index.TryAdd(Path.GetFileNameWithoutExtension(f), f);
        int found = 0;
        foreach (var line in patch.Lines)
        {
            line.SourceWav = index.TryGetValue(LineNaming.Stem(line.FinalLine, NamingMode.Legacy), out var a) ? a
                : index.TryGetValue(LineNaming.Stem(line.FinalLine, NamingMode.Dbvo2Hash), out var b) ? b : null;
            if (line.SourceWav != null) found++;
        }
        return found;
    }

    /// <summary>Decode a .fuz's xWMA audio to WAV bytes (for Voice Changer sources and previews).</summary>
    public static async Task<byte[]> FuzToWavAsync(string fuzPath, ExternalTools tools, string workRoot, CancellationToken ct)
    {
        Directory.CreateDirectory(workRoot);
        using var work = new WorkFolder(workRoot);
        var (_, xwm) = FuzFile.Split(await File.ReadAllBytesAsync(fuzPath, ct));
        var x = work.File("s.xwm");
        await File.WriteAllBytesAsync(x, xwm, ct);
        var wav = work.File("s.wav");
        await tools.ToWavAsync(x, wav, 44100, false, 0, ct);
        return await File.ReadAllBytesAsync(wav, ct);
    }

    /// <summary>Delete every take except the one in use (for the given lines, or all). Returns how many were removed.</summary>
    public int PurgeUnusedTakes(DbvoPatch patch, IEnumerable<string>? lineIds = null)
    {
        var only = lineIds?.ToHashSet();
        int n = 0;
        foreach (var line in patch.Lines.Where(l => only == null || only.Contains(l.LineId)))
        {
            foreach (var t in line.Takes.Where(t => t.Id != line.SelectedTakeId && !t.Kept).ToList())
            {
                var dir = Path.GetDirectoryName(Path.Combine(_lib.PatchFolder(patch.Id), t.WavPath));
                try { if (dir != null && Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
                line.Takes.Remove(t);
                n++;
            }
        }
        _lib.Save(patch);
        return n;
    }

    /// <summary>Seconds of audio in a WAV file, from its header (0 if unreadable).</summary>
    public static double WavSeconds(string path)
    {
        if (path.EndsWith(".fuz", StringComparison.OrdinalIgnoreCase))
        {
            // xWMA: estimate from size at the 48 kbps Yakitori/2.7.0 bitrate
            try { return Math.Max(0, new FileInfo(path).Length - 2000) / 6000.0; } catch { return 0; }
        }
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
    /// <summary>
    /// The takes to export for a line and their file names. Legacy names: the selected take. DBVO 2.0 names: every
    /// kept take (variants, as in DBVO Voice Pack Maker 2.7.0); with two or more they go to &lt;name&gt;\0, &lt;name&gt;\1 …;
    /// without kept takes, the selected one.
    /// </summary>
    public static List<(Take Take, string Stem)> ExportTakes(Phase2Line line, NamingMode naming)
    {
        var stem = line.FileStem ?? LineNaming.Stem(line.FinalLine, naming);
        var kept = line.Takes.Where(t => t.Kept).ToList();
        if (naming == NamingMode.Legacy || kept.Count == 0)
        {
            var t = line.SelectedTake ?? kept.LastOrDefault();
            return t == null ? new() : new() { (t, stem) };
        }
        if (kept.Count == 1) return new() { (kept[0], stem) };
        return kept.Select((t, i) => (t, Path.Combine(stem, i.ToString()))).ToList();
    }

    private static string ExportKey(string path) => Path.GetFullPath(path);

    /// <summary>Lines an export writes: not omitted or excluded, and approved when the patch exports approved lines only.</summary>
    public static IEnumerable<Phase2Line> ExportLines(DbvoPatch patch) =>
        patch.Lines.Where(l => !l.IsOmitted && !l.Excluded && (!patch.ExportApprovedOnly || l.Approved));

    /// <summary>The export map, compared without case like Windows paths (a loaded map has no comparer).</summary>
    private static void ExportMap(DbvoPatch patch)
    {
        if (patch.Exported.Comparer != StringComparer.OrdinalIgnoreCase)
            patch.Exported = new Dictionary<string, string>(patch.Exported, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Write a target unless it holds this take already, or it is someone else's file and overwriting is off.</summary>
    private static bool ShouldWrite(DbvoPatch patch, string target, Take take, bool overwrite)
    {
        if (!File.Exists(target)) return true;
        if (patch.Exported.TryGetValue(ExportKey(target), out var tid)) return tid != take.Id;
        return overwrite;
    }

    /// <summary>The files a full export would write now: WAV + LIP folder and each mod's .fuz folder.</summary>
    public HashSet<string> PlannedExports(DbvoPatch patch, VoicePack voice, IReadOnlyList<Mod> mods)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var wl = WavLipFolder(patch, voice);
        var root = FinalRoot(patch, voice);
        var template = FuzTemplate(patch, voice);
        foreach (var line in ExportLines(patch))
            foreach (var (_, stem) in ExportTakes(line, voice.Naming))
            {
                if (!string.IsNullOrWhiteSpace(wl)) { set.Add(ExportKey(Path.Combine(wl, stem + ".wav"))); set.Add(ExportKey(Path.Combine(wl, stem + ".lip"))); }
                if (!string.IsNullOrWhiteSpace(root))
                    foreach (var name in line.Sources.Select(x => mods.FirstOrDefault(m => m.Id == x.ModId)?.Name ?? "Mod").Distinct())
                        set.Add(ExportKey(Path.Combine(root, ExpandTemplate(template, name, voice, patch), stem + ".fuz")));
            }
        return set;
    }

    /// <summary>Files from earlier exports that a full export would no longer write (still on disk).</summary>
    public List<string> StaleExports(DbvoPatch patch, VoicePack voice, IReadOnlyList<Mod> mods)
    {
        ExportMap(patch);
        var planned = PlannedExports(patch, voice, mods);
        return patch.Exported.Keys.Where(k => !planned.Contains(k) && File.Exists(k)).OrderBy(k => k).ToList();
    }

    /// <summary>Each stale file with the reason its line is no longer exported, in plain words.</summary>
    public List<(string Path, string Why)> StaleExportReasons(DbvoPatch patch, VoicePack voice, IReadOnlyList<Mod> mods)
    {
        var byStem = new Dictionary<string, Phase2Line>(StringComparer.OrdinalIgnoreCase);
        foreach (var l in patch.Lines) byStem.TryAdd(l.FileStem ?? LineNaming.Stem(l.FinalLine, voice.Naming), l);
        var list = new List<(string, string)>();
        foreach (var f in StaleExports(patch, voice, mods))
        {
            var stem = System.IO.Path.GetFileNameWithoutExtension(f);
            var variant = false;
            if (!byStem.ContainsKey(stem) && int.TryParse(stem, out _)) { stem = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(f)) ?? stem; variant = true; }
            string why;
            if (!byStem.TryGetValue(stem, out var line)) why = "its line is no longer in this patch (removed or renamed in Phase 1, or the naming mode changed)";
            else if (line.Excluded) why = "the line is excluded";
            else if (line.IsOmitted) why = "the line is omitted (players already have it)";
            else if (patch.ExportApprovedOnly && !line.Approved) why = "the line isn't approved, and Export is set to approved lines only";
            else if (line.Takes.Count == 0) why = "the line's takes were deleted";
            else if (variant) why = "an older variant of the line (fewer ★ takes are kept now)";
            else why = "the line is now exported under another name or folder";
            list.Add((f, why));
        }
        return list;
    }

    /// <summary>Delete the stale exported files (and empty folders they leave). Returns how many were removed.</summary>
    public int RemoveStaleExports(DbvoPatch patch, VoicePack voice, IReadOnlyList<Mod> mods, List<string>? errors = null)
    {
        int n = 0;
        foreach (var f in StaleExports(patch, voice, mods))
        {
            try
            {
                File.Delete(f);
                patch.Exported.Remove(f);
                n++;
                var dir = Path.GetDirectoryName(f);
                if (dir != null && Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
            }
            catch (Exception ex) { errors?.Add($"{f}: not removed ({FriendlyError.From(ex)})"); }
        }
        // forget entries whose files are gone
        foreach (var k in patch.Exported.Keys.Where(k => !File.Exists(k)).ToList()) patch.Exported.Remove(k);
        return n;
    }

    public ExportReport ExportWavLip(DbvoPatch patch, VoicePack voice)
    {
        var rep = new ExportReport();
        var folder = WavLipFolder(patch, voice);
        if (string.IsNullOrWhiteSpace(folder)) throw new InvalidOperationException("Set the WAV + LIP folder first.");
        ExportMap(patch);
        foreach (var (line, t, stem) in ExportLines(patch).SelectMany(l => ExportTakes(l, voice.Naming).Select(x => (l, x.Take, x.Stem))))
        {
            try
            {
                bool any = false;
                foreach (var (srcRel, ext) in new[] { (t.WavPath, ".wav"), (t.LipPath, ".lip") })
                {
                    var target = Path.Combine(folder, stem + ext);
                    if (!ShouldWrite(patch, target, t, patch.OverwriteWavLip)) continue;
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    FileLinks.LinkOrCopy(Path.Combine(_lib.PatchFolder(patch.Id), srcRel), target, true);
                    patch.Exported[ExportKey(target)] = t.Id;
                    any = true;
                }
                if (any) rep.Written++; else rep.Skipped++;
            }
            catch (Exception ex)
            {
                rep.Failed++;
                rep.Errors.Add($"{stem}: {FriendlyError.From(ex)}");
            }
        }
        _lib.Save(patch);
        return rep;
    }

    /// <summary>
    /// Build .fuz files from the selected takes into each source mod's template folder (or one custom folder).
    /// A line shared by several mods is encoded once and hardlinked into each folder. A .fuz this patch made earlier
    /// is rebuilt when its line has another take; other files follow "Overwrite .fuz".
    /// </summary>
    public async Task<ExportReport> ExportFuzAsync(DbvoPatch patch, VoicePack voice, IReadOnlyList<Mod> mods, ExternalTools tools,
        string? customFolder = null, IProgress<GenerationProgress>? progress = null, CancellationToken ct = default, SemaphoreSlim? stateGate = null)
    {
        var rep = new ExportReport();
        var root = FinalRoot(patch, voice);
        var template = FuzTemplate(patch, voice);
        if (customFolder == null && string.IsNullOrWhiteSpace(root)) throw new InvalidOperationException("Set the final folder first.");
        var workRoot = Path.Combine(Path.GetTempPath(), "dbvo");
        var lines = ExportLines(patch).SelectMany(l => ExportTakes(l, voice.Naming).Select(x => (Line: l, x.Take, x.Stem))).ToList();
        var cache = new FuzCache(_lib.FuzCacheFolder);
        var encoder = FuzCache.EncoderId(tools.XwmaEncodePath, 48000);
        int done = 0;
        var gate = new SemaphoreSlim(Math.Max(2, Environment.ProcessorCount / 2));
        var written = new List<(string Target, string TakeId)>();
        bool track = customFolder == null;
        ExportMap(patch);

        await Task.WhenAll(lines.Select(async item =>
        {
            var (line, take, stem) = item;
            await gate.WaitAsync(ct);
            try
            {
                var folders = customFolder != null
                    ? new List<string> { customFolder }
                    : line.Sources.Select(s => mods.FirstOrDefault(m => m.Id == s.ModId)?.Name ?? "Mod").Distinct()
                        .Select(name => Path.Combine(root, ExpandTemplate(template, name, voice, patch))).ToList();
                var targets = folders.Select(f => Path.Combine(f, stem + ".fuz")).ToList();
                List<string> needed;
                lock (rep) needed = targets.Where(t => track ? ShouldWrite(patch, t, take, patch.OverwriteFuz) : patch.OverwriteFuz || !File.Exists(t)).ToList();
                if (needed.Count == 0)
                {
                    lock (rep) rep.Skipped++;
                    return;
                }
                var t = take;
                var pf = _lib.PatchFolder(patch.Id);
                byte[] fuz;
                var original = t.OriginalFuz != null ? Path.Combine(pf, t.OriginalFuz) : null;
                if (original != null && File.Exists(original))
                {
                    fuz = await File.ReadAllBytesAsync(original, ct); // imported audio: as it was, no second encode
                    lock (rep) rep.Reused++;
                }
                else
                {
                    var wavPath = Path.Combine(pf, t.WavPath);
                    var lipBytes = await File.ReadAllBytesAsync(Path.Combine(pf, t.LipPath), ct);
                    var key = FuzCache.Key(await File.ReadAllBytesAsync(wavPath, ct), lipBytes, encoder);
                    if (cache.TryGet(key) is { } hit)
                    {
                        fuz = hit;
                        lock (rep) rep.Cached++;
                    }
                    else
                    {
                        using var work = new WorkFolder(workRoot);
                        var wav = work.File("a.wav");
                        File.Copy(wavPath, wav, true);
                        var xwm = work.File("a.xwm");
                        await tools.ToXwmaAsync(wav, xwm, 48000, ct);
                        fuz = FuzFile.Build(lipBytes, await File.ReadAllBytesAsync(xwm, ct));
                        cache.Put(key, fuz);
                    }
                }
                var first = needed[0];
                Directory.CreateDirectory(Path.GetDirectoryName(first)!);
                await File.WriteAllBytesAsync(first, fuz, ct);
                foreach (var other in needed.Skip(1)) FileLinks.LinkOrCopy(first, other, true);
                lock (rep)
                {
                    rep.Written++;
                    if (track) foreach (var n in needed) written.Add((n, t.Id));
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                lock (rep)
                {
                    rep.Failed++;
                    rep.Errors.Add($"{line.FinalLine}: {FriendlyError.From(ex)}");
                }
            }
            finally
            {
                gate.Release();
                int d = Interlocked.Increment(ref done);
                progress?.Report(new GenerationProgress(d, lines.Count, rep.Failed, line.FinalLine, null));
            }
        }));
        var sg = stateGate ?? new SemaphoreSlim(1, 1);
        await sg.WaitAsync(CancellationToken.None);
        try
        {
            foreach (var (target, takeId) in written) patch.Exported[ExportKey(target)] = takeId;
            if (track) patch.LastFuzUtc = DateTime.UtcNow;
            _lib.Save(patch);
        }
        finally { sg.Release(); }
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
            // the same rows Phase 1 flags as not reviewed (a player-name line is not one of them)
            int broken = Phase1Service.UnreviewedFlags(p).Count(l => l.Kind == LineKind.Broken);
            if (broken > 0) list.Add($"{p.FileName}: {broken} BROKEN LINE row(s) were never reviewed.");
        }
        return list;
    }
}
