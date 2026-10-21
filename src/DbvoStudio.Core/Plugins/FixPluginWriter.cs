using DbvoStudio.Core.Text;

namespace DbvoStudio.Core.Plugins;

/// <summary>One text change to write into a DBVO Fix plugin.</summary>
public sealed record LineEdit(uint FormId, LineField Field, string NewText);

/// <summary>
/// Writes a "DBVO Fix - &lt;plugin&gt;.esp": ESL-flagged, override-only, with changed DIAL FULL / INFO RNAM text.
/// Records are copied byte-for-byte from the source plugin (decompressed); only the edited text changes.
/// The master list is the source's masters followed by the source plugin, so every FormID keeps its
/// master index and nothing needs remapping. Localized source text is written inline (the patch is
/// not localized). INFO overrides are written inside a copy of their parent DIAL, as xEdit does.
/// </summary>
public static class FixPluginWriter
{
    public const string DefaultPrefix = "DBVO Fix - ";

    public static string FixFileName(string sourceFileName, string? prefix = null)
    {
        if (string.IsNullOrEmpty(prefix)) prefix = DefaultPrefix;
        return prefix + Path.GetFileNameWithoutExtension(sourceFileName) + ".esp";
    }

    public static byte[] Build(PluginFile source, IReadOnlyCollection<LineEdit> edits)
    {
        if (edits.Count == 0) throw new InvalidOperationException("No edits: a DBVO Fix plugin is only written when a line changed.");

        var byForm = source.Records.Where(r => r.Signature is "DIAL" or "INFO").ToDictionary(r => (r.Signature, r.FormId));
        var dialEdits = edits.Where(e => e.Field == LineField.TopicText).ToDictionary(e => e.FormId);
        var infoEdits = edits.Where(e => e.Field == LineField.Prompt).ToDictionary(e => e.FormId);

        foreach (var e in dialEdits.Keys)
            if (!byForm.ContainsKey(("DIAL", e))) throw new InvalidOperationException($"DIAL {e:X8} not found in {source.FileName}.");
        foreach (var e in infoEdits.Keys)
            if (!byForm.ContainsKey(("INFO", e))) throw new InvalidOperationException($"INFO {e:X8} not found in {source.FileName}.");

        // Topics to write: edited ones plus parents of edited INFOs, in source order.
        var infosByParent = infoEdits.Keys
            .Select(id => byForm[("INFO", id)])
            .GroupBy(r => r.ParentTopic ?? throw new InvalidOperationException($"INFO {r.FormId:X8} has no parent topic."))
            .ToDictionary(g => g.Key, g => g.OrderBy(r => r.Index).ToList());
        var topicIds = new HashSet<uint>(dialEdits.Keys.Concat(infosByParent.Keys));
        var topics = topicIds.Select(id => byForm.TryGetValue(("DIAL", id), out var r)
                ? r
                : throw new InvalidOperationException($"Parent topic {id:X8} is not in {source.FileName}."))
            .OrderBy(r => r.Index).ToList();

        int recordCount = 1; // the top DIAL group
        using var body = new MemoryStream();
        using (var group = new MemoryStream())
        {
            foreach (var dial in topics)
            {
                var subs = CopySubs(source, dial);
                if (dialEdits.TryGetValue(dial.FormId, out var de)) SetText(subs, "FULL", de.NewText);
                WriteRecord(group, dial, subs);
                recordCount++;

                if (infosByParent.TryGetValue(dial.FormId, out var infos))
                {
                    using var children = new MemoryStream();
                    foreach (var info in infos)
                    {
                        var isubs = CopySubs(source, info);
                        SetText(isubs, "RNAM", infoEdits[info.FormId].NewText);
                        WriteRecord(children, info, isubs);
                        recordCount++;
                    }
                    WriteGroup(group, BitConverter.GetBytes(dial.FormId), 7, children.ToArray());
                    recordCount++;
                }
            }
            WriteGroup(body, "DIAL"u8.ToArray(), 0, group.ToArray());
        }

        var masters = source.Masters.Concat(new[] { source.FileName }).ToList();
        using var outStream = new MemoryStream();
        WriteHeader(outStream, masters, recordCount);
        body.Position = 0;
        body.CopyTo(outStream);
        return outStream.ToArray();
    }

    public static void Write(PluginFile source, IReadOnlyCollection<LineEdit> edits, string path)
    {
        var bytes = Build(source, edits);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllBytes(path, bytes);
    }

    private static List<Subrecord> CopySubs(PluginFile source, PluginRecord rec)
    {
        var list = rec.Subrecords.Select(s => new Subrecord(s.Type, (byte[])s.Data.Clone())).ToList();
        if (!source.IsLocalized) return list;
        // Inline localized strings so the non-localized patch shows real text.
        var lstringTypes = rec.Signature == "DIAL" ? new[] { "FULL" } : new[] { "RNAM", "NAM1" };
        foreach (var s in list.Where(s => lstringTypes.Contains(s.Type)))
        {
            var text = source.DecodeLString(s.Data)
                ?? throw new InvalidOperationException($"{rec.Signature} {rec.FormId:X8}: localized string {BitConverter.ToUInt32(s.Data, 0):X8} not found. Add the plugin's strings files.");
            s.Data = ZString(text);
        }
        return list;
    }

    private static void SetText(List<Subrecord> subs, string type, string text)
    {
        var s = subs.FirstOrDefault(x => x.Type == type);
        if (s == null) throw new InvalidOperationException($"Record has no {type} to replace.");
        s.Data = ZString(text);
    }

    public static byte[] ZString(string text)
    {
        var enc = Encodings.Cp1252;
        var b = enc.GetBytes(text);
        var roundTrip = enc.GetString(b);
        if (roundTrip != text) throw new InvalidOperationException($"Text contains characters the game's code page cannot store: \"{text}\"");
        var z = new byte[b.Length + 1];
        b.CopyTo(z, 0);
        return z;
    }

    private static void WriteRecord(Stream s, PluginRecord rec, List<Subrecord> subs)
    {
        using var data = new MemoryStream();
        Subrecord.Write(data, subs);
        var d = data.ToArray();
        WriteAscii(s, rec.Signature);
        s.Write(BitConverter.GetBytes((uint)d.Length));
        s.Write(BitConverter.GetBytes(rec.Flags & ~PluginRecord.FlagCompressed));
        s.Write(BitConverter.GetBytes(rec.FormId));
        s.Write(rec.HeaderTail);
        s.Write(d);
    }

    private static void WriteGroup(Stream s, byte[] label, int type, byte[] contents)
    {
        WriteAscii(s, "GRUP");
        s.Write(BitConverter.GetBytes((uint)(24 + contents.Length)));
        s.Write(label);
        s.Write(BitConverter.GetBytes(type));
        s.Write(new byte[8]);
        s.Write(contents);
    }

    private static void WriteHeader(Stream s, List<string> masters, int recordCount)
    {
        var subs = new List<Subrecord>();
        var hedr = new byte[12];
        BitConverter.GetBytes(1.71f).CopyTo(hedr, 0);
        BitConverter.GetBytes((uint)recordCount).CopyTo(hedr, 4);
        BitConverter.GetBytes(0x800u).CopyTo(hedr, 8);
        subs.Add(new Subrecord("HEDR", hedr));
        subs.Add(new Subrecord("CNAM", ZString("DBVO Studio")));
        foreach (var m in masters)
        {
            subs.Add(new Subrecord("MAST", ZString(m)));
            subs.Add(new Subrecord("DATA", new byte[8]));
        }
        using var data = new MemoryStream();
        Subrecord.Write(data, subs);
        var d = data.ToArray();
        WriteAscii(s, "TES4");
        s.Write(BitConverter.GetBytes((uint)d.Length));
        s.Write(BitConverter.GetBytes(PluginFile.HeaderFlagLight));
        s.Write(BitConverter.GetBytes(0u));
        // Timestamp/VC (4 bytes), form version 44 (SSE), unknown.
        s.Write(new byte[4]);
        s.Write(BitConverter.GetBytes((ushort)44));
        s.Write(new byte[2]);
        s.Write(d);
    }

    private static void WriteAscii(Stream s, string text)
    {
        foreach (char c in text) s.WriteByte((byte)c);
    }
}
