namespace DbvoStudio.Core.Audio;

/// <summary>
/// Audacity's Reverb effect (its port of the libSoX/Freeverb reverb), so a line sounds as it would after the user's
/// Audacity macro. The defaults are the "inner thoughts / narration" preset: Delay 99 ms, Dry 0 dB, HF damping 62,
/// Reverberance 23, Room size 81, Stereo width 100, Tone high 19, Tone low 16, Wet -4 dB.
/// </summary>
public sealed class ReverbSettings
{
    public double PreDelayMs { get; set; } = 99;
    public double DryGainDb { get; set; }
    public double HfDamping { get; set; } = 62;
    public double Reverberance { get; set; } = 23;
    public double RoomSize { get; set; } = 81;
    public double StereoWidth { get; set; } = 100;
    public double ToneHigh { get; set; } = 19;
    public double ToneLow { get; set; } = 16;
    public double WetGainDb { get; set; } = -4;
    public bool WetOnly { get; set; }
    /// <summary>Silence added at the end first (the macro pastes one second, or the whole line when it is shorter), so the tail rings out.</summary>
    public double TailSeconds { get; set; } = 1;
}

public static class Reverb
{
    private static readonly int[] CombLengths = { 1116, 1188, 1277, 1356, 1422, 1491, 1557, 1617 };
    private static readonly int[] AllpassLengths = { 225, 341, 441, 556 };
    private const int StereoAdjust = 12;

    private sealed class Filter
    {
        public readonly float[] Buf;
        public int Pos;
        public float Store;
        public Filter(int size) => Buf = new float[Math.Max(1, size)];
    }

    private sealed class OnePole
    {
        public double A0, A1, B1, I1, O1;
        public static OnePole Low(double fc, double rate) { var b1 = -Math.Exp(-2 * Math.PI * fc / rate); return new OnePole { B1 = b1, A0 = 1 + b1 }; }
        public static OnePole High(double fc, double rate) { var b1 = -Math.Exp(-2 * Math.PI * fc / rate); var a0 = (1 - b1) / 2; return new OnePole { B1 = b1, A0 = a0, A1 = -a0 }; }
        public double Process(double x) { O1 = x * A0 + I1 * A1 - O1 * B1; I1 = x; return O1; }
    }

    private sealed class FilterArray
    {
        public readonly Filter[] Comb = new Filter[8];
        public readonly Filter[] Allpass = new Filter[4];
        public readonly OnePole Hp, Lp;
        public FilterArray(double rate, double scale, double offset, double fcHigh, double fcLow)
        {
            double r = rate / 44100.0;
            for (int i = 0; i < 8; i++, offset = -offset) Comb[i] = new Filter((int)(scale * r * (CombLengths[i] + StereoAdjust * offset) + .5));
            for (int i = 0; i < 4; i++, offset = -offset) Allpass[i] = new Filter((int)(r * (AllpassLengths[i] + StereoAdjust * offset) + .5));
            Hp = OnePole.High(fcHigh, rate);
            Lp = OnePole.Low(fcLow, rate);
        }

        public float Process(float input, float feedback, float hfDamping, float gain)
        {
            float o = 0;
            for (int i = 7; i >= 0; i--)
            {
                var f = Comb[i];
                float output = f.Buf[f.Pos];
                f.Store = output + (f.Store - output) * hfDamping;
                f.Buf[f.Pos] = input + f.Store * feedback;
                if (++f.Pos >= f.Buf.Length) f.Pos = 0;
                o += output;
            }
            for (int i = 3; i >= 0; i--)
            {
                var f = Allpass[i];
                float output = f.Buf[f.Pos];
                f.Buf[f.Pos] = o + output * .5f;
                if (++f.Pos >= f.Buf.Length) f.Pos = 0;
                o = output - o;
            }
            double y = Lp.Process(Hp.Process(o));
            return (float)y * gain;
        }
    }

    private static double MidiToFreq(double n) => 440 * Math.Pow(2, (n - 69) / 12);

    /// <summary>Applies the reverb to 16-bit interleaved samples; returns the new samples (longer by the tail).</summary>
    public static short[] Apply(short[] samples, int rate, int channels, ReverbSettings s)
    {
        channels = Math.Max(1, channels);
        int frames = samples.Length / channels;
        int tail = (int)Math.Min(frames, Math.Round(s.TailSeconds * rate));
        int total = frames + tail;
        int delay = (int)(s.PreDelayMs / 1000 * rate + .5);
        double scale = s.RoomSize / 100 * .9 + .1;
        double depth = channels == 2 ? s.StereoWidth / 100 : 0;
        double a = -1 / Math.Log(1 - .3);
        double b = 100 / (Math.Log(1 - .98) * a + 1);
        float feedback = (float)(1 - Math.Exp((s.Reverberance - b) / (a * b)));
        float hf = (float)(s.HfDamping / 100 * .3 + .2);
        float gain = (float)(Math.Pow(10, s.WetGainDb / 20) * .015);
        double dry = s.WetOnly ? 0 : Math.Pow(10, s.DryGainDb / 20);
        double fcHigh = MidiToFreq(72 - s.ToneLow / 100 * 48), fcLow = MidiToFreq(72 + s.ToneHigh / 100 * 48);
        int outs = channels == 2 ? 2 : 1;

        // wet[c][w][i]: input channel c's reverb, output w
        var wet = new float[channels][][];
        for (int c = 0; c < channels; c++)
        {
            wet[c] = new float[outs][];
            for (int w = 0; w < outs; w++)
            {
                var fa = new FilterArray(rate, scale, w * depth, fcHigh, fcLow);
                var o = wet[c][w] = new float[total];
                for (int i = 0; i < total; i++)
                {
                    int src = i - delay;
                    float x = src >= 0 && src < frames ? samples[src * channels + c] / 32768f : 0f;
                    o[i] = fa.Process(x, feedback, hf, gain);
                }
            }
        }
        var result = new short[total * channels];
        for (int i = 0; i < total; i++)
            for (int c = 0; c < channels; c++)
            {
                double d = i < frames ? samples[i * channels + c] / 32768.0 : 0;
                double w = channels == 2 ? 0.5 * (wet[0][c][i] + wet[1][c][i]) : wet[c][0][i];
                double y = (dry * d + w) * 32768.0;
                result[i * channels + c] = (short)Math.Clamp(Math.Round(y), short.MinValue, short.MaxValue);
            }
        return result;
    }
}
