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
