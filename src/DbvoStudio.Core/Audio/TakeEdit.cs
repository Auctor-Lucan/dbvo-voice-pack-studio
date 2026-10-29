namespace DbvoStudio.Core.Audio;

/// <summary>One step of Edit take, in seconds of the audio as it is at that step.</summary>
public sealed class EditOp
{
    /// <summary>delete | keep | silence | fadeIn | fadeOut | gain</summary>
    public string Op { get; set; } = "";
    public double A { get; set; }
    public double B { get; set; }
    /// <summary>gain only.</summary>
    public double Db { get; set; }
}

/// <summary>
/// Edit take: cuts, silences, fades and gain on a take's samples. Cuts get a short crossfade so the join doesn't click.
/// The page previews the same steps (edit.js), so what is heard there is what is saved.
/// </summary>
public static class TakeEdit
{
    public const double CrossfadeMs = 6;

    public static short[] Apply(short[] s, int rate, int channels, IEnumerable<EditOp> ops, double crossfadeMs = CrossfadeMs)
    {
        channels = Math.Max(1, channels);
        foreach (var op in ops) s = One(s, rate, channels, op, crossfadeMs);
        return s;
    }

    private static short[] One(short[] s, int rate, int ch, EditOp op, double xfMs)
    {
        int frames = s.Length / ch;
        int a = Math.Clamp((int)Math.Round(Math.Min(op.A, op.B) * rate), 0, frames);
        int b = Math.Clamp((int)Math.Round(Math.Max(op.A, op.B) * rate), 0, frames);
        int xf = Math.Max(0, (int)Math.Round(xfMs / 1000 * rate));
        switch (op.Op)
        {
            case "delete":
            {
                if (b <= a) return s;
                int n = Math.Min(xf, Math.Min(a, frames - b));
                int outFrames = frames - (b - a) - n;
                var o = new short[outFrames * ch];
                Array.Copy(s, 0, o, 0, (a - n) * ch);
                for (int i = 0; i < n; i++)
                {
                    double g = (i + 0.5) / n; // 0..1: from the part before the cut to the part after it
                    for (int c = 0; c < ch; c++)
                        o[(a - n + i) * ch + c] = Clip(s[(a - n + i) * ch + c] * (1 - g) + s[(b + i) * ch + c] * g);
                }
                Array.Copy(s, (b + n) * ch, o, a * ch, (frames - b - n) * ch);
                return o;
            }
            case "keep":
            {
                if (b <= a) return s;
                var o = new short[(b - a) * ch];
                Array.Copy(s, a * ch, o, 0, o.Length);
                int len = b - a;
                int n = Math.Min(xf, len / 2);
                for (int i = 0; i < n; i++)
                {
                    double g = (i + 0.5) / n;
                    for (int c = 0; c < ch; c++)
                    {
                        if (a > 0) o[i * ch + c] = Clip(o[i * ch + c] * g);
                        if (b < frames) o[(len - 1 - i) * ch + c] = Clip(o[(len - 1 - i) * ch + c] * g);
                    }
                }
                return o;
            }
            case "silence":
            {
                var o = (short[])s.Clone();
                int n = Math.Min(xf, (b - a) / 2);
                for (int f = a; f < b; f++)
                {
                    double g = 0;
                    if (f - a < n && a > 0) g = 1 - (f - a + 0.5) / n;
                    else if (b - 1 - f < n && b < frames) g = 1 - (b - 1 - f + 0.5) / n;
                    for (int c = 0; c < ch; c++) o[f * ch + c] = Clip(s[f * ch + c] * g);
                }
                return o;
            }
            case "fadeIn":
            case "fadeOut":
            case "gain":
            {
                var o = (short[])s.Clone();
                double lin = Math.Pow(10, op.Db / 20);
                int len = Math.Max(1, b - a);
                for (int f = a; f < b; f++)
                {
                    double p = (f - a + 0.5) / len;
                    double g = op.Op == "fadeIn" ? p : op.Op == "fadeOut" ? 1 - p : lin;
                    for (int c = 0; c < ch; c++) o[f * ch + c] = Clip(s[f * ch + c] * g);
                }
                return o;
            }
            default: throw new InvalidOperationException("Unknown edit: " + op.Op);
        }
    }

    private static short Clip(double v) => (short)Math.Clamp(Math.Round(v), short.MinValue, short.MaxValue);
}
