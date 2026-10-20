using DbvoStudio.Core.Text;

namespace DbvoStudio.Core.Plugins;

/// <summary>
/// Writes an ESL-flagged plugin that contains only voice type (VTYP) records. No masters, no overrides:
/// SkyPatcher assigns the voice type to NPCs and FormList Manipulator adds it to form lists at runtime.
/// </summary>
public static class VoiceTypePluginWriter
{
    /// <param name="types">(object ID from 0x800, editor ID, DNAM flags: 1 = allow default dialogue, 2 = female)</param>
    public static byte[] Build(IReadOnlyList<(uint Id, string EditorId, byte Flags)> types, string description)
    {
        if (types.Count == 0) throw new InvalidOperationException("No voice types to write.");
        using var group = new MemoryStream();
        foreach (var (id, edid, flags) in types)
        {
            var subs = new List<Subrecord>
            {
                new("EDID", FixPluginWriter.ZString(edid)),
                new("DNAM", new[] { flags }),
            };
            using var data = new MemoryStream();
            Subrecord.Write(data, subs);
            var d = data.ToArray();
            Ascii(group, "VTYP");
            group.Write(BitConverter.GetBytes((uint)d.Length));
            group.Write(BitConverter.GetBytes(0u));
            group.Write(BitConverter.GetBytes(id & 0xFFF));
            group.Write(new byte[4]);
            group.Write(BitConverter.GetBytes((ushort)44));
            group.Write(new byte[2]);
            group.Write(d);
        }
        var g = group.ToArray();
        using var outStream = new MemoryStream();
        var hsubs = new List<Subrecord>();
        var hedr = new byte[12];
        BitConverter.GetBytes(1.71f).CopyTo(hedr, 0);
        BitConverter.GetBytes((uint)(types.Count + 1)).CopyTo(hedr, 4);
        BitConverter.GetBytes(0x800u + (uint)types.Count).CopyTo(hedr, 8);
        hsubs.Add(new Subrecord("HEDR", hedr));
        hsubs.Add(new Subrecord("CNAM", FixPluginWriter.ZString("DBVO Voice Pack Studio")));
        hsubs.Add(new Subrecord("SNAM", FixPluginWriter.ZString(description)));
        using (var hd = new MemoryStream())
        {
            Subrecord.Write(hd, hsubs);
            var h = hd.ToArray();
            Ascii(outStream, "TES4");
            outStream.Write(BitConverter.GetBytes((uint)h.Length));
            outStream.Write(BitConverter.GetBytes(PluginFile.HeaderFlagLight));
            outStream.Write(BitConverter.GetBytes(0u));
            outStream.Write(new byte[4]);
            outStream.Write(BitConverter.GetBytes((ushort)44));
            outStream.Write(new byte[2]);
            outStream.Write(h);
        }
        Ascii(outStream, "GRUP");
        outStream.Write(BitConverter.GetBytes((uint)(24 + g.Length)));
        Ascii(outStream, "VTYP");
        outStream.Write(BitConverter.GetBytes(0));
        outStream.Write(new byte[8]);
        outStream.Write(g);
        return outStream.ToArray();
    }

    private static void Ascii(Stream s, string t) { foreach (char c in t) s.WriteByte((byte)c); }
}
