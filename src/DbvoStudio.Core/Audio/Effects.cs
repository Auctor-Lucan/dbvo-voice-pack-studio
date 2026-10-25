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
    /// <summary>Gain at the very end (dB), e.g. to make up for a quieter filter.</summary>
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

        new() { Id = "radio", Name = "Radio", Group = "Filter", Description = "Narrow band, compressed and a little crunchy.", Filter = "highpass=f=300,lowpass=f=3400,acompressor=threshold=-20dB:ratio=6:attack=5:release=60,acrusher=bits=10:mode=log:aa=1:mix=0.25", GainDb = 2 },
        new() { Id = "telephone", Name = "Telephone", Group = "Filter", Description = "Thin and boxy.", Filter = "highpass=f=400,lowpass=f=3000,acompressor=threshold=-18dB:ratio=4" , GainDb = 2 },
        new() { Id = "muffled", Name = "Muffled", Group = "Filter", Description = "Through a wall, a door or a closed helmet.", Filter = "lowpass=f=900,lowpass=f=1400", GainDb = 1 },
        new() { Id = "underwater", Name = "Underwater", Group = "Filter", Description = "Dull and wobbly.", Filter = "lowpass=f=500,vibrato=f=3:d=0.2,chorus=0.5:0.9:40|55:0.4|0.3:0.25|0.4:2|1.3", GainDb = 2 },
        new() { Id = "custom1", Name = "Custom slot 1", Group = "Filter", Description = "" },
        new() { Id = "custom2", Name = "Custom slot 2", Group = "Filter", Description = "" },
        new() { Id = "custom3", Name = "Custom slot 3", Group = "Filter", Description = "" },

        new() { Id = "telepathy", Name = "Telepathy", Group = "Character", Description = "Doubled, shimmering voice inside the head.", Filter = "chorus=0.6:0.9:50|60:0.4|0.32:0.25|0.4:2|1.3", Reverb = new ReverbSettings { RoomSize = 70, Reverberance = 40, PreDelayMs = 60, WetGainDb = -4, ToneLow = 30, ToneHigh = 50, TailSeconds = 1 } },
        new() { Id = "whisper", Name = "Ghostly whisper", Group = "Character", Description = "Turns the voice into a breathy whisper.", Filter = Whisperize, Reverb = new ReverbSettings { RoomSize = 60, Reverberance = 35, PreDelayMs = 30, WetGainDb = -6, TailSeconds = 0.8 }, GainDb = 4 },
        new() { Id = "ghost", Name = "Ghost", Group = "Character", Description = "Hollow, wavering and far away.", Filter = Pitch(1) + ",flanger=delay=3:depth=4:regen=40:speed=0.4,tremolo=f=5:d=0.25,highpass=f=200", Reverb = new ReverbSettings { RoomSize = 95, Reverberance = 60, PreDelayMs = 40, WetGainDb = -2, TailSeconds = 1.5 } },
        new() { Id = "automaton", Name = "Dwemer automaton", Group = "Character", Description = "Metallic robot voice with a short tinny ring.", Filter = Robot + ",flanger=delay=2:depth=3:regen=60:speed=0.3,highpass=f=150", Reverb = new ReverbSettings { RoomSize = 20, Reverberance = 30, PreDelayMs = 5, WetGainDb = -6, ToneHigh = 80, TailSeconds = 0.4 } },
        new() { Id = "daedra", Name = "Daedric Prince", Group = "Character", Description = "Huge, layered and echoing: a voice from Oblivion.", Filter = Layered(-7, 0.7) + ",chorus=0.5:0.9:40|60:0.3|0.25:0.25|0.4:2|1.3", Reverb = new ReverbSettings { RoomSize = 100, Reverberance = 65, PreDelayMs = 50, WetGainDb = -3, TailSeconds = 2 } },
    };

    /// <summary>Effects 8.3.0 had that were taken out (lines that have one keep it until it is taken off).</summary>
    public static readonly HashSet<string> Retired = new() { "robot", "demon", "dragon", "deep", "high" };

    public static EffectPreset? Find(IEnumerable<EffectPreset>? list, string id) =>
        (list ?? Enumerable.Empty<EffectPreset>()).FirstOrDefault(p => p.Id == id) ?? BuiltIn().FirstOrDefault(p => p.Id == id);

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
            try { w = WavFile.Read(src); plain = true; } catch (InvalidDataException) { w = null!; }
            int rate = plain ? w.SampleRate : Grunts.GruntService.ProbeWavHeader(src)?.SampleRate ?? 44100;
            var chain = new List<string>();
            if (fx.Filter.Trim().Length > 0) chain.Add(fx.Filter.Replace("{rate}", rate.ToString(CultureInfo.InvariantCulture)));
            if (fx.Reverb == null && Math.Abs(fx.GainDb) > 0.01) chain.Add($"volume={fx.GainDb.ToString("0.##", CultureInfo.InvariantCulture)}dB");
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
            if (fx.Reverb != null)
            {
                w = new WavFile { SampleRate = w.SampleRate, Channels = w.Channels, Samples = Audio.Reverb.Apply(w.Samples, w.SampleRate, w.Channels, fx.Reverb) };
                if (Math.Abs(fx.GainDb) > 0.01)
                {
                    var g = Math.Pow(10, fx.GainDb / 20);
                    var s = w.Samples;
                    for (int i = 0; i < s.Length; i++) s[i] = (short)Math.Clamp(Math.Round(s[i] * g), short.MinValue, short.MaxValue);
                }
            }
            var outTmp = dest + ".fx-out.wav";
            w.Write(outTmp);
            File.Move(outTmp, dest, true);
        }
        finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch { } }
    }
}
