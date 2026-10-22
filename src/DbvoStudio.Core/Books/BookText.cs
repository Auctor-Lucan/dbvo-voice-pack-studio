using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using DbvoStudio.Core.Naming;

namespace DbvoStudio.Core.Books;

/// <summary>
/// Book text: from the game's markup to readable text, and from readable text to what a voice reads
/// (the IVBOS spoken-text rules: separators, asides, volume numbers, regnal names, dates).
/// </summary>
public static partial class BookText
{
    // ------------------------------------------------------------------ in-game text

    [GeneratedRegex(@"<img\s[^>]*src\s*=\s*['""]?img://[^'"">]*?/Illuminated_Letters/([A-Za-z])_[^'"">]*['""]?[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex DropCapRx();
    [GeneratedRegex(@"<[^<>]*>")]
    private static partial Regex TagRx();
    [GeneratedRegex(@"\[pagebreak\]", RegexOptions.IgnoreCase)]
    private static partial Regex PageBreakRx();
    [GeneratedRegex(@"<\s*(br\s*/?|/p|p(\s[^>]*)?|/div|div(\s[^>]*)?)\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockTagRx();
    [GeneratedRegex(@"face\s*=\s*['""]?\$?(FalmerFont|DwemerFont|DaedricFont|MageScriptFont|DragonFont|SkyrimBooks_UnreadableFont)", RegexOptions.IgnoreCase)]
    private static partial Regex UnreadableFontRx();

    /// <summary>True when a markup token must be kept verbatim: it is in-game alias markup (contains "alias").</summary>
    public static bool IsAlias(string tag) => tag.Contains("alias", StringComparison.OrdinalIgnoreCase);

    /// <summary>The alias tokens of a text, in order (&lt;Alias=Player&gt;, &lt;Alias.Pronoun=Player&gt;…).</summary>
    public static List<string> Aliases(string text) => TagRx().Matches(text).Select(m => m.Value).Where(IsAlias).ToList();

    /// <summary>
    /// The book's text as the game shows it: drop-cap images become their letter, layout markup and [pagebreak] go,
    /// alias markup stays exactly as it is, paragraphs are kept as blank lines.
    /// </summary>
    public static string Clean(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        var s = raw.Replace("\r\n", "\n").Replace('\r', '\n');
        s = DropCapRx().Replace(s, m => "\u0001" + m.Groups[1].Value.ToUpperInvariant());
        s = PageBreakRx().Replace(s, "\n\n");
        s = BlockTagRx().Replace(s, m => m.Value.StartsWith("<br", StringComparison.OrdinalIgnoreCase) ? "\n" : "\n\n");
        s = TagRx().Replace(s, m => IsAlias(m.Value) ? m.Value : " ");
        s = s.Replace(' ', ' ');
        // A drop-cap letter joins the word it starts ("T" + "he" → "The").
        s = Regex.Replace(s, "\u0001([A-Z])\\s*", "$1");
        var lines = s.Split('\n').Select(l => Regex.Replace(l, @"[ \t]+", " ").Trim()).ToList();
        var sb = new StringBuilder();
        int blank = 0;
        foreach (var l in lines)
        {
            if (l.Length == 0) { blank++; continue; }
            if (sb.Length > 0) sb.Append(blank > 0 ? "\n\n" : "\n");
            sb.Append(l);
            blank = 0;
        }
        return sb.ToString().Trim();
    }

    /// <summary>Share of the readable text written in a script the player cannot read (Falmer, Dwemer, Daedric, mage script, dragon runes).</summary>
    public static double UnreadableShare(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return 0;
        int total = 0, unreadable = 0;
        bool inside = false;
        foreach (Match m in Regex.Matches(raw, @"<[^<>]*>|[^<]+"))
        {
            var v = m.Value;
            if (v.StartsWith('<'))
            {
                if (Regex.IsMatch(v, @"^<\s*font\b", RegexOptions.IgnoreCase)) inside = UnreadableFontRx().IsMatch(v);
                else if (Regex.IsMatch(v, @"^<\s*/\s*font", RegexOptions.IgnoreCase)) inside = false;
                continue;
            }
            int n = v.Count(char.IsLetter);
            total += n;
            if (inside) unreadable += n;
        }
        return total == 0 ? 0 : unreadable / (double)total;
    }

    public static int WordCount(string text) => Regex.Matches(text, @"[\p{L}\p{N}']+").Count;

    // ------------------------------------------------------------------ spoken text

    public const string TitleSep = " ----- ";
    public const string AuthorSep = " ---- ";

    [GeneratedRegex(@"^\s*(?:written\s+)?by\s+(?<name>[^\n]{2,60})$", RegexOptions.IgnoreCase)]
    private static partial Regex ByLineRx();

    /// <summary>
    /// An author credit at the start of the text ("by Mymophonus" on its own line, the way books credit authors).
    /// Returns the name and the text without that line.
    /// </summary>
    public static (string? Author, string Remainder) SplitByLine(string text, string title)
    {
        text = DropRepeatedTitle(text, title);
        var paras = text.Split('\n');
        for (int i = 0; i < Math.Min(paras.Length, 6); i++)
        {
            var p = paras[i].Trim();
            if (p.Length == 0) continue;
            // Skip the title repeated at the top of the page.
            if (Same(p, title) || (title.Length > 0 && Same(p.Replace(",", ""), title.Replace(",", "")))) continue;
            var m = ByLineRx().Match(p);
            if (m.Success)
            {
                var name = m.Groups["name"].Value.Trim().TrimEnd('.', ',');
                if (WordCount(name) <= 6 && char.IsUpper(name[0]))
                    return (name, string.Join('\n', paras.Where((_, j) => j != i)).Trim());
            }
            if (WordCount(p) > 8) break;
        }
        return (null, text);
    }

    /// <summary>Remove the title when the page repeats it at the top, also when it is spread over several lines ("Roads of the North" / "Vol. II").</summary>
    public static string DropRepeatedTitle(string body, string title)
    {
        var t = Norm(title).ToLowerInvariant();
        if (t.Length == 0) return body;
        var lines = body.Split('\n');
        var acc = "";
        for (int i = 0; i < Math.Min(lines.Length, 6); i++)
        {
            if (lines[i].Trim().Length == 0) continue;
            acc = (acc + " " + Norm(lines[i]).ToLowerInvariant()).Trim();
            if (acc == t) return string.Join('\n', lines.Skip(i + 1)).TrimStart();
            if (!t.StartsWith(acc + " ", StringComparison.Ordinal)) break;
        }
        return body;
    }

    private static bool Same(string a, string b) => string.Equals(Norm(a), Norm(b), StringComparison.OrdinalIgnoreCase);
    private static string Norm(string s) => Regex.Replace(s, @"[^\p{L}\p{N}]+", " ").Trim();

    [GeneratedRegex(@"^(?:[-~–—]+\s*)?(?<name>[A-Z][\p{L}'\-]+(?:\s+(?:the\s+|of\s+|de\s+|al-)?[A-Z][\p{L}'\-]+){0,3}|[A-Z]\.?)\s*[.,]?$")]
    private static partial Regex SignatureRx();
    [GeneratedRegex(@"^(?:sincerely|yours|regards|faithfully|with love|love|your friend|your loving|signed|best|respectfully|forever yours|your servant|cheers)\b[^\n]{0,40}[,.]?$", RegexOptions.IgnoreCase)]
    private static partial Regex ClosingRx();

    /// <summary>
    /// A letter's signature: the sender's name on the last line (optionally after "Sincerely," or a dash).
    /// Returns the sender as written and the text without it. Only clear cases are recognized.
    /// </summary>
    public static (string? Sender, string Remainder) SplitSignature(string text)
    {
        var lines = text.TrimEnd().Split('\n').ToList();
        if (lines.Count < 2) return (null, text);
        var last = lines[^1].Trim();
        if (last.Length == 0 || last.Length > 48) return (null, text);
        var m = SignatureRx().Match(last);
        if (!m.Success) return (null, text);
        var name = m.Groups["name"].Value.Trim();
        // A single word line is only a signature when it follows a closing ("Sincerely,") or starts with a dash.
        bool dashed = Regex.IsMatch(last, @"^[-~–—]");
        bool closing = lines.Count >= 2 && ClosingRx().IsMatch(lines[^2].Trim());
        if (WordCount(name) == 1 && !dashed && !closing && !last.EndsWith(',')) return (null, text);
        if (Regex.IsMatch(name, @"^(The|A|An|And|But|I|You|We|He|She|It|They|This|That|Farewell|Goodbye)$")) return (null, text);
        lines.RemoveAt(lines.Count - 1);
        return (last.TrimStart('-', '~', '–', '—', ' ').TrimEnd(), string.Join('\n', lines).TrimEnd());
    }

    /// <summary>
    /// Spoken text for a new book (the IVBOS structure): "Title ---- By Author ----- Body ----- Sender", with the
    /// deterministic spoken rules applied. Paragraph breaks stay as blank lines so parts can break there.
    /// </summary>
    public static string DefaultSpoken(string title, string inGame, out string? author, out string? sender)
    {
        var (a, rest) = SplitByLine(inGame, title);
        author = a;
        var (s, body) = SplitSignature(rest);
        sender = s;
        body = DropRepeatedTitle(body, title);
        var sb = new StringBuilder();
        if (title.Trim().Length > 0) sb.Append(title.Trim());
        if (author != null) sb.Append(AuthorSep).Append("By ").Append(author);
        if (sb.Length > 0 && body.Length > 0) sb.Append(TitleSep.TrimEnd()).Append('\n');
        sb.Append(body);
        if (sender != null) sb.Append('\n').Append(TitleSep.Trim()).Append(' ').Append(sender);
        return ApplyRules(sb.ToString().Trim());
    }

    /// <summary>The deterministic spoken-text rules: asides to dashes, volume numbers and regnal names in words, dates spoken.</summary>
    public static string ApplyRules(string text)
    {
        var s = Asides(text);
        s = VolumeNumbers(s);
        s = RegnalNames(s);
        s = Dates(s);
        return s;
    }

    // ---- asides: "(for the most part)" → "- for the most part -", sentence-final without the closing dash

    [GeneratedRegex(@"\s*[\(\[]\s*(?<inner>[^\(\)\[\]\n]{1,300}?)\s*[\)\]](?<after>\s*[.!?…,;:]*)")]
    private static partial Regex AsideRx();

    public static string Asides(string text) => AsideRx().Replace(text, m =>
    {
        var inner = m.Groups["inner"].Value.Trim();
        // Audio tags ([sighs]) and alias-like markup are not asides.
        if (m.Value.TrimStart().StartsWith('[') && Regex.IsMatch(inner, @"^[a-z][a-z \-]{1,30}$")) return m.Value;
        var after = m.Groups["after"].Value;
        var punct = after.Trim();
        if (punct.Length > 0 && ".!?…".Contains(punct[0])) return $" - {inner}{punct}";
        if (punct.StartsWith(',') || punct.StartsWith(';') || punct.StartsWith(':')) return $" - {inner} -{punct}";
        return $" - {inner} -{after}";
    });

    // ---- Roman numerals

    private static readonly (string R, int V)[] RomanTable =
        { ("M", 1000), ("CM", 900), ("D", 500), ("CD", 400), ("C", 100), ("XC", 90), ("L", 50), ("XL", 40), ("X", 10), ("IX", 9), ("V", 5), ("IV", 4), ("I", 1) };

    public static int? FromRoman(string r)
    {
        if (string.IsNullOrEmpty(r) || !Regex.IsMatch(r, "^[IVXLCDM]+$")) return null;
        int v = 0, i = 0;
        foreach (var (sym, val) in RomanTable)
            while (r.AsSpan(i).StartsWith(sym)) { v += val; i += sym.Length; }
        return i == r.Length && v > 0 && ToRoman(v) == r ? v : null;
    }

    public static string ToRoman(int v)
    {
        var sb = new StringBuilder();
        foreach (var (sym, val) in RomanTable) while (v >= val) { sb.Append(sym); v -= val; }
        return sb.ToString();
    }

    private static readonly string[] Ones = { "Zero", "One", "Two", "Three", "Four", "Five", "Six", "Seven", "Eight", "Nine", "Ten", "Eleven", "Twelve",
        "Thirteen", "Fourteen", "Fifteen", "Sixteen", "Seventeen", "Eighteen", "Nineteen" };
    private static readonly string[] Tens = { "", "", "Twenty", "Thirty", "Forty", "Fifty", "Sixty", "Seventy", "Eighty", "Ninety" };
    private static readonly string[] OrdOnes = { "Zeroth", "First", "Second", "Third", "Fourth", "Fifth", "Sixth", "Seventh", "Eighth", "Ninth", "Tenth",
        "Eleventh", "Twelfth", "Thirteenth", "Fourteenth", "Fifteenth", "Sixteenth", "Seventeenth", "Eighteenth", "Nineteenth" };
    private static readonly string[] OrdTens = { "", "", "Twentieth", "Thirtieth", "Fortieth", "Fiftieth", "Sixtieth", "Seventieth", "Eightieth", "Ninetieth" };

    /// <summary>"Two", "Twenty-One", "Two-hundred-Ninety-Seven" (the IVBOS year style).</summary>
    public static string Cardinal(int n)
    {
        if (n < 0) return "Minus-" + Cardinal(-n);
        if (n < 20) return Ones[n];
        if (n < 100) return Tens[n / 10] + (n % 10 > 0 ? "-" + Ones[n % 10] : "");
        if (n < 1000) return Ones[n / 100] + "-hundred" + (n % 100 > 0 ? "-" + Cardinal(n % 100) : "");
        return Cardinal(n / 1000) + "-thousand" + (n % 1000 > 0 ? "-" + Cardinal(n % 1000) : "");
    }

    public static string Ordinal(int n)
    {
        if (n < 20) return OrdOnes[n];
        if (n < 100) return n % 10 == 0 ? OrdTens[n / 10] : Tens[n / 10] + "-" + OrdOnes[n % 10];
        return n % 100 == 0 ? Cardinal(n) + "th" : Cardinal(n - n % 100) + "-" + Ordinal(n % 100);
    }

    [GeneratedRegex(@"\b(?<label>Vol\.|Vol|Volume|Part|Book|Chapter|Tome|Act|Section|Canto|Pt\.)\s+(?<num>[IVXLC]+|\d{1,3})\b(?!['’]\w)")]
    private static partial Regex VolumeRx();

    /// <summary>"Vol. II" → "Volume Two", "Part 3" → "Part Three".</summary>
    public static string VolumeNumbers(string text) => VolumeRx().Replace(text, m =>
    {
        var label = m.Groups["label"].Value;
        var num = m.Groups["num"].Value;
        int? v = char.IsDigit(num[0]) ? int.Parse(num, CultureInfo.InvariantCulture) : FromRoman(num);
        if (v is null or 0 or > 200) return m.Value;
        // "Book I" etc. only when the numeral is not the pronoun I at a sentence start ("Book I read").
        if (num == "I" && label is not ("Vol." or "Vol" or "Volume" or "Part" or "Pt.") && !IsEndOfTitle(text, m.Index + m.Length)) return m.Value;
        var spoken = label switch { "Vol." or "Vol" => "Volume", "Pt." => "Part", _ => label };
        return $"{spoken} {Cardinal(v.Value)}";
    });

    private static bool IsEndOfTitle(string text, int at)
    {
        var rest = text.AsSpan(at).TrimStart(' ');
        return rest.Length == 0 || rest[0] is '\n' or ',' or ':' or '-' or '.' or ')' or ';';
    }

    private static readonly HashSet<string> Dynasties = new(StringComparer.Ordinal)
    {
        "Septim", "Mede", "Pelagius", "Uriel", "Cephorus", "Antiochus", "Kintyra", "Magnus", "Potema", "Tiber", "Cassynder", "Thules", "Katariah",
        "Attrebus", "Leovic", "Reman", "Gaiden", "Hestra", "Moricar", "Varen", "Gothryd", "Eadwyre", "Lysandus", "Camoran", "Kvatch", "Durcorach",
    };

    [GeneratedRegex(@"\b(?<name>[A-Z][a-z]{2,})\s+(?<num>[IVX]{1,5})\b(?!['’]\w)")]
    private static partial Regex RegnalRx();

    private static readonly HashSet<string> NotNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Volume", "Vol", "Part", "Book", "Chapter", "Tome", "Act", "Section", "Canto", "Map", "Article", "Rule", "Step", "Level", "Type", "Class", "Grade",
        "Rank", "Tier", "Mark", "Case", "Item", "Number", "Year", "Era", "Then", "When", "And", "But", "So", "Now", "If", "As", "Than", "That", "What", "Where",
        "Shall", "Will", "Could", "Should", "Would", "May", "Might", "Must", "Can", "Did", "Do", "Am", "Was", "Have", "Had", "Hope", "Think", "Know", "Say",
    };

    /// <summary>"Uriel Septim VII" → "Uriel Septim the Seventh". The pronoun I is only read as "the First" after a known dynasty.</summary>
    public static string RegnalNames(string text) => RegnalRx().Replace(text, m =>
    {
        var name = m.Groups["name"].Value;
        var num = m.Groups["num"].Value;
        if (NotNames.Contains(name)) return m.Value;
        if (num == "I" && !Dynasties.Contains(name)) return m.Value;
        if (num.Length == 1 && num != "I" && !Dynasties.Contains(name)) return m.Value; // "Map V", "Plan X": only known names for single letters
        var v = FromRoman(num);
        if (v is null or > 40) return m.Value;
        return $"{name} the {Ordinal(v.Value)}";
    });

    // ---- dates

    public static readonly string[] Months = { "Morning Star", "Sun's Dawn", "First Seed", "Rain's Hand", "Second Seed", "Mid Year", "Midyear", "Sun's Height",
        "Last Seed", "Hearthfire", "Heartfire", "Frostfall", "Sun's Dusk", "Evening Star" };
    private static readonly string MonthAlt = string.Join("|", Months.Select(m => Regex.Escape(m).Replace("'", "['’]")));
    private static readonly string[] EraNames = { "", "First Era", "Second Era", "Third Era", "Fourth Era" };

    private static readonly Regex DayMonthRx = new(@"\b(?<d>\d{1,2})(?:st|nd|rd|th)?(?:\s+day)?(?:\s+of)?\s+(?<m>" + MonthAlt + @")\b(?:,?\s+(?<era>[1-4])E,?\s*(?<y>\d{1,4}))?", RegexOptions.Compiled);
    private static readonly Regex MonthDayRx = new(@"\b(?<m>" + MonthAlt + @")\s+(?<d>\d{1,2})(?:st|nd|rd|th)?\b(?:,?\s+(?<era>[1-4])E,?\s*(?<y>\d{1,4}))?", RegexOptions.Compiled);
    private static readonly Regex EraYearRx = new(@"\b(?<era>[1-4])E,?\s*(?<y>\d{1,4})\b", RegexOptions.Compiled);
    private static readonly Regex NamedEraYearRx = new(@"\b(?<era>First|Second|Third|Fourth) Era,?\s+(?:Year\s+)?(?<y>\d{1,4})\b", RegexOptions.Compiled);

    /// <summary>"6 Morning Star 4E 297" → "----- Sixth of Morning Star, Fourth Era, Year Two-hundred-Ninety-Seven -----".</summary>
    public static string Dates(string text)
    {
        string Era(Match m) => m.Groups["era"].Success && m.Groups["era"].Value.Length > 0
            ? $", {EraNames[int.Parse(m.Groups["era"].Value, CultureInfo.InvariantCulture)]}, Year {Cardinal(int.Parse(m.Groups["y"].Value, CultureInfo.InvariantCulture))}" : "";
        string Month(string m) => m.Replace('’', '\'') is var x && (x == "Midyear" ? "Mid Year" : x == "Heartfire" ? "Hearthfire" : x) is var y ? y : x;
        var spans = new List<(int Start, int End, string Spoken)>();
        void Collect(Regex rx, Func<Match, string?> f)
        {
            foreach (Match m in rx.Matches(text))
            {
                if (spans.Any(s => m.Index < s.End && m.Index + m.Length > s.Start)) continue;
                var spoken = f(m);
                if (spoken != null) spans.Add((m.Index, m.Index + m.Length, spoken));
            }
        }
        Collect(DayMonthRx, m => int.Parse(m.Groups["d"].Value, CultureInfo.InvariantCulture) is var d && d is >= 1 and <= 31
            ? $"{Ordinal(d)} of {Month(m.Groups["m"].Value)}{Era(m)}" : null);
        Collect(MonthDayRx, m => int.Parse(m.Groups["d"].Value, CultureInfo.InvariantCulture) is var d && d is >= 1 and <= 31
            ? $"{Ordinal(d)} of {Month(m.Groups["m"].Value)}{Era(m)}" : null);
        Collect(EraYearRx, m => $"{EraNames[int.Parse(m.Groups["era"].Value, CultureInfo.InvariantCulture)]}, Year {Cardinal(int.Parse(m.Groups["y"].Value, CultureInfo.InvariantCulture))}");
        Collect(NamedEraYearRx, m => $"{m.Groups["era"].Value} Era, Year {Cardinal(int.Parse(m.Groups["y"].Value, CultureInfo.InvariantCulture))}");
        if (spans.Count == 0) return text;
        var sb = new StringBuilder();
        int at = 0;
        foreach (var (start0, end, spoken0) in spans.OrderBy(s => s.Start))
        {
            // "On the 17th of Last Seed": the separator goes before "the", which belongs to the date.
            int start = start0; var spoken = spoken0;
            if (start >= 4 && text.AsSpan(start - 4, 4).Equals("the ", StringComparison.OrdinalIgnoreCase) && (start == 4 || !char.IsLetter(text[start - 5])))
            { spoken = text.Substring(start - 4, 3) + " " + spoken; start -= 4; }
            sb.Append(text, at, start - at);
            bool sepBefore = Regex.IsMatch(sb.ToString(), @"-{5}\s*$");
            bool sepAfter = Regex.IsMatch(text.AsSpan(end).ToString(), @"^\s*[.,;:]?\s*-{5}");
            var trimmed = sb.ToString().TrimEnd(' ');
            if (!sepBefore) { sb.Clear().Append(trimmed); if (sb.Length > 0 && sb[^1] != '\n') sb.Append(' '); sb.Append("----- "); }
            sb.Append(spoken);
            // Punctuation right after the date stays before the separator.
            int e = end;
            while (e < text.Length && ".,;:".Contains(text[e])) { sb.Append(text[e]); e++; }
            if (!sepAfter && e < text.Length) sb.Append(" -----");
            at = e;
        }
        sb.Append(text, at, text.Length - at);
        return sb.ToString();
    }

    // ------------------------------------------------------------------ checks

    public sealed record Issue(string Kind, string Message, bool Serious);

    [GeneratedRegex(@"\b[\p{Lu}]{2,}(?:['’][\p{Lu}]+)?\b")]
    private static partial Regex CapsWordRx();

    /// <summary>
    /// Checks of a spoken text against the in-game text (IVBOS validators). They flag, they never block:
    /// alias markup intact and in order, no leftover markup, no new ALL-CAPS words under four letters,
    /// no unconverted asides or numeric dates, and word changes beyond the spoken rules (silent proofreading).
    /// </summary>
    public static List<Issue> Check(string inGame, string spoken, string? raw = null, string? title = null)
    {
        var issues = new List<Issue>();
        // The baseline: the game's text (with its title) as the spoken rules make it.
        var baseline = inGame.Length == 0 ? "" : title != null ? DefaultSpoken(title, inGame, out _, out _) : ApplyRules(inGame);
        var srcAliases = inGame.Length > 0 ? Aliases(baseline) : Aliases(raw ?? "");
        var outAliases = Aliases(spoken);
        if (!srcAliases.SequenceEqual(outAliases, StringComparer.Ordinal))
            issues.Add(new("alias", srcAliases.Count == outAliases.Count
                ? "Alias markup changed (it must stay exactly as in the game, e.g. <Alias=Player>)."
                : $"Alias markup missing or added: the game text has {srcAliases.Count}, the spoken text {outAliases.Count}.", true));
        foreach (Match m in TagRx().Matches(spoken))
            if (!IsAlias(m.Value)) { issues.Add(new("markup", $"Leftover markup {Short(m.Value)} would be read out.", true)); break; }
        if (PageBreakRx().IsMatch(spoken)) issues.Add(new("markup", "[pagebreak] would be read out.", true));
        var srcCaps = new HashSet<string>(CapsWordRx().Matches(baseline + "\n" + (title ?? "")).Select(m => m.Value), StringComparer.Ordinal);
        var badCaps = CapsWordRx().Matches(spoken).Select(m => m.Value)
            .Where(w => w.Count(char.IsLetter) < 4 && !srcCaps.Contains(w) && FromRoman(w) == null).Distinct().ToList();
        if (badCaps.Count > 0)
            issues.Add(new("caps", $"New ALL-CAPS word{(badCaps.Count > 1 ? "s" : "")} shorter than four letters: {string.Join(", ", badCaps.Take(6))}. Keep emphasis to words of four letters or more.", true));
        if (spoken.Contains('(') || spoken.Contains(')'))
            issues.Add(new("aside", "Parentheses left in: asides read better as \"- aside -\".", false));
        if (EraYearRx.IsMatch(spoken) || DayMonthRx.IsMatch(spoken) || MonthDayRx.IsMatch(spoken))
            issues.Add(new("date", "A date is still in numbers (e.g. 4E 201): spoken dates read better.", false));
        if (Regex.IsMatch(spoken, @"\bVol\.\s"))
            issues.Add(new("vol", "\"Vol.\" is often misread: write \"Volume\".", false));
        var changed = inGame.Length == 0 ? new List<string>() : WordChanges(baseline, spoken);
        if (changed.Count > 0)
            issues.Add(new("words", $"Words differ from the game text: {string.Join("; ", changed.Take(4))}{(changed.Count > 4 ? $" (+{changed.Count - 4} more)" : "")}. Fine if intended, but check nothing was \"proofread\" by accident.", false));
        return issues;
    }

    private static string Short(string s) => s.Length > 30 ? s[..30] + "…" : s;

    /// <summary>
    /// Word-level differences between a baseline (the in-game text with the spoken rules applied) and the spoken text,
    /// ignoring case, punctuation, tags and separators. Each entry reads "old → new".
    /// </summary>
    public static List<string> WordChanges(string inGame, string spoken)
    {
        static List<string> Words(string t) => Regex.Matches(SpokenText.StripTags(t).Replace('’', '\''), @"[\p{L}\p{N}']+")
            .Select(m => m.Value.Trim('\'').ToLowerInvariant()).Where(w => w.Length > 0).ToList();
        var a = Words(inGame);
        var b = Words(spoken);
        // Words the spoken structure adds ("By" before an author) are not changes.
        var ops = Diff(a, b);
        var result = new List<string>();
        foreach (var (del, ins) in ops)
        {
            if (del.Count == 0 && ins.All(w => w is "by" or "the" or "volume" or "year" or "era")) continue;
            if (del.Count == 0 && ins.Count == 0) continue;
            var left = string.Join(" ", del.Take(6));
            var right = string.Join(" ", ins.Take(6));
            result.Add(del.Count == 0 ? $"+\"{right}\"" : ins.Count == 0 ? $"−\"{left}\"" : $"\"{left}\" → \"{right}\"");
        }
        return result;
    }

    /// <summary>Changed runs between two word lists (a simple LCS diff, fine for book-sized texts).</summary>
    private static List<(List<string> Del, List<string> Ins)> Diff(List<string> a, List<string> b)
    {
        // Trim common head and tail first: most edits are local.
        int head = 0;
        while (head < a.Count && head < b.Count && a[head] == b[head]) head++;
        int tail = 0;
        while (tail < a.Count - head && tail < b.Count - head && a[a.Count - 1 - tail] == b[b.Count - 1 - tail]) tail++;
        var x = a.GetRange(head, a.Count - head - tail);
        var y = b.GetRange(head, b.Count - head - tail);
        var res = new List<(List<string>, List<string>)>();
        if (x.Count == 0 && y.Count == 0) return res;
        if ((long)x.Count * y.Count > 4_000_000) { res.Add((x.Take(6).ToList(), y.Take(6).ToList())); return res; }
        var dp = new int[x.Count + 1, y.Count + 1];
        for (int i = x.Count - 1; i >= 0; i--)
            for (int j = y.Count - 1; j >= 0; j--)
                dp[i, j] = x[i] == y[j] ? dp[i + 1, j + 1] + 1 : Math.Max(dp[i + 1, j], dp[i, j + 1]);
        int p = 0, q = 0;
        List<string> del = new(), ins = new();
        void Flush() { if (del.Count > 0 || ins.Count > 0) { res.Add((del, ins)); del = new(); ins = new(); } }
        while (p < x.Count || q < y.Count)
        {
            if (p < x.Count && q < y.Count && x[p] == y[q]) { Flush(); p++; q++; }
            else if (q < y.Count && (p == x.Count || dp[p, q + 1] >= dp[p + 1, q])) ins.Add(y[q++]);
            else del.Add(x[p++]);
        }
        Flush();
        return res;
    }

    /// <summary>One physical line (for topic files): paragraphs joined with spaces.</summary>
    public static string OneLine(string text) => Regex.Replace(text.Replace("\r", ""), @"\s*\n\s*", " ").Trim();
}
