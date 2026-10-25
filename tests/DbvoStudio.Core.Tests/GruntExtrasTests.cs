using DbvoStudio.Core.Audio;
using DbvoStudio.Core.Grunts;
using Xunit;

public class GruntExtrasTests
{
    private static string TempDir() { var d = Path.Combine(Path.GetTempPath(), "gx-" + Guid.NewGuid().ToString("N")[..8]); Directory.CreateDirectory(d); return d; }

    private static WavFile Tone(int rate = 44100, double secs = 0.5)
    {
        var s = new short[(int)(rate * secs)];
        for (int i = 0; i < s.Length; i++) s[i] = (short)(Math.Sin(2 * Math.PI * 440 * i / rate) * 12000 * (i < rate / 10 ? 1 : 0.2));
        return new WavFile { SampleRate = rate, Channels = 1, Samples = s };
    }

    [Fact]
    public void Reverb_adds_a_tail_and_keeps_the_dry_signal()
    {
        var w = Tone();
        var o = Reverb.Apply(w.Samples, w.SampleRate, 1, new ReverbSettings());
        Assert.Equal(w.Samples.Length * 2, o.Length); // 0.5 s line: the tail is as long as the line (macro pastes min(1 s, length))
        double tail = o.Skip(w.Samples.Length + 4410).Take(4410).Sum(x => Math.Abs((double)x));
        Assert.True(tail > 0, "the reverb rings into the added tail");
        Assert.True(Math.Abs(o[100] - w.Samples[100]) < 2000, "dry signal kept at 0 dB (before the 99 ms pre-delay only dry)");
        var st = Reverb.Apply(new short[] { 1000, -1000, 500, -500 }, 44100, 2, new ReverbSettings());
        Assert.Equal(8, st.Length);
    }

    [Fact]
    public async Task Reverb_toggle_changes_the_file_and_restores_the_original()
    {
        var dir = TempDir();
        var wav = Path.Combine(dir, "01.wav");
        Tone().Write(wav);
        var orig = File.ReadAllBytes(wav);
        var p = new GruntProject();
        var a = new GruntLine { Source = wav, Origin = "import" };
        var b = new GruntLine { Source = wav, Origin = "import" };
        GruntService.State(p, "e1").Lines.Add(a);
        GruntService.State(p, "e2").Lines.Add(b);
        var proj = Path.Combine(dir, "proj");
        await GruntService.SetReverbAsync(p, a, proj, true, null);
        Assert.True(a.Reverb && b.Reverb);
        Assert.NotEqual(orig, File.ReadAllBytes(wav));
        Assert.Equal(1.0, a.DurationSeconds, 2);
        Assert.True(File.Exists(GruntService.ReverbOriginal(proj, wav)));
        // changed outside since: asks first
        File.SetLastWriteTimeUtc(wav, DateTime.UtcNow.AddMinutes(5));
        await Assert.ThrowsAsync<GruntService.FileChangedException>(() => GruntService.SetReverbAsync(p, a, proj, false, null));
        await GruntService.SetReverbAsync(p, a, proj, false, null, force: true);
        Assert.False(a.Reverb || b.Reverb);
        Assert.Equal(orig, File.ReadAllBytes(wav));
        Assert.False(File.Exists(GruntService.ReverbOriginal(proj, wav)));
        // removing a reverbed line gives the file its original back
        await GruntService.SetReverbAsync(p, a, proj, true, null);
        GruntService.State(p, "e2").Lines.Clear();
        GruntService.ReleaseReverb(p, a, proj);
        Assert.Equal(orig, File.ReadAllBytes(wav));
        Directory.Delete(dir, true);
    }

    [Fact]
    public async Task Reverb_refuses_non_wav()
    {
        var dir = TempDir();
        var f = Path.Combine(dir, "a.xwm"); File.WriteAllBytes(f, new byte[10]);
        var p = new GruntProject(); var l = new GruntLine { Source = f }; GruntService.State(p, "e").Lines.Add(l);
        await Assert.ThrowsAsync<InvalidOperationException>(() => GruntService.SetReverbAsync(p, l, dir, true, null));
        Directory.Delete(dir, true);
    }

    [Fact]
    public void Wav_header_probe()
    {
        var dir = TempDir();
        var f = Path.Combine(dir, "a.wav"); Tone(22050, 1.5).Write(f);
        var i = GruntService.ProbeWavHeader(f)!;
        Assert.Equal(22050, i.SampleRate); Assert.Equal(1, i.Channels); Assert.Equal(1.5, i.Seconds, 3);
        File.WriteAllBytes(f, new byte[50]);
        Assert.Null(GruntService.ProbeWavHeader(f));
        Directory.Delete(dir, true);
    }

    [Fact]
    public void Subtitles_toml_round_trip()
    {
        var cat = new GruntCatalog { Events = { new GruntEventDef { Id = "e1", Category = "Combat", Label = "Attack" } } };
        var p = new GruntProject { Name = "T", Target = GruntTargets.Spvs };
        var l = new GruntLine { Source = @"C:\x\attack_01.wav", Text = "Take \"this\"!" };
        GruntService.State(p, "e1").Lines.Add(l);
        var toml = GruntService.BuildSubtitlesToml(p, cat, "proj");
        Assert.Contains("attack_01.wav", toml);
        var edited = toml.Replace("Take \\\"this\\\"!", "Have at you!") + "\nzzz = \"?\"\n";
        var (changed, unknown) = GruntService.ReadSubtitlesToml(p, edited);
        Assert.Equal(1, changed); Assert.Equal("Have at you!", l.Text); Assert.Single(unknown);
        Assert.Equal(0, GruntService.ReadSubtitlesToml(p, toml.Replace("Take \\\"this\\\"!", "Have at you!")).Changed);
    }
}
