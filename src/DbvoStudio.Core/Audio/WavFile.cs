namespace DbvoStudio.Core.Audio;

/// <summary>16-bit PCM WAV read/write plus the tail trim and fade applied to every generated line.</summary>
public sealed class WavFile
{
    public int SampleRate { get; init; }
    public int Channels { get; init; }
    /// <summary>Interleaved 16-bit samples.</summary>
    public short[] Samples { get; set; } = Array.Empty<short>();

    public double DurationSeconds => Samples.Length / (double)(SampleRate * Channels);

    public static WavFile Read(byte[] b)
    {
        if (b.Length < 12 || b[0] != 'R' || b[1] != 'I' || b[2] != 'F' || b[3] != 'F' || b[8] != 'W' || b[9] != 'A')
            throw new InvalidDataException("Not a WAV file.");
        int o = 12, rate = 0, ch = 0, bits = 0, fmt = 0;
        short[]? samples = null;
        while (o + 8 <= b.Length)
        {
            string id = System.Text.Encoding.ASCII.GetString(b, o, 4);
            int size = BitConverter.ToInt32(b, o + 4);
            int start = o + 8;
            if (size < 0 || start + size > b.Length) size = b.Length - start;
            if (id == "fmt ")
            {
                fmt = BitConverter.ToUInt16(b, start);
                ch = BitConverter.ToUInt16(b, start + 2);
                rate = BitConverter.ToInt32(b, start + 4);
                bits = BitConverter.ToUInt16(b, start + 14);
            }
            else if (id == "data")
            {
                samples = new short[size / 2];
                Buffer.BlockCopy(b, start, samples, 0, samples.Length * 2);
            }
            o = start + size + (size & 1);
        }
        if (fmt != 1 || bits != 16) throw new InvalidDataException($"Only 16-bit PCM WAV is supported (format {fmt}, {bits}-bit).");
        if (samples == null) throw new InvalidDataException("WAV has no data chunk.");
        return new WavFile { SampleRate = rate, Channels = ch, Samples = samples };
    }

    public static WavFile Read(string path) => Read(File.ReadAllBytes(path));

    public byte[] ToBytes()
    {
        int dataBytes = Samples.Length * 2;
        var b = new byte[44 + dataBytes];
        void Ascii(int at, string s) { for (int i = 0; i < 4; i++) b[at + i] = (byte)s[i]; }
        Ascii(0, "RIFF");
        BitConverter.GetBytes(36 + dataBytes).CopyTo(b, 4);
        Ascii(8, "WAVE");
        Ascii(12, "fmt ");
        BitConverter.GetBytes(16).CopyTo(b, 16);
        BitConverter.GetBytes((ushort)1).CopyTo(b, 20);
        BitConverter.GetBytes((ushort)Channels).CopyTo(b, 22);
        BitConverter.GetBytes(SampleRate).CopyTo(b, 24);
        BitConverter.GetBytes(SampleRate * Channels * 2).CopyTo(b, 28);
        BitConverter.GetBytes((ushort)(Channels * 2)).CopyTo(b, 32);
        BitConverter.GetBytes((ushort)16).CopyTo(b, 34);
        Ascii(36, "data");
        BitConverter.GetBytes(dataBytes).CopyTo(b, 40);
        Buffer.BlockCopy(Samples, 0, b, 44, dataBytes);
        return b;
    }

    public void Write(string path) => File.WriteAllBytes(path, ToBytes());

    /// <summary>
    /// Cut <paramref name="trimMs"/> from the end, then fade the new last <paramref name="fadeMs"/> linearly to silence.
    /// Removes the end-of-clip artefact the voice model produces in roughly a third of generations.
    /// </summary>
    public void TrimTailAndFade(double trimMs = 15, double fadeMs = 10)
    {
        int frames = Samples.Length / Channels;
        int trimFrames = (int)Math.Round(SampleRate * trimMs / 1000.0);
        int keep = Math.Max(0, frames - trimFrames);
        if (keep < frames)
        {
            var s = new short[keep * Channels];
            Array.Copy(Samples, s, s.Length);
            Samples = s;
        }
        int fadeFrames = Math.Min(keep, (int)Math.Round(SampleRate * fadeMs / 1000.0));
        for (int f = 0; f < fadeFrames; f++)
        {
            int frame = keep - fadeFrames + f;
            // Gain goes from just under 1 down to 0 at the final sample.
            double gain = fadeFrames == 1 ? 0 : 1.0 - (f + 1) / (double)fadeFrames;
            for (int c = 0; c < Channels; c++)
            {
                int i = frame * Channels + c;
                Samples[i] = (short)Math.Round(Samples[i] * gain);
            }
        }
    }
}
