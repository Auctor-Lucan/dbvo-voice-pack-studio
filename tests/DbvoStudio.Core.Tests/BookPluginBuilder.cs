using System.Text;

namespace DbvoStudio.Core.Tests;

/// <summary>Writes small, non-localized plugins with BOOK, NPC_ and VTYP records for the Voiced Books tests and the demo.</summary>
public sealed class BookPluginBuilder
{
    public sealed record BookRec(uint FormId, string EditorId, string Title, string Text, byte Flags = 0, uint[]? Keywords = null);
    public sealed record NpcRec(uint FormId, string EditorId, string Name, uint VoiceType, bool Female = false, uint Template = 0, bool UseTraits = false);
    public sealed record VtypRec(uint FormId, string EditorId, bool Female = false);
    public sealed record LvlnRec(uint FormId, string EditorId, uint[] Entries);
    public sealed record KywdRec(uint FormId, string EditorId);

    public List<string> Masters { get; } = new();
    public List<BookRec> Books { get; } = new();
    public List<NpcRec> Npcs { get; } = new();
    public List<VtypRec> VoiceTypes { get; } = new();
    public List<LvlnRec> Leveled { get; } = new();
    public List<KywdRec> Keywords { get; } = new();
    public uint HeaderFlags { get; set; }

    private static readonly Encoding Cp = Encoding.Latin1;

    private static byte[] Sub(string type, byte[] data)
    {
        var b = new byte[6 + data.Length];
        Encoding.ASCII.GetBytes(type).CopyTo(b, 0);
        BitConverter.GetBytes((ushort)data.Length).CopyTo(b, 4);
        data.CopyTo(b, 6);
        return b;
    }
    private static byte[] Z(string s) => Cp.GetBytes(s + "\0");
    private static byte[] U32(uint v) => BitConverter.GetBytes(v);

    private static byte[] Record(string sig, uint formId, params byte[][] subs)
    {
        var data = subs.SelectMany(x => x).ToArray();
        var b = new byte[24 + data.Length];
        Encoding.ASCII.GetBytes(sig).CopyTo(b, 0);
        BitConverter.GetBytes((uint)data.Length).CopyTo(b, 4);
        BitConverter.GetBytes(formId).CopyTo(b, 12);
        BitConverter.GetBytes((ushort)44).CopyTo(b, 20);
        data.CopyTo(b, 24);
        return b;
    }

    private static byte[] Group(string label, IEnumerable<byte[]> records)
    {
        var body = records.SelectMany(x => x).ToArray();
        var b = new byte[24 + body.Length];
        Encoding.ASCII.GetBytes("GRUP").CopyTo(b, 0);
        BitConverter.GetBytes((uint)b.Length).CopyTo(b, 4);
        Encoding.ASCII.GetBytes(label).CopyTo(b, 8);
        body.CopyTo(b, 24);
        return b;
    }

    public byte[] Build()
    {
        var hdr = new List<byte[]> { Sub("HEDR", U32(0x3FD9999A).Concat(U32((uint)(Books.Count + Npcs.Count))).Concat(U32(0x800)).ToArray()), Sub("CNAM", Z("test")) };
        foreach (var m in Masters) { hdr.Add(Sub("MAST", Z(m))); hdr.Add(Sub("DATA", new byte[8])); }
        var head = Record("TES4", 0, hdr.ToArray());
        BitConverter.GetBytes(HeaderFlags).CopyTo(head, 8);
        var parts = new List<byte[]> { head };
        if (Keywords.Count > 0) parts.Add(Group("KYWD", Keywords.Select(k => Record("KYWD", k.FormId, Sub("EDID", Z(k.EditorId))))));
        if (VoiceTypes.Count > 0) parts.Add(Group("VTYP", VoiceTypes.Select(v => Record("VTYP", v.FormId, Sub("EDID", Z(v.EditorId)), Sub("DNAM", new[] { (byte)(v.Female ? 2 : 0) })))));
        if (Books.Count > 0)
            parts.Add(Group("BOOK", Books.Select(b =>
            {
                var subs = new List<byte[]> { Sub("EDID", Z(b.EditorId)), Sub("FULL", Z(b.Title)) };
                if (b.Keywords is { Length: > 0 } k) { subs.Add(Sub("KSIZ", U32((uint)k.Length))); subs.Add(Sub("KWDA", k.SelectMany(U32).ToArray())); }
                subs.Add(Sub("DESC", Z(b.Text)));
                var data = new byte[16]; data[0] = b.Flags;
                subs.Add(Sub("DATA", data));
                return Record("BOOK", b.FormId, subs.ToArray());
            })));
        if (Npcs.Count > 0)
            parts.Add(Group("NPC_", Npcs.Select(n =>
            {
                var acbs = new byte[24];
                BitConverter.GetBytes(n.Female ? 1u : 0u).CopyTo(acbs, 0);
                BitConverter.GetBytes((ushort)(n.UseTraits ? 1 : 0)).CopyTo(acbs, 18);
                var subs = new List<byte[]> { Sub("EDID", Z(n.EditorId)), Sub("ACBS", acbs), Sub("FULL", Z(n.Name)) };
                if (n.VoiceType != 0) subs.Add(Sub("VTCK", U32(n.VoiceType)));
                if (n.Template != 0) subs.Add(Sub("TPLT", U32(n.Template)));
                return Record("NPC_", n.FormId, subs.ToArray());
            })));
        if (Leveled.Count > 0)
            parts.Add(Group("LVLN", Leveled.Select(l => Record("LVLN", l.FormId,
                new[] { Sub("EDID", Z(l.EditorId)) }.Concat(l.Entries.Select(e => Sub("LVLO", new byte[] { 1, 0, 0, 0 }.Concat(U32(e)).Concat(new byte[] { 1, 0, 0, 0 }).ToArray()))).ToArray()))));
        return parts.SelectMany(x => x).ToArray();
    }

    public void Write(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllBytes(path, Build());
    }
}
