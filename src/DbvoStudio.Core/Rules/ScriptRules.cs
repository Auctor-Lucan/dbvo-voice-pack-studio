using System.Text;

namespace DbvoStudio.Core.Rules;

/// <summary>Outcome of checking one player line with the IdrinthDBVOTextExport rules.</summary>
public enum LineKind
{
    /// <summary>Line is usable as-is.</summary>
    Valid,
    /// <summary>Line was reformatted; the fix goes into the DBVO Fix plugin.</summary>
    Fixed,
    /// <summary>Line cannot be fixed automatically (e.g. contains * or an alias placeholder).</summary>
    Broken,
    /// <summary>Line (or its fix) is over 150 characters.</summary>
    TooLong,
    /// <summary>Text equals the record's EditorID.</summary>
    SkippedEditorId,
    /// <summary>Empty, "..." or a pure parenthetical: nothing to voice, not reported.</summary>
    Empty,
}

public sealed record LineResult(LineKind Kind, string Original, string? Final);

/// <summary>
/// Faithful port of the string rules in IdrinthDBVOTextExport.pas, using Delphi Pos/Copy/Trim semantics.
/// Validated against the xEdit output for Inigo.esp, Lucien.esp and Skyrim.esm.
/// </summary>
public static class ScriptRules
{
    public const int MaxLength = 150;

    private const char LSingle = '‘', RSingle = '’', LDouble = '“', RDouble = '”';
    private const char EmDash = '—', EnDash = '–', Ellipsis = '…';

    /// <summary>Delphi Pos: 1-based index of the first occurrence, 0 if absent.</summary>
    internal static int Pos(string sub, string s) => s.IndexOf(sub, StringComparison.Ordinal) + 1;

    /// <summary>Delphi Copy(s, index, count) with Delphi's clamping behaviour.</summary>
    internal static string Copy(string s, int index, int count)
    {
        if (index < 1) index = 1;
        if (count <= 0 || index > s.Length) return string.Empty;
        int start = index - 1;
        int len = Math.Min(count, s.Length - start);
        return s.Substring(start, len);
    }

    /// <summary>Delphi Trim: removes characters &lt;= ' ' from both ends.</summary>
    public static string Trim(string s)
    {
        int i = 0, j = s.Length;
        while (i < j && s[i] <= ' ') i++;
        while (j > i && s[j - 1] <= ' ') j--;
        return s.Substring(i, j - i);
    }

    private static bool SameText(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    public static bool IsValidLine(string val)
    {
        int len = val.Length;
        int close = Pos(")", val), open = Pos("(", val), lt = Pos("<", val);
        return !SameText("", val) && !SameText("...", val)
            && (close == len || close == 0)
            && Pos(LSingle.ToString(), val) == 0 && Pos("-", val) != 1 && Pos(RSingle.ToString(), val) == 0
            && Pos(LDouble.ToString(), val) == 0 && Pos(RDouble.ToString(), val) == 0
            && Pos(EmDash.ToString(), val) == 0 && Pos(EnDash.ToString(), val) == 0
            && Pos(Ellipsis.ToString(), val) == 0 && Pos("*", val) == 0
            && Pos("...", val) != 1 && Pos(";", val) == 0 && Pos("[", val) == 0
            && (lt == 0 || (lt > open && open > 1))
            && open != 1;
    }

    public static bool IsFixableLine(string val)
    {
        int open = Pos("(", val), lt = Pos("<", val);
        return !SameText("", val) && !SameText("...", val) && Pos("*", val) == 0
            && (lt == 0 || (lt > open && open > 1))
            && !(open == 1 && Pos(")", val) == val.Length);
    }

    /// <summary>The script's FixPlayerDialogueString.</summary>
    /// <summary>
    /// The script's tofu fix only: curly quotes, em/en dashes and the ellipsis character become plain punctuation
    /// that Skyrim's subtitle font can show (otherwise they show as squares).
    /// </summary>
    public static string Detofu(string val) => val.Replace(LSingle, '\'').Replace(RSingle, '\'')
        .Replace(LDouble, '"').Replace(RDouble, '"').Replace(EmDash.ToString(), "--").Replace(EnDash, '-').Replace(Ellipsis.ToString(), "...");

    public static string Fix(string val)
    {
        string t = val.Replace(';', ',').Replace('[', '(').Replace(']', ')')
            .Replace(LSingle, '\'').Replace(RSingle, '\'')
            .Replace(LDouble, '"').Replace(RDouble, '"')
            .Replace(EmDash.ToString(), "--").Replace(EnDash, '-')
            .Replace(Ellipsis.ToString(), "...");
        string pre = string.Empty;
        int guard = 0;
        while (!SameText(pre, t))
        {
            if (++guard > 200) break;
            pre = t;
            foreach (char ch in ".,!?:-")
            {
                while (Pos(ch.ToString(), t) == 1) t = Trim(Copy(t, 2, t.Length));
            }
            if (Pos("(", t) == 1)
            {
                string part1 = Trim(Copy(t, 1, Pos(")", t)));
                string part2 = Trim(Copy(t, Pos(")", t) + 1, t.Length));
                t = part2 + " " + part1;
            }
            int close = Pos(")", t);
            if (close != t.Length && close != 0)
            {
                int open = Pos("(", t);
                string part0 = Trim(Copy(t, 1, open - 1));
                string part1 = Trim(Copy(t, open, close - open + 1));
                string part2 = Trim(Copy(t, close + 1, t.Length));
                t = part0 + " " + part2 + " " + part1;
            }
            t = Trim(t);
            t = t.Replace(") (", " - ").Replace("  ", " ").Replace(" .", ".").Replace(" ,", ",").Replace(" :", ":");
            t = Trim(t);
        }
        if (t.Length > 0 && t[0] >= 'a' && t[0] <= 'z')
            t = char.ToUpperInvariant(t[0]) + t[1..];
        return t;
    }

    /// <summary>The script's AddValue decision for one string, without side effects.</summary>
    public static LineResult Classify(string val, string? editorId)
    {
        string trimmed = Trim(val);
        if (editorId != null && trimmed.Length > 0 && SameText(editorId, trimmed))
            return new LineResult(LineKind.SkippedEditorId, val, null);

        if (IsValidLine(val) && SameText(trimmed, val))
        {
            if (val.Length > MaxLength) return new LineResult(LineKind.TooLong, val, null);
            return new LineResult(LineKind.Valid, val, val);
        }

        if (IsFixableLine(trimmed))
        {
            string target = Fix(trimmed);
            if (target.Length > MaxLength) return new LineResult(LineKind.TooLong, val, target);
            if (IsValidLine(target)) return new LineResult(LineKind.Fixed, val, target);
        }

        if (trimmed.Length > 0 && trimmed != "..." && Pos("(", trimmed) != 1)
            return new LineResult(LineKind.Broken, val, null);

        return new LineResult(LineKind.Empty, val, null);
    }

    /// <summary>The whole line is one (parenthesis) or [bracket] group: a stage direction such as "(Laughs)" or "[Sighs]".</summary>
    public static bool IsStageDirection(string text)
    {
        var t = Trim(text);
        if (t.Length < 3) return false;
        return (t[0] == '(' && t[^1] == ')' && t.IndexOf(')') == t.Length - 1)
            || (t[0] == '[' && t[^1] == ']' && t.IndexOf(']') == t.Length - 1);
    }

    /// <summary>True when the text passes the script unchanged and fits DBVO's limit: it can be voiced as is.</summary>
    public static bool IsVoiceReady(string text)
    {
        var r = Classify(text, null);
        return r.Kind == LineKind.Valid;
    }

    /// <summary>
    /// Every reason the script would not accept this text as is, in plain words (empty when it is voice-ready).
    /// Mirrors the checks in IsValidLine / IsFixableLine, plus the 150 character limit.
    /// </summary>
    public static List<string> Explain(string text)
    {
        var why = new List<string>();
        if (text.Length == 0) return why;
        string t = Trim(text);
        if (text.Length > 0 && text[0] <= ' ') why.Add("Starts with a space: DBVO can't match the line to its file. (Removed automatically when you save.)");
        if (text.Length > 0 && text[^1] <= ' ') why.Add("Ends with a space: DBVO can't match the line to its file. (Removed automatically when you save.)");
        if (t.Length > MaxLength) why.Add($"{t.Length} characters: lines over {MaxLength} crash DBVO (TOO LONG WILL CTD).");
        if (t.Contains(';')) why.Add("Contains a semicolon (;): DBVO can't find the file. Use a comma or a period instead.");
        if (t.StartsWith("...", StringComparison.Ordinal)) why.Add("Starts with \"...\": DBVO can't pick the right file. Remove the leading dots.");
        if (t.StartsWith("-", StringComparison.Ordinal)) why.Add("Starts with \"-\": the script removes it.");
        if (t.Contains('*')) why.Add("Contains an asterisk (*): ElevenLabs reads it out as \"asterisk\". Use a trailing (parenthesis) for stage directions, e.g. \"(laughs)\".");
        if (t.Contains('[') || t.Contains(']')) why.Add("Contains [brackets]: DBVO voices them. Put the words in a trailing (parenthesis) instead, which DBVO keeps silent.");
        int open = Pos("(", t), close = Pos(")", t);
        if (open == 1) why.Add("Starts with a parenthesis: DBVO voices text in (parentheses) at the start. Move it to the end, where it stays silent.");
        else if (close != 0 && close != t.Length) why.Add("Has a parenthesis in the middle: only a (parenthesis) at the very end stays silent. Move it to the end.");
        int lt = Pos("<", t);
        if (HasPlayerAlias(t) && !HasOtherAlias(t)) { /* player-name lines are handled separately */ }
        else if (HasPlaceholder(t)) why.Add("Contains a <Alias=…> placeholder: the subtitle shows a name chosen in game, so no file can match it. Write the name out, or reword the line.");
        else if (lt != 0 && !(lt > open && open > 1)) why.Add("Contains a \"<\": the script only accepts it inside a trailing parenthesis.");
        if (t.Contains(LSingle) || t.Contains(RSingle)) why.Add("Contains curly single quotes (‘ ’): use a straight apostrophe (').");
        if (t.Contains(LDouble) || t.Contains(RDouble)) why.Add("Contains curly double quotes (“ ”): use straight quotes (\").");
        if (t.Contains(EmDash)) why.Add("Contains an em dash (—): use two hyphens (--).");
        if (t.Contains(EnDash)) why.Add("Contains an en dash (–): use a hyphen (-).");
        if (t.Contains(Ellipsis)) why.Add("Contains the single \"…\" character: use three periods (...).");
        if (why.Count == 0 && !IsVoiceReady(text))
        {
            var r = Classify(text, null);
            if (r.Kind == LineKind.Fixed && r.Final != null) why.Add("The script would reformat it as: " + r.Final);
            else if (r.Kind == LineKind.Broken) why.Add("The script can't make this line voice-ready.");
        }
        return why;
    }

    /// <summary>True when the text contains &lt;Alias=Player&gt; in any letter case. Such lines are voiced through player-name patches.</summary>
    public static bool HasPlayerAlias(string s) => s.Contains("<Alias=Player>", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the text contains an alias placeholder other than the player's name.</summary>
    public static bool HasOtherAlias(string s)
    {
        int i = 0;
        while ((i = s.IndexOf("<Alias", i, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            if (!s.AsSpan(i).StartsWith("<Alias=Player>", StringComparison.OrdinalIgnoreCase)) return true;
            i += 6;
        }
        return false;
    }

    /// <summary>True when the text contains an alias or global placeholder such as &lt;Alias=Player&gt;.</summary>
    public static bool HasPlaceholder(string s) =>
        s.Contains("<Alias", StringComparison.OrdinalIgnoreCase) || s.Contains("<Global", StringComparison.OrdinalIgnoreCase);
}
