namespace DbvoStudio.Core.Plugins;

/// <summary>A subrecord: 4-character type plus raw data.</summary>
public sealed class Subrecord
{
    public Subrecord(string type, byte[] data)
    {
        if (type.Length != 4) throw new ArgumentException("Subrecord type must be 4 characters", nameof(type));
        Type = type;
        Data = data;
    }

    public string Type { get; }
    public byte[] Data { get; set; }

    public override string ToString() => $"{Type} ({Data.Length} bytes)";

    public static List<Subrecord> Parse(ReadOnlySpan<byte> data)
    {
        var list = new List<Subrecord>();
        int o = 0;
        uint? bigSize = null;
        while (o + 6 <= data.Length)
        {
            string type = Ascii(data.Slice(o, 4));
            int size = BitConverter.ToUInt16(data.Slice(o + 4, 2));
            o += 6;
            if (type == "XXXX")
            {
                bigSize = BitConverter.ToUInt32(data.Slice(o, 4));
                o += size;
                continue;
            }
            if (bigSize.HasValue)
            {
                size = checked((int)bigSize.Value);
                bigSize = null;
            }
            if (o + size > data.Length)
                throw new InvalidDataException($"Subrecord {type} overruns its record ({o + size} > {data.Length}).");
            list.Add(new Subrecord(type, data.Slice(o, size).ToArray()));
            o += size;
        }
        return list;
    }

    public static void Write(Stream s, IEnumerable<Subrecord> subs)
    {
        Span<byte> hdr = stackalloc byte[6];
        foreach (var sr in subs)
        {
            if (sr.Data.Length > ushort.MaxValue)
            {
                WriteHeader(s, "XXXX", 4);
                s.Write(BitConverter.GetBytes((uint)sr.Data.Length));
                WriteHeader(s, sr.Type, 0);
            }
            else
            {
                WriteHeader(s, sr.Type, (ushort)sr.Data.Length);
            }
            s.Write(sr.Data);
        }
    }

    private static void WriteHeader(Stream s, string type, ushort size)
    {
        foreach (char c in type) s.WriteByte((byte)c);
        s.Write(BitConverter.GetBytes(size));
    }

    internal static string Ascii(ReadOnlySpan<byte> b)
    {
        Span<char> c = stackalloc char[b.Length];
        for (int i = 0; i < b.Length; i++) c[i] = (char)b[i];
        return new string(c);
    }
}
