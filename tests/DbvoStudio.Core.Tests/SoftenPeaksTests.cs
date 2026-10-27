using DbvoStudio.Core.Audio;
using DbvoStudio.Core.Projects;
using Xunit;

namespace DbvoStudio.Core.Tests;

public class SoftenPeaksTests
{
    /// A voice-like tone with short sharp spikes (hard consonants) far above it.
    private static short[] Spiky(int rate, out int[] spikes)
    {
        var s = new short[rate * 2];
        for (int i = 0; i < s.Length; i++) s[i] = (short)(3000 * Math.Sin(2 * Math.PI * 220 * i / rate) * (0.6 + 0.4 * Math.Sin(2 * Math.PI * 3 * i / rate)));
        spikes = new[] { rate / 4, rate / 2 + 1234, rate + 777, rate * 3 / 2 };
        foreach (var at in spikes)
            for (int k = 0; k < rate / 1000; k++) s[at + k] = (short)((k % 2 == 0 ? 1 : -1) * 26000);
        return s;
    }

    [Fact]
    public void Standard_limits_peaks_to_about_8_dB_above_the_speech()
    {
        const int rate = 44100;
        var s = Spiky(rate, out _);
        var before = PeakSoftener.PeakAboveSpeechDb(s, rate, 1)!.Value;
        var o = PeakSoftener.Process(s, rate, 1, PeakSoftener.Standard);
        var after = PeakSoftener.PeakAboveSpeechDb(o, rate, 1)!.Value;
        Assert.True(before > 15, $"before {before:0.0}");
        Assert.InRange(after, 6, 8.6);
        Assert.Equal(s.Length, o.Length);
        // away from the spikes the voice is untouched
        Assert.Equal(s[rate / 8], o[rate / 8]);
    }

    [Fact]
    public void Strong_goes_further_than_standard()
    {
        const int rate = 44100;
        var s = Spiky(rate, out _);
        var std = PeakSoftener.PeakAboveSpeechDb(PeakSoftener.Process(s, rate, 1, PeakSoftener.Standard), rate, 1)!.Value;
        var strong = PeakSoftener.PeakAboveSpeechDb(PeakSoftener.Process(s, rate, 1, PeakSoftener.Strong), rate, 1)!.Value;
        Assert.True(strong < std - 1, $"standard {std:0.0}, strong {strong:0.0}");
    }

    [Fact]
    public async Task Take_soften_is_undoable_and_works_with_an_effect()
    {
        var root = Path.Combine(Path.GetTempPath(), "dbvo-sp-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(root, "takes"));
        try
        {
            var s = Spiky(44100, out _);
            var wavPath = Path.Combine(root, "takes", "a.wav");
            new WavFile { SampleRate = 44100, Channels = 1, Samples = s }.Write(wavPath);
            var original = File.ReadAllBytes(wavPath);
            var t = new Take { WavPath = Path.Combine("takes", "a.wav") };
            var line = new Phase2Line(); line.Takes.Add(t);

            Assert.Equal(1, await TakeEffects.SetAsync(root, line, null, null, null, default, PeakSoftener.Standard));
            Assert.Equal(PeakSoftener.Standard, t.Soften);
            Assert.Equal(PeakSoftener.Standard, line.Soften);
            Assert.NotEqual(original, File.ReadAllBytes(wavPath));
            Assert.True(File.Exists(TakeEffects.DryPath(root, t)));

            // an effect on top keeps Soften peaks (both from the dry original)
            Assert.Equal(1, await TakeEffects.SetAsync(root, line, "inner", null, null));
            Assert.Equal("inner", t.Effect);
            Assert.Equal(PeakSoftener.Standard, t.Soften);

            // both off: the original comes back exactly
            await TakeEffects.SetAsync(root, line, "", null, null, default, "");
            Assert.Equal("", t.Effect);
            Assert.Equal("", t.Soften);
            Assert.Equal(original, File.ReadAllBytes(wavPath));
            Assert.False(File.Exists(TakeEffects.DryPath(root, t)));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }
}
