using System.Security.Cryptography;
using System.Text;
using DbvoStudio.Core.Archives;
using DbvoStudio.Core.Audio;
using DbvoStudio.Core.Plugins;
using DbvoStudio.Core.Projects;
using DbvoStudio.Core.Tools;

namespace DbvoStudio.Core.Revoice;

/// <summary>Loaded plugins and voice files for one project. Opening Skyrim.esm takes a while, so the app keeps this around.</summary>
public sealed class RevoiceSession : IDisposable
{
    public required DialogueIndex Dialogue { get; init; }
    public required VoiceFileIndex Files { get; init; }
    public required string Signature { get; init; }
    public void Dispose() => Files.Dispose();
}

public sealed class CoverageReport
{
    public int CanSay { get; set; }
    public int Covered { get; set; }
    public List<CoverageGap> Gaps { get; set; } = new();
}

public sealed record CoverageGap(string Plugin, string Reason, int Lines, List<string> Examples);

public sealed record ListSuggestion(string Key, string EditorId, string Plugin, bool ContainsBase, int GatedLines, bool Selected);

public sealed record NpcCopy(string Key, string EditorId, string Name, string Plugin, string VoiceType);

public sealed class RevoiceExportReport
{
    public int Written { get; set; }
    public int Failed { get; set; }
    public List<string> Errors { get; } = new();
    public List<string> Files { get; } = new();
}

/// <summary>NPC Revoice Tool: scan plugins and voice files into lines, play originals, export .fuz files and the optional new voice type pack.</summary>
public sealed class RevoiceService
{
    private readonly Library _lib;
    private readonly AppSettings _settings;
    public RevoiceService(Library lib, AppSettings settings) { _lib = lib; _settings = settings; }

    public static readonly string[] BaseMasters = { "Skyrim.esm", "Update.esm", "Dawnguard.esm", "HearthFires.esm", "Dragonborn.esm" };

    /// <summary>Add (or refresh) the base game plugins and archives from the base game folder.</summary>
    public void ApplyBaseGame(RevoiceProject p)
    {
        p.Plugins.RemoveAll(x => x.BaseGame);
        p.Sources.RemoveAll(x => x.BaseGame);
        if (!p.UseBaseGame) return;
        var data = DataFolder(_settings.BaseGameFolder);
        if (data == null) return;
        int i = 0;
        foreach (var m in BaseMasters)
        {
            var f = Path.Combine(data, m);
            if (File.Exists(f)) p.Plugins.Insert(i++, new RevoicePlugin { Path = f, FileName = m, BaseGame = true });
        }
        foreach (var bsa in Directory.EnumerateFiles(data, "*.bsa"))
        {
            var name = Path.GetFileName(bsa);
            bool voice = name.StartsWith("Skyrim - Voices", StringComparison.OrdinalIgnoreCase);
            bool strings = name.Equals("Skyrim - Interface.bsa", StringComparison.OrdinalIgnoreCase);
            bool master = BaseMasters.Any(m => Path.GetFileNameWithoutExtension(m).Equals(Path.GetFileNameWithoutExtension(name), StringComparison.OrdinalIgnoreCase));
            if (voice || strings || master) p.Sources.Add(new RevoiceSource { Kind = "bsa", Path = bsa, Priority = -100, BaseGame = true });
        }
        p.Sources.Add(new RevoiceSource { Kind = "folder", Path = data, Priority = -99, BaseGame = true });
    }

    public static string? DataFolder(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return null;
        if (File.Exists(Path.Combine(folder, "Skyrim.esm"))) return folder;
        var d = Path.Combine(folder, "Data");
        return File.Exists(Path.Combine(d, "Skyrim.esm")) ? d : null;
    }

    /// <summary>Masters first (topological), keeping the given order otherwise.</summary>
    public static List<RevoicePlugin> Ordered(IEnumerable<RevoicePlugin> plugins, Func<RevoicePlugin, IReadOnlyList<string>> masters)
    {
        var list = plugins.ToList();
        var byName = list.GroupBy(p => p.FileName, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<RevoicePlugin>();
        void Visit(RevoicePlugin p, int depth)
        {
            if (done.Contains(p.FileName) || depth > 64) return;
            foreach (var m in masters(p)) if (byName.TryGetValue(m, out var mp)) Visit(mp, depth + 1);
            if (done.Add(p.FileName)) result.Add(p);
        }
        foreach (var p in list) Visit(p, 0);
        return result;
    }

    public static string SessionSignature(RevoiceProject p) =>
        string.Join("|", p.Plugins.Select(x => x.Path + "@" + (File.Exists(x.Path) ? File.GetLastWriteTimeUtc(x.Path).Ticks : 0))) + "#" +
        string.Join("|", p.Sources.Select(s => s.Kind + ":" + s.Path + ":" + s.Priority)) + "#" + p.Language;

    /// <summary>Read every plugin and voice source. Slow for Skyrim.esm (tens of seconds); reuse the session while nothing changed.</summary>
    public RevoiceSession Open(RevoiceProject p, IProgress<string>? progress = null)
    {
        var bsas = new List<BsaArchive>();
        try
        {
            foreach (var s in p.Sources.Where(s => s.Kind == "bsa" && File.Exists(s.Path)))
            {
                try { bsas.Add(BsaArchive.Open(s.Path)); } catch { /* reported by the voice index */ }
            }
            var files = new List<PluginFile>();
            var plugins = p.Plugins.Where(x => File.Exists(x.Path)).ToList();
            var headers = plugins.ToDictionary(x => x, x => (IReadOnlyList<string>)ReadMasters(x.Path));
            foreach (var pl in Ordered(plugins, x => headers[x]))
            {
                progress?.Report("Reading " + pl.FileName);
                files.Add(PluginFile.Read(pl.Path, DialogueIndex.Signatures));
            }
            StringsTable? Strings(PluginFile f)
            {
                var folder = Path.Combine(Path.GetDirectoryName(f.Path)!, "Strings");
                if (StringsTable.Exists(folder, f.BaseName, p.Language)) return StringsTable.Load(folder, f.BaseName, p.Language);
                if (!string.IsNullOrWhiteSpace(_settings.StringsFolder) && StringsTable.Exists(_settings.StringsFolder, f.BaseName, p.Language))
                    return StringsTable.Load(_settings.StringsFolder, f.BaseName, p.Language);
                foreach (var b in bsas) if (StringsTable.FromArchive(b, f.BaseName, p.Language) is { } t) return t;
                return null;
            }
            progress?.Report("Indexing dialogue");
            var dialogue = DialogueIndex.Build(files, Strings);
            progress?.Report("Indexing voice files");
            var vf = VoiceFileIndex.Build(p.Sources, _settings.SevenZipPath);
            return new RevoiceSession { Dialogue = dialogue, Files = vf, Signature = SessionSignature(p) };
        }
        finally
        {
            foreach (var b in bsas) b.Dispose();
        }
    }

    public static List<string> ReadMasters(string path)
    {
        using var fs = File.OpenRead(path);
        var head = new byte[24];
        if (fs.Read(head, 0, 24) < 24) return new();
        uint size = BitConverter.ToUInt32(head, 4);
        var data = new byte[size];
        fs.ReadExactly(data);
        return Subrecord.Parse(data).Where(s => s.Type == "MAST").Select(s => PluginRecord.ZString(s.Data)).ToList();
    }

    private static string LineId(string pluginFolder, string vt, string stem)
    {
        var h = SHA1.HashData(Encoding.UTF8.GetBytes((pluginFolder + "|" + vt + "|" + stem).ToLowerInvariant()));
        return Convert.ToHexString(h, 0, 6).ToLowerInvariant();
    }

    private static readonly Dictionary<int, string> EmotionTag = new()
    {
        [1] = "[angry]", [2] = "[disgusted]", [3] = "[scared]", [4] = "[sad]", [5] = "[happy]", [6] = "[surprised]", [7] = "[confused]",
    };

    public static string? EmotionTagFor(int emotion, int value) => value >= 30 && EmotionTag.TryGetValue(emotion, out var t) ? t : null;

    /// <summary>Rebuild the line list from the session, keeping takes and edits of lines that still exist.</summary>
    public RevoiceScanReport Scan(RevoiceProject p, RevoiceSession s)
    {
        var d = s.Dialogue;
        var rep = new RevoiceScanReport
        {
            ScannedUtc = DateTime.UtcNow, VoiceFiles = s.Files.FileCount,
            MissingMasters = d.MissingMasters.OrderBy(x => x).ToList(), MissingStrings = d.MissingStrings.ToList(), Errors = s.Files.Errors.ToList(),
        };
        var targets = new HashSet<string>(p.Plugins.Where(x => x.Target).Select(x => x.FileName), StringComparer.OrdinalIgnoreCase);
        var old = p.Lines.ToDictionary(l => l.LineId);
        var fresh = new Dictionary<string, RevoiceLine>();
        var byLine = s.Files.ByLine();
        var sharers = d.Infos.Values.Where(i => i.SharedInfo != null).GroupBy(i => i.SharedInfo!.Value).ToDictionary(g => g.Key, g => g.ToList());

        RevoiceLine Make(InfoRecord info, ResponseInfo r, string vtFolder, string vtKey, string stem, SpeakerResult sp, string speakerKey, string speakerName, string? planId)
        {
            string pf = d.PluginFolder(info);
            string id = LineId(pf, vtFolder, stem);
            var line = old.TryGetValue(id, out var o) ? o : new RevoiceLine { LineId = id };
            line.Info = info.Key.ToString();
            line.Response = r.Number;
            line.PluginFolder = pf;
            line.Plugin = info.WinningPlugin;
            line.VoiceType = vtFolder;
            line.VoiceTypeKey = vtKey;
            line.FileStem = stem;
            line.Key = stem.ToUpperInvariant();
            line.FinalLine = r.Text;
            line.OriginalLine = r.Text;
            if (!line.SpokenEdited || string.IsNullOrEmpty(line.Spoken)) line.Spoken = r.Text;
            if (p.EmotionTags && line.Takes.Count == 0 && !line.SpokenEdited && line.TagSuggestion == null && EmotionTagFor(r.Emotion, r.EmotionValue) is { } tag)
                line.TagSuggestion = tag + " " + r.Text;
            line.Emotion = r.Emotion;
            line.EmotionValue = r.EmotionValue;
            line.Notes = r.Notes;
            line.SpeakerKey = speakerKey;
            line.SpeakerName = speakerName;
            line.Npcs = string.Join(", ", sp.Npcs.Select(d.Describe).Distinct().OrderBy(x => x).Take(8)) + (sp.Npcs.Count > 8 ? $" +{sp.Npcs.Count - 8}" : "");
            line.Certainty = sp.Certainty.ToString();
            line.Via = sp.Via;
            line.PlanId = planId;
            line.Gone = false;
            if (d.Topics.TryGetValue(info.Topic, out var t))
            {
                line.Topic = t.EditorId;
                line.Quest = t.Quest is { } q && d.Quests.TryGetValue(q, out var qi) ? qi.EditorId : "";
            }
            var file = s.Files.Find(pf, planId != null ? d.VoiceTypeFolder(ParseKey(p.NewVoiceTypes.First(x => x.Id == planId).BaseVoiceType)) : vtFolder, info.Key.Id, r.Number);
            if (file == null && line.OriginalSourceId != null && p.Sources.FirstOrDefault(x => x.Id == line.OriginalSourceId) is { } os)
                line.Original = $"{Path.GetFileName(os.Path)}: {line.OriginalInner} (linked: {line.OriginalMatch})";
            else { line.Original = file?.Display; if (file != null) { line.OriginalSourceId = null; line.OriginalInner = null; line.OriginalMatch = null; } }
            line.SourceWav = null;
            if (line.Takes.Count == 0 && line.Status is not Phase2Status.Failed) line.Status = Phase2Status.Pending;
            fresh[id] = line;
            return line;
        }

        // Voice type -> NPCs that use it (directly or through templates), built once.
        Dictionary<FormKey, List<NpcInfo>>? users = null;
        List<NpcInfo> UsersOf(FormKey vt)
        {
            if (users == null)
            {
                users = new();
                foreach (var n in d.Npcs.Values)
                    foreach (var v in d.VoiceTypesOf(n.Key))
                        (users.TryGetValue(v, out var l) ? l : users[v] = new()).Add(n);
            }
            return users.TryGetValue(vt, out var r) ? r : new();
        }
        static string Norm(NpcInfo n) => n.Name.Trim().ToLowerInvariant();
        // One key per character: copies of an NPC (same name and voice type, e.g. a quest copy) share it.
        FormKey Canon(NpcInfo n, FormKey vt)
        {
            if (n.Name.Length == 0) return n.Key;
            return UsersOf(vt).Where(x => Norm(x) == Norm(n)).Select(x => x.Key).OrderBy(k => d.LoadOrder.FindIndex(p => p.Equals(k.Plugin, StringComparison.OrdinalIgnoreCase))).ThenBy(k => k.Id).FirstOrDefault(n.Key);
        }
        HashSet<FormKey> QuestActors(FormKey? quest)
        {
            var r = new HashSet<FormKey>();
            if (quest is not { } q || !d.Quests.TryGetValue(q, out var qi)) return r;
            foreach (var a in qi.Aliases.Values)
            {
                if (a.UniqueActor is { } u) r.Add(u);
                if (a.ForcedRef is { } f && d.PlacedActors.TryGetValue(f, out var b)) r.Add(b);
            }
            return r;
        }
        // Who says a line of one voice type: the conditions' NPC, else the only named character with that voice type,
        // else the only one from this mod or this quest. Falls back to the voice type with the likely NPCs listed.
        (string Key, string Name, string? Guess) ResolveSpeaker(InfoRecord info, FormKey? vt, string vtFolder, List<FormKey> npcs)
        {
            if (vt is not { } v) return ("vt:" + vtFolder.ToLowerInvariant(), vtFolder, null);
            if (npcs.Count == 1)
            {
                var one = d.Npcs.TryGetValue(npcs[0], out var ni) ? Canon(ni, v) : npcs[0];
                return ("npc:" + one, d.Describe(npcs[0]), null);
            }
            var cand = npcs.Count > 1 ? npcs.Where(d.Npcs.ContainsKey).Select(k => d.Npcs[k]).ToList() : UsersOf(v);
            string? via = npcs.Count > 1 ? "named in the conditions" : "only NPC with this voice type";
            List<IGrouping<string, NpcInfo>> Named(IEnumerable<NpcInfo> xs) => xs.Where(x => x.Name.Length > 0).GroupBy(Norm).ToList();
            var named = Named(cand);
            if (named.Count > 1)
            {
                var qa = d.Topics.TryGetValue(info.Topic, out var t) ? QuestActors(t.Quest) : new();
                var inQuest = Named(cand.Where(x => qa.Contains(x.Key) || (x.Template is { } tp && qa.Contains(tp))));
                if (inQuest.Count == 1) { named = inQuest; via = "only NPC in this quest with this voice type"; }
            }
            if (named.Count == 1)
            {
                var n = named[0].First();
                return ("npc:" + Canon(n, v), n.Display, via);
            }
            var names = named.Select(g => g.First().Display).OrderBy(x => x).ToList();
            var label = names.Count == 0 ? vtFolder
                : $"{vtFolder} ({names.Count} NPCs: {string.Join(", ", names.Take(3))}{(names.Count > 3 ? ", …" : "")})";
            return ("vt:" + vtFolder.ToLowerInvariant(), label, null);
        }

        foreach (var info in d.Infos.Values)
        {
            if (!targets.Contains(info.Key.Plugin) || info.Responses.Count == 0) continue;
            rep.Infos++;
            var sp = d.Speakers(info);
            if (sharers.TryGetValue(info.Key, out var sh))
                foreach (var other in sh) { var o = d.Speakers(other); sp.VoiceTypes.UnionWith(o.VoiceTypes); sp.Npcs.UnionWith(o.Npcs); }
            string pf = d.PluginFolder(info);
            foreach (var r in info.Responses)
            {
                rep.Responses++;
                var stem = d.FileName(info, r.Number);
                if (stem == null) continue;
                var folders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var vt in sp.VoiceTypes) folders[d.VoiceTypeFolder(vt)] = vt.ToString();
                foreach (var f in byLine[(pf.ToLowerInvariant(), info.Key.Id, r.Number)])
                    if (!folders.ContainsKey(f.VoiceType)) folders[f.VoiceType] = d.VoiceTypes.Values.FirstOrDefault(v => v.EditorId.Equals(f.VoiceType, StringComparison.OrdinalIgnoreCase))?.Key.ToString() ?? "";
                if (folders.Count == 0) { rep.UnknownSpeaker++; continue; }
                foreach (var (vtFolder, vtKey) in folders)
                {
                    var vtk = vtKey.Length > 0 ? ParseKey(vtKey) : (FormKey?)null;
                    var npcs = vtk == null ? new List<FormKey>() : sp.Npcs.Where(n => d.VoiceTypesOf(n).Contains(vtk.Value)).ToList();
                    var (key, name, guess) = ResolveSpeaker(info, vtk, vtFolder, npcs);
                    var line = Make(info, r, vtFolder, vtKey, stem, sp, key, name, null);
                    if (guess != null)
                    {
                        line.Via = (line.Via.Length > 0 ? line.Via + " · " : "") + guess;
                        if (line.Certainty == nameof(SpeakerCertainty.Unknown)) line.Certainty = nameof(SpeakerCertainty.Probable);
                    }
                    if (line.Original != null) rep.WithOriginal++;
                }
            }
        }

        // New voice type plans: every line the NPC can say with the original voice type, under the new voice type.
        foreach (var plan in p.NewVoiceTypes.Where(x => x.EditorId.Length > 0 && x.BaseVoiceType.Length > 0))
        {
            var baseVt = ParseKey(plan.BaseVoiceType);
            var npcKeys = plan.Npcs.Select(ParseKey).ToHashSet();
            foreach (var (info, r, sp) in LinesFor(d, baseVt, npcKeys, plan.LineSet))
            {
                var stem = d.FileName(info, r.Number);
                if (stem == null) continue;
                var name = npcKeys.Count > 0 ? d.Describe(npcKeys.First()) : plan.EditorId;
                Make(info, r, plan.EditorId, "", stem, sp, "nvt:" + plan.Id, name + " (new voice type)", plan.Id);
            }
        }

        // Keep lines that disappeared but have takes.
        foreach (var o in old.Values.Where(o => !fresh.ContainsKey(o.LineId) && o.Takes.Count > 0)) { o.Gone = true; fresh[o.LineId] = o; }
        p.Lines = fresh.Values.OrderBy(l => l.Plugin).ThenBy(l => l.Quest).ThenBy(l => l.Topic).ThenBy(l => l.FileStem).ToList();
        foreach (var key in p.Lines.Select(l => l.SpeakerKey).Distinct()) if (!p.Speakers.ContainsKey(key)) p.Speakers[key] = new SpeakerAssignment();
        rep.Lines = p.Lines.Count;
        rep.Orphans = FindOrphans(p, s).Count;
        rep.MasterCosts = MasterCosts(p, d);
        p.LastScan = rep;
        return rep;
    }

    /// <summary>What each missing master costs, in plain words.</summary>
    private static List<string> MasterCosts(RevoiceProject p, DialogueIndex d)
    {
        var list = new List<string>();
        foreach (var m in d.MissingMasters)
        {
            var key = m.ToLowerInvariant();
            int topics = d.Infos.Values.Count(i => i.Topic.Plugin == key && !d.Topics.ContainsKey(i.Topic));
            int quests = d.Topics.Values.Count(t => t.Quest is { } q && q.Plugin == key && !d.Quests.ContainsKey(q));
            int npcRefs = d.Infos.Values.Count(i => i.Conditions.Any(c => c.Param1 is { } k && k.Plugin == key));
            var parts = new List<string>();
            if (topics + quests > 0) parts.Add($"{topics + quests} line(s) or topics can't be named (their topic or quest is in {m})");
            if (npcRefs > 0) parts.Add($"{npcRefs} line(s) name NPCs, factions or voice types from {m}, so their speakers may be unknown");
            if (parts.Count == 0) parts.Add("nothing in the loaded plugins depends on it for voice files");
            list.Add($"{m}: " + string.Join("; ", parts) + ".");
        }
        return list;
    }

    public static FormKey ParseKey(string s)
    {
        int i = s.LastIndexOf('|');
        return new FormKey(s[..i].ToLowerInvariant(), Convert.ToUInt32(s[(i + 1)..], 16));
    }

    /// <summary>Lines (with responses) the given NPCs can say with a voice type.</summary>
    public static IEnumerable<(InfoRecord Info, ResponseInfo Response, SpeakerResult Speakers)> LinesFor(DialogueIndex d, FormKey vt, HashSet<FormKey> npcs, string lineSet)
    {
        foreach (var info in d.Infos.Values)
        {
            if (info.Responses.Count == 0) continue;
            var sp = d.Speakers(info);
            if (!sp.VoiceTypes.Contains(vt)) continue;
            if (lineSet == "npc" && sp.Npcs.Count > 0 && npcs.Count > 0 && !sp.Npcs.Overlaps(npcs))
            {
                // Tied to other NPCs; keep only when one of them uses the same voice type through a leveled list or template of ours.
                continue;
            }
            foreach (var r in info.Responses) yield return (info, r, sp);
        }
    }

    // ---------------------------------------------------------------- orphaned files (merged, compacted or renamed plugins)

    /// <summary>Voice files in the mod's own sources (not the base game) that match no line in the loaded plugins and are not linked yet.</summary>
    public static List<VoiceFile> FindOrphans(RevoiceProject p, RevoiceSession s)
    {
        var d = s.Dialogue;
        var linked = p.Lines.Where(l => l.OriginalInner != null).Select(l => (l.OriginalSourceId, l.OriginalInner!.ToLowerInvariant())).ToHashSet();
        return s.Files.Files.Where(f => !f.Source.BaseGame)
            .Where(f => !(d.Infos.TryGetValue(new FormKey(f.PluginFolder.ToLowerInvariant(), f.FormId), out var info) && info.Responses.Any(r => r.Number == f.Response)))
            .Where(f => !linked.Contains((f.Source.Id, f.Inner.ToLowerInvariant())))
            .ToList();
    }

    private static string Prefix(string stem) { var b = stem.Split('_'); return string.Join("_", b.Take(b.Length - 2)).ToLowerInvariant(); }

    /// <summary>Silent lines (no current file) an orphan could belong to: same voice type when possible.</summary>
    private static List<RevoiceLine> Candidates(RevoiceProject p, VoiceFile f)
    {
        var silent = p.Lines.Where(l => l.Original == null && l.PlanId == null && !l.Gone).ToList();
        var sameVt = silent.Where(l => l.VoiceType.Equals(f.VoiceType, StringComparison.OrdinalIgnoreCase)).ToList();
        return sameVt.Count > 0 ? sameVt : silent;
    }

    /// <summary>Quick pass without audio: same quest and topic part of the name and the same response number, with one candidate.</summary>
    public static List<OrphanMatch> MatchByName(RevoiceProject p, IEnumerable<VoiceFile> orphans)
    {
        var list = new List<OrphanMatch>();
        foreach (var f in orphans)
        {
            var pre = Prefix(f.Stem);
            var c = Candidates(p, f).Where(l => l.Response == f.Response && l.FileStem != null && Prefix(l.FileStem) == pre).ToList();
            list.Add(new OrphanMatch
            {
                SourceId = f.Source.Id, Inner = f.Inner, Display = f.Display, VoiceType = f.VoiceType,
                LineId = c.Count == 1 ? c[0].LineId : null, LineText = c.Count == 1 ? c[0].FinalLine : null,
                Score = c.Count == 1 ? 0.95 : 0, Method = c.Count == 1 ? "same quest and topic" : c.Count > 1 ? $"{c.Count} lines with the same quest and topic" : "",
            });
        }
        return list;
    }

    public static string NormalizeWords(string text)
    {
        var sb = new StringBuilder();
        foreach (var ch in SpokenTextStrip(text).ToLowerInvariant()) sb.Append(char.IsLetterOrDigit(ch) || ch == '\'' ? ch : ' ');
        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static string SpokenTextStrip(string s) => System.Text.RegularExpressions.Regex.Replace(s, @"\[[^\]]*\]|\([^)]*\)|<[^>]*>", " ");

    /// <summary>Word-level similarity 0..1 (1 - edit distance / longer length).</summary>
    public static double Similarity(string a, string b)
    {
        var x = NormalizeWords(a).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var y = NormalizeWords(b).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (x.Length == 0 || y.Length == 0) return 0;
        var prev = new int[y.Length + 1];
        var cur = new int[y.Length + 1];
        for (int j = 0; j <= y.Length; j++) prev[j] = j;
        for (int i = 1; i <= x.Length; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= y.Length; j++)
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + (x[i - 1] == y[j - 1] ? 0 : 1));
            (prev, cur) = (cur, prev);
        }
        return 1.0 - (double)prev[y.Length] / Math.Max(x.Length, y.Length);
    }

    /// <summary>Match transcripts of orphaned files to silent lines' subtitles; each line is used once, best scores first.</summary>
    public static List<OrphanMatch> MatchByTranscript(RevoiceProject p, IReadOnlyList<(VoiceFile File, string Transcript)> items, double minScore = 0.55)
    {
        var scored = new List<(int Item, RevoiceLine Line, double Score)>();
        for (int i = 0; i < items.Count; i++)
        {
            var (f, t) = items[i];
            foreach (var l in Candidates(p, f))
            {
                double sc = Similarity(t, l.FinalLine);
                if (l.Response == f.Response) sc += 0.02;
                if (l.FileStem != null && Prefix(l.FileStem) == Prefix(f.Stem)) sc += 0.05;
                if (sc >= minScore) scored.Add((i, l, Math.Min(1, sc)));
            }
        }
        var usedLines = new HashSet<string>();
        var result = items.Select(x => new OrphanMatch { SourceId = x.File.Source.Id, Inner = x.File.Inner, Display = x.File.Display, VoiceType = x.File.VoiceType, Transcript = x.Transcript }).ToList();
        foreach (var (i, l, sc) in scored.OrderByDescending(x => x.Score))
        {
            if (result[i].LineId != null || usedLines.Contains(l.LineId)) continue;
            result[i].LineId = l.LineId;
            result[i].LineText = l.FinalLine;
            result[i].Score = Math.Round(sc, 2);
            result[i].Method = $"Whisper {sc:0.00}";
            usedLines.Add(l.LineId);
        }
        return result;
    }

    /// <summary>Link approved orphans to their lines (their audio becomes the line's current file).</summary>
    public static int ApplyOrphanMatches(RevoiceProject p, IEnumerable<OrphanMatch> approved)
    {
        int n = 0;
        foreach (var m in approved)
        {
            var l = p.Lines.FirstOrDefault(x => x.LineId == m.LineId);
            var src = p.Sources.FirstOrDefault(x => x.Id == m.SourceId);
            if (l == null || src == null) continue;
            l.OriginalSourceId = m.SourceId;
            l.OriginalInner = m.Inner;
            l.OriginalMatch = m.Method;
            l.Original = $"{Path.GetFileName(src.Path)}: {m.Inner} (linked: {m.Method})";
            n++;
        }
        return n;
    }

    // ---------------------------------------------------------------- original audio

    /// <summary>The game's current file for a line, decoded to WAV bytes (for playback, or as the Voice Changer source).</summary>
    public static async Task<byte[]?> OriginalWavAsync(RevoiceProject p, RevoiceLine line, RevoiceSession s, ExternalTools tools, CancellationToken ct)
    {
        var vt = line.VoiceType;
        if (line.PlanId != null && p.NewVoiceTypes.FirstOrDefault(x => x.Id == line.PlanId) is { } plan) vt = s.Dialogue.VoiceTypeFolder(ParseKey(plan.BaseVoiceType));
        var info = ParseKey(line.Info);
        var f = s.Files.Find(line.PluginFolder, vt, info.Id, line.Response);
        if (f == null && line.OriginalSourceId != null && line.OriginalInner != null && p.Sources.FirstOrDefault(x => x.Id == line.OriginalSourceId) is { } os)
            f = VoiceFileIndex.Make(os, line.OriginalInner);
        if (f == null) return null;
        return await DecodeAsync(s.Files, f, tools, ct);
    }

    /// <summary>Any voice file (.fuz / .xwm / .wav, loose or in an archive) as WAV bytes.</summary>
    public static async Task<byte[]> DecodeAsync(VoiceFileIndex files, VoiceFile f, ExternalTools tools, CancellationToken ct)
    {
        var bytes = files.Read(f);
        var work = Path.Combine(Path.GetTempPath(), "dbvo");
        Directory.CreateDirectory(work);
        using var w = new WorkFolder(work);
        string input;
        if (f.Extension == ".fuz")
        {
            var (_, audio) = FuzFile.Split(bytes);
            bool isWav = audio.Length > 12 && audio[8] == 'W' && audio[9] == 'A' && audio[10] == 'V' && audio[11] == 'E';
            if (isWav) return audio;
            input = w.File("s.xwm");
            await File.WriteAllBytesAsync(input, audio, ct);
        }
        else if (f.Extension == ".wav") return bytes;
        else
        {
            input = w.File("s.xwm");
            await File.WriteAllBytesAsync(input, bytes, ct);
        }
        var wav = w.File("s.wav");
        await tools.ToWavAsync(input, wav, 44100, false, 0, ct);
        return await File.ReadAllBytesAsync(wav, ct);
    }

    // ---------------------------------------------------------------- export

    /// <summary>Write the chosen takes as .fuz files at the game's paths under the output folder.</summary>
    public async Task<RevoiceExportReport> ExportAsync(RevoiceProject p, ExternalTools tools, bool overwrite, IProgress<GenerationProgress>? progress, CancellationToken ct, RevoiceSession? session = null)
    {
        if (string.IsNullOrWhiteSpace(p.OutputFolder)) throw new InvalidOperationException("Set the output folder first.");
        var rep = new RevoiceExportReport();
        var lines = p.Lines.Where(l => !l.Excluded && l.SelectedTake != null && !(p.Speakers.TryGetValue(l.SpeakerKey, out var a) && a.Skip)).ToList();
        var root = _lib.RevoiceFolder(p.Id);
        var workRoot = Path.Combine(Path.GetTempPath(), "dbvo");
        Directory.CreateDirectory(workRoot);
        int done = 0;
        var gate = new SemaphoreSlim(Math.Max(2, Environment.ProcessorCount / 2));
        await Task.WhenAll(lines.Select(async line =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var target = Path.Combine(p.OutputFolder, line.RelativePath);
                if (!overwrite && File.Exists(target)) return;
                using var work = new WorkFolder(workRoot);
                var t = line.SelectedTake!;
                var xwm = work.File("a.xwm");
                await tools.ToXwmaAsync(Path.Combine(root, t.WavPath), xwm, 48000, ct);
                var fuz = FuzFile.Build(await File.ReadAllBytesAsync(Path.Combine(root, t.LipPath), ct), await File.ReadAllBytesAsync(xwm, ct));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await File.WriteAllBytesAsync(target, fuz, ct);
                lock (rep) { rep.Written++; }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { lock (rep) { rep.Failed++; rep.Errors.Add($"{line.FileStem}: {ex.Message}"); } }
            finally
            {
                gate.Release();
                progress?.Report(new GenerationProgress(Interlocked.Increment(ref done), lines.Count, rep.Failed, line.FileStem, null));
            }
        }));
        if (p.NewVoiceTypes.Count > 0) rep.Files.AddRange(WriteVoiceTypePack(p, session));
        p.LastExportUtc = DateTime.UtcNow;
        return rep;
    }

    // ---------------------------------------------------------------- new voice type pack

    public string PluginName(RevoiceProject p) =>
        (string.IsNullOrWhiteSpace(p.VoiceTypePlugin) ? Safe(p.Name) + " Voices" : Path.GetFileNameWithoutExtension(p.VoiceTypePlugin)) + ".esp";

    private static string Safe(string s) => string.Concat(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();

    /// <summary>
    /// The ESL plugin with only the new voice type records, a SkyPatcher NPC file, a FormList Manipulator file and
    /// instructions. Returns the files written. No NPC or form list records are overridden.
    /// </summary>
    public List<string> WriteVoiceTypePack(RevoiceProject p, RevoiceSession? s)
    {
        var written = new List<string>();
        var plans = p.NewVoiceTypes.Where(x => x.EditorId.Length > 0).ToList();
        if (plans.Count == 0) return written;
        string plugin = PluginName(p), baseName = Path.GetFileNameWithoutExtension(plugin);
        var records = plans.Select((x, i) => (x, Id: 0x800u + (uint)i)).ToList();
        byte Flags(NewVoiceTypePlan x) => s != null && s.Dialogue.VoiceTypes.TryGetValue(ParseKey(x.BaseVoiceType), out var v) ? v.Flags : (byte)1;
        var bytes = VoiceTypePluginWriter.Build(records.Select(r => (r.Id, r.x.EditorId, Flags(r.x))).ToList(),
            "Made with DBVO Voice Pack Studio (NPC Revoice Tool). Voice types are assigned by SkyPatcher and added to form lists by FormList Manipulator.");
        var espPath = Path.Combine(p.OutputFolder, plugin);
        Directory.CreateDirectory(p.OutputFolder);
        File.WriteAllBytes(espPath, bytes);
        written.Add(espPath);

        string Ref(string key, string? editorId)
        {
            var k = ParseKey(key);
            var spelled = s?.Dialogue.Spelling.TryGetValue(k.Plugin, out var sp) == true ? sp : k.Plugin;
            return p.ReferenceByEditorId && !string.IsNullOrEmpty(editorId) ? editorId : $"{spelled}|{k.Id:X}";
        }
        string FlmRef(string key, string? editorId)
        {
            var k = ParseKey(key);
            var spelled = s?.Dialogue.Spelling.TryGetValue(k.Plugin, out var sp) == true ? sp : k.Plugin;
            return p.ReferenceByEditorId && !string.IsNullOrEmpty(editorId) ? editorId : k.ToFlm(spelled);
        }

        var sky = new StringBuilder();
        sky.AppendLine($"; SkyPatcher: give these NPC records the new voice type. Requires iEnableNPCPatching=1 in SKSE\\Plugins\\SkyPatcher.ini.");
        var flm = new StringBuilder();
        flm.AppendLine("; FormList Manipulator: add the new voice type to the lists that unlock dialogue for it.");
        foreach (var (x, id) in records)
        {
            string vtRef = p.ReferenceByEditorId ? x.EditorId : $"{plugin}|{id:X}";
            sky.AppendLine($"; {x.EditorId}");
            foreach (var npc in x.Npcs)
            {
                var k = ParseKey(npc);
                var spelled = s?.Dialogue.Spelling.TryGetValue(k.Plugin, out var sp) == true ? sp : k.Plugin;
                var name = s?.Dialogue.Npcs.TryGetValue(k, out var n) == true ? "  ; " + n.Display : "";
                sky.AppendLine($"filterByNPCs={spelled}|{k.Id:X}:voiceType={vtRef}{name}");
            }
            foreach (var extra in x.ExtraNpcs.Where(e => e.Contains('|')))
                sky.AppendLine($"filterByNPCs={extra.Trim()}:voiceType={vtRef}  ; added by hand");
            flm.AppendLine($"; {x.EditorId}");
            foreach (var list in x.Lists)
            {
                var k = ParseKey(list);
                string? ed = s?.Dialogue.Kinds.ContainsKey(k) == true ? EditorIdOf(s.Dialogue, k) : null;
                flm.AppendLine($"FormList = {FlmRef(list, ed)}|{(p.ReferenceByEditorId ? x.EditorId : $"0x{id:X}~{plugin}")}");
            }
        }
        var skyPath = Path.Combine(p.OutputFolder, "SKSE", "Plugins", "SkyPatcher", "npc", baseName, baseName + ".ini");
        Directory.CreateDirectory(Path.GetDirectoryName(skyPath)!);
        File.WriteAllText(skyPath, sky.ToString());
        written.Add(skyPath);
        var flmPath = Path.Combine(p.OutputFolder, baseName.Replace(' ', '_') + "_FLM.ini");
        File.WriteAllText(flmPath, flm.ToString());
        written.Add(flmPath);

        var doc = new StringBuilder();
        doc.AppendLine($"{baseName}: install notes");
        doc.AppendLine(new string('=', baseName.Length + 15));
        doc.AppendLine();
        doc.AppendLine("Requirements:");
        doc.AppendLine("- SkyPatcher, with NPC patching enabled: set iEnableNPCPatching=1 in SKSE\\Plugins\\SkyPatcher.ini.");
        doc.AppendLine("- FormList Manipulator (FLM).");
        doc.AppendLine();
        doc.AppendLine($"Load order: {plugin} is ESL-flagged and only adds voice type records, so it can load anywhere. Give the mod low priority so other mods that edit these NPCs still win; SkyPatcher applies the voice type afterwards.");
        doc.AppendLine();
        doc.AppendLine("Without SkyPatcher (xEdit method): load the plugin early, then forward the new voice type into the winning record of each NPC listed in the SkyPatcher file (and any copies of the character from other mods).");
        doc.AppendLine();
        doc.AppendLine("Mods that add dialogue for the original voice type, and are not covered by the form lists in the FLM file, will be silent for these NPCs (subtitles only). See the coverage report in the tool.");
        var docPath = Path.Combine(p.OutputFolder, baseName + " - Install notes.txt");
        File.WriteAllText(docPath, doc.ToString());
        written.Add(docPath);
        return written;
    }

    private static string? EditorIdOf(DialogueIndex d, FormKey k) =>
        d.VoiceTypes.TryGetValue(k, out var v) ? v.EditorId : d.Npcs.TryGetValue(k, out var n) ? n.EditorId : d.ListNames.TryGetValue(k, out var e) ? e : null;

    /// <summary>Form lists that contain the original voice type (directly or nested), with how many of the NPC's lines check each.</summary>
    public static List<ListSuggestion> SuggestLists(DialogueIndex d, NewVoiceTypePlan plan)
    {
        var baseVt = ParseKey(plan.BaseVoiceType);
        var npcs = plan.Npcs.Select(ParseKey).ToHashSet();
        var candidates = LinesFor(d, baseVt, npcs, plan.LineSet).Select(x => x.Info).Distinct().ToList();
        var gated = new Dictionary<FormKey, int>();
        foreach (var info in candidates)
        {
            foreach (var c in info.Conditions.Where(c => c.Function == Condition.GetIsVoiceType && c.Positive && c.Param1 is { } k && d.FormLists.ContainsKey(k)))
                gated[c.Param1!.Value] = gated.GetValueOrDefault(c.Param1!.Value) + 1;
            if (d.Topics.TryGetValue(info.Topic, out var t) && t.Quest is { } q && d.Quests.TryGetValue(q, out var qi))
                foreach (var a in qi.Aliases.Values.Where(a => a.VoiceTypes is { } vt && d.FormLists.ContainsKey(vt)))
                    if (info.Conditions.Any(c => c.Function == Condition.GetIsAliasRef && c.Param1Raw == a.Id)) gated[a.VoiceTypes!.Value] = gated.GetValueOrDefault(a.VoiceTypes!.Value) + 1;
        }
        var selected = plan.Lists.ToHashSet();
        return d.FormLists.Keys
            .Where(k => d.VoiceTypesOf(k).Contains(baseVt) || gated.ContainsKey(k))
            .Select(k => new ListSuggestion(k.ToString(), d.ListNames.GetValueOrDefault(k, k.ToString()), d.Spelling.GetValueOrDefault(k.Plugin, k.Plugin),
                d.FormLists[k].Contains(baseVt), gated.GetValueOrDefault(k), selected.Contains(k.ToString())))
            .OrderByDescending(x => x.GatedLines).ThenBy(x => x.EditorId).ToList();
    }

    /// <summary>
    /// What happens after the switch: give the NPCs the new voice type and add it to the chosen lists (in memory),
    /// then see which of the lines they could say before still pass their conditions.
    /// </summary>
    public static CoverageReport Coverage(DialogueIndex d, NewVoiceTypePlan plan, string pluginName)
    {
        var baseVt = ParseKey(plan.BaseVoiceType);
        var npcs = plan.Npcs.Select(ParseKey).ToHashSet();
        var before = LinesFor(d, baseVt, npcs, plan.LineSet).Select(x => x.Info).Distinct().ToList();
        var newVt = new FormKey(pluginName.ToLowerInvariant(), 0x800);
        var rep = new CoverageReport { CanSay = before.Sum(i => i.Responses.Count) };
        d.VoiceTypes[newVt] = new VoiceTypeInfo { Key = newVt, EditorId = plan.EditorId, Flags = d.VoiceTypes.TryGetValue(baseVt, out var bv) ? bv.Flags : (byte)1 };
        foreach (var n in npcs) d.NpcVoiceOverride[n] = newVt;
        foreach (var l in plan.Lists.Select(ParseKey))
        {
            if (!d.ExtraListMembers.TryGetValue(l, out var set)) d.ExtraListMembers[l] = set = new();
            set.Add(newVt);
        }
        try
        {
            var gaps = new Dictionary<(string Plugin, string Reason), List<InfoRecord>>();
            foreach (var info in before)
            {
                var sp = d.Speakers(info);
                if (sp.VoiceTypes.Contains(newVt)) { rep.Covered += info.Responses.Count; continue; }
                string reason = WhyBlocked(d, info, baseVt);
                var key = (d.Spelling.GetValueOrDefault(info.Key.Plugin, info.Key.Plugin), reason);
                if (!gaps.TryGetValue(key, out var list)) gaps[key] = list = new();
                list.Add(info);
            }
            rep.Gaps = gaps.Select(g => new CoverageGap(g.Key.Plugin, g.Key.Reason, g.Value.Sum(i => i.Responses.Count),
                    g.Value.Take(3).Select(i => i.Responses[0].Text).ToList()))
                .OrderByDescending(g => g.Lines).ToList();
        }
        finally
        {
            d.VoiceTypes.Remove(newVt);
            foreach (var n in npcs) d.NpcVoiceOverride.Remove(n);
            foreach (var l in plan.Lists.Select(ParseKey)) if (d.ExtraListMembers.TryGetValue(l, out var set)) { set.Remove(newVt); if (set.Count == 0) d.ExtraListMembers.Remove(l); }
        }
        return rep;
    }

    private static string WhyBlocked(DialogueIndex d, InfoRecord info, FormKey baseVt)
    {
        foreach (var c in info.Conditions)
        {
            if (c.Function == Condition.GetIsVoiceType && c.Param1 is { } k)
            {
                if (k == baseVt) return $"checks the voice type {d.VoiceTypeFolder(baseVt)} directly";
                if (d.FormLists.ContainsKey(k)) return $"needs list {d.ListNames.GetValueOrDefault(k, k.ToString())}";
            }
            if (c.Function == Condition.GetIsAliasRef) return "quest alias needs the original voice type";
        }
        if (d.Topics.TryGetValue(info.Topic, out var t) && t.Quest is { } q && d.Quests.TryGetValue(q, out var qi) && qi.DialogueConditions.Count > 0) return "quest conditions need the original voice type";
        return "other conditions";
    }

    /// <summary>Other NPC records that look like the same character (same name or editor ID), e.g. copies in other mods.</summary>
    public static List<NpcCopy> NpcCopies(DialogueIndex d, FormKey npc)
    {
        if (!d.Npcs.TryGetValue(npc, out var n)) return new();
        return d.Npcs.Values
            .Where(x => x.Key == npc || (n.Name.Length > 0 && x.Name == n.Name) || (n.EditorId.Length > 3 && x.EditorId.Contains(n.EditorId, StringComparison.OrdinalIgnoreCase)))
            .Select(x => new NpcCopy(x.Key.ToString(), x.EditorId, x.Display, d.Spelling.GetValueOrDefault(x.Key.Plugin, x.Key.Plugin), string.Join(",", d.VoiceTypesOf(x.Key).Select(d.VoiceTypeFolder))))
            .ToList();
    }
}
