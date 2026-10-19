using System.Runtime.InteropServices;

namespace DbvoStudio.Core.Projects;

/// <summary>Hardlinks on Windows (NTFS, same volume), copying otherwise.</summary>
public static class FileLinks
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateHardLinkW")]
    private static extern bool CreateHardLink(string newFile, string existingFile, IntPtr security);

    /// <summary>Place <paramref name="source"/> at <paramref name="target"/>. Returns true if a hardlink was made.</summary>
    public static bool LinkOrCopy(string source, string target, bool overwrite)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(target))!);
        if (File.Exists(target))
        {
            if (!overwrite) return false;
            File.Delete(target);
        }
        if (OperatingSystem.IsWindows())
        {
            try
            {
                if (CreateHardLink(LongPath(target), LongPath(source), IntPtr.Zero)) return true;
            }
            catch (EntryPointNotFoundException) { }
            catch (DllNotFoundException) { }
        }
        File.Copy(source, target, overwrite);
        return false;
    }

    private static string LongPath(string p)
    {
        var full = Path.GetFullPath(p);
        if (!OperatingSystem.IsWindows() || full.StartsWith(@"\\?\")) return full;
        return full.StartsWith(@"\\") ? @"\\?\UNC\" + full[2..] : @"\\?\" + full;
    }
}
