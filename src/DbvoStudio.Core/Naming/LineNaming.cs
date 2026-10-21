using System.Text;
using System.Text.RegularExpressions;
using DbvoStudio.Core.Rules;

namespace DbvoStudio.Core.Naming;

public enum NamingMode
{
    /// <summary>DBVO 1.0: readable filenames derived from the in-game line.</summary>
    Legacy,
    /// <summary>DBVO 2.0: xxHash64 of the in-game line.</summary>
    Dbvo2Hash,
}

/// <summary>Turns an in-game line into the file stem DBVO looks up (no extension).</summary>
public static partial class LineNaming
{
    [GeneratedRegex(@"[\s\\/:*?""<>|]")]
    private static partial Regex LegacyReplace();

    /// <summary>
    /// Legacy (DBVO 1.0): trim, keep the text before the first " (" (DBVO plays a trailing parenthesis
    /// silent), then replace each whitespace character and each of \ / : * ? " &lt; &gt; | with "_".
    /// "(M) Can we talk about our life together?" -> "(M)_Can_we_talk_about_our_life_together_"
    /// "I don't know if I trust any of this. (Whisper)" -> "I_don't_know_if_I_trust_any_of_this."
    /// </summary>
    public static string Legacy(string line)
    {
        string t = line.Trim();
        int cut = t.IndexOf(" (", StringComparison.Ordinal);
        if (cut >= 0) t = t[..cut];
        return LegacyReplace().Replace(t, "_");
    }

    /// <summary>DBVO 2.0: xxHash64 (seed 0) of the trimmed line as UTF-8, 16 uppercase hex digits.</summary>
    public static string Dbvo2(string line)
    {
        var bytes = Encoding.UTF8.GetBytes(line.Trim());
        return XxHash64.Hash(bytes).ToString("X16");
    }

    public static string Stem(string line, NamingMode mode) => mode == NamingMode.Legacy ? Legacy(line) : Dbvo2(line);

    /// <summary>
    /// Key for duplicate detection and reference matching. Legacy keys ignore case because Windows file
    /// names do; hash keys are exact.
    /// </summary>
    public static string Key(string line, NamingMode mode) =>
        mode == NamingMode.Legacy ? Legacy(line).ToUpperInvariant() : Dbvo2(line);

    /// <summary>The spoken part of a line: what DBVO voices (trailing parenthesis removed).</summary>
    public static string SpokenPart(string line)
    {
        string t = line.Trim();
        int cut = t.IndexOf(" (", StringComparison.Ordinal);
        return cut >= 0 ? t[..cut] : t;
    }
}

/// <summary>Helpers for the spoken (ElevenLabs) text.</summary>
public static partial class SpokenText
{
    [GeneratedRegex(@"\[[^\]]*\]")]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"\s+([,.!?;:])")]
    private static partial Regex SpaceBeforePunct();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"^\s*(\([^)]*\)|\[[^\]]*\])\s*")]
    private static partial Regex LeadingMarker();

    /// <summary>Remove [audio tags]: the text FaceFXWrapper gets for lip sync.</summary>
    public static string StripTags(string text)
    {
        var t = TagRegex().Replace(text, " ");
        t = SpaceBeforePunct().Replace(t, "$1");
        return Spaces().Replace(t, " ").Trim();
    }

    /// <summary>
    /// Default spoken text for a final in-game line: the part DBVO voices, without leading markers such as
    /// "(M)" or "[AFT]" that the voice model would read as words or tags.
    /// </summary>
    public static string DefaultFor(string finalLine)
    {
        // A stage direction that is voiced, such as "(Laughs)", is performed as an audio tag: "[laughs]".
        if (Rules.ScriptRules.IsStageDirection(finalLine))
        {
            var inner = finalLine.Trim()[1..^1].Trim();
            return "[" + inner.ToLowerInvariant() + "]";
        }
        var t = LineNaming.SpokenPart(finalLine);
        string prev;
        do
        {
            prev = t;
            t = LeadingMarker().Replace(t, string.Empty);
        } while (t != prev);
        return t.Trim();
    }

    /// <summary>The words of a text, ignoring tags, case and punctuation: used to reject AI auto-tag results that change words.</summary>
    public static string WordsOnly(string text)
    {
        var t = StripTags(text).ToLowerInvariant();
        var sb = new StringBuilder();
        foreach (char c in t)
        {
            if (char.IsLetterOrDigit(c) || c == '\'') sb.Append(c);
            else if (sb.Length > 0 && sb[^1] != ' ') sb.Append(' ');
        }
        return sb.ToString().Trim();
    }

    /// <summary>Add tags at the start of a text, skipping any already present.</summary>
    public static string AddPrefixTags(string text, IEnumerable<string> tags)
    {
        var existing = TagRegex().Matches(text).Select(m => m.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var add = tags.Select(NormalizeTag).Where(t => t.Length > 2 && !existing.Contains(t)).ToList();
        if (add.Count == 0) return text;
        return string.Join(" ", add) + " " + text.TrimStart();
    }

    /// <summary>
    /// Add tags at the start of the line, after the tags it already starts with ("[angry] [whispers] text"),
    /// or in place of them when <paramref name="replaceLeading"/> is set ("[whispers] text").
    /// </summary>
    public static string AddTagsAfterLeading(string text, IEnumerable<string> tags, bool replaceLeading)
    {
        var m = Regex.Match(text, @"^\s*((?:\[[^\]]+\]\s*)*)");
        var leading = TagRegex().Matches(m.Groups[1].Value).Select(x => x.Value).ToList();
        var rest = text[m.Length..].TrimStart();
        var keep = replaceLeading ? new List<string>() : leading;
        var inLine = TagRegex().Matches(rest).Select(x => x.Value).Concat(keep).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var add = tags.Select(NormalizeTag).Where(t => t.Length > 2 && !inLine.Contains(t)).ToList();
        if (add.Count == 0 && (!replaceLeading || leading.Count == 0)) return text;
        return string.Join(" ", keep.Concat(add).Append(rest).Where(x => x.Length > 0));
    }

    /// <summary>Remove the given tags from anywhere in the text.</summary>
    public static string RemoveTags(string text, IEnumerable<string> tags)
    {
        var t = text;
        foreach (var tag in tags.Select(NormalizeTag))
            t = Regex.Replace(t, Regex.Escape(tag) + @"\s*", string.Empty, RegexOptions.IgnoreCase);
        return Spaces().Replace(t, " ").Trim();
    }

    public static string NormalizeTag(string tag)
    {
        var t = tag.Trim().Trim('[', ']').Trim();
        return "[" + t + "]";
    }
}
