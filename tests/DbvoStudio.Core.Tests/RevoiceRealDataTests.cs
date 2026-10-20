using System.Text.Json;
using DbvoStudio.Core.Archives;
using DbvoStudio.Core.Plugins;
using DbvoStudio.Core.Revoice;
using Xunit.Abstractions;

namespace DbvoStudio.Core.Tests;

/// <summary>Speaker resolution and naming against real mods (skipped where the files are absent).</summary>
public class RevoiceRealDataTests
{
    private readonly ITestOutputHelper _out;
    public RevoiceRealDataTests(ITestOutputHelper o) => _out = o;
    private const string Up = "/mnt/user-data/uploads";

    private static DialogueIndex Load(params string[] paths)
    {
        var interfaceBsa = Path.Combine(RealDataTests.Data, "Skyrim - Interface.bsa");
        using var bsa = File.Exists(interfaceBsa) ? BsaArchive.Open(interfaceBsa) : null;
        var files = paths.Select(p => PluginFile.Read(p, DialogueIndex.Signatures)).ToList();
        return DialogueIndex.Build(files, f => bsa == null ? null : StringsTable.FromArchive(bsa, f.BaseName));
    }

    [Fact]
    public void Vigilant_english_voice_files_match_names_and_speakers()
    {
        var vig = Path.Combine(Up, "VIGILANT Voiced - English Addon", "Vigilant.esm");
        var list = Path.Combine(Up, "vigilant-english-files.json");
        if (RealDataTests.F("Skyrim.esm") is not { } sky || !File.Exists(vig) || !File.Exists(list)) return;
        var idx = Load(sky, RealDataTests.F("Update.esm")!, vig);
        _out.WriteLine($"infos {idx.Infos.Count} npcs {idx.Npcs.Count} vts {idx.VoiceTypes.Count} quests {idx.Quests.Count} scenes {idx.Scenes.Count} missing masters {string.Join(",", idx.MissingMasters)} missing strings {string.Join(",", idx.MissingStrings)}");
        var entries = JsonDocument.Parse(File.ReadAllText(list)).RootElement.GetProperty("entries").EnumerateArray()
            .Select(e => e.GetProperty("name").GetString()!).Where(n => n.EndsWith(".fuz", StringComparison.OrdinalIgnoreCase)).ToList();
        int named = 0, speakerOk = 0, total = 0;
        var byVia = new Dictionary<string, (int ok, int all)>();
        var misses = new List<string>();
        foreach (var path in entries)
        {
            var parts = path.Split('/');
            string vt = parts[3], stem = Path.GetFileNameWithoutExtension(parts[4]);
            var bits = stem.Split('_');
            uint id = Convert.ToUInt32(bits[^2], 16);
            int resp = int.Parse(bits[^1]);
            total++;
            if (!idx.Infos.TryGetValue(new FormKey("vigilant.esm", id), out var info)) { misses.Add("no info " + stem); continue; }
            if (string.Equals(idx.FileName(info, resp), stem, StringComparison.OrdinalIgnoreCase)) named++;
            var sp = idx.Speakers(info);
            bool ok = sp.VoiceTypes.Any(v => string.Equals(idx.VoiceTypeFolder(v), vt, StringComparison.OrdinalIgnoreCase));
            var key = sp.Via.Length == 0 ? "(none)" : sp.Via;
            var cur = byVia.GetValueOrDefault(key);
            byVia[key] = (cur.ok + (ok ? 1 : 0), cur.all + 1);
            if (ok) speakerOk++;
            else if (misses.Count < 25) misses.Add($"{vt} {stem} via={key} got={string.Join(",", sp.VoiceTypes.Select(idx.VoiceTypeFolder).Take(6))}");
        }
        _out.WriteLine($"files {total}, names match {named}, speaker voice type found {speakerOk}");
        foreach (var kv in byVia) _out.WriteLine($"  {kv.Key}: {kv.Value.ok}/{kv.Value.all}");
        foreach (var m in misses) _out.WriteLine("  miss " + m);
        Assert.Equal(total, named);
        Assert.True(speakerOk >= total * 0.9, $"speaker resolution found the file's voice type for {speakerOk}/{total}");
    }

    [Fact]
    public void Voice_folder_is_the_plugin_that_defines_the_line()
    {
        // Aela Revoiced's archive holds files for lines Hearthfire adds to Skyrim.esm quests: they live under hearthfires.esm.
        var bsaPath = Path.Combine(Up, "Aela Revoiced", "Aela Revoiced.bsa");
        if (RealDataTests.F("Skyrim.esm") is not { } sky || RealDataTests.F("HearthFires.esm") is not { } hf || RealDataTests.F("Dawnguard.esm") is not { } dg || !File.Exists(bsaPath)) return;
        var idx = Load(sky, RealDataTests.F("Update.esm")!, dg, hf);
        using var bsa = BsaArchive.Open(bsaPath);
        int total = 0, found = 0, named = 0, inOtherQuest = 0;
        foreach (var e in bsa.Entries.Where(e => e.Path.EndsWith(".fuz") && (e.Path.Contains(@"\hearthfires.esm\") || e.Path.Contains(@"\dawnguard.esm\"))))
        {
            var parts = e.Path.Split('\\');
            string plugin = parts[2], stem = Path.GetFileNameWithoutExtension(parts[4]);
            var bits = stem.Split('_');
            uint id = Convert.ToUInt32(bits[^2], 16);
            total++;
            if (!idx.Infos.TryGetValue(new FormKey(plugin, id), out var info)) { _out.WriteLine("no info " + e.Path); continue; }
            found++;
            if (string.Equals(idx.FileName(info, int.Parse(bits[^1])), stem, StringComparison.OrdinalIgnoreCase)) named++;
            else _out.WriteLine($"name {stem} vs {idx.FileName(info, int.Parse(bits[^1]))}");
            if (idx.Topics.TryGetValue(info.Topic, out var t) && t.Quest is { } q && q.Plugin != plugin) inOtherQuest++;
        }
        _out.WriteLine($"files {total}, line found in that plugin {found}, names match {named}, line's quest in another plugin {inOtherQuest}");
        Assert.Equal(total, found);
        Assert.Equal(total, named);
        Assert.True(inOtherQuest > 0);
    }

    [Fact]
    public void Vyrthur_revoice_files_resolve_to_his_voice_type()
    {
        if (RealDataTests.F("Skyrim.esm") is not { } sky || RealDataTests.F("Dawnguard.esm") is not { } dg) return;
        var idx = Load(sky, RealDataTests.F("Update.esm")!, dg);
        string[] stems = { "DLC1_BF_Du_DLC1_BF_DunTemp_00006BC3_1", "DLC1_BF_DunTempleQST__00006BC1_1", "DLC1VQ07__0000D673_2", "DLC1VQ07_DLC1VQ07AlertIdle_00016329_1", "DLC1VQ07_DLC1VQ07Taunt_00016345_1" };
        foreach (var stem in stems)
        {
            var bits = stem.Split('_');
            var info = idx.Infos[new FormKey("dawnguard.esm", Convert.ToUInt32(bits[^2], 16))];
            Assert.Equal(stem, idx.FileName(info, int.Parse(bits[^1])), ignoreCase: true);
            var sp = idx.Speakers(info);
            _out.WriteLine($"{stem}: {string.Join(",", sp.VoiceTypes.Select(idx.VoiceTypeFolder))} via {sp.Via} npcs {string.Join(",", sp.Npcs.Select(idx.Describe).Take(3))}");
            Assert.Contains(sp.VoiceTypes, v => idx.VoiceTypeFolder(v).Equals("DLC1MaleUniqueVyrthur", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void Aela_new_voice_type_plan_suggests_lists_and_coverage()
    {
        if (RealDataTests.F("Skyrim.esm") is not { } sky || RealDataTests.F("HearthFires.esm") is not { } hf || RealDataTests.F("Dawnguard.esm") is not { } dg) return;
        var idx = Load(sky, RealDataTests.F("Update.esm")!, dg, hf);
        var aela = new FormKey("skyrim.esm", 0x01A696);
        var npc = idx.Npcs[aela];
        var baseVt = idx.VoiceTypesOf(aela).Single();
        _out.WriteLine($"{npc.Display} uses {idx.VoiceTypeFolder(baseVt)}; shared by {idx.NpcsWithVoiceType(baseVt).Count} NPC records");
        _out.WriteLine("1A697 kind: " + idx.Kinds.GetValueOrDefault(new FormKey("skyrim.esm", 0x01A697), "?") + " base " + (idx.PlacedActors.TryGetValue(new FormKey("skyrim.esm", 0x01A697), out var b) ? idx.Describe(b) + " " + b : "-"));
        if (idx.Npcs.TryGetValue(new FormKey("skyrim.esm", 0x01A697), out var a2)) _out.WriteLine($"1A697: {a2.EditorId} '{a2.Name}' vt={a2.VoiceType} tpl={a2.Template} traits={a2.UseTraits}");
        var copies = RevoiceService.NpcCopies(idx, aela);
        _out.WriteLine("copies: " + string.Join("; ", copies.Select(c => $"{c.Plugin}|{c.Key} {c.EditorId} {c.VoiceType}")));
        var plan = new NewVoiceTypePlan { EditorId = "_AelaTest", BaseVoiceType = baseVt.ToString(), Npcs = copies.Select(c => c.Key).ToList() };
        var lists = RevoiceService.SuggestLists(idx, plan);
        foreach (var l in lists.Take(25)) _out.WriteLine($"  list {l.EditorId} ({l.Plugin}) direct={l.ContainsBase} gated={l.GatedLines}");
        var none = RevoiceService.Coverage(idx, plan, "Test.esp");
        plan.Lists = lists.Where(l => l.ContainsBase).Select(l => l.Key).ToList();
        var with = RevoiceService.Coverage(idx, plan, "Test.esp");
        _out.WriteLine($"can say {none.CanSay}; covered without lists {none.Covered}; with {plan.Lists.Count} lists {with.Covered}");
        foreach (var g in with.Gaps.Take(10)) _out.WriteLine($"  gap {g.Plugin}: {g.Reason} x{g.Lines} e.g. {string.Join(" | ", g.Examples)}");
        Assert.True(with.Covered > none.Covered);
        // Aela Revoiced's own FLM file adds these vanilla lists; the suggestions should include them.
        foreach (var name in new[] { "VoicesFollowerAll", "VoicesMarriageAll", "DefaultNPCVoiceTypes", "VoicesCommonCombatant" })
            Assert.Contains(lists, l => l.EditorId == name);
    }

    [Fact]
    public void Vigilant_scan_finds_the_english_files_as_originals()
    {
        var vig = Path.Combine(Up, "VIGILANT Voiced - English Addon", "Vigilant.esm");
        if (RealDataTests.F("Skyrim.esm") is not { } sky || !File.Exists(vig)) return;
        // Build the folder layout the index expects from the listing (empty files are enough to match).
        var list = Path.Combine(Up, "vigilant-english-files.json");
        var root = Path.Combine(Path.GetTempPath(), "dbvo-vig-" + Guid.NewGuid().ToString("N")[..6]);
        foreach (var e in JsonDocument.Parse(File.ReadAllText(list)).RootElement.GetProperty("entries").EnumerateArray())
        {
            var name = e.GetProperty("name").GetString()!;
            if (!name.EndsWith(".fuz")) continue;
            var path = Path.Combine(root, name.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, Array.Empty<byte>());
        }
        try
        {
            var lib = new DbvoStudio.Core.Projects.Library(Path.Combine(root, "lib"));
            var svc = new RevoiceService(lib, lib.LoadSettings());
            var p = new RevoiceProject { Name = "Vigilant", UseBaseGame = false };
            p.Plugins.Add(new RevoicePlugin { Path = sky, FileName = "Skyrim.esm" });
            p.Plugins.Add(new RevoicePlugin { Path = RealDataTests.F("Update.esm")!, FileName = "Update.esm" });
            p.Plugins.Add(new RevoicePlugin { Path = vig, FileName = "Vigilant.esm", Target = true });
            p.Sources.Add(new RevoiceSource { Kind = "folder", Path = root });
            p.Sources.Add(new RevoiceSource { Kind = "bsa", Path = Path.Combine(RealDataTests.Data, "Skyrim - Interface.bsa"), Priority = -100 });
            // Pretend 40 files come from a compacted plugin: new FormIDs in their names.
            var moved = Directory.EnumerateFiles(root, "*.fuz", SearchOption.AllDirectories).Take(40).ToList();
            foreach (var f in moved)
            {
                var stem = Path.GetFileNameWithoutExtension(f);
                var bits = stem.Split('_');
                bits[^2] = (Convert.ToUInt32(bits[^2], 16) + 0x300000).ToString("X8");
                File.Move(f, Path.Combine(Path.GetDirectoryName(f)!, string.Join("_", bits) + ".fuz"));
            }
            var sw = System.Diagnostics.Stopwatch.StartNew();
            using var session = svc.Open(p);
            var rep = svc.Scan(p, session);
            _out.WriteLine($"{sw.Elapsed.TotalSeconds:F1}s: infos {rep.Infos} responses {rep.Responses} lines {rep.Lines} with original {rep.WithOriginal} unknown {rep.UnknownSpeaker} voice files {rep.VoiceFiles}");
            _out.WriteLine("speakers: " + string.Join(", ", p.Lines.GroupBy(l => l.SpeakerName).OrderByDescending(g => g.Count()).Take(15).Select(g => $"{g.Key} {g.Count()}")));
            Assert.True(rep.WithOriginal >= 1339 - 40);
            Assert.Equal(40, rep.Orphans);
            var orphans = RevoiceService.FindOrphans(p, session);
            var byName = RevoiceService.MatchByName(p, orphans);
            int found = byName.Count(m => m.LineId != null);
            _out.WriteLine($"orphans {orphans.Count}, matched by quest/topic name {found}");
            Assert.True(found >= 5);
            // A transcript that equals the subtitle matches the right line.
            var sample = orphans.Take(10).Select(o => (o, p.Lines.First(l => l.FileStem != null && l.VoiceType.Equals(o.VoiceType, StringComparison.OrdinalIgnoreCase) && l.Original == null && l.Response == o.Response && l.FileStem.Split('_')[^2] == (o.FormId - 0x300000).ToString("X8")).FinalLine)).ToList();
            var byText = RevoiceService.MatchByTranscript(p, sample);
            int right = byText.Zip(sample).Count(z => p.Lines.First(l => l.LineId == z.First.LineId).FinalLine == z.Second.Item2);
            _out.WriteLine($"transcript matches right {right}/10");
            Assert.True(right >= 9);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }
}
