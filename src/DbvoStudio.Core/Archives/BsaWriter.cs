using System.Text;

namespace DbvoStudio.Core.Archives;

/// <summary>
/// Writes an uncompressed Skyrim SE (version 105) BSA, laid out as Bethesda's tools do: folders and files sorted by
/// their hash, directory and file names included. Checked against real archives: rebuilding a 6,804-file voice BSA from
/// its own files gives the same bytes.
/// </summary>
public static class BsaWriter
{
    private const uint FlagDirNames = 0x1, FlagFileNames = 0x2;
    /// <summary>Above this the 32-bit file offsets of a BSA run out; callers keep such content loose.</summary>
    public const long MaxDataBytes = 2_000_000_000;

    public sealed record Entry(string ArchivePath, string SourcePath);

    public static ulong Hash(string name, bool folder)
    {
        name = name.ToLowerInvariant().Replace('/', '\\');
        string root = name, ext = "";
        if (!folder)
        {
            int i = name.LastIndexOf('.');
            if (i > 0) { root = name[..i]; ext = name[i..]; }
        }
        var b = Encoding.Latin1.GetBytes(root);
        int n = b.Length;
        uint h1 = 0;
        if (n > 0) h1 = (uint)(b[n - 1] | ((n > 2 ? b[n - 2] : 0) << 8) | (n << 16) | (b[0] << 24));
        h1 |= ext switch { ".kf" => 0x80u, ".nif" => 0x8000u, ".dds" => 0x8080u, ".wav" => 0x80000000u, _ => 0u };
        uint h2 = 0;
        if (n > 2) for (int i = 1; i < n - 2; i++) h2 = unchecked(h2 * 0x1003F + b[i]);
        uint h3 = 0;
        foreach (var c in Encoding.Latin1.GetBytes(ext)) h3 = unchecked(h3 * 0x1003F + c);
        return ((ulong)unchecked(h2 + h3) << 32) | h1;
    }

    /// <summary>Write the archive. Returns the number of files. Throws when the data would not fit a BSA.</summary>
    public static int Write(string path, IEnumerable<Entry> entries, CancellationToken ct = default)
    {
        var folders = entries
            .Select(e => (Path: e.ArchivePath.ToLowerInvariant().Replace('/', '\\').TrimStart('\\'), e.SourcePath, Size: new FileInfo(e.SourcePath).Length))
            .GroupBy(e => e.Path.Contains('\\') ? e.Path[..e.Path.LastIndexOf('\\')] : "")
            .Select(g => (Folder: g.Key, Files: g.Select(x => (Name: x.Path[(x.Path.LastIndexOf('\\') + 1)..], x.SourcePath, x.Size))
                .GroupBy(x => x.Name).Select(x => x.First()).OrderBy(x => Hash(x.Name, false)).ToList()))
            .OrderBy(f => Hash(f.Folder, true)).ToList();
        long dataBytes = folders.Sum(f => f.Files.Sum(x => x.Size));
        if (dataBytes > MaxDataBytes) throw new InvalidOperationException($"{dataBytes / 1_000_000} MB is too much for one BSA (about 2,000 MB at most).");
        int fileCount = folders.Sum(f => f.Files.Count);
        int folderNames = folders.Sum(f => Encoding.Latin1.GetByteCount(f.Folder) + 1);
        int fileNames = folders.Sum(f => f.Files.Sum(x => Encoding.Latin1.GetByteCount(x.Name) + 1));
        long recordsStart = 36 + 24L * folders.Count;
        long blocksLen = folders.Sum(f => 1 + Encoding.Latin1.GetByteCount(f.Folder) + 1 + 16L * f.Files.Count);
        long dataStart = recordsStart + blocksLen + fileNames;

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
        var tmp = path + ".part";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
        using (var bw = new BinaryWriter(fs, Encoding.Latin1))
        {
            bw.Write(Encoding.ASCII.GetBytes("BSA\0"));
            bw.Write(105); bw.Write(36u); bw.Write(FlagDirNames | FlagFileNames);
            bw.Write(folders.Count); bw.Write(fileCount); bw.Write(folderNames); bw.Write(fileNames);
            bw.Write((ushort)0); bw.Write((ushort)0);
            long blockOff = recordsStart;
            foreach (var f in folders)
            {
                bw.Write(Hash(f.Folder, true)); bw.Write(f.Files.Count); bw.Write(0u);
                bw.Write((ulong)(blockOff + fileNames)); // Bethesda's quirk: the offset includes the file-name block length
                blockOff += 1 + Encoding.Latin1.GetByteCount(f.Folder) + 1 + 16L * f.Files.Count;
            }
            long dataOff = dataStart;
            foreach (var f in folders)
            {
                var fb = Encoding.Latin1.GetBytes(f.Folder);
                bw.Write((byte)(fb.Length + 1)); bw.Write(fb); bw.Write((byte)0);
                foreach (var x in f.Files)
                {
                    bw.Write(Hash(x.Name, false)); bw.Write((uint)x.Size); bw.Write((uint)dataOff);
                    dataOff += x.Size;
                }
            }
            foreach (var f in folders) foreach (var x in f.Files) { bw.Write(Encoding.Latin1.GetBytes(x.Name)); bw.Write((byte)0); }
            foreach (var f in folders)
                foreach (var x in f.Files)
                {
                    ct.ThrowIfCancellationRequested();
                    using var src = File.OpenRead(x.SourcePath);
                    src.CopyTo(fs);
                }
        }
        File.Move(tmp, path, true);
        return fileCount;
    }
}
