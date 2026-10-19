using System.IO.Compression;
using DbvoStudio.Core.Text;

namespace DbvoStudio.Core.Plugins;

/// <summary>A record read from a plugin, with raw header fields preserved for byte-exact copying.</summary>
public sealed class PluginRecord
{
    public required string Signature { get; init; }
    public required uint Flags { get; init; }
    public required uint FormId { get; init; }
    /// <summary>Bytes 16..23 of the record header (timestamp/version control, form version, unknown).</summary>
    public required byte[] HeaderTail { get; init; }
    public required List<Subrecord> Subrecords { get; init; }
    /// <summary>FormID of the parent DIAL for INFO records (from the enclosing topic-children group).</summary>
    public uint? ParentTopic { get; init; }
    /// <summary>Position in file order.</summary>
    public int Index { get; init; }

    public const uint FlagDeleted = 0x20;
    public const uint FlagCompressed = 0x40000;
    public bool IsDeleted => (Flags & FlagDeleted) != 0;

    public Subrecord? Get(string type) => Subrecords.FirstOrDefault(s => s.Type == type);

    public string? EditorId
    {
        get
        {
            var e = Get("EDID");
            return e == null ? null : ZString(e.Data);
        }
    }

    internal static string ZString(byte[] d)
    {
        int n = Array.IndexOf(d, (byte)0);
        if (n < 0) n = d.Length;
        return Encodings.Cp1252.GetString(d, 0, n);
    }
}

/// <summary>
/// Minimal Skyrim Special Edition plugin reader for the records Phase 1 needs (DIAL and INFO).
/// Records are read in file order; INFO records know their parent topic.
/// </summary>
public sealed class PluginFile
{
    public const uint HeaderFlagMaster = 0x1;
    public const uint HeaderFlagLocalized = 0x80;
    public const uint HeaderFlagLight = 0x200;

    private PluginFile(string path, string fileName, uint headerFlags, List<Subrecord> header, List<string> masters, List<PluginRecord> records)
    {
        Path = path;
        FileName = fileName;
        HeaderFlags = headerFlags;
        HeaderSubrecords = header;
        Masters = masters;
        Records = records;
    }

    public string Path { get; }
    public string FileName { get; }
    public uint HeaderFlags { get; }
    public List<Subrecord> HeaderSubrecords { get; }
    public IReadOnlyList<string> Masters { get; }
    public IReadOnlyList<PluginRecord> Records { get; }
    public StringsTable? Strings { get; private set; }

    public bool IsLocalized => (HeaderFlags & HeaderFlagLocalized) != 0;
    public bool IsMaster => (HeaderFlags & HeaderFlagMaster) != 0;
    public bool IsLight => (HeaderFlags & HeaderFlagLight) != 0;
    public string BaseName => System.IO.Path.GetFileNameWithoutExtension(FileName);

    /// <summary>Read a plugin. Only DIAL and INFO records are materialized; other records are skipped.</summary>
    public static PluginFile Read(string path, ISet<string>? signatures = null)
    {
        signatures ??= new HashSet<string> { "DIAL", "INFO" };
        var b = File.ReadAllBytes(path);
        if (b.Length < 24 || Subrecord.Ascii(b.AsSpan(0, 4)) != "TES4")
            throw new InvalidDataException($"{System.IO.Path.GetFileName(path)} is not a Skyrim plugin (missing TES4 header).");

        uint hdrSize = BitConverter.ToUInt32(b, 4);
        uint hdrFlags = BitConverter.ToUInt32(b, 8);
        var header = Subrecord.Parse(b.AsSpan(24, (int)hdrSize));
        var masters = header.Where(s => s.Type == "MAST").Select(s => PluginRecord.ZString(s.Data)).ToList();

        var records = new List<PluginRecord>();
        var groupStack = new Stack<(long End, uint Label, uint Type)>();
        long o = 24 + hdrSize;
        int index = 0;
        while (o + 24 <= b.Length)
        {
            while (groupStack.Count > 0 && o >= groupStack.Peek().End) groupStack.Pop();
            string sig = Subrecord.Ascii(b.AsSpan((int)o, 4));
            if (sig == "GRUP")
            {
                uint gsize = BitConverter.ToUInt32(b, (int)o + 4);
                uint label = BitConverter.ToUInt32(b, (int)o + 8);
                uint gtype = BitConverter.ToUInt32(b, (int)o + 12);
                if (gsize < 24) throw new InvalidDataException($"Corrupt group at offset {o}.");
                groupStack.Push((o + gsize, label, gtype));
                o += 24;
                continue;
            }
            uint dataSize = BitConverter.ToUInt32(b, (int)o + 4);
            uint flags = BitConverter.ToUInt32(b, (int)o + 8);
            uint formId = BitConverter.ToUInt32(b, (int)o + 12);
            if (signatures.Contains(sig))
            {
                var raw = b.AsSpan((int)o + 24, (int)dataSize);
                byte[] data = (flags & PluginRecord.FlagCompressed) != 0 ? Decompress(raw) : raw.ToArray();
                uint? parent = null;
                foreach (var g in groupStack)
                {
                    if (g.Type == 7) { parent = g.Label; break; }
                }
                records.Add(new PluginRecord
                {
                    Signature = sig,
                    Flags = flags,
                    FormId = formId,
                    HeaderTail = b.AsSpan((int)o + 16, 8).ToArray(),
                    Subrecords = Subrecord.Parse(data),
                    ParentTopic = parent,
                    Index = index++,
                });
            }
            o += 24 + dataSize;
        }

        return new PluginFile(path, System.IO.Path.GetFileName(path), hdrFlags, header, masters, records);
    }

    private static byte[] Decompress(ReadOnlySpan<byte> raw)
    {
        int size = (int)BitConverter.ToUInt32(raw[..4]);
        var result = new byte[size];
        using var ms = new MemoryStream(raw[4..].ToArray());
        using var z = new ZLibStream(ms, CompressionMode.Decompress);
        int read = 0;
        while (read < size)
        {
            int n = z.Read(result, read, size - read);
            if (n == 0) break;
            read += n;
        }
        return result;
    }

    /// <summary>Attach localized strings. Returns false when the plugin is localized but no strings were found.</summary>
    public bool AttachStrings(string? stringsFolder, string language = "English")
    {
        if (!IsLocalized) return true;
        if (stringsFolder == null || !StringsTable.Exists(stringsFolder, BaseName, language)) return false;
        Strings = StringsTable.Load(stringsFolder, BaseName, language);
        return true;
    }

    public void AttachStrings(StringsTable table) => Strings = table;

    /// <summary>
    /// Decode a localized-string subrecord (FULL, RNAM, NAM1). Returns null when the plugin is localized
    /// and the string ID cannot be resolved.
    /// </summary>
    public string? DecodeLString(byte[] data)
    {
        if (!IsLocalized) return PluginRecord.ZString(data);
        if (data.Length != 4) return PluginRecord.ZString(data);
        uint id = BitConverter.ToUInt32(data, 0);
        if (id == 0) return string.Empty;
        if (Strings != null && Strings.TryGet(id, out var s)) return s;
        return null;
    }

    /// <summary>The plugin that owns a FormID (by its master index), as a file name.</summary>
    public string OwnerOf(uint formId)
    {
        int idx = (int)(formId >> 24);
        return idx < Masters.Count ? Masters[idx] : FileName;
    }
}
