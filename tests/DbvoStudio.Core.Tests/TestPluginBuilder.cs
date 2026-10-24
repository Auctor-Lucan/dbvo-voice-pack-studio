using System.IO.Compression;
using System.Text;
using DbvoStudio.Core.Plugins;
using DbvoStudio.Core.Text;

namespace DbvoStudio.Core.Tests;

/// <summary>Builds small synthetic Skyrim plugins for tests.</summary>
public sealed class TestPluginBuilder
{
    public sealed class Info
    {
        public uint FormId;
        public string? Prompt;     // RNAM
        public string Response = "Response text."; // NAM1
        public bool Compressed;
    }

    public sealed class Topic
    {
        public uint FormId;
        public string EditorId = "";
        public string? Text;       // FULL
        public string Subtype = "CUST";
        public List<Info> Infos = new();
        /// <summary>TIFC as written (default: the responses in this plugin).</summary>
        public uint? ResponseCount;
    }

    public List<string> Masters { get; } = new() { "Skyrim.esm" };
    public List<Topic> Topics { get; } = new();
    public bool Localized { get; set; }
    /// <summary>Filled when Localized: string ID -> text, for writing strings files.</summary>
    public Dictionary<uint, string> Strings { get; } = new();
    private uint _nextString = 1;

    public Topic AddTopic(uint formId, string edid, string? text, params (uint id, string? prompt)[] infos)
    {
        var t = new Topic { FormId = formId, EditorId = edid, Text = text };
        foreach (var (id, prompt) in infos) t.Infos.Add(new Info { FormId = id, Prompt = prompt });
        Topics.Add(t);
        return t;
    }

    public byte[] Build()
    {
        using var body = new MemoryStream();
        using (var top = new MemoryStream())
        {
            foreach (var t in Topics)
            {
                var subs = new List<Subrecord>
                {
                    Z("EDID", t.EditorId),
                };
                if (t.Text != null) subs.Add(L("FULL", t.Text));
                subs.Add(new Subrecord("PNAM", BitConverter.GetBytes(50f)));
                subs.Add(new Subrecord("QNAM", BitConverter.GetBytes(0x00012345u)));
                subs.Add(new Subrecord("DATA", new byte[4]));
                subs.Add(new Subrecord("SNAM", Encoding.ASCII.GetBytes(t.Subtype)));
                subs.Add(new Subrecord("TIFC", BitConverter.GetBytes(t.ResponseCount ?? (uint)t.Infos.Count)));
                Record(top, "DIAL", t.FormId, subs, false);
                if (t.Infos.Count > 0)
                {
                    using var kids = new MemoryStream();
                    foreach (var i in t.Infos)
                    {
                        var isubs = new List<Subrecord>
                        {
                            new("DATA", new byte[4]),
                            new("ENAM", new byte[4]),
                            new("TRDT", new byte[24]),
                            L("NAM1", i.Response),
                            Z("NAM2", ""),
                            Z("NAM3", ""),
                        };
                        if (i.Prompt != null) isubs.Add(L("RNAM", i.Prompt));
                        Record(kids, "INFO", i.FormId, isubs, i.Compressed);
                    }
                    Group(top, BitConverter.GetBytes(t.FormId), 7, kids.ToArray());
                }
            }
            Group(body, Encoding.ASCII.GetBytes("DIAL"), 0, top.ToArray());
        }

        var hdr = new List<Subrecord>();
        var hedr = new byte[12];
        BitConverter.GetBytes(1.71f).CopyTo(hedr, 0);
        hdr.Add(new Subrecord("HEDR", hedr));
        foreach (var m in Masters)
        {
            hdr.Add(Z("MAST", m));
            hdr.Add(new Subrecord("DATA", new byte[8]));
        }
        using var ms = new MemoryStream();
        using var hd = new MemoryStream();
        Subrecord.Write(hd, hdr);
        Ascii(ms, "TES4");
        ms.Write(BitConverter.GetBytes((uint)hd.Length));
        ms.Write(BitConverter.GetBytes(Localized ? PluginFile.HeaderFlagLocalized : 0u));
        ms.Write(new byte[12]);
        ms.Write(hd.ToArray());
        ms.Write(body.ToArray());
        return ms.ToArray();
    }

    /// <summary>Write Name_English.STRINGS / ILSTRINGS / DLSTRINGS (all strings in each, which is enough for tests).</summary>
    public void WriteStrings(string folder, string baseName)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, baseName + "_English.STRINGS"), StringsFile(false));
        File.WriteAllBytes(Path.Combine(folder, baseName + "_English.ILSTRINGS"), StringsFile(true));
        File.WriteAllBytes(Path.Combine(folder, baseName + "_English.DLSTRINGS"), StringsFile(true));
    }

    private byte[] StringsFile(bool lengthPrefixed)
    {
        using var data = new MemoryStream();
        var dir = new List<(uint, uint)>();
        foreach (var (id, text) in Strings)
        {
            dir.Add((id, (uint)data.Length));
            var b = Encodings.Cp1252.GetBytes(text);
            if (lengthPrefixed) data.Write(BitConverter.GetBytes((uint)b.Length + 1));
            data.Write(b);
            data.WriteByte(0);
        }
        using var ms = new MemoryStream();
        ms.Write(BitConverter.GetBytes((uint)dir.Count));
        ms.Write(BitConverter.GetBytes((uint)data.Length));
        foreach (var (id, off) in dir)
        {
            ms.Write(BitConverter.GetBytes(id));
            ms.Write(BitConverter.GetBytes(off));
        }
        ms.Write(data.ToArray());
        return ms.ToArray();
    }

    private Subrecord Z(string type, string text) => new(type, FixPluginWriter.ZString(text));

    private Subrecord L(string type, string text)
    {
        if (!Localized) return Z(type, text);
        uint id = _nextString++;
        Strings[id] = text;
        return new Subrecord(type, BitConverter.GetBytes(id));
    }

    private static void Record(Stream s, string sig, uint formId, List<Subrecord> subs, bool compress)
    {
        using var d = new MemoryStream();
        Subrecord.Write(d, subs);
        byte[] data = d.ToArray();
        uint flags = 0;
        if (compress)
        {
            using var c = new MemoryStream();
            c.Write(BitConverter.GetBytes((uint)data.Length));
            using (var z = new ZLibStream(c, CompressionLevel.Optimal, true)) z.Write(data);
            data = c.ToArray();
            flags |= PluginRecord.FlagCompressed;
        }
        Ascii(s, sig);
        s.Write(BitConverter.GetBytes((uint)data.Length));
        s.Write(BitConverter.GetBytes(flags));
        s.Write(BitConverter.GetBytes(formId));
        s.Write(new byte[] { 0, 0, 0, 0, 44, 0, 0, 0 });
        s.Write(data);
    }

    private static void Group(Stream s, byte[] label, int type, byte[] contents)
    {
        Ascii(s, "GRUP");
        s.Write(BitConverter.GetBytes((uint)(24 + contents.Length)));
        s.Write(label);
        s.Write(BitConverter.GetBytes(type));
        s.Write(new byte[8]);
        s.Write(contents);
    }

    private static void Ascii(Stream s, string t)
    {
        foreach (char c in t) s.WriteByte((byte)c);
    }
}
