using System.Text;
using DbvoStudio.Core.Text;

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

    /// <summary>
    /// File and folder names are stored, and hashed, in Windows-1252: the code page Skyrim SE looks them up in on Western
    /// systems. Latin-1 lost ’ “ ” … – — † ™ • œ and the like (they became "?", so the game never found those files).
    /// </summary>
    private static Encoding Names => Encodings.Cp1252;

    /// <summary>True when Windows-1252 can hold the name exactly.</summary>
    public static bool CanStore(string name) => Names.GetString(Names.GetBytes(name)) == name;

    /// <summary>A folder name is stored with a one-byte length (its bytes plus the closing zero).</summary>
    public const int MaxFolderBytes = 254;

    /// <summary>True when the folder part of an archive path fits a BSA.</summary>
    public static bool FolderFits(string archivePath)
    {
        var p = archivePath.Replace('/', '\\').TrimStart('\\');
        int i = p.LastIndexOf('\\');
        return i < 0 || Names.GetByteCount(p[..i]) <= MaxFolderBytes;
    }

    public static ulong Hash(string name, bool folder)
    {
        name = name.ToLowerInvariant().Replace('/', '\\');
        string root = name, ext = "";
        if (!folder)
        {
            int i = name.LastIndexOf('.');
            if (i > 0) { root = name[..i]; ext = name[i..]; }
        }
        var b = Names.GetBytes(root);
        int n = b.Length;
        uint h1 = 0;
        if (n > 0) h1 = (uint)(b[n - 1] | ((n > 2 ? b[n - 2] : 0) << 8) | (n << 16) | (b[0] << 24));
        h1 |= ext switch { ".kf" => 0x80u, ".nif" => 0x8000u, ".dds" => 0x8080u, ".wav" => 0x80000000u, _ => 0u };
        uint h2 = 0;
        if (n > 2) for (int i = 1; i < n - 2; i++) h2 = unchecked(h2 * 0x1003F + b[i]);
        uint h3 = 0;
        foreach (var c in Names.GetBytes(ext)) h3 = unchecked(h3 * 0x1003F + c);
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
        // A name Windows-1252 can't hold would be written as "?" and never found by the game: refuse it and say which.
        var bad = folders.SelectMany(f => (CanStore(f.Folder) ? Enumerable.Empty<string>() : new[] { f.Folder + "\\" })
            .Concat(f.Files.Where(x => !CanStore(x.Name)).Select(x => (f.Folder.Length > 0 ? f.Folder + "\\" : "") + x.Name))).ToList();
        var tooLong = folders.FirstOrDefault(f => Names.GetByteCount(f.Folder) > MaxFolderBytes).Folder;
        if (tooLong != null) throw new InvalidOperationException($"The folder name \"{tooLong}\" is too long for a BSA ({MaxFolderBytes} characters at most).");
        if (bad.Count > 0)
            throw new InvalidOperationException($"{bad.Count} file name(s) have characters a BSA can't store (Skyrim reads BSA names in the Windows-1252 code page), for example: "
                + string.Join(", ", bad.Take(5).Select(b => "\"" + b + "\"")) + ". Keep these files loose, or change the line's text in Phase 1 so its file name uses plain characters.");
        // Entries with the same source file share one copy of its data (their records point at the same bytes).
        var dataAt = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var order = new List<(string Source, long Size)>();
        foreach (var f in folders)
            foreach (var x in f.Files)
                if (!dataAt.ContainsKey(Path.GetFullPath(x.SourcePath))) { dataAt[Path.GetFullPath(x.SourcePath)] = -1; order.Add((Path.GetFullPath(x.SourcePath), x.Size)); }
        long dataBytes = order.Sum(o => o.Size);
        if (dataBytes > MaxDataBytes) throw new InvalidOperationException($"{dataBytes / 1_000_000} MB is too much for one BSA (about 2,000 MB at most).");
        int fileCount = folders.Sum(f => f.Files.Count);
        int folderNames = folders.Sum(f => Names.GetByteCount(f.Folder) + 1);
        int fileNames = folders.Sum(f => f.Files.Sum(x => Names.GetByteCount(x.Name) + 1));
        long recordsStart = 36 + 24L * folders.Count;
        long blocksLen = folders.Sum(f => 1 + Names.GetByteCount(f.Folder) + 1 + 16L * f.Files.Count);
        long dataStart = recordsStart + blocksLen + fileNames;

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
        var tmp = path + ".part";
        try
        {
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
        using (var bw = new BinaryWriter(fs, Names))
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
                blockOff += 1 + Names.GetByteCount(f.Folder) + 1 + 16L * f.Files.Count;
            }
            long dataOff = dataStart;
            foreach (var o in order) { dataAt[o.Source] = dataOff; dataOff += o.Size; }
            foreach (var f in folders)
            {
                var fb = Names.GetBytes(f.Folder);
                bw.Write((byte)(fb.Length + 1)); bw.Write(fb); bw.Write((byte)0);
                foreach (var x in f.Files)
                {
                    bw.Write(Hash(x.Name, false)); bw.Write((uint)x.Size); bw.Write((uint)dataAt[Path.GetFullPath(x.SourcePath)]);
                }
            }
            foreach (var f in folders) foreach (var x in f.Files) { bw.Write(Names.GetBytes(x.Name)); bw.Write((byte)0); }
            foreach (var o in order)
            {
                ct.ThrowIfCancellationRequested();
                using var src = File.OpenRead(o.Source);
                src.CopyTo(fs);
            }
        }
        File.Move(tmp, path, true);
        }
        catch
        {
            // a cancelled or failed write leaves no half archive behind (the earlier one, if any, is untouched)
            try { File.Delete(tmp); } catch { }
            throw;
        }
        return fileCount;
    }
}
