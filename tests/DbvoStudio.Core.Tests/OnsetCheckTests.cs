using DbvoStudio.Core.Audio;
using Xunit;

namespace DbvoStudio.Core.Tests;

public class OnsetCheckTests
{
    private const int Sr = 44100;

    private static void Tone(List<short> s, double ms, double hz, double db)
    {
        double amp = Math.Pow(10, db / 20) * 32767;
        int n = (int)(Sr * ms / 1000);
        for (int i = 0; i < n; i++) s.Add((short)(amp * Math.Sin(2 * Math.PI * hz * i / Sr)));
    }
    private static void Silence(List<short> s, double ms) => s.AddRange(new short[(int)(Sr * ms / 1000)]);
    private static WavFile Wav(List<short> s) => new() { SampleRate = Sr, Channels = 1, Samples = s.ToArray() };

    [Fact]
    public void A_loud_blip_then_silence_before_the_line_is_flagged()
    {
        var s = new List<short>();
        Tone(s, 75, 165, -8);      // the stray burst
        Silence(s, 200);
        Tone(s, 900, 90, -15);     // the line
        var r = OnsetCheck.Analyze(Wav(s));
        Assert.True(r.Flagged);
        Assert.InRange(r.BlipMs, 60, 90);
        Assert.InRange(r.GapMs, 150, 260);
    }

    [Fact]
    public void A_line_that_starts_at_once_is_not_flagged()
    {
        var s = new List<short>();
        Tone(s, 10, 3000, -6);     // a hard consonant
        Tone(s, 900, 90, -12);     // straight into the vowel
        Assert.False(OnsetCheck.Analyze(Wav(s)).Flagged);
    }

    [Fact]
    public void A_quiet_first_word_with_a_pause_is_only_flagged_when_strict()
    {
        var s = new List<short>();
        Silence(s, 20);
        Tone(s, 120, 110, -20);    // "Ah..."
        Silence(s, 90);
        Tone(s, 900, 110, -12);
        Assert.False(OnsetCheck.Analyze(Wav(s)).Flagged);
        Assert.False(OnsetCheck.Analyze(Wav(s), "relaxed").Flagged);
    }

    [Fact]
    public void A_loud_short_first_word_at_the_voice_pitch_is_not_flagged()
    {
        var s = new List<short>();
        Silence(s, 30);
        Tone(s, 90, 120, -6);      // "What…"
        Silence(s, 140);
        Tone(s, 900, 115, -12);    // "…brings you here"
        Assert.False(OnsetCheck.Analyze(Wav(s)).Flagged);
        Assert.False(OnsetCheck.Analyze(Wav(s), "strict").Flagged);
    }

    /// <summary>Real takes with the burst, when DBVO_ONSET_SAMPLES points at a folder of them (not shipped in the repo).</summary>
    [Fact]
    public void Real_samples_are_flagged_when_available()
    {
        var dir = Environment.GetEnvironmentVariable("DBVO_ONSET_SAMPLES");
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
        foreach (var f in Directory.GetFiles(dir, "*.wav"))
        {
            var r = OnsetCheck.Analyze(WavFile.Read(f));
            Assert.True(r.Flagged, $"{Path.GetFileName(f)}: {r}");
        }
    }
}
