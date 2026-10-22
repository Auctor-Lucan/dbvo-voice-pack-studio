using DbvoStudio.Core.Archives;
using DbvoStudio.Core.Audio;
using DbvoStudio.Core.Naming;
using DbvoStudio.Core.Projects;
using DbvoStudio.Core.Tools;

namespace DbvoStudio.Core.Tests;

public class NewFeatures70Tests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dbvo-70-" + Guid.NewGuid().ToString("N")[..8]);
    public NewFeatures70Tests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static byte[] Xwm(int n) { var b = new byte[n + 12]; new Random(n).NextBytes(b); "RIFF"u8.CopyTo(b); BitConverter.GetBytes(n + 4).CopyTo(b, 4); "XWMA"u8.CopyTo(b.AsSpan(8)); return b; }

    // ---------------------------------------------------------------- BSA

    [Theory]
    // Values read from real archives (Aela Revoiced.bsa, AelaRevoicedOStimVoice.bsa)
    [InlineData(@"sound\fx\ostim\actor\postscenedialogue\aelarevoiced", true, 0x25a8295073336564UL)]
    [InlineData("aelapostscenedialogue-01.wav", false, 0x04516181e1183031UL)]
    [InlineData(@"sound\voice\arial smooches of skyrim.esp\_aelabyauctorvoice", true, 0x0f9982af733b6365UL)]
    [InlineData("arialaffec_arialaffectiona_00000800_1.fuz", false, 0x50b2b4f561255f31UL)]
    public void Bsa_hash_matches_bethesda(string name, bool folder, ulong expected) => Assert.Equal(expected, BsaWriter.Hash(name, folder));

    [Fact]
    public void Bsa_written_reads_back_with_every_file()
    {
        var src = Path.Combine(_dir, "Sound", "DBVO", "MyVoice");
        Directory.CreateDirectory(Path.Combine(src, "Sub"));
        var files = new Dictionary<string, byte[]>();
        var rnd = new Random(1);
        for (int i = 0; i < 40; i++)
        {
            var rel = (i % 3 == 0 ? "Sub\\" : "") + $"Line_{i}_A1B2.fuz";
            var data = new byte[100 + rnd.Next(5000)]; rnd.NextBytes(data);
            File.WriteAllBytes(Path.Combine(src, rel), data);
            files[@"sound\dbvo\myvoice\" + rel.ToLowerInvariant()] = data;
        }
        var bsaPath = Path.Combine(_dir, "out", "DBVO - MyVoice.bsa");
        var entries = Directory.EnumerateFiles(Path.Combine(_dir, "Sound"), "*", SearchOption.AllDirectories)
            .Select(f => new BsaWriter.Entry("sound\\" + Path.GetRelativePath(Path.Combine(_dir, "Sound"), f), f));
        Assert.Equal(40, BsaWriter.Write(bsaPath, entries));
        using var bsa = BsaArchive.Open(bsaPath);
        Assert.Equal(105, bsa.Version);
        Assert.Equal(40, bsa.Entries.Count);
        foreach (var (path, data) in files) Assert.Equal(data, bsa.Read(path));
        Assert.False(File.Exists(bsaPath + ".part"));
    }

    [Fact]
    public void Loader_plugin_is_an_empty_light_plugin()
    {
        var p = Path.Combine(_dir, "DBVO - MyVoice.esp");
        Plugins.FixPluginWriter.WriteLoader(p);
        var b = File.ReadAllBytes(p);
        Assert.Equal("TES4", System.Text.Encoding.ASCII.GetString(b, 0, 4));
        Assert.NotEqual(0u, BitConverter.ToUInt32(b, 8) & Plugins.PluginFile.HeaderFlagLight);
        var plugin = Plugins.PluginFile.Read(p);
        Assert.True(plugin.IsLight);
        Assert.Empty(plugin.Masters);
    }

    // ---------------------------------------------------------------- QA

    [Theory]
    [InlineData("Die, vampire!", "DIE, VAMPIER!", true)]
    [InlineData("So be it, but know that the All-Maker's plans are not always what we expect.", "So be it, but know that the all-makers' plans are not always what we expect.", true)]
    [InlineData("[nervous] I've been meaning to talk to you... *sighs* I'm pregnant.", "I've been meaning to talk to you, I'm pregnant.", true)]
    [InlineData("You're no companion anymore, monster!", "Leave that dog alone.", false)]
    [InlineData("Leave that dog alone.", "Can I help you?", false)]
    [InlineData("Without us, the people of Skyrim will not have your protection. You are just fools blinded by fear.", "Without us, the people of Skyrim will not have your", false)]
    [InlineData("She's doing fine enough.", "She's doing fine enough, dear. Her aim is good, but her patience needs work.", false)]
    public void Qa_score_passes_right_lines_and_flags_wrong_ones(string intended, string heard, bool pass) =>
        Assert.Equal(pass, QaText.Score(intended, heard) >= 0.75);

    [Fact]
    public void Ebur128_summary_is_parsed()
    {
        var log = "[Parsed_ebur128_0 @ x] t: 1.2 ...\n[Parsed_ebur128_0 @ 0x1] Summary:\n\n  Integrated loudness:\n    I:         -18.3 LUFS\n    Threshold: -28.4 LUFS\n\n  True peak:\n    Peak:       -1.4 dBFS\n";
        var (i, tp) = ExternalTools.ParseEbur128(log);
        Assert.Equal(-18.3, i);
        Assert.Equal(-1.4, tp);
    }

    // ---------------------------------------------------------------- CSV

    [Theory]
    [InlineData(',')]
    [InlineData(';')]
    public void Spoken_text_csv_round_trips(char sep)
    {
        var rows = new List<SpokenCsv.Row>
        {
            new("a1", "Hello_there", "Hello, there; friend.", "[warmly] Hello, there; friend."),
            new("a2", "Say_\"hi\"", "Say \"hi\".", "Say \"hi\"\nto them."),
            new("a3", "Dovahkiin", "Dovahkiin! Ald'ruhn — ё", "Dovahkiin! Ald'ruhn — ё"),
        };
        var text = "﻿" + SpokenCsv.Write(rows, sep);
        Assert.Equal(rows, SpokenCsv.Read(text));
    }

    [Fact]
    public void Spoken_text_csv_needs_its_columns() =>
        Assert.Throws<InvalidOperationException>(() => SpokenCsv.Read("a,b\n1,2\n"));

    // ---------------------------------------------------------------- legacy import, original .fuz, cache, merge, approved only

    private (Library lib, AppSettings settings, Mod mod, VoicePack voice, DbvoPatch patch) Setup(params string[] lines)
    {
        var lib = new Library(Path.Combine(_dir, "lib"));
        var settings = lib.LoadSettings();
        var mod = new Mod { Name = "Example Mod" };
        var plugin = new ModPlugin { FileName = "Example.esp" };
        uint id = 1;
        foreach (var l in lines) plugin.Lines.Add(new Phase1Line { FormId = id++, Original = l, Final = l, Kind = Rules.LineKind.Valid });
        mod.Plugins.Add(plugin);
        lib.Save(mod);
        var voice = new VoicePack { Name = "Sample Voice", PackId = "SampleVoice", Naming = NamingMode.Dbvo2Hash };
        var patch = new DbvoPatch { Name = "Example", VoicePackId = voice.Id, ModIds = { mod.Id }, FinalRoot = Path.Combine(_dir, "final") };
        new Phase2Service(lib, settings).Sync(patch, voice, new[] { mod });
        return (lib, settings, mod, voice, patch);
    }

    [Fact]
    public void Legacy_scan_finds_both_namings_at_any_depth_and_reports_the_rest()
    {
        var (lib, settings, mod, voice, patch) = Setup("Follow me.", "Wait here.", "Not voiced yet.", "Has a take.");
        var old = Path.Combine(_dir, "Old pack", "Sound", "DBVO", "OldVoice", "deep");
        Directory.CreateDirectory(old);
        File.WriteAllBytes(Path.Combine(old, LineNaming.Legacy("Follow me.") + ".fuz"), FuzFile.Build(new byte[4], Xwm(50)));
        File.WriteAllBytes(Path.Combine(old, LineNaming.Dbvo2("Wait here.") + ".fuz"), FuzFile.Build(new byte[4], Xwm(50)));
        File.WriteAllBytes(Path.Combine(old, "Some_other_mod_line.fuz"), FuzFile.Build(new byte[4], Xwm(50)));
        patch.Lines.Single(l => l.FinalLine == "Has a take.").Takes.Add(new Take { WavPath = "x.wav", LipPath = "x.lip" });
        var scan = new Phase2Service(lib, settings).ScanLegacy(patch, Path.Combine(_dir, "Old pack"));
        Assert.Equal(2, scan.FromFuz);
        Assert.Equal(1, scan.LegacyNamed);
        Assert.Equal(1, scan.HashNamed);
        Assert.Equal(1, scan.Missing);
        Assert.Equal(1, scan.HaveTakes);
        Assert.Equal(1, scan.Unmatched);
    }

    [Fact]
    public async Task Legacy_wav_lip_import_adds_takes_and_never_touches_lines_with_takes()
    {
        var (lib, settings, mod, voice, patch) = Setup("Follow me.", "Wait here.");
        var old = Path.Combine(_dir, "old");
        Directory.CreateDirectory(old);
        var wav = new WavFile { SampleRate = 44100, Channels = 1, Samples = new short[22050] };
        wav.Write(Path.Combine(old, LineNaming.Legacy("Follow me.") + ".wav"));
        File.WriteAllBytes(Path.Combine(old, LineNaming.Legacy("Follow me.") + ".lip"), new byte[] { 1, 2 });
        var tools = new ExternalTools { FfmpegPath = "ffmpeg", FaceFxWrapperPath = "x" };
        int n = await new Phase2Service(lib, settings).ImportLegacyAsync(patch, voice, old, tools, null, CancellationToken.None);
        Assert.Equal(1, n);
        var line = patch.Lines.Single(l => l.FinalLine == "Follow me.");
        Assert.Equal(Phase2Status.Generated, line.Status);
        Assert.Equal(0.5, line.SelectedTake!.DurationSeconds, 2);
        Assert.Empty(patch.Lines.Single(l => l.FinalLine == "Wait here.").Takes);
        Assert.Equal(0, await new Phase2Service(lib, settings).ImportLegacyAsync(patch, voice, old, tools, null, CancellationToken.None));
    }

    [Fact]
    public async Task Fuz_build_reuses_the_original_fuz_and_the_cache_without_encoding()
    {
        var (lib, settings, mod, voice, patch) = Setup("Follow me.", "Wait here.");
        var pf = lib.PatchFolder(patch.Id);
        var wav = new WavFile { SampleRate = 44100, Channels = 1, Samples = new short[4410] };
        foreach (var l in patch.Lines)
        {
            var rel = Path.Combine("takes", l.LineId, "01");
            Directory.CreateDirectory(Path.Combine(pf, rel));
            var t = new Take { Slot = 1, WavPath = Path.Combine(rel, "a.wav"), LipPath = Path.Combine(rel, "a.lip") };
            wav.Write(Path.Combine(pf, t.WavPath));
            File.WriteAllBytes(Path.Combine(pf, t.LipPath), System.Text.Encoding.UTF8.GetBytes(l.FinalLine));
            l.Takes.Add(t); l.SelectedTakeId = t.Id; l.Status = Phase2Status.Generated;
        }
        // line 1: imported .fuz kept as it was
        var follow = patch.Lines.Single(l => l.FinalLine == "Follow me.");
        var original = FuzFile.Build(new byte[] { 9, 9 }, Xwm(300));
        follow.SelectedTake!.OriginalFuz = Path.Combine("takes", follow.LineId, "01", "a.fuz");
        File.WriteAllBytes(Path.Combine(pf, follow.SelectedTake.OriginalFuz), original);
        // line 2: an earlier build is in the cache
        var wait = patch.Lines.Single(l => l.FinalLine == "Wait here.");
        var cached = FuzFile.Build(new byte[] { 7 }, Xwm(200));
        var cache = new FuzCache(lib.FuzCacheFolder);
        cache.Put(FuzCache.Key(File.ReadAllBytes(Path.Combine(pf, wait.SelectedTake!.WavPath)), File.ReadAllBytes(Path.Combine(pf, wait.SelectedTake.LipPath)), FuzCache.EncoderId(null, 48000)), cached);
        // no xWMAEncode at all: any encode would throw
        var tools = new ExternalTools { FfmpegPath = "ffmpeg", FaceFxWrapperPath = "x", XwmaEncodePath = null };
        var rep = await new Phase2Service(lib, settings).ExportFuzAsync(patch, voice, new[] { mod }, tools);
        Assert.Equal(0, rep.Failed);
        Assert.Equal(1, rep.Reused);
        Assert.Equal(1, rep.Cached);
        var folder = Directory.EnumerateFiles(patch.FinalRoot, "*.fuz", SearchOption.AllDirectories).ToList();
        Assert.Equal(original, File.ReadAllBytes(folder.Single(f => Path.GetFileNameWithoutExtension(f) == LineNaming.Dbvo2("Follow me."))));
        Assert.Equal(cached, File.ReadAllBytes(folder.Single(f => Path.GetFileNameWithoutExtension(f) == LineNaming.Dbvo2("Wait here."))));
        Assert.Equal((1, (long)cached.Length), cache.Size());
    }

    [Fact]
    public void Fuz_cache_key_changes_with_audio_lip_and_encoder()
    {
        var a = FuzCache.Key(new byte[] { 1 }, new byte[] { 2 }, "e1");
        Assert.Equal(a, FuzCache.Key(new byte[] { 1 }, new byte[] { 2 }, "e1"));
        Assert.NotEqual(a, FuzCache.Key(new byte[] { 3 }, new byte[] { 2 }, "e1"));
        Assert.NotEqual(a, FuzCache.Key(new byte[] { 1 }, new byte[] { 3 }, "e1"));
        Assert.NotEqual(a, FuzCache.Key(new byte[] { 1 }, new byte[] { 2 }, "e2"));
    }

    [Fact]
    public void Approved_only_export_plans_only_approved_lines()
    {
        var (lib, settings, mod, voice, patch) = Setup("One.", "Two.");
        foreach (var l in patch.Lines) { var t = new Take { WavPath = "a.wav", LipPath = "a.lip" }; l.Takes.Add(t); l.SelectedTakeId = t.Id; }
        patch.Lines[0].Approved = true;
        Assert.Equal(2, Phase2Service.ExportLines(patch).Count());
        patch.ExportApprovedOnly = true;
        Assert.Equal("One.", Phase2Service.ExportLines(patch).Single().FinalLine);
    }

    [Fact]
    public void Merge_adds_takes_and_keeps_selection()
    {
        var (lib, settings, mod, voice, patch) = Setup("One.", "Two.");
        var other = new DbvoPatch { Name = "Other", VoicePackId = voice.Id, ModIds = { mod.Id } };
        new Phase2Service(lib, settings).Sync(other, voice, new[] { mod });
        var wav = new WavFile { SampleRate = 44100, Channels = 1, Samples = new short[441] };
        void Give(DbvoPatch p, Phase2Line l, string tag)
        {
            var rel = Path.Combine("takes", l.LineId, "0" + (l.Takes.Count + 1));
            Directory.CreateDirectory(Path.Combine(lib.PatchFolder(p.Id), rel));
            var t = new Take { Slot = l.Takes.Count + 1, WavPath = Path.Combine(rel, "a.wav"), LipPath = Path.Combine(rel, "a.lip"), SpokenText = tag };
            wav.Write(Path.Combine(lib.PatchFolder(p.Id), t.WavPath));
            File.WriteAllBytes(Path.Combine(lib.PatchFolder(p.Id), t.LipPath), new byte[] { 1 });
            l.Takes.Add(t); l.SelectedTakeId = t.Id; l.Status = Phase2Status.Generated;
        }
        Give(patch, patch.Lines[0], "mine");
        Give(other, other.Lines[0], "theirs");
        Give(other, other.Lines[1], "theirs");
        var mine = patch.Lines[0].SelectedTakeId;
        var (takes, lines, onlyThere) = new Phase2Service(lib, settings).MergeTakes(patch, voice, other);
        Assert.Equal((2, 2, 0), (takes, lines, onlyThere));
        Assert.Equal(mine, patch.Lines[0].SelectedTakeId);
        Assert.Equal(2, patch.Lines[0].Takes.Count);
        Assert.Equal("theirs", patch.Lines[1].SelectedTake!.SpokenText);
        Assert.True(File.Exists(Path.Combine(lib.PatchFolder(patch.Id), patch.Lines[1].SelectedTake!.WavPath)));
    }
}
