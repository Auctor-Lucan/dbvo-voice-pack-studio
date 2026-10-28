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

public class TurboDegrainTests
{
    [Fact]
    public void Only_v4_turbo_and_low_band_untouched()
    {
        Assert.True(TurboDegrain.Applies("eleven_v4_turbo"));
        Assert.False(TurboDegrain.Applies("eleven_v4"));
        Assert.False(TurboDegrain.Applies("eleven_turbo_v2_5"));
        // a 500 Hz tone passes through unchanged (only 3.5-10 kHz between harmonics is touched; the lift starts at 2.5 kHz)
        var s = Enumerable.Range(0, 44100).Select(i => (short)(8000 * Math.Sin(2 * Math.PI * 500 * i / 44100.0))).ToArray();
        var o = TurboDegrain.Process(s, 44100);
        Assert.Equal(s.Length, o.Length);
        Assert.True(s.Skip(4096).Take(30000).Zip(o.Skip(4096).Take(30000)).All(p => Math.Abs(p.First - p.Second) <= 40));
    }
}
