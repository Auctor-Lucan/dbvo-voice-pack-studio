using DbvoStudio.Core.Archives;
using DbvoStudio.Core.Naming;
using DbvoStudio.Core.Projects;
using DbvoStudio.Core.Tools;
using DbvoStudio.Core.Variants;

namespace DbvoStudio.Core.Tests;

/// <summary>12.0.0: Phase 2 → Variants (text sent, grouping across patches, the Variants Pack BSA).</summary>
public class VariantsPackTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dbvo-variants-" + Guid.NewGuid().ToString("N")[..8]);
    public VariantsPackTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Theory]
    [InlineData("[British Baleful] Falkreath", "[British Extremely Baleful]", "", "[British Extremely Baleful] Falkreath")]
    [InlineData("[British Baleful] Falkreath", "[British Extremely Baleful]", "softly", "[British Extremely Baleful, softly] Falkreath")]
    [InlineData("Falkreath", "British Extremely Baleful", "wry", "[British Extremely Baleful, wry] Falkreath")]
    [InlineData("[British Baleful] [Thoughtful] Hello.", "", "softly", "[British Baleful] [Thoughtful, softly] Hello.")]
    [InlineData("Hello.", "", "softly", "[softly] Hello.")]
    [InlineData("Hello.", "", "", "Hello.")]
    [InlineData("[A] Hello.", "[British Softly Baleful]", "softly", "[British Softly Baleful] Hello.")]
    public void Variant_text_merges_the_word_into_one_tag(string spoken, string prefix, string word, string expected)
        => Assert.Equal(expected, VariantsService.VariantText(spoken, prefix, word));

    [Fact]
    public void Billed_length_leaves_out_tags_sent_as_context()
    {
        Assert.Equal("Falkreath".Length, VariantsService.BilledLength("[British Extremely Baleful] Falkreath", true));
        Assert.Equal("[British Extremely Baleful] Falkreath".Length, VariantsService.BilledLength("[British Extremely Baleful] Falkreath", false));
    }

    [Fact]
    public void Variant_paths_cover_dbvo2_and_dbrev_with_the_period_kept()
    {
        var p = VariantsService.VariantPaths("Vampira", "Falkreath.", 21);
        Assert.Equal(@"sound\dbvo\vampira\Falkreath.\1.fuz", p[0].Dbvo2);
        Assert.Equal(@"sound\dbvo\vampira\Falkreath.(1).fuz", p[0].Dbrev);
        Assert.Equal(21, p.Count);
        Assert.Null(p[19].Dbrev); // DBReV stops at (19)
        Assert.Equal(@"sound\dbvo\vampira\Falkreath.\20.fuz", p[19].Dbvo2);
    }

    private static Phase2Line Line(string text, params bool[] kept)
    {
        var l = new Phase2Line { LineId = NewId.Short(), FinalLine = text, Spoken = text, Key = LineNaming.Key(text, NamingMode.Legacy) };
        for (int i = 0; i < kept.Length; i++)
            l.Takes.Add(new Take { Slot = i + 1, Kept = kept[i], CreatedUtc = DateTime.UtcNow.AddMinutes(i) });
        l.SelectedTakeId = l.Takes.FirstOrDefault()?.Id;
        return l;
    }

    [Fact]
    public void Groups_join_the_same_file_name_across_patches_and_added_lines()
    {
        var v = new VoicePack { Id = "v1", Name = "DBVO Vampira", PackId = "Vampira", Naming = NamingMode.Legacy };
        var a = new DbvoPatch { Id = "a", Name = "A", VoicePackId = "v1", Lines = { Line("Falkreath", true, true), Line("Falkreath.", true) } };
        var b = new DbvoPatch { Id = "b", Name = "B", VoicePackId = "v1", Lines = { Line("falkreath", true) } };
        var other = new DbvoPatch { Id = "c", Name = "C", VoicePackId = "v2", Lines = { Line("Falkreath") } };
        var cfg = VariantsConfig.Default("v1");
        cfg.AddedLines.Add(Line("Lead the way."));
        var g = VariantsService.Groups(v, new[] { a, b, other }, cfg);
        Assert.Equal(3, g.Count); // Falkreath (A + B, case-insensitive like Windows), Falkreath., Lead the way.
        var f = g.Single(x => x.Key == "FALKREATH");
        Assert.Equal(2, f.Members.Count);
        Assert.Equal(3, f.KeptCount);
        Assert.Same(a, f.Home.Patch); // the one with the most ★ already
        Assert.True(g.Single(x => x.Text == "Lead the way.").Added);
        Assert.Equal(1, g.Single(x => x.Key == "FALKREATH.").KeptCount);
    }

    [Fact]
    public void Takes_of_omitted_or_excluded_lines_never_count()
    {
        var v = new VoicePack { Id = "v1", Name = "DBVO Vampira", PackId = "Vampira", Naming = NamingMode.Legacy };
        var baseGame = new DbvoPatch { Id = "b", Name = "0 Skyrim Base Game", VoicePackId = "v1", Lines = { Line("Who are you?", true) } };
        var oldImport = Line("Who are you?", true, true, false); // old WAV + LIP takes, line covered by the Skyrim Base Game reference
        oldImport.OmittedBy = "Skyrim Base Game";
        var glen = new DbvoPatch { Id = "g", Name = "A Glenmoril", VoicePackId = "v1", Lines = { oldImport } };
        var gone = Line("Only covered elsewhere.", true, true);
        gone.OmittedBy = "Dawnguard";
        var excl = Line("Excluded.", true, true);
        excl.Excluded = true;
        var other = new DbvoPatch { Id = "o", Name = "Other", VoicePackId = "v1", Lines = { gone, excl } };
        var groups = VariantsService.Groups(v, new[] { baseGame, glen, other }, VariantsConfig.Default("v1"));
        var who = Assert.Single(groups); // the two lines no patch exports are left out
        Assert.Equal(2, who.Members.Count); // Glenmoril still counts as a mod that says it
        Assert.Equal(1, who.KeptCount);
        Assert.Equal(1, who.TakeCount);
        Assert.Same(baseGame, who.Home.Patch); // even though Glenmoril's old line has more ★
        Assert.All(who.Kept, k => Assert.Same(baseGame, k.Patch));
    }

    [Fact]
    public void Pack_name_and_folder_follow_the_voice_pack()
    {
        var cfg = VariantsConfig.Default("v1");
        Assert.Equal("DBVO Vampira Variants Pack", cfg.PackNameFor(new VoicePack { Name = "DBVO Vampira" }));
        Assert.Equal("Vampire DBVO Variants Pack", cfg.PackNameFor(new VoicePack { Name = "Vampire DBVO" }));
        Assert.Equal("DBVO Sam Variants Pack", cfg.PackNameFor(new VoicePack { Name = "Sam" }));
        Assert.Equal(Path.Combine(@"C:\FOMOD", "Variants Pack"), cfg.OutputFolderFor(new VoicePack { Destinations = { ReleaseFolder = @"C:\FOMOD" } }));
    }

    [Fact]
    public void Typed_lines_follow_the_script_rules()
    {
        Assert.Empty(VariantsService.CheckTypedLine("Is there anything else you need?"));
        Assert.NotEmpty(VariantsService.CheckTypedLine("Wait; what?"));
        Assert.NotEmpty(VariantsService.CheckTypedLine("...and then"));
    }

    [Fact]
    public async Task Variants_pack_holds_both_formats_and_the_period_folder()
    {
        var lib = new Library(Path.Combine(_dir, "lib"));
        var v = new VoicePack { Id = "v1", Name = "DBVO Vampira", PackId = "Vampira", Naming = NamingMode.Legacy };
        var patch = new DbvoPatch { Id = "p1", Name = "P", VoicePackId = "v1" };
        Phase2Line Imported(string text, int takes, int keptTakes)
        {
            var l = new Phase2Line { LineId = NewId.Short(), FinalLine = text, Spoken = text, Key = LineNaming.Key(text, NamingMode.Legacy) };
            for (int i = 0; i < takes; i++)
            {
                var rel = Path.Combine("takes", l.LineId, (i + 1).ToString("00"), "x.fuz");
                var full = Path.Combine(lib.PatchFolder(patch.Id), rel);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllBytes(full, new[] { (byte)'F', (byte)'U', (byte)'Z', (byte)'E', (byte)i, (byte)text.Length });
                l.Takes.Add(new Take { Slot = i + 1, OriginalFuz = rel, Kept = i < keptTakes, CreatedUtc = DateTime.UtcNow.AddMinutes(i) });
            }
            l.SelectedTakeId = l.Takes[0].Id;
            return l;
        }
        patch.Lines.Add(Imported("Falkreath.", 3, 3));
        patch.Lines.Add(Imported("Falkreath", 2, 2));
        patch.Lines.Add(Imported("Only one star.", 2, 1));
        var cfg = VariantsConfig.Default("v1");
        cfg.OutputFolder = Path.Combine(_dir, "out");
        var groups = VariantsService.Groups(v, new[] { patch }, cfg);
        Assert.DoesNotContain(groups, g => g.Added);
        var rep = await new VariantsPackBuilder(lib).BuildAsync(v, groups, cfg, new ExternalTools { FfmpegPath = "ffmpeg.exe", FaceFxWrapperPath = "FaceFXWrapper.exe" });
        Assert.Equal(2, rep.Lines);
        Assert.Equal(5, rep.Takes);
        Assert.Equal(10, rep.Files);
        Assert.True(File.Exists(Path.Combine(_dir, "out", "DBVO Vampira Variants Pack.esp")));
        using var bsa = BsaArchive.Open(rep.Bsa);
        var names = bsa.Entries.Select(e => e.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains(@"sound\dbvo\vampira\falkreath.\1.fuz", names);
        Assert.Contains(@"sound\dbvo\vampira\falkreath.\3.fuz", names);
        Assert.Contains(@"sound\dbvo\vampira\falkreath.(3).fuz", names);
        Assert.Contains(@"sound\dbvo\vampira\falkreath\2.fuz", names);
        Assert.Contains(@"sound\dbvo\vampira\falkreath(2).fuz", names);
        Assert.DoesNotContain(names, n => n.Contains("only_one_star", StringComparison.OrdinalIgnoreCase));
        // the oldest ★ take is 1, and both names of a variant hold the same audio
        Assert.Equal(new byte[] { (byte)'F', (byte)'U', (byte)'Z', (byte)'E', 0, 10 }, bsa.Read(@"sound\dbvo\vampira\falkreath.\1.fuz"));
        Assert.Equal(bsa.Read(@"sound\dbvo\vampira\falkreath.\2.fuz"), bsa.Read(@"sound\dbvo\vampira\falkreath.(2).fuz"));
    }

    [Fact]
    public async Task Hand_added_lines_also_get_their_own_file_in_the_pack()
    {
        var lib = new Library(Path.Combine(_dir, "lib3"));
        var v = new VoicePack { Id = "v1", Name = "DBVO Vampira", PackId = "Vampira", Naming = NamingMode.Legacy };
        var cfg = VariantsConfig.Default("v1");
        cfg.OutputFolder = Path.Combine(_dir, "out3");
        var l = VariantsService.NewAddedLine(v, "Is there anything else you need?", "");
        for (int i = 0; i < 2; i++)
        {
            var rel = Path.Combine("takes", l.LineId, (i + 1).ToString("00"), "x.fuz");
            var full = Path.Combine(lib.VariantsFolder(v.Id), rel);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, new[] { (byte)'F', (byte)'U', (byte)'Z', (byte)'E', (byte)i });
            l.Takes.Add(new Take { Slot = i + 1, OriginalFuz = rel, Kept = true, CreatedUtc = DateTime.UtcNow.AddMinutes(i) });
        }
        cfg.AddedLines.Add(l);
        var groups = VariantsService.Groups(v, Array.Empty<DbvoPatch>(), cfg);
        var rep = await new VariantsPackBuilder(lib).BuildAsync(v, groups, cfg, new ExternalTools { FfmpegPath = "ffmpeg.exe", FaceFxWrapperPath = "FaceFXWrapper.exe" });
        using var bsa = BsaArchive.Open(rep.Bsa);
        var names = bsa.Entries.Select(e => e.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains(@"sound\dbvo\vampira\is_there_anything_else_you_need_.fuz", names);
        Assert.Contains(@"sound\dbvo\vampira\is_there_anything_else_you_need_\2.fuz", names);
        Assert.Equal(5, rep.Files);
    }

    [Fact]
    public async Task Variants_pack_refuses_dbvo2_names_and_an_empty_set()
    {
        var lib = new Library(Path.Combine(_dir, "lib2"));
        var cfg = VariantsConfig.Default("v1");
        cfg.OutputFolder = Path.Combine(_dir, "out2");
        var tools = new ExternalTools { FfmpegPath = "ffmpeg.exe", FaceFxWrapperPath = "FaceFXWrapper.exe" };
        await Assert.ThrowsAsync<InvalidOperationException>(() => new VariantsPackBuilder(lib).BuildAsync(new VoicePack { Naming = NamingMode.Dbvo2Hash }, new(), cfg, tools));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new VariantsPackBuilder(lib).BuildAsync(new VoicePack { Naming = NamingMode.Legacy, PackId = "x", Name = "X" }, new(), cfg, tools));
    }

    // ---- 12.0.4: what the words say

    [Fact]
    public void Lines_a_patch_doesnt_export_because_they_arent_approved_never_count()
    {
        var v = new VoicePack { Id = "v1", Name = "V", PackId = "V", Naming = NamingMode.Legacy };
        var unapproved = Line("Hello.", true, true);
        var approved = Line("Bye.", true, true);
        approved.Approved = true;
        var p = new DbvoPatch { Id = "p", Name = "P", VoicePackId = "v1", ExportApprovedOnly = true, Lines = { unapproved, approved } };
        var g = VariantsService.Groups(v, new[] { p }, VariantsConfig.Default("v1"));
        Assert.DoesNotContain(g, x => x.Text == "Hello.");
        Assert.Equal(2, g.Single(x => x.Text == "Bye.").KeptCount);
    }

    [Fact]
    public void Dbvo2_names_count_only_the_home_patch_stars()
    {
        // each DBVO 2.0 patch exports its own stars, so one star in each of two patches is not two variants
        var v = new VoicePack { Id = "v1", Name = "V", PackId = "V", Naming = NamingMode.Dbvo2Hash };
        Phase2Line L() { var l = Line("Hello.", true); l.FileStem = "ABC123"; return l; }
        var a = new DbvoPatch { Id = "a", Name = "A", VoicePackId = "v1", Lines = { L() } };
        var b = new DbvoPatch { Id = "b", Name = "B", VoicePackId = "v1", Lines = { L() } };
        var g = VariantsService.Groups(v, new[] { a, b }, VariantsConfig.Default("v1")).Single();
        Assert.Equal(1, g.KeptCount);
        Assert.Single(g.Kept);
    }

    [Fact]
    public void A_typed_line_next_to_an_excluded_patch_copy_is_still_added_and_removable()
    {
        var v = new VoicePack { Id = "v1", Name = "V", PackId = "V", Naming = NamingMode.Legacy };
        var excluded = Line("Lead the way.", true);
        excluded.Excluded = true;
        var p = new DbvoPatch { Id = "p", Name = "P", VoicePackId = "v1", Lines = { excluded } };
        var cfg = VariantsConfig.Default("v1");
        cfg.AddedLines.Add(Line("Lead the way.", true, true));
        var g = VariantsService.Groups(v, new[] { p }, cfg).Single();
        Assert.True(g.Added);        // no patch exports its file, so the pack writes it
        Assert.True(g.HasAddedCopy); // and "Remove this line" is offered
        Assert.Equal(2, g.KeptCount);
    }

    [Theory]
    [InlineData("British Extremely Baleful", "[British Extremely Baleful]")]
    [InlineData("[British] [Baleful]", "[British] [Baleful]")]
    [InlineData("[British] softly", "[British softly]")]   // a loose word would be spoken
    [InlineData("[British", "[British]")]                  // an open bracket would be spoken
    [InlineData("[]", "")]
    [InlineData("[A] [] [B]", "[A] [B]")]
    [InlineData("  ", "")]
    public void The_prefix_is_only_ever_tags(string prefix, string expected)
        => Assert.Equal(expected, VariantsService.NormalizePrefix(prefix));

    [Fact]
    public void Leading_tags_split_off_and_mid_line_tags_stay_in_the_words()
    {
        var (lead, words) = VariantsService.SplitLeading("[British]  [Calm] Hello [laughs] there.");
        Assert.Equal("[British] [Calm]", lead);
        Assert.Equal("Hello [laughs] there.", words);
    }

    private static Phase2Line Packed(Library lib, DbvoPatch? patch, string voiceId, string text, int kept, bool withFiles = true)
    {
        var l = new Phase2Line { LineId = NewId.Short(), FinalLine = text, Spoken = text, Key = LineNaming.Key(text, NamingMode.Legacy) };
        var root = patch != null ? lib.PatchFolder(patch.Id) : lib.VariantsFolder(voiceId);
        for (int i = 0; i < kept; i++)
        {
            var rel = Path.Combine("takes", l.LineId, (i + 1).ToString("00"), "x.fuz");
            if (withFiles)
            {
                var full = Path.Combine(root, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllBytes(full, Enumerable.Repeat((byte)(i + 1), 4000).ToArray());
            }
            l.Takes.Add(new Take { Slot = i + 1, OriginalFuz = rel, WavPath = Path.Combine("takes", l.LineId, (i + 1).ToString("00"), "x.wav"), LipPath = "", Kept = true, CreatedUtc = DateTime.UtcNow.AddMinutes(i) });
        }
        l.SelectedTakeId = l.Takes.FirstOrDefault()?.Id;
        return l;
    }

    [Fact]
    public async Task A_take_with_missing_files_leaves_out_its_line_not_the_whole_pack()
    {
        var lib = new Library(Path.Combine(_dir, "lib4"));
        var v = new VoicePack { Id = "v1", Name = "V", PackId = "V", Naming = NamingMode.Legacy };
        var p = new DbvoPatch { Id = "p4", Name = "P", VoicePackId = "v1" };
        p.Lines.Add(Packed(lib, p, "v1", "Good line.", 2));
        p.Lines.Add(Packed(lib, p, "v1", "Broken line.", 2, withFiles: false));
        var cfg = VariantsConfig.Default("v1");
        cfg.OutputFolder = Path.Combine(_dir, "out4");
        var rep = await new VariantsPackBuilder(lib).BuildAsync(v, VariantsService.Groups(v, new[] { p }, cfg), cfg, new ExternalTools { FfmpegPath = "ffmpeg.exe", FaceFxWrapperPath = "FaceFXWrapper.exe" });
        Assert.Equal(1, rep.Lines);
        Assert.Contains(rep.Skipped, x => x.StartsWith("Broken line."));
    }

    [Fact]
    public async Task Both_names_of_a_variant_share_one_copy_of_its_audio()
    {
        var lib = new Library(Path.Combine(_dir, "lib5"));
        var v = new VoicePack { Id = "v1", Name = "V", PackId = "V", Naming = NamingMode.Legacy };
        var p = new DbvoPatch { Id = "p5", Name = "P", VoicePackId = "v1" };
        p.Lines.Add(Packed(lib, p, "v1", "Falkreath.", 3));
        var cfg = VariantsConfig.Default("v1");
        cfg.OutputFolder = Path.Combine(_dir, "out5");
        var rep = await new VariantsPackBuilder(lib).BuildAsync(v, VariantsService.Groups(v, new[] { p }, cfg), cfg, new ExternalTools { FfmpegPath = "ffmpeg.exe", FaceFxWrapperPath = "FaceFXWrapper.exe" });
        Assert.Equal(6, rep.Files);                         // 3 DBVO 2 names + 3 DBReV names
        Assert.True(new FileInfo(rep.Bsa).Length < 3 * 4000 + 2000); // the audio of 3 takes once, not twice
        using var bsa = BsaArchive.Open(rep.Bsa);
        for (int i = 1; i <= 3; i++)
        {
            Assert.Equal(Enumerable.Repeat((byte)i, 4000).ToArray(), bsa.Read($@"sound\dbvo\v\falkreath.\{i}.fuz"));
            Assert.Equal(bsa.Read($@"sound\dbvo\v\falkreath.\{i}.fuz"), bsa.Read($@"sound\dbvo\v\falkreath.({i}).fuz"));
        }
    }

    [Fact]
    public async Task A_line_named_like_another_lines_variant_is_left_out_not_mixed_in()
    {
        var lib = new Library(Path.Combine(_dir, "lib6"));
        var v = new VoicePack { Id = "v1", Name = "V", PackId = "V", Naming = NamingMode.Legacy };
        var p = new DbvoPatch { Id = "p6", Name = "P", VoicePackId = "v1" };
        p.Lines.Add(Packed(lib, p, "v1", "Hello", 2));
        var cfg = VariantsConfig.Default("v1");
        cfg.OutputFolder = Path.Combine(_dir, "out6");
        cfg.AddedLines.Add(Packed(lib, null, "v1", "Hello(1)", 2)); // its own file would be Hello's DBReV variant (1)
        var rep = await new VariantsPackBuilder(lib).BuildAsync(v, VariantsService.Groups(v, new[] { p }, cfg), cfg, new ExternalTools { FfmpegPath = "ffmpeg.exe", FaceFxWrapperPath = "FaceFXWrapper.exe" });
        Assert.Equal(1, rep.Lines);
        Assert.Contains(rep.Skipped, x => x.StartsWith("Hello(1)"));
        using var bsa = BsaArchive.Open(rep.Bsa);
        Assert.Equal(Enumerable.Repeat((byte)1, 4000).ToArray(), bsa.Read(@"sound\dbvo\v\hello(1).fuz"));
    }

    [Fact]
    public void A_cancelled_bsa_write_leaves_the_old_archive_and_no_part_file()
    {
        var src = Path.Combine(_dir, "a.fuz");
        File.WriteAllBytes(src, new byte[100]);
        var bsaPath = Path.Combine(_dir, "cancel", "x.bsa");
        BsaWriter.Write(bsaPath, new[] { new BsaWriter.Entry(@"sound\a.fuz", src) });
        var before = File.ReadAllBytes(bsaPath);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => BsaWriter.Write(bsaPath, new[] { new BsaWriter.Entry(@"sound\b.fuz", src) }, cts.Token));
        Assert.Equal(before, File.ReadAllBytes(bsaPath));
        Assert.False(File.Exists(bsaPath + ".part"));
    }

    [Fact]
    public void A_folder_name_too_long_for_a_bsa_is_refused_with_a_reason()
    {
        var src = Path.Combine(_dir, "b.fuz");
        File.WriteAllBytes(src, new byte[10]);
        var longFolder = @"sound\dbvo\v\" + new string('a', 260);
        Assert.False(BsaWriter.FolderFits(longFolder + @"\1.fuz"));
        Assert.True(BsaWriter.FolderFits(@"sound\dbvo\v\falkreath.\1.fuz"));
        Assert.Throws<InvalidOperationException>(() => BsaWriter.Write(Path.Combine(_dir, "long.bsa"), new[] { new BsaWriter.Entry(longFolder + @"\1.fuz", src) }));
    }

    [Fact]
    public async Task A_variant_name_a_patch_exports_itself_leaves_that_line_out()
    {
        var lib = new Library(Path.Combine(_dir, "lib7"));
        var v = new VoicePack { Id = "v1", Name = "V", PackId = "V", Naming = NamingMode.Legacy };
        var p = new DbvoPatch { Id = "p7", Name = "P", VoicePackId = "v1" };
        p.Lines.Add(Packed(lib, p, "v1", "Hello", 2));
        p.Lines.Add(Packed(lib, p, "v1", "Bye", 2));
        var cfg = VariantsConfig.Default("v1");
        cfg.OutputFolder = Path.Combine(_dir, "out7");
        // another patch exports a line named "Hello(1)" itself: Hello's DBReV variant (1) would be overruled by it in game
        var files = new HashSet<string> { "HELLO", "BYE", "HELLO(1)" };
        var rep = await new VariantsPackBuilder(lib).BuildAsync(v, VariantsService.Groups(v, new[] { p }, cfg), cfg, new ExternalTools { FfmpegPath = "ffmpeg.exe", FaceFxWrapperPath = "FaceFXWrapper.exe" }, null, default, files);
        Assert.Equal(1, rep.Lines);
        Assert.Contains(rep.Skipped, x => x.StartsWith("Hello:"));
    }

    [Fact]
    public void With_dbvo2_names_new_variants_never_go_to_a_hand_added_copy()
    {
        var v = new VoicePack { Id = "v1", Name = "V", PackId = "V", Naming = NamingMode.Dbvo2Hash };
        var patchLine = Line("Hello.", true); patchLine.FileStem = "ABC";
        var p = new DbvoPatch { Id = "a", Name = "A", VoicePackId = "v1", Lines = { patchLine } };
        var cfg = VariantsConfig.Default("v1");
        var added = Line("Hello.", true, true, true); added.FileStem = "ABC";
        cfg.AddedLines.Add(added);
        var g = VariantsService.Groups(v, new[] { p }, cfg).Single();
        Assert.Same(p, g.Home.Patch);
        Assert.Equal(1, g.KeptCount);
    }
}
