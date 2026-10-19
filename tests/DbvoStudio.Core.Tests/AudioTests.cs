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
