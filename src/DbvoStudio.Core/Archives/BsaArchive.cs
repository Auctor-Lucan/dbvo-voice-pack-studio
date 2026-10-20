using System.IO.Compression;
using System.Text;
using K4os.Compression.LZ4.Streams;

namespace DbvoStudio.Core.Archives;

/// <summary>One file inside a BSA.</summary>
public sealed record BsaEntry(string Path, long Offset, uint Size, bool Compressed);

/// <summary>
/// Reader for Skyrim BSA archives: version 104 (Skyrim LE, zlib) and 105 (Skyrim SE / AE, LZ4 frames).
/// Only the directory is read when opening; file data is read on demand, so huge voice archives open instantly.
/// Layout follows the UESP "BSA File Format" page and xEdit's wbBSArchive.
/// </summary>
public sealed class BsaArchive : IDisposable
{
    private const uint FlagDirNames = 0x1, FlagFileNames = 0x2, FlagCompressed = 0x4, FlagEmbedNames = 0x100;
    private readonly FileStream _fs;
    private readonly Dictionary<string, BsaEntry> _byPath;

    public string FilePath { get; }
    public int Version { get; }
    public uint ArchiveFlags { get; }
    public IReadOnlyList<BsaEntry> Entries { get; }

    private BsaArchive(string path, FileStream fs, int version, uint flags, List<BsaEntry> entries)
    {
        FilePath = path;
        _fs = fs;
        Version = version;
        ArchiveFlags = flags;
        Entries = entries;
        _byPath = new Dictionary<string, BsaEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in entries) _byPath[e.Path] = e;
    }

    public static BsaArchive Open(string path)
    {
        var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.RandomAccess);
        try
        {
            var br = new BinaryReader(fs, Encoding.Latin1, leaveOpen: true);
            if (Encoding.ASCII.GetString(br.ReadBytes(4)) != "BSA\0") throw new InvalidDataException($"{Path.GetFileName(path)} is not a BSA archive.");
            int version = br.ReadInt32();
            if (version != 103 && version != 104 && version != 105)
                throw new InvalidDataException($"{Path.GetFileName(path)}: BSA version {version} is not supported (Skyrim LE is 104, SE/AE is 105).");
            uint folderOffset = br.ReadUInt32();
            uint flags = br.ReadUInt32();
            int folderCount = br.ReadInt32();
            int fileCount = br.ReadInt32();
            br.ReadUInt32(); // total folder name length
            uint fileNamesLength = br.ReadUInt32();
            br.ReadUInt32(); // file flags + padding
            fs.Position = folderOffset;

            var folders = new List<(int Count, long Offset)>(folderCount);
            for (int i = 0; i < folderCount; i++)
            {
                br.ReadUInt64(); // hash
                int count = br.ReadInt32();
                if (version == 105) { br.ReadUInt32(); folders.Add((count, (long)br.ReadUInt64())); }
                else folders.Add((count, br.ReadUInt32()));
            }

            var raw = new List<(string Folder, uint Size, long Offset)>(fileCount);
            foreach (var _ in folders)
            {
                string folder = "";
                if ((flags & FlagDirNames) != 0)
                {
                    int len = br.ReadByte();
                    folder = Encoding.Latin1.GetString(br.ReadBytes(len)).TrimEnd('\0');
                }
                for (int j = 0; j < _.Count; j++)
                {
                    br.ReadUInt64(); // hash
                    uint size = br.ReadUInt32();
                    uint offset = br.ReadUInt32();
                    raw.Add((folder, size, offset));
                }
            }

            var names = new List<string>(fileCount);
            if ((flags & FlagFileNames) != 0)
            {
                var block = br.ReadBytes((int)fileNamesLength);
                int start = 0;
                for (int i = 0; i < block.Length; i++)
                {
                    if (block[i] != 0) continue;
                    names.Add(Encoding.Latin1.GetString(block, start, i - start));
                    start = i + 1;
                }
            }

            bool defaultCompressed = (flags & FlagCompressed) != 0;
            var entries = new List<BsaEntry>(raw.Count);
            for (int i = 0; i < raw.Count; i++)
            {
                var (folder, size, offset) = raw[i];
                bool compressed = defaultCompressed ^ ((size & 0x40000000) != 0);
                string name = i < names.Count ? names[i] : $"file{i:00000}";
                string full = folder.Length == 0 ? name : folder + "\\" + name;
                entries.Add(new BsaEntry(full.Replace('/', '\\'), offset, size & 0x3FFFFFFF, compressed));
            }
            return new BsaArchive(path, fs, version, flags, entries);
        }
        catch
        {
            fs.Dispose();
            throw;
        }
    }

    public bool Contains(string path) => _byPath.ContainsKey(Normalize(path));

    public BsaEntry? Find(string path) => _byPath.TryGetValue(Normalize(path), out var e) ? e : null;

    public static string Normalize(string path) => path.Replace('/', '\\').TrimStart('\\');

    /// <summary>Read one file's bytes, decompressed.</summary>
    public byte[] Read(BsaEntry e)
    {
        byte[] data;
        lock (_fs)
        {
            _fs.Position = e.Offset;
            data = new byte[e.Size];
            int read = 0;
            while (read < data.Length)
            {
                int n = _fs.Read(data, read, data.Length - read);
                if (n == 0) throw new EndOfStreamException($"{Path.GetFileName(FilePath)}: {e.Path} is truncated.");
                read += n;
            }
        }
        int p = 0;
        if ((ArchiveFlags & FlagEmbedNames) != 0 && Version >= 104)
        {
            p = 1 + data[0];
        }
        if (!e.Compressed) return p == 0 ? data : data.AsSpan(p).ToArray();
        int original = BitConverter.ToInt32(data, p);
        p += 4;
        var result = new byte[original];
        using var ms = new MemoryStream(data, p, data.Length - p);
        using Stream z = Version == 105 ? LZ4Stream.Decode(ms) : new ZLibStream(ms, CompressionMode.Decompress);
        int got = 0;
        while (got < original)
        {
            int n = z.Read(result, got, original - got);
            if (n == 0) break;
            got += n;
        }
        if (got != original) throw new InvalidDataException($"{Path.GetFileName(FilePath)}: {e.Path} decompressed to {got} of {original} bytes.");
        return result;
    }

    public byte[]? Read(string path) => Find(path) is { } e ? Read(e) : null;

    public void Dispose() => _fs.Dispose();
}
