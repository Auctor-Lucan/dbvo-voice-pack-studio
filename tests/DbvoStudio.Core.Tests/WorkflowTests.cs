using DbvoStudio.Core.Naming;
using DbvoStudio.Core.Plugins;
using DbvoStudio.Core.Projects;
using DbvoStudio.Core.Rules;
using DbvoStudio.Core.Topics;

namespace DbvoStudio.Core.Tests;

/// <summary>Phase 1 to Phase 2 end to end: import, edit, update the mod, sync, omit.</summary>
public class WorkflowTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dbvo-flow-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly Library _lib;
    private readonly AppSettings _settings;

    public WorkflowTests()
    {
        Directory.CreateDirectory(_dir);
        _lib = new Library(Path.Combine(_dir, "library"));
        _settings = _lib.LoadSettings();
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string WritePlugin(string name, Action<TestPluginBuilder> configure)
    {
        var b = new TestPluginBuilder();
        configure(b);
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, b.Build());
        return path;
    }

    private static void Version1(TestPluginBuilder b)
    {
        b.AddTopic(0x01000800, "T1", "What is your homeland like? ", (0x01000801, null));
        b.AddTopic(0x01000802, "T2", "Can you build a portal?", (0x01000803, null));
        b.AddTopic(0x01000804, "T3", "<Alias=Friend> sent me, by the way.", (0x01000805, null));
        b.AddTopic(0x01000806, "T4", "Follow me.", (0x01000807, null));
    }

    [Fact]
    public void Import_edit_update_and_sync()
    {
        var p1 = new Phase1Service(_lib, _settings);
        var mod = new Mod { Name = "Test Mod" };
        _lib.Save(mod);

        var path = WritePlugin("Test.esp", Version1);
        var rep = p1.Import(mod, path);
        var plugin = Assert.Single(mod.Plugins);
        Assert.Equal(4, rep.Added);
        Assert.Contains(rep.MissingMasters, m => m.FileName == "Skyrim.esm");

        // The alias line is BROKEN; resolve it with written-out variants.
        var broken = plugin.Lines.Single(l => l.FormId == 0x01000804);
        Assert.Equal(LineKind.Broken, broken.Kind);
        Assert.Null(broken.Final);
        Assert.Single(Phase1Service.UnreviewedFlags(plugin));
        broken.Variants = new() { "Alvor sent me, by the way." };
        broken.Reviewed = true;

        // The user rewords a valid line.
        var follow = plugin.Lines.Single(l => l.FormId == 0x01000806);
        Phase1Service.SetFinal(follow, "Follow me, friend.");
        _lib.Save(mod);

        Assert.Equal(new[] { "What is your homeland like?", "Can you build a portal?", "Alvor sent me, by the way.", "Follow me, friend." }, Phase1Service.TopicLines(plugin));

        // Only real changes go to the fix plugin; the unresolved alias line gets no override.
        var edits = Phase1Service.FixEdits(plugin);
        Assert.Equal(new uint[] { 0x01000800, 0x01000806 }, edits.Select(e => e.FormId).OrderBy(x => x));

        var outDir = Path.Combine(_dir, "out");
        var written = p1.WriteOutputs(mod, plugin, new[] { outDir }, outDir);
        Assert.Contains(written, f => f.EndsWith("DBVO Fix - Test.esp"));
        Assert.Contains(written, f => f.EndsWith("TopicListTest.txt"));
        var fix = PluginFile.Read(Path.Combine(outDir, "DBVO Fix - Test.esp"));
        Assert.Equal("Follow me, friend.", fix.DecodeLString(fix.Records.Single(r => r.FormId == 0x01000806).Get("FULL")!.Data));
        Assert.Equal(4, TopicFile.Read(Path.Combine(outDir, "TopicListTest.txt")).Count);

        // Mod update: one line changed upstream, one removed, one added. Edits on unchanged lines survive.
        var path2 = WritePlugin("Test.esp", b =>
        {
            b.AddTopic(0x01000800, "T1", "What is your homeland like? ", (0x01000801, null));
            b.AddTopic(0x01000802, "T2", "Can you build me a portal?", (0x01000803, null));
            b.AddTopic(0x01000806, "T4", "Follow me.", (0x01000807, null));
            b.AddTopic(0x01000808, "T5", "Where to next?", (0x01000809, null));
        });
        var rep2 = p1.Import(mod, path2);
        Assert.Equal(1, rep2.Changed);
        Assert.Equal(1, rep2.Removed);
        Assert.Equal(1, rep2.Added);
        plugin = mod.Plugins.Single();
        Assert.Equal("Follow me, friend.", plugin.Lines.Single(l => l.FormId == 0x01000806).Final);
        var changed = plugin.Lines.Single(l => l.FormId == 0x01000802);
        Assert.Equal(Phase1Status.ChangedInMod, changed.Status);
        Assert.Equal("Can you build a portal?", changed.PreviousFinal);
        Assert.Equal(Phase1Status.Removed, plugin.Lines.Single(l => l.FormId == 0x01000804).Status);

        // Phase 2: a reference topic file omits a line every user already has.
        var refFile = Path.Combine(_dir, "base.txt");
        TopicFile.Write(refFile, new[] { "Where to next?" });
        var reference = _lib.AddReference(_settings, "Skyrim Base Game + DLC", refFile);
        var voice = new VoicePack { Name = "Vampira", PackId = "vampira", Naming = NamingMode.Legacy };
        _lib.Save(voice);
        var patch = new DbvoPatch { Name = "Vampira – Test", VoicePackId = voice.Id, ModIds = { mod.Id }, ReferenceIds = { reference.Id } };
        var p2 = new Phase2Service(_lib, _settings);
        var sync = p2.Sync(patch, voice, new[] { mod });

        Assert.Equal(4, sync.Total);
        Assert.Equal(1, sync.Omitted);
        var omitted = patch.Lines.Single(l => l.FinalLine == "Where to next?");
        Assert.True(omitted.IsOmitted);
        Assert.Equal("Skyrim Base Game + DLC", omitted.OmittedBy);
        Assert.DoesNotContain(omitted, Phase2Service.Eligible(patch, false));

        var f = patch.Lines.Single(l => l.FinalLine == "Follow me, friend.");
        Assert.Equal("Follow me.", f.OriginalLine);
        Assert.True(f.EditedInPhase1);
        Assert.Equal("Follow_me,_friend.", LineNaming.Stem(f.FinalLine, voice.Naming));
        Assert.Equal(NewId.FromKey(f.Key), f.LineId);

        // Spoken edits survive a re-sync.
        f.Spoken = "[warm] Follow me, friend.";
        p2.Sync(patch, voice, new[] { mod });
        Assert.Equal("[warm] Follow me, friend.", patch.Lines.Single(l => l.FinalLine == "Follow me, friend.").Spoken);

        // Saved and loaded back intact.
        var loaded = _lib.LoadPatch(patch.Id)!;
        Assert.Equal(4, loaded.Lines.Count);
        Assert.Equal("[warm] Follow me, friend.", loaded.Lines.Single(l => l.FinalLine == "Follow me, friend.").Spoken);
        Assert.NotNull(_lib.LoadMod(mod.Id));
    }

    [Fact]
    public void Reference_matching_follows_naming_mode()
    {
        var keysLegacy = new[] { "Follow Me." }.Select(l => LineNaming.Key(l, NamingMode.Legacy)).ToHashSet();
        Assert.Contains(LineNaming.Key("Follow me.", NamingMode.Legacy), keysLegacy);
        var keysHash = new[] { "Follow Me." }.Select(l => LineNaming.Key(l, NamingMode.Dbvo2Hash)).ToHashSet();
        Assert.DoesNotContain(LineNaming.Key("Follow me.", NamingMode.Dbvo2Hash), keysHash);
    }

    [Fact]
    public void Path_check_warns_for_long_legacy_names()
    {
        var longLine = new string('x', 140) + ".";
        var deep = Path.Combine(_dir, new string('d', 80), new string('e', 40));
        var w = PathChecks.Check(new[] { longLine }, NamingMode.Legacy, deep, Array.Empty<string>(), Path.Combine(_dir, "takes"));
        Assert.Contains(w, x => x.Where == "WAV + LIP folder");
        Assert.Empty(PathChecks.Check(new[] { longLine }, NamingMode.Dbvo2Hash, deep, Array.Empty<string>(), _dir));
    }

    [Fact]
    public void Pronunciations_apply_whole_words()
    {
        var entries = new List<Pronunciation>
        {
            new() { Word = "Alduin", Replacement = "Aldooin", WholeWord = true },
            new() { Word = "Jarl", Kind = "phoneme", Replacement = "jɑːrl" },
        };
        Assert.Equal("Aldooin is coming. Alduins?", Phase2Service.ApplyPronunciations("Alduin is coming. Alduins?", entries.Take(1), "eleven_v4"));
        Assert.Equal("The /jɑːrl/ waits.", Phase2Service.ApplyPronunciations("The Jarl waits.", entries.Skip(1), "eleven_v4"));
        Assert.Equal("The Jarl waits.", Phase2Service.ApplyPronunciations("The Jarl waits.", entries.Skip(1), "eleven_multilingual_v2"));
    }
}
