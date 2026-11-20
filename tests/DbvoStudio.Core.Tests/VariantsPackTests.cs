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
}
