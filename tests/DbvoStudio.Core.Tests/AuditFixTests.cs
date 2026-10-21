using DbvoStudio.Core.Audio;
using DbvoStudio.Core.Fomod;
using DbvoStudio.Core.Naming;
using DbvoStudio.Core.Projects;

namespace DbvoStudio.Core.Tests;

/// <summary>Fixes from the October 2026 audit: data that must survive updates, syncs and deletions.</summary>
public class AuditFixTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dbvo-audit-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly Library _lib;
    private readonly AppSettings _settings;

    public AuditFixTests()
    {
        Directory.CreateDirectory(_dir);
        _lib = new Library(Path.Combine(_dir, "library"));
        _settings = _lib.LoadSettings();
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string WritePlugin(string name, Action<TestPluginBuilder> configure)
    {
        var b = new TestPluginBuilder();
        configure(b);
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, b.Build());
        return path;
    }

    [Fact]
    public void Update_that_adds_a_master_keeps_edits_and_decisions()
    {
        var p1 = new Phase1Service(_lib, _settings);
        var mod = new Mod { Name = "M" };
        _lib.Save(mod);
        p1.Import(mod, WritePlugin("M.esp", b =>
        {
            b.AddTopic(0x01000800, "T1", "Follow me.", (0x01000801, null));
            b.AddTopic(0x01000802, "T2", "Wait here.", (0x01000803, null));
        }));
        var pl = mod.Plugins.Single();
        Phase1Service.SetFinal(pl.Lines.Single(l => l.Original == "Follow me."), "Follow me, friend.");
        var laugh = pl.Lines.Single(l => l.Original == "Wait here.");
        laugh.VoiceApproved = false;
        laugh.FixApproved = true;
        _lib.Save(mod);

        // The update adds a second master: the plugin's own records now start with 02.
        var rep = p1.Import(mod, WritePlugin("M.esp", b =>
        {
            b.Masters.Add("Dawnguard.esm");
            b.AddTopic(0x02000800, "T1", "Follow me.", (0x02000801, null));
            b.AddTopic(0x02000802, "T2", "Wait here.", (0x02000803, null));
        }));

        Assert.Equal(0, rep.Removed);
        Assert.Equal(0, rep.Added);
        pl = mod.Plugins.Single();
        var follow = pl.Lines.Single(l => l.Original == "Follow me.");
        Assert.Equal("Follow me, friend.", follow.Final);
        Assert.Equal(0x02000800u, follow.FormId);
        laugh = pl.Lines.Single(l => l.Original == "Wait here.");
        Assert.False(laugh.VoiceApproved);
        Assert.True(laugh.FixApproved);
        Assert.DoesNotContain(pl.Lines, l => l.Status == Phase1Status.Removed);
    }

    [Fact]
    public void Stable_key_and_remap_follow_the_owner()
    {
        var a = new[] { "Skyrim.esm" };
        var b = new[] { "Skyrim.esm", "Dawnguard.esm" };
        Assert.Equal(Phase1Service.StableKey(0x01000800, Plugins.LineField.TopicText, a, "M.esp"),
                     Phase1Service.StableKey(0x02000800, Plugins.LineField.TopicText, b, "M.esp"));
        Assert.Equal(Phase1Service.StableKey(0x00012345, Plugins.LineField.Prompt, a, "M.esp"),
                     Phase1Service.StableKey(0x00012345, Plugins.LineField.Prompt, b, "M.esp"));
        Assert.Equal(0x02000800u, Phase1Service.Remap(0x01000800, a, b, "M.esp"));
        Assert.Equal(0x00000001u, Phase1Service.Remap(0x00000001, a, b, "M.esp"));
        Assert.Equal(0x01000005u, Phase1Service.Remap(0x01000005, b, a, "M.esp")); // Dawnguard is gone: unchanged
    }

    private (Mod Mod, DbvoPatch Patch, VoicePack Voice, Phase2Service Svc) OneLinePatch(string wavLip = "")
    {
        var mod = new Mod { Name = "Serana" };
        mod.Plugins.Add(new ModPlugin
        {
            FileName = "Serana.esp",
            Lines = { new Phase1Line { FormId = 1, Original = "Follow me.", Final = "Follow me.", Kind = Rules.LineKind.Valid } },
        });
        _lib.Save(mod);
        var voice = new VoicePack { Name = "Vampira", PackId = "vampira" };
        var patch = new DbvoPatch { Name = "Serana", VoicePackId = voice.Id, ModIds = { mod.Id }, WavLipFolder = wavLip };
        return (mod, patch, voice, new Phase2Service(_lib, _settings));
    }

    [Fact]
    public void Takes_survive_a_line_leaving_phase1_and_coming_back()
    {
        var (mod, patch, voice, svc) = OneLinePatch();
        svc.Sync(patch, voice, new[] { mod });
        var line = patch.Lines.Single();
        line.Takes.Add(new Take { Slot = 1, SpokenText = line.Spoken, WavPath = "takes/x/01/a.wav", LipPath = "takes/x/01/a.lip" });
        line.SelectedTakeId = line.Takes[0].Id;
        line.Status = Phase2Status.Generated;

        mod.Plugins[0].Lines[0].VoiceApproved = false; // ♪ off in Phase 1
        mod.Plugins[0].Lines[0].Final = "(Laughs)";
        var rep = svc.Sync(patch, voice, new[] { mod });
        Assert.Equal(1, rep.Parked);
        Assert.DoesNotContain(patch.Lines, l => l.FinalLine == "Follow me.");
        Assert.Single(patch.Parked);

        mod.Plugins[0].Lines[0].Final = "Follow me.";
        mod.Plugins[0].Lines[0].VoiceApproved = null;
        svc.Sync(patch, voice, new[] { mod });
        var back = patch.Lines.Single(l => l.FinalLine == "Follow me.");
        Assert.Single(back.Takes);
        Assert.Equal(Phase2Status.Generated, back.Status);
        Assert.Empty(patch.Parked);
    }

    [Fact]
    public void Deleted_takes_are_not_adopted_again()
    {
        var wavLip = Path.Combine(_dir, "wavlip");
        Directory.CreateDirectory(wavLip);
        new WavFile { SampleRate = 44100, Channels = 1, Samples = new short[4410] }.Write(Path.Combine(wavLip, LineNaming.Stem("Follow me.", NamingMode.Legacy) + ".wav"));
        File.WriteAllBytes(Path.Combine(wavLip, LineNaming.Stem("Follow me.", NamingMode.Legacy) + ".lip"), new byte[] { 1 });
        var (mod, patch, voice, svc) = OneLinePatch(wavLip);
        Assert.Equal(1, svc.Sync(patch, voice, new[] { mod }).Adopted);
        var line = patch.Lines.Single();
        line.Takes.Clear(); line.SelectedTakeId = null; line.Status = Phase2Status.Pending; line.NoAdopt = true; // what deleteTake does
        Assert.Equal(0, svc.Sync(patch, voice, new[] { mod }).Adopted);
        Assert.Empty(line.Takes);
    }

    [Fact]
    public void Refresh_stale_follows_the_selected_take()
    {
        var l = new Phase2Line { Spoken = "B", Status = Phase2Status.Generated };
        var a = new Take { SpokenText = "A" };
        var b = new Take { SpokenText = "B" };
        l.Takes.AddRange(new[] { a, b });
        l.SelectedTakeId = a.Id;
        l.RefreshStale();
        Assert.Equal(Phase2Status.Stale, l.Status);
        l.SelectedTakeId = b.Id;
        l.RefreshStale();
        Assert.Equal(Phase2Status.Generated, l.Status);
    }

    [Fact]
    public void Routine_backups_are_throttled_and_rotated_on_their_own()
    {
        _lib.Save(new Mod { Name = "X" });
        Assert.NotNull(_lib.Backup("before-import", 2));
        Assert.NotNull(_lib.Backup("before-generate", 2, routine: true));
        Assert.Null(_lib.Backup("before-generate", 2, routine: true)); // within 10 minutes
        for (int i = 0; i < 4; i++) Assert.NotNull(_lib.Backup("gen", 2, routine: true, routineMinutes: 0));
        var files = Directory.GetFiles(Path.Combine(_lib.Root, "backups"), "*.zip").Select(Path.GetFileName).ToList();
        Assert.Contains(files, f => f!.Contains("before-import"));
        Assert.Equal(2, files.Count(f => f!.Contains("-auto-")));
    }

    [Fact]
    public void Relink_keeps_user_files_and_replaces_custom_prefix_fix_plugins()
    {
        var mod = new Mod { Name = "M", Plugins = { new ModPlugin { FileName = "M.esp", FixPrefix = "Vampira Fix - " } } };
        var o = new FomodOption
        {
            PatchId = "p",
            Files =
            {
                new FomodFile { Kind = "folder", Source = "old\\Sound", Destination = "Sound" },
                new FomodFile { Kind = "file", Source = "old\\Vampira Fix - M.esp", Destination = "Vampira Fix - M.esp" },
                new FomodFile { Kind = "file", Source = "extras\\readme.txt", Destination = "readme.txt" },
            },
        };
        FomodService.Relink(o, new List<FomodFile> { new() { Kind = "folder", Source = "new\\Sound", Destination = "Sound", Linked = true } }, mod);
        Assert.Equal(new[] { "new\\Sound", "extras\\readme.txt" }, o.Files.Select(f => f.Source));
    }

    [Fact]
    public void Undone_edits_remove_the_old_fix_plugin()
    {
        var p1 = new Phase1Service(_lib, _settings);
        var mod = new Mod { Name = "M" };
        _lib.Save(mod);
        p1.Import(mod, WritePlugin("M.esp", b => b.AddTopic(0x01000800, "T1", "Follow me.", (0x01000801, null))));
        var pl = mod.Plugins.Single();
        var line = pl.Lines.Single();
        Phase1Service.SetFinal(line, "Follow me, friend.");
        var outDir = Path.Combine(_dir, "fix");
        p1.WriteOutputs(mod, pl, new[] { outDir }, outDir, removeStaleFix: true);
        Assert.True(File.Exists(Path.Combine(outDir, pl.FixFileName)));
        Phase1Service.SetFinal(line, "Follow me.");
        var written = p1.WriteOutputs(mod, pl, new[] { outDir }, outDir, removeStaleFix: true);
        Assert.False(File.Exists(Path.Combine(outDir, pl.FixFileName)));
        Assert.Contains(written, w => w.StartsWith("Removed"));
    }
}
