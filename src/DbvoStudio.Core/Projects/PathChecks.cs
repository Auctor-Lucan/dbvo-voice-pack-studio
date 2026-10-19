using DbvoStudio.Core.Naming;

namespace DbvoStudio.Core.Projects;

public sealed record PathWarning(string Where, string Example, int Length, int Limit);

/// <summary>
/// Windows' classic 260-character path limit breaks Yakitori, FaceFXWrapper and xWMAEncode.
/// Checks the longest planned paths for Legacy names (hash names are always 16 characters).
/// </summary>
public static class PathChecks
{
    public const int Limit = 260;
    public const int WarnMargin = 10;

    public static List<PathWarning> Check(IEnumerable<string> finalLines, NamingMode mode, string wavLipFolder, IEnumerable<string> fuzFolders, string takesFolder)
    {
        var warnings = new List<PathWarning>();
        if (mode != NamingMode.Legacy) return warnings;
        var longest = finalLines.Select(LineNaming.Legacy).OrderByDescending(s => s.Length).FirstOrDefault();
        if (longest == null) return warnings;

        void Probe(string where, string folder, string ext, int extraFolderChars = 0)
        {
            if (string.IsNullOrWhiteSpace(folder)) return;
            var full = Path.Combine(Path.GetFullPath(folder), longest + ext);
            int len = full.Length + extraFolderChars;
            if (len >= Limit - WarnMargin) warnings.Add(new PathWarning(where, full, len, Limit));
        }

        Probe("WAV + LIP folder", wavLipFolder, ".wav");
        foreach (var f in fuzFolders) Probe(".fuz folder", f, ".fuz");
        // takes\<8-char id>\NN\  = 13 characters
        Probe("Takes folder", Path.Combine(takesFolder, "xxxxxxxx", "00"), ".wav");
        return warnings;
    }
}
