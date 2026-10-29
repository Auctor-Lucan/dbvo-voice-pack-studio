using DbvoStudio.Core.Audio;

namespace DbvoStudio.Core.Tests;

public class AudioTests
{
    [Fact]
    public void Trim_removes_15ms_and_fades_last_10ms_to_silence()
    {
        var wav = new WavFile { SampleRate = 44100, Channels = 1, Samples = Enumerable.Repeat((short)10000, 44100).ToArray() };
        wav.TrimTailAndFade(15, 10);
        Assert.Equal(44100 - 662, wav.Samples.Length); // round(661.5) = 662
        Assert.Equal(0, wav.Samples[^1]);
        Assert.Equal(10000, wav.Samples[^442]);          // just before the 441-sample fade
        Assert.InRange(wav.Samples[^221], 4000, 6000);   // halfway through the fade
        Assert.True(wav.Samples.Skip(wav.Samples.Length - 441).Zip(wav.Samples.Skip(wav.Samples.Length - 440)).All(p => p.First >= p.Second));
    }

    [Fact]
    public void Wav_round_trips()
    {
        var wav = new WavFile { SampleRate = 44100, Channels = 1, Samples = new short[] { 1, -2, 3, short.MaxValue, short.MinValue } };
        var back = WavFile.Read(wav.ToBytes());
        Assert.Equal(44100, back.SampleRate);
        Assert.Equal(1, back.Channels);
        Assert.Equal(wav.Samples, back.Samples);
    }

    [Fact]
    public void Fuz_layout_matches_yakitori()
    {
        var lip = new byte[] { 1, 2, 3, 4, 5 };
        var xwm = new byte[40];
        "RIFF"u8.CopyTo(xwm);
        "XWMA"u8.CopyTo(xwm.AsSpan(8));
        var fuz = FuzFile.Build(lip, xwm);
        Assert.Equal("FUZE"u8.ToArray(), fuz[..4]);
        Assert.Equal(1u, BitConverter.ToUInt32(fuz, 4));
        Assert.Equal(5u, BitConverter.ToUInt32(fuz, 8));
        Assert.Equal(lip, fuz[12..17]);
        Assert.Equal(xwm, fuz[17..]);
        var (l, x) = FuzFile.Split(fuz);
        Assert.Equal(lip, l);
        Assert.Equal(xwm, x);
    }
}

public class TrimImportedTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dbvo-trim-" + Guid.NewGuid().ToString("N"));
    public TrimImportedTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void Imported_wav_gets_the_tail_trim_once_and_the_source_stays()
    {
        var voice = new DbvoStudio.Core.Projects.VoicePack();
        var src = Path.Combine(_dir, "raw.wav");
        new WavFile { SampleRate = 44100, Channels = 1, Samples = Enumerable.Repeat((short)3000, 44100).ToArray() }.Write(src);
        var dest = Path.Combine(_dir, "take.wav");
        Assert.True(DbvoStudio.Core.Projects.Phase2Service.TrimImported(src, dest, voice));
        var t = WavFile.Read(dest);
        Assert.Equal(44100 - 662, t.Samples.Length);
        Assert.Equal(0, t.Samples[^1]);
        Assert.Equal(44100, WavFile.Read(src).Samples.Length);            // the source is never changed
        var again = Path.Combine(_dir, "again.wav");
        Assert.False(DbvoStudio.Core.Projects.Phase2Service.TrimImported(dest, again, voice)); // already faded: not trimmed twice
    }
}


public class PauseNoiseReductionTests
{
    [Fact]
    public void Noisy_pause_goes_down_and_the_voice_stays()
    {
        const int sr = 44100;
        var rnd = new Random(1);
        // 0.5 s "voice" (300 Hz at -20 dBFS), 1 s pause with -60 dBFS noise, 0.5 s voice
        var s = new short[sr * 2];
        for (int i = 0; i < s.Length; i++)
        {
            bool voice = i < sr / 2 || i >= sr * 3 / 2;
            double v = voice ? 0.1 * Math.Sin(2 * Math.PI * 300 * i / sr) : 0.001 * (rnd.NextDouble() * 2 - 1);
            s[i] = (short)Math.Round(v * 32767);
        }
        var o = PauseNoiseReduction.Process(s, sr);
        Assert.Equal(s.Length, o.Length);
        static double Rms(short[] x, int a, int b) => Math.Sqrt(x.Skip(a).Take(b - a).Average(v => (double)v * v));
        double before = Rms(s, sr * 3 / 4, sr * 5 / 4), after = Rms(o, sr * 3 / 4, sr * 5 / 4);
        Assert.True(20 * Math.Log10(after / before) < -10);                           // the middle of the pause: down by 10+ dB
        double vb = Rms(s, sr / 10, sr * 4 / 10), va = Rms(o, sr / 10, sr * 4 / 10);
        Assert.InRange(20 * Math.Log10(va / vb), -0.3, 0.3);                          // the voice: unchanged
    }
}

public class NoiseReducedTakeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dbvo-nr-" + Guid.NewGuid().ToString("N"));
    public NoiseReducedTakeTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public async Task On_then_off_brings_the_original_back()
    {
        const int sr = 44100; var rnd = new Random(2);
        var s = new short[sr * 2];
        for (int i = 0; i < s.Length; i++) { bool v = i < sr / 2 || i >= sr * 3 / 2; s[i] = (short)Math.Round((v ? 0.1 * Math.Sin(2 * Math.PI * 300 * i / sr) : 0.001 * (rnd.NextDouble() * 2 - 1)) * 32767); }
        Directory.CreateDirectory(Path.Combine(_dir, "takes"));
        new WavFile { SampleRate = sr, Channels = 1, Samples = s }.Write(Path.Combine(_dir, "takes", "a.wav"));
        var t = new DbvoStudio.Core.Projects.Take { WavPath = Path.Combine("takes", "a.wav") };
        Assert.True(await DbvoStudio.Core.Projects.TakeEffects.RenderAsync(_dir, t, "", "", null, null, default, true));
        Assert.True(t.NoiseReduced);
        Assert.NotEqual(s, WavFile.Read(Path.Combine(_dir, "takes", "a.wav")).Samples);
        Assert.True(await DbvoStudio.Core.Projects.TakeEffects.RenderAsync(_dir, t, "", "", null, null, default, false));
        Assert.False(t.NoiseReduced);
        Assert.Equal(s, WavFile.Read(Path.Combine(_dir, "takes", "a.wav")).Samples);
    }
}

public class TakeEditTests
{
    private static short[] Ramp(int n) => Enumerable.Range(0, n).Select(i => (short)(i % 1000 + 1)).ToArray();

    [Fact]
    public void Delete_cuts_the_part_out_with_a_crossfade()
    {
        var s = Ramp(44100);
        var o = DbvoStudio.Core.Audio.TakeEdit.Apply(s, 44100, 1, new[] { new DbvoStudio.Core.Audio.EditOp { Op = "delete", A = 0.2, B = 0.5 } });
        int xf = (int)Math.Round(DbvoStudio.Core.Audio.TakeEdit.CrossfadeMs / 1000 * 44100);
        Assert.Equal(44100 - (int)Math.Round(0.3 * 44100) - xf, o.Length);
        Assert.Equal(s[100], o[100]);
        Assert.Equal(s[^1], o[^1]); // the end is the original end
    }

    [Fact]
    public void Keep_and_silence_and_gain()
    {
        var s = Enumerable.Repeat((short)1000, 44100).ToArray();
        var k = DbvoStudio.Core.Audio.TakeEdit.Apply(s, 44100, 1, new[] { new DbvoStudio.Core.Audio.EditOp { Op = "keep", A = 0.25, B = 0.75 } });
        Assert.Equal(22050, k.Length);
        Assert.True(k[0] < 1000 && k[k.Length / 2] == 1000);
        var z = DbvoStudio.Core.Audio.TakeEdit.Apply(s, 44100, 1, new[] { new DbvoStudio.Core.Audio.EditOp { Op = "silence", A = 0.4, B = 0.6 } });
        Assert.Equal(s.Length, z.Length);
        Assert.Equal(0, z[22050]);
        Assert.Equal(1000, z[1000]);
        var g = DbvoStudio.Core.Audio.TakeEdit.Apply(s, 44100, 1, new[] { new DbvoStudio.Core.Audio.EditOp { Op = "gain", A = 0, B = 1, Db = 6.0206 } });
        Assert.InRange(g[500], 1995, 2005);
    }

    [Fact]
    public void Steps_apply_in_order_on_stereo()
    {
        var s = new short[44100 * 2];
        for (int i = 0; i < s.Length; i++) s[i] = 500;
        var o = DbvoStudio.Core.Audio.TakeEdit.Apply(s, 44100, 2, new[]
        {
            new DbvoStudio.Core.Audio.EditOp { Op = "delete", A = 0.5, B = 1.0 },
            new DbvoStudio.Core.Audio.EditOp { Op = "fadeOut", A = 0.3, B = 0.5 },
        });
        Assert.Equal(0, o.Length % 2);
        Assert.True(o[^1] < 10 && o[^2] < 10);
        Assert.Equal(500, o[100]);
    }
}
