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

    /// <summary>True when the text contains an alias or global placeholder such as &lt;Alias=Player&gt;.</summary>
    public static bool HasPlaceholder(string s) =>
        s.Contains("<Alias", StringComparison.OrdinalIgnoreCase) || s.Contains("<Global", StringComparison.OrdinalIgnoreCase);
}
