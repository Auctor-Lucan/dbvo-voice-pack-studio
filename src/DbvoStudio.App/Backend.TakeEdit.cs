using System.IO;
using System.Text.Json;
using DbvoStudio.App.Tools;
using DbvoStudio.Core.Audio;
using DbvoStudio.Core.Naming;
using DbvoStudio.Core.Text;
using DbvoStudio.Core.Projects;
using DbvoStudio.Core.Tools;

namespace DbvoStudio.App;

/// <summary>
/// Edit take (Phase 2, Voice Changer, NPC Revoice, Books): cut, silence, fade and gain a take, then save it as a new take or
/// over the old one. Edits are made to the unprocessed take; its post-processing is put back on afterwards, and a new .lip
/// is made from the take's text.
/// </summary>
public sealed partial class Backend
{
    private sealed record EditOwner(List<Phase2Line> Lines, string Root, Action Save, Func<Phase2Line, object> Result);

    private EditOwner EditOwnerOf(string kind, string id)
    {
        switch (kind)
        {
            case "books":
            {
                var proj = LoadBk(id);
                return new EditOwner(proj.Lines.Cast<Phase2Line>().ToList(), _lib.BooksFolder(proj.Id), () => _lib.Save(proj),
                    l => BkBookDetail(proj, ((Core.Books.BookLine)l).Stem));
            }
            case "revoice":
            {
                var proj = LoadRv(id);
                return new EditOwner(proj.Lines.Cast<Phase2Line>().ToList(), _lib.RevoiceFolder(proj.Id), () => _lib.Save(proj),
                    l => RvLineDto((Core.Revoice.RevoiceLine)l));
            }
            default:
            {
                var patch = LoadPatch(id);
                var voice = _lib.LoadVoicePacks().FirstOrDefault(v => v.Id == patch.VoicePackId);
                return new EditOwner(patch.Lines, _lib.PatchFolder(patch.Id), () => _lib.Save(patch), l => Line2Dto(l, voice));
            }
        }
    }

    private (EditOwner Owner, Phase2Line Line, Take Take) EditTarget(JsonElement p)
    {
        var o = EditOwnerOf(S(p, "kind", "patch"), S(p, "id"));
        var line = o.Lines.FirstOrDefault(l => l.LineId == S(p, "lineId")) ?? throw new InvalidOperationException("Line not found.");
        var take = line.Takes.FirstOrDefault(t => t.Id == S(p, "takeId")) ?? line.SelectedTake ?? throw new InvalidOperationException("This line has no take.");
        return (o, line, take);
    }

    private async Task<object?> DispatchTakeEdit(string m, JsonElement p)
    {
        switch (m)
        {
            case "take.edit.open":
                return await Locked(() =>
                {
                    var (o, line, t) = EditTarget(p);
                    var dry = TakeEffects.DryPath(o.Root, t);
                    var src = File.Exists(dry) ? dry : Path.Combine(o.Root, t.WavPath);
                    if (!File.Exists(src)) throw new InvalidOperationException("This take's WAV isn't there anymore.");
                    return new
                    {
                        url = "https://media.dbvo/" + Path.GetRelativePath(_lib.Root, src).Replace('\\', '/') + "?t=" + DateTime.UtcNow.Ticks,
                        takeId = t.Id, t.Slot, text = t.SpokenText, line = line.FinalLine, seconds = t.DurationSeconds,
                        engine = t.Settings.Engine, model = t.Settings.ModelId,
                        lip = !string.IsNullOrEmpty(t.LipPath),
                        effect = t.Effect, effectName = t.Effect.Length > 0 ? Effects.Find(null, t.Effect)?.Name ?? t.Effect : "",
                        soften = t.Soften, noise = t.NoiseReduced,
                        used = t.Id == line.SelectedTakeId,
                        words = WavToLip.WordsReady(_settings.WavToLip, WhisperModelsFolder()),
                    };
                });
            case "take.edit.words": return await EditWords(p);
            case "take.edit.save": return await EditSave(p);
            default: return NotHandled;
        }
    }

    // One Whisper for words, kept loaded for a while: opening the editor again is then quick.
    private readonly SemaphoreSlim _wordsLock = new(1, 1);
    private (Func<string, CancellationToken, Task<List<WordTime>>> Words, IAsyncDisposable Engine, string Key)? _wordsEngine;
    private long _wordsUsed;

    private async Task<object?> EditWords(JsonElement p)
    {
        string src;
        await _gate.WaitAsync();
        try
        {
            var (o, _, t) = EditTarget(p);
            var dry = TakeEffects.DryPath(o.Root, t);
            src = File.Exists(dry) ? dry : Path.Combine(o.Root, t.WavPath);
        }
        finally { _gate.Release(); }
        // a copy: the take can change while Whisper reads it
        var tmp = Path.Combine(WavToLip.CheckTempFolder, "edit_" + Guid.NewGuid().ToString("N")[..8] + ".wav");
        Directory.CreateDirectory(WavToLip.CheckTempFolder);
        File.Copy(src, tmp, true);
        await _wordsLock.WaitAsync();
        try
        {
            var ws = _settings.WavToLip;
            var key = JsonSerializer.Serialize(ws, Library.Json);
            if (_wordsEngine is { } old && old.Key != key) { _wordsEngine = null; await old.Engine.DisposeAsync(); }
            if (_wordsEngine == null)
            {
                var tool = new WavToLip(ws, GrExternal(), WhisperModelsFolder(), e => { if (e.Kind == "log") App.Log("Whisper: " + e.Text); });
                var (words, engine) = await tool.CreateWordTimerAsync(CancellationToken.None);
                _wordsEngine = (words, engine, key);
            }
            var list = await _wordsEngine.Value.Words(tmp, CancellationToken.None);
            var stamp = Interlocked.Increment(ref _wordsUsed);
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromMinutes(3));
                await _wordsLock.WaitAsync();
                try { if (Interlocked.Read(ref _wordsUsed) == stamp && _wordsEngine is { } e) { _wordsEngine = null; await e.Engine.DisposeAsync(); } }
                finally { _wordsLock.Release(); }
            });
            return list.Select(w => new { w = w.Word, s = w.Start, e = w.End }).ToList();
        }
        finally
        {
            _wordsLock.Release();
            try { File.Delete(tmp); } catch { }
        }
    }

    private async Task<object?> EditSave(JsonElement p)
    {
        if (_jobCts != null) throw new InvalidOperationException("Saving an edit waits until the running task (top bar) has finished: it may be writing this take.");
        var ops = p.TryGetProperty("ops", out var oe) ? JsonSerializer.Deserialize<List<EditOp>>(oe.GetRawText(), JsonOptions) ?? new() : new();
        if (ops.Count == 0) throw new InvalidOperationException("No edits to save.");
        bool asNew = S(p, "mode", "new") != "over";
        await _gate.WaitAsync();
        try
        {
            var (o, line, t) = EditTarget(p);
            var root = o.Root;
            var dry = TakeEffects.DryPath(root, t);
            var wavNow = Path.Combine(root, t.WavPath);
            var src = File.Exists(dry) ? dry : wavNow;
            if (!File.Exists(src)) throw new InvalidOperationException("This take's WAV isn't there anymore.");
            var w = WavFile.Read(src);
            var edited = new WavFile { SampleRate = w.SampleRate, Channels = w.Channels, Samples = TakeEdit.Apply(w.Samples, w.SampleRate, w.Channels, ops) };
            if (edited.Samples.Length < w.SampleRate / 20 * Math.Max(1, w.Channels)) throw new InvalidOperationException("Less than 0.05 s would be left of this take.");
            bool lip = !string.IsNullOrEmpty(t.LipPath);
            var tools = lip ? Tools() : File.Exists(_settings.FfmpegPath) ? GrExternal() : null;
            var (effect, soften, noise) = (t.Effect, t.Soften, t.NoiseReduced);

            using var work = new WorkFolder(Path.Combine(Path.GetTempPath(), "DbvoStudio", "edit"));
            var wav = work.File("e.wav");
            edited.Write(wav);
            var lipFile = work.File("e.lip");
            if (lip)
            {
                var text = SpokenText.StripTags(t.SpokenText.Length > 0 ? t.SpokenText : line.Spoken);
                await tools!.MakeLipAsync(wav, work.File("r.wav"), lipFile, text);
            }

            Take target;
            if (asNew)
            {
                // next to the other takes of the line: …\<line>\<slot>\<stem>.wav
                var slot = line.Takes.Max(x => x.Slot) + 1;
                var lineDir = Path.GetDirectoryName(Path.GetDirectoryName(t.WavPath)!) ?? "";
                var rel = Path.Combine(lineDir, slot.ToString("00"));
                Directory.CreateDirectory(Path.Combine(root, rel));
                target = new Take
                {
                    Slot = slot, SpokenText = t.SpokenText, Settings = t.Settings,
                    WavPath = Path.Combine(rel, Path.GetFileName(t.WavPath)),
                    LipPath = lip ? Path.Combine(rel, Path.GetFileName(t.LipPath)) : "",
                };
                File.Copy(wav, Path.Combine(root, target.WavPath), true);
                if (lip) File.Copy(lipFile, Path.Combine(root, target.LipPath), true);
                line.Takes.Add(target);
                bool wasUsed = line.SelectedTakeId == t.Id;
                if (wasUsed || line.SelectedTakeId == null) line.SelectedTakeId = target.Id;
                if (t.Kept && wasUsed) { t.Kept = false; target.Kept = true; }
            }
            else
            {
                target = t;
                // new files in place (take files can be hard links to the WAV + LIP folder)
                File.Copy(wav, wavNow + ".edit-tmp", true);
                File.Move(wavNow + ".edit-tmp", wavNow, true);
                if (lip)
                {
                    var lipNow = Path.Combine(root, t.LipPath);
                    File.Copy(lipFile, lipNow + ".edit-tmp", true);
                    File.Move(lipNow + ".edit-tmp", lipNow, true);
                }
                if (File.Exists(dry)) File.Delete(dry);
                t.Effect = ""; t.Soften = ""; t.NoiseReduced = false;
                t.OriginalFuz = null; t.DryOriginalFuz = null; // the edit isn't what the imported .fuz holds
                t.Qa = null;
                t.CreatedUtc = DateTime.UtcNow;
            }
            target.DurationSeconds = edited.DurationSeconds;
            string? problem = null;
            if (effect.Length > 0 || soften.Length > 0 || noise)
            {
                try { await TakeEffects.RenderAsync(root, target, effect, soften, null, tools, CancellationToken.None, noise); }
                catch (Exception ex) { problem = "The edit was saved, but its post-processing couldn't be put back: " + FriendlyError.From(ex); }
            }
            if (line.Status is Phase2Status.Pending or Phase2Status.Failed) line.Status = Phase2Status.Generated;
            line.RefreshStale();
            o.Save();
            return new { result = o.Result(line), takeId = target.Id, problem };
        }
        finally { _gate.Release(); }
    }
}
