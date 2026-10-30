using System.IO;
using System.Text.Json;
using DbvoStudio.Core.Audio;
using DbvoStudio.Core.Projects;
using DbvoStudio.Core.Text;
using DbvoStudio.Core.Tools;

namespace DbvoStudio.App;

/// <summary>8.2.1: Undo (the last 10 edits), the background job queue, take effects (Phase 2 / NPC Revoice), Phase 1 strings comparison.</summary>
public sealed partial class Backend
{
    // ------------------------------------------------------------------ undo

    private sealed class UndoEntry
    {
        public string Label = "";
        public string Method = "";
        public DateTime At = DateTime.Now;
        public readonly Dictionary<string, byte[]?> Files = new(StringComparer.OrdinalIgnoreCase);
        public bool Closed;
        /// <summary>The action also changed files Undo can't take back (takes deleted or added): it isn't kept as a step.</summary>
        public bool Discard;
    }

    private const int UndoDepth = 10;
    private readonly List<UndoEntry> _undo = new();
    private static readonly AsyncLocal<UndoEntry?> UndoCapture = new();
    /// <summary>Set while a page brings itself up to date (patch.sync): its save neither is an undo step nor drops the earlier ones.</summary>
    private static readonly AsyncLocal<bool> UndoQuiet = new();
    private bool _undoHooked;

    /// <summary>Methods that never become an undo step: reads, and changes to audio files a project can't take back.</summary>
    private static bool Undoable(string m)
    {
        if (m.StartsWith("ui.") || m.StartsWith("window.") || m.StartsWith("pick.") || m.StartsWith("open.")) return false;
        if (m is "state" or "undo.apply" or "undo.list" or "queue.list" or "queue.remove" or "window.ready" or "job.cancel") return false;
        if (m.StartsWith("take.edit.")) return false; // the audio and .lip files change too, which Undo can't put back
        var a = m.Contains('.') ? m[(m.IndexOf('.') + 1)..] : m;
        if (a is "get" or "list" or "paths" or "audio" or "draft" or "estimate" or "checks" or "check" or "recordTarget" or "transcribe" or "stringsDiff") return false;
        // Opening a page brings it up to date (patch.sync) and reads around it: not something the user did, and undoing it
        // would only make the page sync again (Ctrl+Z would never get past it).
        if (a is "sync" or "sharedFiles" or "staleExports" or "fixConflicts" or "health" or "voices" or "models" or "stsModels" or "subscription") return false;
        foreach (var bad in new[] { "delete", "Delete", "reverb", "effect", "record", "export", "Export", "generate", "build", "Build", "discard", "commit", "try", "scan", "qa", "measure", "whisper", "Whisper", "open", "Open", "pick", "backup", "restore", "Restore", "library", "settings" })
            if (m.Contains(bad, StringComparison.Ordinal)) return false;
        return true;
    }

    private static string UndoLabel(string m)
    {
        var area = m.Split('.')[0] switch
        {
            "mod" or "line1" => "Phase 1", "patch" or "line2" => "Phase 2", "revoice" => "NPC Revoice", "books" => "Voiced Books",
            "grunts" => "Player Grunts", "fomod" => "FOMOD", "voice" => "Voice pack", "takes" => "Takes", _ => "Edit",
        };
        var action = m.Contains('.') ? m[(m.IndexOf('.') + 1)..] : m;
        // camelCase → words: bulkMark → bulk mark
        action = System.Text.RegularExpressions.Regex.Replace(action, "([a-z])([A-Z])", "$1 $2").ToLowerInvariant();
        return $"{area}: {action}";
    }

    private UndoEntry? BeginUndo(string method)
    {
        if (!_undoHooked)
        {
            _undoHooked = true;
            Library.BeforeSave = path =>
            {
                if (UndoQuiet.Value) return;
                var e = UndoCapture.Value;
                // Saved by a job or an action that isn't undoable: earlier undo steps for this file would now put back
                // an old copy over that work (and lose takes made since), so they are dropped.
                if (e == null || e.Closed || e.Discard) { ForgetUndoFor(path); return; }
                if (e.Files.ContainsKey(path)) return;
                if (path.EndsWith("settings.json", StringComparison.OrdinalIgnoreCase)) return; // settings aren't project edits (and hold keys)
                try { e.Files[path] = File.Exists(path) ? File.ReadAllBytes(path) : null; } catch { }
            };
        }
        if (!Undoable(method)) return null;
        var entry = new UndoEntry { Method = method, Label = UndoLabel(method) };
        UndoCapture.Value = entry;
        return entry;
    }

    /// <summary>Call in an action that also deletes or adds take files: it doesn't become an undo step, and earlier steps for its files go.</summary>
    private static void NoUndoHere()
    {
        var e = UndoCapture.Value;
        if (e == null) return;
        e.Discard = true;
        e.Files.Clear();
    }

    private void ForgetUndoFor(string path)
    {
        int left;
        lock (_undo)
        {
            int n = _undo.RemoveAll(x => x.Files.ContainsKey(path));
            if (n == 0) return;
            left = _undo.Count;
        }
        Emit("undo", new { count = left, label = (string?)null });
    }

    private void EndUndo(UndoEntry? e)
    {
        if (e == null) return;
        e.Closed = true;
        UndoCapture.Value = null;
        if (e.Discard || e.Files.Count == 0) return;
        lock (_undo)
        {
            _undo.Add(e);
            while (_undo.Count > UndoDepth) _undo.RemoveAt(0);
        }
        Emit("undo", new { count = _undo.Count, label = e.Label });
    }

    private object UndoList()
    {
        lock (_undo) return _undo.AsEnumerable().Reverse().Select((e, i) => new { index = i, e.Label, at = e.At, files = e.Files.Count }).ToList();
    }

    /// <summary>Goes back to before the given step (0 = the last edit): every newer step is taken back too.</summary>
    private object UndoApply(int index)
    {
        if (_jobCts != null) throw new InvalidOperationException("Undo waits until the running task (top bar) has finished, so it doesn't take back half of its work.");
        List<UndoEntry> steps;
        lock (_undo)
        {
            if (_undo.Count == 0) throw new InvalidOperationException("Nothing to undo.");
            index = Math.Clamp(index, 0, _undo.Count - 1);
            steps = _undo.Skip(_undo.Count - 1 - index).Reverse().ToList(); // newest first
            _undo.RemoveRange(_undo.Count - 1 - index, index + 1);
        }
        foreach (var e in steps)
            foreach (var (path, bytes) in e.Files)
            {
                try
                {
                    if (bytes == null) { if (File.Exists(path)) File.Delete(path); }
                    else { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllBytes(path + ".undo", bytes); File.Move(path + ".undo", path, true); }
                }
                catch (Exception ex) { App.Log("Undo: " + ex.Message); }
            }
        // Projects kept in memory are read again from the restored files.
        lock (_patches) _patches.Clear();
        lock (_gr) _gr.Clear();
        lock (_rv) _rv.Clear();
        lock (_bk) _bk.Clear();
        return new { undone = steps.Select(x => x.Label).ToList(), left = _undo.Count };
    }

    // ------------------------------------------------------------------ job queue

    private sealed record QueuedJob(string Id, string Method, JsonElement Params, List<string> Dropped, string Label, DateTime At);
    private readonly List<QueuedJob> _queue = new();
    private System.Threading.Timer? _queueTimer;
    private int _queueBusy;

    private static readonly Dictionary<string, string> JobMethods = new()
    {
        ["patch.generate"] = "Generate (Phase 2)", ["patch.buildFuz"] = "Build .fuz", ["patch.importFuz"] = "Import .fuz", ["patch.importLegacy"] = "Import a voice pack",
        ["patch.qa"] = "Check takes (Phase 2)", ["revoice.qa"] = "Check takes (NPC Revoice)", ["revoice.generate"] = "Generate (NPC Revoice)", ["revoice.export"] = "Export (NPC Revoice)",
        ["revoice.scan"] = "Scan (NPC Revoice)", ["revoice.orphansWhisper"] = "Transcribe orphans", ["books.generate"] = "Generate (Voiced Books)", ["books.export"] = "Export (Voiced Books)",
        ["books.scan"] = "Scan (Voiced Books)", ["fomod.build"] = "Build FOMOD", ["wavlip.run"] = "WAV to LIP", ["grunts.export"] = "Export (Player Grunts)",
        ["grunts.measure"] = "Measure loudness", ["grunts.whisperAll"] = "Whisper subtitles",
    };

    /// <summary>A job start while another job runs waits in the queue instead of failing; it starts when the running one ends.</summary>
    private object? TryQueue(string method, JsonElement p, List<string> dropped)
    {
        if (!JobMethods.TryGetValue(method, out var label)) return null;
        lock (_queue)
        {
            if (_jobCts == null && _queue.Count == 0) return null;
            var name = p.ValueKind == JsonValueKind.Object && p.TryGetProperty("id", out var idp) && idp.ValueKind == JsonValueKind.String ? QueueTarget(method, idp.GetString()!) : "";
            var q = new QueuedJob(NewId.Short(), method, p.ValueKind == JsonValueKind.Undefined ? default : p.Clone(), dropped.ToList(), label + (name.Length > 0 ? " · " + name : ""), DateTime.Now);
            _queue.Add(q);
            _queueTimer ??= new System.Threading.Timer(_ => _ = RunQueue(), null, 700, 700);
            EmitQueue();
            return new { started = true, queued = _queue.Count, label = q.Label };
        }
    }

    private string QueueTarget(string method, string id)
    {
        try
        {
            return method.Split('.')[0] switch
            {
                "patch" => _lib.LoadPatches().FirstOrDefault(x => x.Id == id)?.Name ?? "",
                "revoice" => LoadRv(id).Name,
                "books" => LoadBk(id).Name,
                "grunts" => LoadGr(id).Name,
                _ => "",
            };
        }
        catch { return ""; }
    }

    private void EmitQueue()
    {
        lock (_queue) Emit("queue", new { items = _queue.Select(x => new { x.Id, x.Label }).ToList() });
    }

    private async Task RunQueue()
    {
        if (_jobCts != null || Interlocked.Exchange(ref _queueBusy, 1) == 1) return;
        try
        {
            QueuedJob? next;
            lock (_queue)
            {
                if (_queue.Count == 0 || _jobCts != null) return;
                next = _queue[0];
                _queue.RemoveAt(0);
            }
            EmitQueue();
            try
            {
                var r = await Dispatch(next.Method, next.Params, next.Dropped);
                // A start that needs an answer first (an export asking to replace files) can't run unattended.
                var notStarted = r != null && JsonSerializer.SerializeToElement(r, JsonOptions) is { ValueKind: JsonValueKind.Object } el && el.TryGetProperty("started", out var st) && st.ValueKind == JsonValueKind.False;
                if (notStarted) Emit("jobDone", new { kind = "queue", ok = false, message = $"{next.Label} (queued) didn't start: it needs your answer. Open it and start it again." });
            }
            catch (Exception ex) when (_jobCts != null)
            {
                // Another job claimed the slot in the meantime: this one waits its turn again instead of being dropped.
                lock (_queue) _queue.Insert(0, next);
                EmitQueue();
                _ = ex;
            }
            catch (Exception ex) { Emit("jobDone", new { kind = "queue", ok = false, message = $"{next.Label} (queued) didn't start: {FriendlyError.From(ex)}" }); }
        }
        finally { Interlocked.Exchange(ref _queueBusy, 0); }
    }

    // ------------------------------------------------------------------ dispatch

    private async Task<object?> DispatchV82(string m, JsonElement p, List<string> dropped)
    {
        switch (m)
        {
            case "undo.list": return UndoList();
            case "undo.apply": return await Locked(() => UndoApply(p.TryGetProperty("index", out var ix) && ix.ValueKind == JsonValueKind.Number ? ix.GetInt32() : 0));
            case "queue.list": lock (_queue) return _queue.Select(x => new { x.Id, x.Label }).ToList();
            case "queue.remove":
                lock (_queue) _queue.RemoveAll(x => x.Id == S(p, "queueId"));
                EmitQueue();
                return true;
            case "effects.list": return Effects.BuiltIn();
            case "patch.importSpoken": return await Locked(() => ImportSpoken(p));
            case "takes.effect": return await TakesEffect(p);
            case "mod.stringsDiff":
                return await Locked(() =>
                {
                    var mod = LoadMod(S(p, "id"));
                    var pl = mod.Plugins.First(x => x.FileName == S(p, "plugin"));
                    var (diffs, game, other) = new Phase1Service(_lib, Runtime()).StringsDiff(mod, pl);
                    return new { diffs, game = Path.GetFileName(game.TrimEnd('\\', '/')), gamePath = game, other };
                });
            case "mod.useOtherWording":
                return await Locked(() =>
                {
                    var mod = LoadMod(S(p, "id"));
                    var pl = mod.Plugins.First(x => x.FileName == S(p, "plugin"));
                    var items = Items(p);
                    int n = 0;
                    foreach (var line in pl.Lines) if (items.TryGetValue(line.Key, out var v)) { Phase1Service.SetFinal(line, v); n++; }
                    _lib.Save(mod);
                    return new { changed = n, mod = ModDto(mod) };
                });
            default: return NotHandled;
        }
    }

    /// <summary>
    /// Spoken texts (with their [tags]) from another patch, e.g. the same mod for another voice pack, matched by the in-game
    /// line. which: edited (only texts edited there) | all. keepMine: lines edited here stay. scope: all | ticked | notake.
    /// tags: keep | words (the words without [tags]) | add (only when the words are the same: the other one's tags).
    /// preview: count only.
    /// </summary>
    private object ImportSpoken(JsonElement p)
    {
        var patch = LoadPatch(S(p, "id"));
        var from = LoadPatch(S(p, "fromId"));
        var which = S(p, "which", "edited");
        bool keepMine = !p.TryGetProperty("keepMine", out var km) || km.ValueKind != JsonValueKind.False;
        var scope = S(p, "scope", "all");
        var tags = S(p, "tags", "keep");
        var ticked = Strings(p, "lineIds").ToHashSet();
        var src = from.Lines.GroupBy(l => l.FinalLine).ToDictionary(g => g.Key, g => g.First());
        int matched = 0, changed = 0, mine = 0, same = 0;
        var samples = new List<object>();
        foreach (var l in patch.Lines)
        {
            if (scope == "ticked" && !ticked.Contains(l.LineId)) continue;
            if (scope == "notake" && l.Takes.Count > 0) continue;
            if (!src.TryGetValue(l.FinalLine, out var o)) continue;
            if (which == "edited" && !o.SpokenEdited) continue;
            matched++;
            string text = tags switch
            {
                "words" => Core.Naming.SpokenText.StripTags(o.Spoken).Trim(),
                "add" => Core.Naming.SpokenText.StripTags(o.Spoken).Trim() == Core.Naming.SpokenText.StripTags(l.Spoken).Trim() ? o.Spoken : l.Spoken,
                _ => o.Spoken,
            };
            if (text == l.Spoken) { same++; continue; }
            if (keepMine && l.SpokenEdited) { mine++; continue; }
            changed++;
            if (samples.Count < 8) samples.Add(new { line = l.FinalLine, now = l.Spoken, then = text });
            if (B(p, "preview")) continue;
            l.Spoken = text;
            l.SpokenEdited = text != Core.Naming.SpokenText.DefaultFor(l.FinalLine);
            l.TagSuggestion = null;
            l.RefreshStale();
        }
        if (!B(p, "preview") && changed > 0) _lib.Save(patch);
        return new { matched, changed, mine, same, samples, patch = B(p, "preview") ? null : PatchDto(patch) };
    }

    /// <summary>An effect on lines' takes (Phase 2 or NPC Revoice): every take now, and new takes as they are made.</summary>
    private async Task<object?> TakesEffect(JsonElement p)
    {
        if (_jobCts != null) throw new InvalidOperationException("Post-processing waits until the running task (top bar) has finished: it may be writing these takes.");
        var kind = S(p, "kind", "patch");
        var id = S(p, "id");
        var ids = Strings(p, "lineIds").ToHashSet();
        // effect and soften are each optional: a key that isn't sent leaves that part of the line as it is
        string? fx = p.TryGetProperty("effect", out var fe) && fe.ValueKind == JsonValueKind.String ? fe.GetString() ?? "" : null;
        string? soften = p.TryGetProperty("soften", out var se) && se.ValueKind == JsonValueKind.String ? se.GetString() ?? "" : null;
        bool? noise = p.TryGetProperty("noise", out var ne) && ne.ValueKind is JsonValueKind.True or JsonValueKind.False ? ne.GetBoolean() : null;
        var tools = File.Exists(_settings.FfmpegPath) ? GrExternal() : null;
        if (fx is { Length: > 0 } && Effects.Find(null, fx) == null) throw new InvalidOperationException("Unknown effect.");
        if (soften is { Length: > 0 } && !PeakSoftener.IsMode(soften)) throw new InvalidOperationException("Unknown Soften peaks setting.");
        await _gate.WaitAsync();
        try
        {
            List<Phase2Line> lines; string root; Action save;
            if (kind == "books")
            {
                var proj = LoadBk(id);
                lines = proj.Lines.Where(l => ids.Contains(l.LineId)).Cast<Phase2Line>().ToList();
                root = _lib.BooksFolder(proj.Id);
                save = () => _lib.Save(proj);
                // An effect on a book's parts turns the project's reverb off for that book (two reverbs sound muddy); off again turns it back on.
                foreach (var stem in proj.Lines.Where(l => ids.Contains(l.LineId)).Select(l => l.Stem).Distinct())
                    if (fx != null && proj.Books.FirstOrDefault(b => b.Stem == stem) is { } bk) bk.Reverb = fx.Length > 0 ? false : null;
            }
            else if (kind == "revoice")
            {
                var proj = LoadRv(id);
                lines = proj.Lines.Where(l => ids.Contains(l.LineId)).Cast<Phase2Line>().ToList();
                root = _lib.RevoiceFolder(proj.Id);
                save = () => _lib.Save(proj);
            }
            else
            {
                var patch = LoadPatch(id);
                lines = patch.Lines.Where(l => ids.Contains(l.LineId)).ToList();
                root = _lib.PatchFolder(patch.Id);
                save = () => _lib.Save(patch);
            }
            int takes = 0; var problems = new List<string>();
            var sem = new SemaphoreSlim(Math.Clamp(Environment.ProcessorCount / 2, 1, 6));
            await Task.WhenAll(lines.Select(async l =>
            {
                await sem.WaitAsync();
                try { Interlocked.Add(ref takes, await TakeEffects.SetAsync(root, l, fx, null, tools, default, soften, noise)); }
                catch (Exception ex) { lock (problems) problems.Add(l.FinalLine + ": " + FriendlyError.From(ex)); }
                finally { sem.Release(); }
            }));
            save();
            if (kind == "books") return new { takes, problems };
            if (kind == "revoice")
            {
                var proj = LoadRv(id);
                return new { lines = proj.Lines.Where(l => ids.Contains(l.LineId)).Select(RvLineDto).ToList(), takes, problems };
            }
            return new { patch = PatchDto(LoadPatch(id)), takes, problems };
        }
        finally { _gate.Release(); }
    }
}
