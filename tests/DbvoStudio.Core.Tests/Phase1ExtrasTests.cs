using DbvoStudio.Core.Plugins;
using DbvoStudio.Core.Projects;
using DbvoStudio.Core.Rules;
using DbvoStudio.Core.Topics;

namespace DbvoStudio.Core.Tests;

/// <summary>Topic-file mods, player-name lines, importing earlier DBVO Fix plugins, merged dictionaries.</summary>
public class Phase1ExtrasTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dbvo-x-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly Library _lib;
    private readonly AppSettings _settings;

    public Phase1ExtrasTests()
    {
        Directory.CreateDirectory(_dir);
        _lib = new Library(Path.Combine(_dir, "lib"));
        _settings = _lib.LoadSettings();
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void Player_name_lines_are_their_own_kind()
    {
        Assert.True(ScriptRules.HasPlayerAlias("My name is <alias=player>. Hi."));
        Assert.False(ScriptRules.HasOtherAlias("I'm <Alias=Player>."));
        Assert.True(ScriptRules.HasOtherAlias("<Alias=Player> and <Alias=Friend>"));

        var b = new TestPluginBuilder();
        b.AddTopic(0x01000800, "T1", "I'm <Alias=Player>; who are you?", (0x01000801, null));
        b.AddTopic(0x01000802, "T2", "<Alias=RiverwoodFriend> sent me.", (0x01000803, null));
        var path = Path.Combine(_dir, "P.esp");
        File.WriteAllBytes(path, b.Build());

        var mod = new Mod { Name = "P" };
        new Phase1Service(_lib, _settings).Import(mod, path);
        var p = mod.Plugins.Single();
        var player = p.Lines.Single(l => l.FormId == 0x01000800);
        Assert.True(player.IsPlayerName);
        Assert.False(player.IsFlagged);
        Assert.Equal("I'm <Alias=Player>, who are you?", player.Final);   // script formatting, alias kept
        Assert.True(player.NeedsOverride);                                  // goes into the DBVO Fix plugin
        Assert.Empty(Phase1Service.TopicLines(p));                          // never voiced in a normal patch
        Assert.Equal(new[] { "I'm <Alias=Player>, who are you?" }, Phase1Service.PlayerNameLines(p));

        var other = p.Lines.Single(l => l.FormId == 0x01000802);
        Assert.False(other.IsPlayerName);
        Assert.True(other.IsFlagged);
    }

    [Fact]
    public void Topic_files_are_checked_but_kept_as_written()
    {
        var txt = Path.Combine(_dir, "OldMod.txt");
        TopicFile.Write(txt, new[] { "Hello there.", "Wait; listen.", "I'm <Alias=Player>.", "Hand it over *now*.", "Hello there." });
        var mod = new Mod { Name = "Old mod" };
        var svc = new Phase1Service(_lib, _settings);
        svc.ImportTopicFile(mod, txt);
        var p = mod.Plugins.Single();
        Assert.True(p.IsTopicFile);
        Assert.False(p.NeedsFixPlugin);
        Assert.Equal(4, p.Lines.Count);                                    // duplicate dropped

        var semi = p.Lines.Single(l => l.Original == "Wait; listen.");
        Assert.Equal(LineKind.Fixed, semi.Kind);                           // issue shown
        Assert.Equal("Wait, listen.", semi.Suggested);
        Assert.Equal("Wait; listen.", semi.Final);                         // but kept as written
        Assert.Equal(LineKind.Broken, p.Lines.Single(l => l.Original.Contains('*')).Kind);
        Assert.Equal(new[] { "Hello there.", "Wait; listen." }, Phase1Service.TopicLines(p));
        Assert.Equal("OldMod.txt", Phase1Service.TopicFileName(p));

        // Update: one line added, one removed; the rest kept.
        TopicFile.Write(txt, new[] { "Hello there.", "Wait; listen.", "I'm <Alias=Player>.", "Goodbye." });
        var rep = svc.ImportTopicFile(mod, txt);
        Assert.Equal(1, rep.Added);
        Assert.Equal(1, rep.Removed);
        Assert.Equal(3, rep.Kept);
    }

    [Fact]
    public void Earlier_fix_plugin_texts_are_imported_across_master_lists()
    {
        var b = new TestPluginBuilder();
        b.Masters.Add("Update.esm");
        b.AddTopic(0x02000800, "T1", "What is your homeland like? ", (0x02000801, null));
        b.AddTopic(0x02000802, "T2", "No Serana. *laughs*", (0x02000803, null));
        b.AddTopic(0x02000804, "T3", "Too long " + new string('x', 150), (0x02000805, null));
        var srcPath = Path.Combine(_dir, "Src.esp");
        File.WriteAllBytes(srcPath, b.Build());

        var mod = new Mod { Name = "Src" };
        var svc = new Phase1Service(_lib, _settings);
        svc.Import(mod, srcPath);
        var p = mod.Plugins.Single();

        // An xEdit-made fix: master list is only Src.esp, so Src's own records are index 00.
        var fixBuilder = new TestPluginBuilder();
        fixBuilder.Masters.Clear();
        fixBuilder.Masters.Add("Src.esp");
        fixBuilder.AddTopic(0x00000800, "T1", "What is your homeland like?");
        fixBuilder.AddTopic(0x00000802, "T2", "No Serana. (laughs)");
        fixBuilder.AddTopic(0x00000804, "T3", "TOO LONG WILL CTD");
        var fixPath = Path.Combine(_dir, "DBVO Fix - Src.esp");
        File.WriteAllBytes(fixPath, fixBuilder.Build());

        var (applied, markers, unmatched) = svc.ImportFixPlugin(mod, p, fixPath);
        Assert.Equal(2, applied);
        Assert.Equal(1, markers);
        Assert.Equal(0, unmatched);
        Assert.Equal("No Serana. (laughs)", p.Lines.Single(l => l.FormId == 0x02000802).Final);
        Assert.Null(p.Lines.Single(l => l.FormId == 0x02000804).Final);
        Assert.Equal("DBVO Fix - Src.esp", p.ImportedFixPlugin);
    }

    [Fact]
    public void Voice_dictionary_wins_and_longer_words_first()
    {
        var merged = Phase2Service.MergePronunciations(
            new[] { new Pronunciation { Word = "Alduin", Replacement = "AL-doo-in" }, new Pronunciation { Word = "Jarl", Replacement = "Yarl" } },
            new[] { new Pronunciation { Word = "alduin", Replacement = "Aldooin" }, new Pronunciation { Word = "Alduin's", Replacement = "Aldooin's" } });
        Assert.Equal(3, merged.Count);
        Assert.Equal("Alduin's", merged[0].Word);
        Assert.Equal("Aldooin", merged.Single(p => p.Word.Equals("alduin", StringComparison.OrdinalIgnoreCase)).Replacement);
        Assert.Equal("Aldooin's fall. Aldooin rises. Yarl", Phase2Service.ApplyPronunciations("Alduin's fall. Alduin rises. Jarl", merged, "eleven_v4"));
    }
}
