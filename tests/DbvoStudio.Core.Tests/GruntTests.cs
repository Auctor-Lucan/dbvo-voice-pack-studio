using DbvoStudio.Core.Audio;
using DbvoStudio.Core.Grunts;
using DbvoStudio.Core.Tools;
using Xunit;

namespace DbvoStudio.Core.Tests;

/// <summary>Player Grunts Tool: catalogs, custom events, pack import and the PCHT / SPVS exports (needs ffmpeg on PATH).</summary>
public class GruntTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "grunt-tests-" + Guid.NewGuid().ToString("N")[..8]);
    public GruntTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static string? Ffmpeg()
    {
        foreach (var d in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            var f = Path.Combine(d, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg");
            if (File.Exists(f)) return f;
        }
        return null;
    }

    private string Tone(string name, double seconds, double freq, short amp = 8000, int rate = 48000, int ch = 2)
    {
        int n = (int)(seconds * rate);
        var s = new short[n * ch];
        for (int i = 0; i < n; i++)
            for (int c = 0; c < ch; c++) s[i * ch + c] = (short)(amp * Math.Sin(2 * Math.PI * freq * i / rate));
        var path = Path.Combine(_dir, "src", name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        new WavFile { SampleRate = rate, Channels = ch, Samples = s }.Write(path);
        return path;
    }

    private static string? PchtRefs() => Environment.GetEnvironmentVariable("DBVO_GRUNT_REFS") is { Length: > 0 } d && Directory.Exists(Path.Combine(d, "PC Head Tracking and Voice Type SE")) ? d : null;

    [Fact]
    public void Built_in_catalogs_are_shipped()
    {
        var p = GruntCatalogs.BuiltIn(GruntTargets.Pcht);
        var s = GruntCatalogs.BuiltIn(GruntTargets.Spvs);
        Assert.Equal(72, p.Events.Count(e => e.Kind == "pool"));
        Assert.Equal(28, p.Events.Count(e => e.Kind == "slots"));
        Assert.True(s.Events.Count(e => e.Kind == "pool") > 90);
        Assert.Contains(s.Events, e => e.Key == "VanillaAttack" && e.VanillaNames.Count == 6);
        // Every SPVS event's PCHT folders exist in the PCHT catalog (cross import works).
        var folders = p.Events.Select(e => e.Folder).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.All(s.Events.SelectMany(e => e.PchtFolders), f => Assert.Contains(f, folders));
        // Shout parts are linked between the two mods.
        var links = p.Events.SelectMany(e => e.Slots).Select(x => x.Link).ToHashSet();
        Assert.Contains(s.Events.SelectMany(e => e.Slots), x => links.Contains(x.Link));
    }

    [Fact]
    public void Voice_file_names_follow_the_game_rule()
    {
        Assert.Equal("Combat_____Attack__00000811_1", GruntCatalogs.VoiceFileName("Combat______StandalonePlayerVoice", "Attack_", 0x05000811, 1, true));
        Assert.Equal("Breath_____EnterBowZoomBre_00000AC1_1", GruntCatalogs.VoiceFileName("Breath______StandalonePlayerVoice", "EnterBowZoomBreath_", 0x05000AC1, 1, true));
        Assert.Equal("_VP_01a_0000A181_1", GruntCatalogs.VoiceFileName("_VP", "01a", 0x0100A181, 1, false));
    }

    [Fact]
    public void Custom_events_are_validated_and_imported_from_an_audio_util_toml()
    {
        var cat = GruntCatalogs.BuiltIn(GruntTargets.Spvs);
        var bad = new GruntEventDef { Target = GruntTargets.Spvs, Label = "Heal again", Key = "MagicHeal", Folder = @"Magic\Heal2" };
        Assert.Contains("already has", GruntCatalogs.Validate(bad, cat));
        var clash = new GruntEventDef { Target = GruntTargets.Spvs, Label = "X", Key = "SwimStart", Folder = @"Magic\Heal" };
        Assert.Contains("belongs to the built-in", GruntCatalogs.Validate(clash, cat));
        var ok = new GruntEventDef { Target = GruntTargets.Spvs, Label = "Swim start", Key = "SwimStart", Folder = "Swim/Start" };
        Assert.Null(GruntCatalogs.Validate(ok, cat));
        Assert.Equal(@"Swim\Start", ok.Folder);
        Assert.Equal("spvs:SwimStart", ok.Id);

        var toml = "[[slot]]\nid = 'CustomVoiceType_FemaleCustom01'\n[slot.categories]\nBleedout = 'Sound\\SPVS_AdditionalVoice\\CustomVoiceType\\FemaleCustom01\\Bleedout'\nSwimStart = 'Sound\\SPVS_AdditionalVoice\\CustomVoiceType\\FemaleCustom01\\Swim\\Start'\n";
        var (events, problems) = GruntCatalogs.ParseImport("SPVS_FemaleCustom01.toml", toml, GruntTargets.Spvs, cat);
        Assert.Single(events);
        Assert.Equal("SwimStart", events[0].Key);
        Assert.Equal(@"Swim\Start", events[0].Folder);
        Assert.Contains(problems, x => x.Contains("Bleedout"));
        var (none, why) = GruntCatalogs.ParseImport("x.json", "{ not json", GruntTargets.Spvs, cat);
        Assert.Empty(none);
        Assert.Contains(why, x => x.Contains("isn't valid JSON"));

        // Custom events join the catalog; one the installed SPVS now defines itself yields to it.
        var notes = new List<string>();
        var merged = GruntCatalogs.Resolve(GruntTargets.Spvs, null, new[] { ok, new GruntEventDef { Id = "spvs:x", Target = GruntTargets.Spvs, Label = "Old", Key = "Bleedout", Folder = "Old" } }, notes);
        Assert.Contains(merged.Events, e => e.Id == "spvs:SwimStart" && e.Origin == "custom");
        Assert.DoesNotContain(merged.Events, e => e.Id == "spvs:x");
        Assert.Single(notes);
    }

    [Fact]
    public async Task Pcht_export_writes_numbered_files_shouts_and_a_checked_plugin()
    {
        var ff = Ffmpeg();
        if (ff == null) return;
        var tools = new ExternalTools { FfmpegPath = ff, FaceFxWrapperPath = "" };
        var cat = GruntCatalogs.BuiltIn(GruntTargets.Pcht);
        var proj = new GruntProject { Name = "Test pack", Target = GruntTargets.Pcht, OutputFolder = Path.Combine(_dir, "out") };
        var attack = cat.Events.First(e => e.Folder == @"Combat\Attack");
        var hello = cat.Events.First(e => e.Folder == @"Greeting\Hello");
        var fus = cat.Events.First(e => e.Label == "Unrelenting Force");
        GruntService.AddFiles(proj, attack, Enumerable.Range(0, 7).Select(i => Tone($"a{i}.wav", 0.4 + i * 0.05, 200 + i * 30)));
        GruntService.AddFiles(proj, hello, Enumerable.Range(0, 10).Select(i => Tone($"h{i}.wav", 0.6, 300)));
        var shout = GruntService.AddFiles(proj, fus, new[] { Tone("fus.wav", 0.5, 150) });
        Assert.Equal("01a", shout.Single().Slot);
        Assert.Equal("Fus!", shout.Single().Text);

        var refs = PchtRefs();
        var src = refs == null ? null : PchtPlugins.ReadSource(Path.Combine(refs, "PC Head Tracking and Voice Type SE", GruntCatalogs.PchtPlugin));
        var plan = GruntService.Plan(proj, cat, _dir, src, new GruntService.AppTools(true, false, false));
        if (src == null)
        {
            Assert.Contains(plan.Issues, i => i.Severity == "error" && i.Text.Contains("PC Head Tracking folder"));
            return;
        }
        Assert.False(plan.HasErrors, string.Join("\n", plan.Issues.Where(i => i.Severity == "error").Select(i => i.Text)));
        Assert.Contains(plan.Files, f => f.Rel == @"Sound\FX\PC Head Tracking\Combat\Attack\07.wav");
        Assert.Contains(plan.Files, f => f.Rel == @"Sound\Voice\PC Head Tracking - MCM.esp\_FemaleBYO\_VP_01a_0000A181_1.wav");
        var pl = Assert.Single(plan.Plugins);
        Assert.Equal(1, pl.Overrides); // Greeting Hello has PCHT's own 10, so only Attack changes
        var r = await GruntService.ExportAsync(proj, cat, plan, _dir, tools, null, CancellationToken.None);
        Assert.Equal(plan.Files.Count, r.Written);
        var w = WavFile.Read(Path.Combine(proj.OutputFolder, "Sound", "FX", "PC Head Tracking", "Combat", "Attack", "01.wav"));
        Assert.Equal(1, w.Channels);
        Assert.Equal(44100, w.SampleRate);
        Assert.True(File.Exists(Path.Combine(proj.OutputFolder, "PCHT Lines - Test pack.esp")));
        Assert.DoesNotContain(Directory.EnumerateDirectories(_dir), d => Path.GetFileName(d).Contains("dbvo-export"));

        // A second export with fewer lines removes this project's own old files and keeps everything else.
        File.WriteAllText(Path.Combine(proj.OutputFolder, "readme.txt"), "mine");
        GruntService.State(proj, attack.Id).Lines.RemoveRange(5, 2);
        var plan2 = GruntService.Plan(proj, cat, _dir, src, new GruntService.AppTools(true, false, false));
        Assert.Empty(plan2.WouldOverwrite);
        Assert.Empty(plan2.Plugins); // 5 lines = PCHT's own count and 10 greetings: nothing to override
        var r2 = await GruntService.ExportAsync(proj, cat, plan2, _dir, tools, null, CancellationToken.None);
        Assert.False(File.Exists(Path.Combine(proj.OutputFolder, "Sound", "FX", "PC Head Tracking", "Combat", "Attack", "07.wav")));
        Assert.False(File.Exists(Path.Combine(proj.OutputFolder, "PCHT Lines - Test pack.esp")));
        Assert.True(File.Exists(Path.Combine(proj.OutputFolder, "readme.txt")));
        Assert.True(r2.Removed >= 3);
    }

    [Fact]
    public async Task Mpcht_export_writes_the_voice_set_ini_and_prefixed_folders()
    {
        var ff = Ffmpeg();
        var refs = Environment.GetEnvironmentVariable("DBVO_GRUNT_REFS");
        if (ff == null || string.IsNullOrEmpty(refs) || !Directory.Exists(Path.Combine(refs, "Multi Voice Packs support for PC Head Tracking"))) return;
        var tools = new ExternalTools { FfmpegPath = ff, FaceFxWrapperPath = "" };
        var cat = GruntCatalogs.BuiltIn(GruntTargets.Pcht);
        var proj = new GruntProject { Name = "Vampira", Target = GruntTargets.Pcht, OutputFolder = Path.Combine(_dir, "out"), Gender = "female" };
        proj.Pcht.Multi = true; proj.Pcht.Prefix = "Vampira"; proj.Pcht.FullName = "Vampira";
        var hit = cat.Events.First(e => e.Folder == @"Combat\Hit");
        GruntService.AddFiles(proj, hit, Enumerable.Range(0, 3).Select(i => Tone($"x{i}.wav", 0.3, 220)));
        var soul = cat.Events.First(e => e.Label == "Soul Tear");
        GruntService.AddFiles(proj, soul, new[] { Tone("rii.wav", 0.3, 120) });
        var src = PchtPlugins.ReadSource(Path.Combine(refs, "Multi Voice Packs support for PC Head Tracking", GruntCatalogs.PchtPlugin));
        var plan = GruntService.Plan(proj, cat, _dir, src, new GruntService.AppTools(true, false, false));
        Assert.False(plan.HasErrors, string.Join("\n", plan.Issues.Where(i => i.Severity == "error").Select(i => i.Text)));
        Assert.Contains(plan.Files, f => f.Rel == @"Sound\FX\PC Head Tracking\Vampira\Combat\Hit\03.wav");
        Assert.Contains(plan.Files, f => f.Rel == @"Sound\Voice\PC Head Tracking - Patch.esp\Vampira_FemaleBYO\_VPDG_DG_01a_00014946_1.wav");
        Assert.Contains(plan.Files, f => f.Rel == @"Sound\Voice\PCHTV_Vampira.esp\Vampira_FemaleBYO\_VPDG_DG_01a_00014946_1.wav");
        var ini = plan.Files.Single(f => f.Kind == "ini");
        var text = System.Text.Encoding.ASCII.GetString(ini.Bytes!);
        Assert.Contains("FormList=BYOVoiceSets|Vampira_MAGA_VoiceEvents", text);
        Assert.Contains("FormList=_VoicePowerCustomPatch|Vampira_FemaleBYO", text);
        await GruntService.ExportAsync(proj, cat, plan, _dir, tools, null, CancellationToken.None);
        Assert.True(File.Exists(Path.Combine(proj.OutputFolder, "PCHTV_Vampira.esp")));
        Assert.True(File.Exists(Path.Combine(proj.OutputFolder, "PCHTV_Vampira_FLM.ini")));
    }

    [Fact]
    public async Task Spvs_export_writes_script_folders_subtitles_vanilla_names_and_shouts()
    {
        var ff = Ffmpeg();
        if (ff == null) return;
        var tools = new ExternalTools { FfmpegPath = ff, FaceFxWrapperPath = "" };
        var cat = GruntCatalogs.BuiltIn(GruntTargets.Spvs);
        var proj = new GruntProject { Name = "S", Target = GruntTargets.Spvs, OutputFolder = Path.Combine(_dir, "out") };
        proj.Spvs.Slot = "FemaleCustom02";
        var heal = cat.Events.First(e => e.Key == "MagicHeal");
        var lines = GruntService.AddFiles(proj, heal, new[] { Tone("heal1.wav", 0.8, 300), Tone("heal2.wav", 0.8, 330) });
        lines[0].Text = "That's \"better\".";
        var atk = cat.Events.First(e => e.Key == "VanillaAttack");
        GruntService.AddFiles(proj, atk, new[] { Tone("atk1.wav", 0.3, 200), Tone("atk2.wav", 0.3, 210) });
        var spec = cat.Events.First(e => e.Folder == @"Discovered\Specific\Voice");
        var wr = GruntService.AddFiles(proj, spec, new[] { Tone("wr.wav", 1, 250) });
        var plan = GruntService.Plan(proj, cat, _dir, null, new GruntService.AppTools(true, false, false));
        Assert.Contains(plan.Issues, i => i.Severity == "error" && i.Text.Contains("English name"));
        wr[0].FileName = "Whiterun";
        var fus = cat.Events.First(e => e.Label == "Unrelenting Force");
        GruntService.AddFiles(proj, fus, new[] { Tone("fus.wav", 0.5, 150) });
        plan = GruntService.Plan(proj, cat, _dir, null, new GruntService.AppTools(true, false, false));
        Assert.False(plan.HasErrors, string.Join("\n", plan.Issues.Where(i => i.Severity == "error").Select(i => i.Text)));
        var root = @"Sound\SPVS_AdditionalVoice\CustomVoiceType\FemaleCustom02";
        Assert.Contains(plan.Files, f => f.Rel == root + @"\Magic\Heal\magicheal_01.wav");
        Assert.Contains(plan.Files, f => f.Rel == root + @"\Discovered\Specific\Voice\Whiterun.wav");
        Assert.Equal(6, plan.Files.Count(f => f.Rel.StartsWith(@"Sound\Voice\SmartPlayerVoiceSystem.esp\SPVS_ReplaceFemaleCustom02\Combat_____Attack__")));
        Assert.Contains(plan.Files, f => f.Rel.StartsWith(@"Sound\Voice\SPVS_VanillaShout.esp\SPVS_ReplaceFemaleCustom02\Shout______01aStartShort___"));
        Assert.Contains(plan.Files, f => f.Rel.StartsWith(@"Sound\Voice\SmartPlayerVoiceSystem.esp\SPVS_ReplaceFemaleCustom02\Shout______01aStartShort__"));
        await GruntService.ExportAsync(proj, cat, plan, _dir, tools, null, CancellationToken.None);
        var toml = File.ReadAllText(Path.Combine(proj.OutputFolder, "Sound", "SPVS_AdditionalVoice", "CustomVoiceType", "FemaleCustom02", "Magic", "Heal", "magicheal_01.toml"));
        Assert.Equal("en = \"That's \\\"better\\\".\"\r\n", toml);
        Assert.False(File.Exists(Path.Combine(proj.OutputFolder, "Sound", "SPVS_AdditionalVoice", "CustomVoiceType", "FemaleCustom02", "Magic", "Heal", "magicheal_02.toml")));
    }

    [Fact]
    public async Task Loudness_is_brought_to_the_target_without_passing_the_peak_ceiling()
    {
        var ff = Ffmpeg();
        if (ff == null) return;
        var tools = new ExternalTools { FfmpegPath = ff, FaceFxWrapperPath = "" };
        var cat = GruntCatalogs.BuiltIn(GruntTargets.Spvs);
        var proj = new GruntProject { Name = "L", Target = GruntTargets.Spvs, OutputFolder = Path.Combine(_dir, "out") };
        proj.Spvs.VanillaBased = false;
        var book = cat.Events.First(e => e.Key == "Book");
        GruntService.AddFiles(proj, book, new[] { Tone("quiet.wav", 2, 440, 600), Tone("loud.wav", 2, 440, 20000) });
        var plan = GruntService.Plan(proj, cat, _dir, null, new GruntService.AppTools(true, false, false));
        await GruntService.ExportAsync(proj, cat, plan, _dir, tools, null, CancellationToken.None);
        var dir = Path.Combine(proj.OutputFolder, "Sound", "SPVS_AdditionalVoice", "CustomVoiceType", "FemaleCustom01", "Book");
        var (l1, p1) = await tools.MeasureAsync(Path.Combine(dir, "book_01.wav"));
        var (l2, p2) = await tools.MeasureAsync(Path.Combine(dir, "book_02.wav"));
        Assert.InRange(l1!.Value, -19, -17);
        Assert.InRange(l2!.Value, -19, -17);
        Assert.True(p1 <= -1.2 && p2 <= -1.2, $"{p1} {p2}");
    }

    [Fact]
    public void A_pcht_pack_folder_imports_into_pcht_and_spvs_projects()
    {
        var pack = Path.Combine(_dir, "Some PCHT pack");
        var atk = Path.Combine(pack, "Sound", "FX", "PC Head Tracking", "Combat", "Attack");
        Directory.CreateDirectory(atk);
        foreach (var n in new[] { "01.wav", "02.wav", "10.wav", "3.wav" }) File.Copy(Tone(n, 0.2, 200), Path.Combine(atk, n));
        var voice = Path.Combine(pack, "Sound", "Voice", "PC Head Tracking - MCM.esp", "_FemaleBYO");
        Directory.CreateDirectory(voice);
        File.Copy(Tone("s.wav", 0.2, 100), Path.Combine(voice, "_VP_01a_0000A181_1.wav"));

        var pcat = GruntCatalogs.BuiltIn(GruntTargets.Pcht);
        var pp = new GruntProject { Target = GruntTargets.Pcht };
        var (files, events, _) = GruntService.ImportPackFolder(pp, pcat, pack);
        Assert.Equal(5, files);
        Assert.Equal(2, events);
        var lines = pp.Events[pcat.Events.First(e => e.Folder == @"Combat\Attack").Id].Lines;
        Assert.Equal(new[] { "01.wav", "02.wav", "3.wav", "10.wav" }, lines.Select(l => Path.GetFileName(l.Source)));
        // Again: nothing new.
        Assert.Equal(0, GruntService.ImportPackFolder(pp, pcat, pack).Files);

        var scat = GruntCatalogs.BuiltIn(GruntTargets.Spvs);
        var sp = new GruntProject { Target = GruntTargets.Spvs };
        var (sf, _, _) = GruntService.ImportPackFolder(sp, scat, pack);
        Assert.Equal(5, sf);
        Assert.Equal(4, sp.Events["spvs:VanillaAttack"].Lines.Count);
        var fusSlot = sp.Events.Single(kv => kv.Key.StartsWith("spvs:shout:Skyrim.esm:013E07")).Value.Lines.Single();
        Assert.Equal("01a", fusSlot.Slot);

        // Project to project.
        var sp2 = new GruntProject { Target = GruntTargets.Spvs };
        Assert.Equal(5, GruntService.ImportProject(sp2, scat, pp, pcat, _dir, _dir));
    }

    [Fact]
    public void Missing_files_are_found_again_by_name()
    {
        var cat = GruntCatalogs.BuiltIn(GruntTargets.Pcht);
        var p = new GruntProject { Target = GruntTargets.Pcht };
        var ev = cat.Events.First(e => e.Kind == "pool");
        GruntService.AddFiles(p, ev, new[] { Path.Combine(_dir, "gone", "Combat", "x.wav") });
        var moved = Path.Combine(_dir, "moved", "Combat");
        Directory.CreateDirectory(moved);
        File.WriteAllBytes(Path.Combine(moved, "x.wav"), new byte[10]);
        Assert.Equal(1, GruntService.Relink(p, _dir, Path.Combine(_dir, "moved")));
        Assert.Equal(Path.Combine(moved, "x.wav"), p.Events[ev.Id].Lines[0].Source);
    }
}

public class SoundsSameTests
{
    [Xunit.Theory]
    [Xunit.InlineData("I'm not so easily molded.", "I'm not so easily moulded.", true)]
    [Xunit.InlineData("I'm not apologizing.", "I'm not apologising.", true)]
    [Xunit.InlineData("Traveling alone is dangerous...", "Travelling alone is dangerous...", true)]
    [Xunit.InlineData("Tell me about the Sepulcher.", "Tell me about the Sepulchre.", true)]
    [Xunit.InlineData("So you handle the defense?", "So you handle the defence?", true)]
    [Xunit.InlineData("Um... The one", "Um... the one", true)]
    [Xunit.InlineData("Can I use your forge?", "May I use your forge?", false)]
    [Xunit.InlineData("I found Freydis's sword.", "I found Freydis' sword.", false)]
    public void Spelling_only(string a, string b, bool same) => Xunit.Assert.Equal(same, DbvoStudio.Core.Projects.Phase2Service.SoundsSame(a, b));
}
