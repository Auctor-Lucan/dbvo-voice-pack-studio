using System.Globalization;
using DbvoStudio.Core.Tools;

namespace DbvoStudio.Core.Audio;

/// <summary>
/// A voice effect: an ffmpeg filter chain (filters, pitch, echo, robot voice…) followed by an optional reverb (Audacity's
/// Reverb, <see cref="Reverb"/>). "{rate}" in the chain is replaced with the file's sample rate.
/// </summary>
public sealed class EffectPreset
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Reverb | Filter | Character</summary>
    public string Group { get; set; } = "Reverb";
    public string Description { get; set; } = "";
    /// <summary>ffmpeg -af chain applied first (empty: none).</summary>
    public string Filter { get; set; } = "";
    /// <summary>Reverb applied after the filter (null: none).</summary>
    public ReverbSettings? Reverb { get; set; }
    /// <summary>Gain at the very end (dB) on top of the level matching (every effect comes out as loud as the line went in), e.g. -4 for Distant.</summary>
    public double GainDb { get; set; }

    public EffectPreset Clone() => new()
    {
        Id = Id, Name = Name, Group = Group, Description = Description, Filter = Filter, GainDb = GainDb,
        Reverb = Reverb == null ? null : new ReverbSettings
        {
            PreDelayMs = Reverb.PreDelayMs, DryGainDb = Reverb.DryGainDb, HfDamping = Reverb.HfDamping, Reverberance = Reverb.Reverberance, RoomSize = Reverb.RoomSize,
            StereoWidth = Reverb.StereoWidth, ToneHigh = Reverb.ToneHigh, ToneLow = Reverb.ToneLow, WetGainDb = Reverb.WetGainDb, WetOnly = Reverb.WetOnly, TailSeconds = Reverb.TailSeconds,
        },
    };

    /// <summary>A custom slot with nothing in it yet (no filter, no reverb): shown, but it can't be applied.</summary>
    public bool IsEmpty => Filter.Trim().Length == 0 && Reverb == null && Math.Abs(GainDb) < 0.01;

    /// <summary>A short label for buttons: the name without anything in brackets.</summary>
    public string Short => Name.Split(" (")[0];
}

public static class Effects
{
    private const string Robot = "afftfilt=real='hypot(re,im)*sin(0)':imag='hypot(re,im)*cos(0)':win_size=512:overlap=0.75";
    private const string Whisperize = "afftfilt=real='hypot(re,im)*cos((random(0)*2-1)*2*3.14)':imag='hypot(re,im)*sin((random(1)*2-1)*2*3.14)':win_size=128:overlap=0.8";
    private static string Pitch(double semitones)
    {
        var f = Math.Pow(2, semitones / 12);
        var inv = 1 / f;
        // atempo takes 0.5–2 per stage (more stages for bigger shifts).
        var tempo = new List<string>();
        while (inv > 2) { tempo.Add("atempo=2"); inv /= 2; }
        while (inv < 0.5) { tempo.Add("atempo=0.5"); inv /= 0.5; }
        tempo.Add("atempo=" + inv.ToString("0.######", CultureInfo.InvariantCulture));
        return $"asetrate={{rate}}*{f.ToString("0.######", CultureInfo.InvariantCulture)},aresample={{rate}},{string.Join(",", tempo)}";
    }
    private static string Layered(double semitones, double lowWeight) =>
        $"asplit[a][b];[b]{Pitch(semitones)}[lo];[a][lo]amix=inputs=2:weights=1 {lowWeight.ToString("0.##", CultureInfo.InvariantCulture)}:normalize=0";

    /// <summary>The built-in effects, in menu order. "inner" is the Player Grunts reverb toggle's default (the user's Audacity macro).</summary>
    public static List<EffectPreset> BuiltIn() => new()
    {
        new() { Id = "inner", Name = "Inner thoughts", Group = "Reverb", Description = "Soft, dark room reverb with a 1 s tail (the Audacity macro: room 81, reverberance 23, delay 99 ms, wet -4 dB). Thoughts and narration.", Reverb = new ReverbSettings() },
        new() { Id = "room", Name = "Small room", Group = "Reverb", Description = "A short, close room.", Reverb = new ReverbSettings { RoomSize = 30, Reverberance = 30, PreDelayMs = 8, HfDamping = 50, WetGainDb = -8, ToneLow = 30, ToneHigh = 60, TailSeconds = 0.5 } },
        new() { Id = "hall", Name = "Hall", Group = "Reverb", Description = "A large stone hall (a keep, a temple).", Reverb = new ReverbSettings { RoomSize = 95, Reverberance = 55, PreDelayMs = 45, HfDamping = 50, WetGainDb = -5, ToneLow = 10, ToneHigh = 70, TailSeconds = 1.5 } },
        new() { Id = "cave", Name = "Cave", Group = "Reverb", Description = "A long, boomy cave or crypt.", Reverb = new ReverbSettings { RoomSize = 100, Reverberance = 75, PreDelayMs = 30, HfDamping = 40, WetGainDb = -3, ToneLow = 20, ToneHigh = 60, TailSeconds = 2 } },
        new() { Id = "distant", Name = "Distant", Group = "Reverb", Description = "Someone far away: thinner, quieter and mostly room.", Filter = "highpass=f=250,lowpass=f=3000", Reverb = new ReverbSettings { RoomSize = 80, Reverberance = 40, PreDelayMs = 60, HfDamping = 70, DryGainDb = -6, WetGainDb = 0, ToneLow = 20, ToneHigh = 40, TailSeconds = 1.2 }, GainDb = -4 },
        new() { Id = "echo", Name = "Echo", Group = "Reverb", Description = "Two clear echoes off far walls (a shout across a valley).", Filter = "apad=pad_dur=0.8,aecho=0.8:0.6:250|500:0.35|0.2" },

        new() { Id = "radio", Name = "Radio", Group = "Filter", Description = "Narrow band, compressed and a little crunchy.", Filter = "highpass=f=300,lowpass=f=3400,acompressor=threshold=-20dB:ratio=6:attack=5:release=60,acrusher=bits=10:mode=log:aa=1:mix=0.25" },
        new() { Id = "telephone", Name = "Telephone", Group = "Filter", Description = "Thin and boxy.", Filter = "highpass=f=400,lowpass=f=3000,acompressor=threshold=-18dB:ratio=4"  },
        new() { Id = "muffled", Name = "Muffled", Group = "Filter", Description = "Through a wall, a door or a closed helmet.", Filter = "lowpass=f=900,lowpass=f=1400" },
        new() { Id = "underwater", Name = "Underwater", Group = "Filter", Description = "Dull and wobbly.", Filter = "lowpass=f=500,vibrato=f=3:d=0.2,chorus=0.5:0.9:40|55:0.4|0.3:0.25|0.4:2|1.3" },
        new() { Id = "custom1", Name = "Custom slot 1", Group = "Filter", Description = "" },
        new() { Id = "custom2", Name = "Custom slot 2", Group = "Filter", Description = "" },
        new() { Id = "custom3", Name = "Custom slot 3", Group = "Filter", Description = "" },

        new() { Id = "telepathy", Name = "Telepathy", Group = "Character", Description = "Doubled, shimmering voice inside the head.", Filter = "chorus=0.6:0.9:50|60:0.4|0.32:0.25|0.4:2|1.3", Reverb = new ReverbSettings { RoomSize = 70, Reverberance = 40, PreDelayMs = 60, WetGainDb = -4, ToneLow = 30, ToneHigh = 50, TailSeconds = 1 } },
        new() { Id = "whisper", Name = "Ghostly whisper", Group = "Character", Description = "Turns the voice into a breathy whisper.", Filter = Whisperize, Reverb = new ReverbSettings { RoomSize = 60, Reverberance = 35, PreDelayMs = 30, WetGainDb = -6, TailSeconds = 0.8 } },
        new() { Id = "ghost", Name = "Ghost", Group = "Character", Description = "Hollow, wavering and far away.", Filter = Pitch(1) + ",flanger=delay=3:depth=4:regen=40:speed=0.4,tremolo=f=5:d=0.25,highpass=f=200", Reverb = new ReverbSettings { RoomSize = 95, Reverberance = 60, PreDelayMs = 40, WetGainDb = -2, TailSeconds = 1.5 } },
        new() { Id = "automaton", Name = "Dwemer automaton", Group = "Character", Description = "Metallic robot voice with a short tinny ring.", Filter = Robot + ",flanger=delay=2:depth=3:regen=60:speed=0.3,highpass=f=150,acompressor=threshold=-26dB:ratio=8:attack=1:release=60:makeup=6", Reverb = new ReverbSettings { RoomSize = 20, Reverberance = 30, PreDelayMs = 5, WetGainDb = -6, ToneHigh = 80, TailSeconds = 0.4 } },
        new() { Id = "daedra", Name = "Daedric Prince", Group = "Character", Description = "Huge, layered and echoing: a voice from Oblivion.", Filter = Layered(-7, 0.7) + ",chorus=0.5:0.9:40|60:0.3|0.25:0.25|0.4:2|1.3", Reverb = new ReverbSettings { RoomSize = 100, Reverberance = 65, PreDelayMs = 50, WetGainDb = -3, TailSeconds = 2 } },
    };

    /// <summary>Effects 8.3.0 had that were taken out (lines that have one keep it until it is taken off).</summary>
    public static readonly HashSet<string> Retired = new() { "robot", "demon", "dragon", "deep", "high" };

    public static EffectPreset? Find(IEnumerable<EffectPreset>? list, string id) =>
        (list ?? Enumerable.Empty<EffectPreset>()).FirstOrDefault(p => p.Id == id) ?? BuiltIn().FirstOrDefault(p => p.Id == id);

    /// <summary>Loudness of speech: the mean power of the 20 ms blocks that aren't silence (more than 30 dB under the loudest block).</summary>
    public static double ActivePower(short[] s, int perSecond)
    {
        int block = Math.Max(1, perSecond / 50);
        var p = new List<double>();
        for (int i = 0; i < s.Length; i += block)
        {
            double sum = 0; int n = Math.Min(block, s.Length - i);
            for (int k = 0; k < n; k++) { double x = s[i + k] / 32768.0; sum += x * x; }
            p.Add(sum / Math.Max(1, n));
        }
        if (p.Count == 0) return 0;
        double gate = p.Max() / 1000; // -30 dB
        var on = p.Where(x => x >= gate).ToList();
        return on.Count == 0 ? 0 : on.Average();
    }

    /// <summary>Gain so the output's speech level matches the dry line, plus extraDb; peaks above -1 dBFS are rounded off softly.</summary>
    private static void Level(short[] o, short[]? dry, int perSecond, double extraDb)
    {
        double g = Math.Pow(10, Math.Clamp(extraDb, -24, 18) / 20);
        if (dry != null)
        {
            double a = ActivePower(dry, perSecond), b = ActivePower(o, perSecond);
            if (a > 1e-9 && b > 1e-9) g *= Math.Clamp(Math.Sqrt(a / b), 0.125, 8); // at most ±18 dB
        }
        if (Math.Abs(g - 1) < 0.005) return;
        const double t = 0.89; // -1 dBFS
        for (int i = 0; i < o.Length; i++)
        {
            double x = o[i] / 32768.0 * g, ax = Math.Abs(x);
            if (ax > t) x = Math.Sign(x) * (t + (1 - t) * Math.Tanh((ax - t) / (1 - t)));
            o[i] = (short)Math.Clamp(Math.Round(x * 32767), short.MinValue, short.MaxValue);
        }
    }

    /// <summary>
    /// Writes <paramref name="dest"/> as <paramref name="src"/> with the effect (16-bit PCM at the source's rate and channels).
    /// ffmpeg is needed for filter chains and for WAVs that aren't 16-bit; a reverb on a 16-bit WAV works without it.
    /// </summary>
    public static async Task ApplyAsync(string src, string dest, EffectPreset fx, ExternalTools? tools, CancellationToken ct = default)
    {
        if (fx.IsEmpty) throw new InvalidOperationException($"{fx.Name} is empty: give it a filter or a reverb first (Setup & export → Effects).");
        var tmp = dest + ".fx-" + Guid.NewGuid().ToString("N")[..6] + ".wav";
        try
        {
            WavFile w;
            bool plain = false;
            short[]? dry = null;
            try { w = WavFile.Read(src); plain = true; dry = (short[])w.Samples.Clone(); } catch (InvalidDataException) { w = null!; }
            int rate = plain ? w.SampleRate : Grunts.GruntService.ProbeWavHeader(src)?.SampleRate ?? 44100;
            var chain = new List<string>();
            if (fx.Filter.Trim().Length > 0) chain.Add(fx.Filter.Replace("{rate}", rate.ToString(CultureInfo.InvariantCulture)));
            if (chain.Count > 0 || !plain)
            {
                if (tools == null || !File.Exists(tools.FfmpegPath)) throw new InvalidOperationException("This effect needs ffmpeg (Global Settings → General & tools → Tools).");
                var args = new List<string> { "-y", "-hide_banner", "-loglevel", "error", "-i", src };
                if (chain.Count > 0) { args.Add("-af"); args.Add(string.Join(",", chain)); }
                args.AddRange(new[] { "-ar", rate.ToString(CultureInfo.InvariantCulture), "-acodec", "pcm_s16le", "-map_metadata", "-1", tmp });
                var r = await ExternalTools.RunAsync(tools.FfmpegPath, args, ct);
                if (r.ExitCode != 0 || !File.Exists(tmp)) throw new ToolException("ffmpeg couldn't apply the effect: " + r.StdErr.Trim().Split('\n').LastOrDefault());
                w = WavFile.Read(tmp);
            }
            if (dry == null && plain) dry = WavFile.Read(src).Samples;
            if (fx.Reverb != null)
                w = new WavFile { SampleRate = w.SampleRate, Channels = w.Channels, Samples = Audio.Reverb.Apply(w.Samples, w.SampleRate, w.Channels, fx.Reverb) };
            if (dry == null && tools != null && File.Exists(tools.FfmpegPath))
            {
                // a WAV that isn't 16-bit: its dry level from a plain conversion
                var dt = tmp + ".dry.wav";
                var r = await ExternalTools.RunAsync(tools.FfmpegPath, new[] { "-y", "-hide_banner", "-loglevel", "error", "-i", src, "-ar", w.SampleRate.ToString(CultureInfo.InvariantCulture), "-acodec", "pcm_s16le", dt }, ct);
                if (r.ExitCode == 0 && File.Exists(dt)) { dry = WavFile.Read(dt).Samples; File.Delete(dt); }
            }
            // Every effect comes out as loud as the line went in (filters and robot voices lose a lot), then the preset's own gain.
            // Loudness is measured the way the export measures it (EBU R128, ffmpeg); very short lines fall back to speech power.
            var outTmp = dest + ".fx-out.wav";
            double? match = null;
            if (tools != null && File.Exists(tools.FfmpegPath))
            {
                w.Write(outTmp);
                var dryFile = src;
                if (!plain && dry != null) { dryFile = tmp + ".dry2.wav"; new WavFile { SampleRate = w.SampleRate, Channels = w.Channels, Samples = dry }.Write(dryFile); }
                try
                {
                    var (a, _) = await tools.MeasureAsync(dryFile, ct);
                    var (b, _) = await tools.MeasureAsync(outTmp, ct);
                    if (a is { } la && b is { } lb && la > -60 && lb > -60 && !double.IsInfinity(la) && !double.IsInfinity(lb)) match = la - lb;
                }
                catch (OperationCanceledException) { throw; }
                catch { /* speech power instead */ }
                finally { if (dryFile != src) try { File.Delete(dryFile); } catch { } }
            }
            Level(w.Samples, match == null ? dry : null, w.SampleRate * Math.Max(1, w.Channels), fx.GainDb + (match ?? 0));
            w.Write(outTmp);
            File.Move(outTmp, dest, true);
        }
        finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch { } }
    }
}
