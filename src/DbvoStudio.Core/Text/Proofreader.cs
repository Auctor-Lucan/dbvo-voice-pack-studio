using System.Text.Json;
using System.Text.RegularExpressions;

namespace DbvoStudio.Core.Text;

/// <summary>One AI proofreading suggestion for a line (null Text = nothing to fix).</summary>
public sealed record ProofSuggestion(string Key, string? Text, string Kind, string Why);

/// <summary>
/// Proofread lines (Phase 1): AI suggestions for spelling, grammar and punctuation, a batch of lines per request.
/// Game markup such as &lt;Alias=Player&gt; and (Persuade) is swapped for numbered placeholders before the text is sent,
/// so the AI never sees or changes it; a suggestion that lost, doubled or moved a placeholder out of order is dropped.
/// </summary>
public static class Proofreader
{
    public const int BatchSize = 50;

    public const string SystemPrompt = """
        You proofread player dialogue lines from Skyrim mods. Fix only real mistakes:
        spelling, typos, missing or doubled words, wrong word forms (their/there, it's/its, lose/loose), broken grammar and
        punctuation (including doubled spaces and a missing apostrophe).

        The lines are matched to voice files by a script with strict rules, so a correction must never:
        - use a semicolon (;) — use a comma or a period instead;
        - use [square brackets], an asterisk (*), curly quotes (‘ ’ “ ”), an em or en dash (— –) or the single … character
          (write ' " -- - ... instead);
        - start the line with "...", "-" or "(";
        - put a (parenthesis) anywhere but at the very end of the line;
        - make the line longer than 150 characters.
        Keep the line's own punctuation style otherwise: don't swap commas, periods and dashes around without a mistake.

        Do NOT change:
        - the character's voice: slang, bluntness, archaic or formal phrasing, dialect, deliberate misspellings that show
          an accent, Khajiit speaking of themselves in the third person, Orc or Argonian speech patterns;
        - Elder Scrolls names and words (Dibella, Mauhulakh, Whiterun, septims, skooma, Thu'um, etc.);
        - British or American spelling: keep whichever the line uses;
        - ellipses (...), dashes and the meaning or tone of the line. Do not rephrase what is merely awkward.
        - placeholders written as ⟦1⟧, ⟦2⟧ …: keep each one exactly once, in the same order.

        If a change is only a matter of style and not a mistake, you may still suggest it, but give it kind "style".

        Input: a JSON array of {"i": number, "t": text}.
        Reply with a JSON array only, one entry per line that needs a change, and nothing else:
        [{"i": number, "t": "corrected text", "kind": "spelling" | "grammar" | "punctuation" | "style", "why": "a few words"}]
        Lines that need no change are left out. Reply [] when none do.
        """;

    // <…>, (…) with one level of (…) inside, […]
    private static readonly Regex Markup = new(@"<[^<>]*>|\((?:[^()<>]|<[^<>]*>|\([^()]*\))*\)|\[[^\[\]]*\]", RegexOptions.Compiled);
    private static readonly Regex Placeholder = new(@"⟦(\d+)⟧", RegexOptions.Compiled);

    /// <summary>The text with its markup replaced by ⟦1⟧, ⟦2⟧ …, and the markup in order.</summary>
    public static (string Text, List<string> Tokens) Protect(string text)
    {
        var tokens = new List<string>();
        var t = Markup.Replace(text, m => { tokens.Add(m.Value); return $"⟦{tokens.Count}⟧"; });
        return (t, tokens);
    }

    /// <summary>Puts the markup back; null when the placeholders aren't each there once, in order.</summary>
    public static string? Restore(string text, IReadOnlyList<string> tokens)
    {
        var found = Placeholder.Matches(text).Select(m => int.Parse(m.Groups[1].Value)).ToList();
        if (!found.SequenceEqual(Enumerable.Range(1, tokens.Count))) return null;
        return Placeholder.Replace(text, m => tokens[int.Parse(m.Groups[1].Value) - 1]);
    }

    /// <summary>The user message for a batch: (key, text) pairs numbered from 0.</summary>
    public static (string Prompt, List<(string Key, string Text, List<string> Tokens)> Sent) BuildBatch(IReadOnlyList<(string Key, string Text)> lines)
    {
        var sent = new List<(string, string, List<string>)>();
        var arr = new List<object>();
        for (int i = 0; i < lines.Count; i++)
        {
            var (t, tokens) = Protect(lines[i].Text);
            sent.Add((lines[i].Key, lines[i].Text, tokens));
            arr.Add(new { i, t });
        }
        return (JsonSerializer.Serialize(arr, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }), sent);
    }

    /// <summary>Reads the AI's reply into one suggestion per sent line (Text null = nothing to fix or unusable).</summary>
    public static List<ProofSuggestion> ParseReply(string reply, List<(string Key, string Text, List<string> Tokens)> sent)
    {
        var byIndex = new Dictionary<int, ProofSuggestion>();
        var json = ExtractArray(reply) ?? throw new FormatException("The AI provider's answer isn't the list DBVO Studio asked for.");
        using var doc = JsonDocument.Parse(json);
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Object) continue;
            if (!e.TryGetProperty("i", out var ie) || !ie.TryGetInt32(out var i) || i < 0 || i >= sent.Count) continue;
            if (!e.TryGetProperty("t", out var te) || te.ValueKind != JsonValueKind.String) continue;
            var kind = e.TryGetProperty("kind", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString()!.Trim().ToLowerInvariant() : "spelling";
            if (kind is not ("spelling" or "grammar" or "punctuation" or "style")) kind = "style";
            var why = e.TryGetProperty("why", out var w) && w.ValueKind == JsonValueKind.String ? w.GetString()!.Trim() : "";
            var restored = Restore(te.GetString()!.Trim(), sent[i].Tokens);
            if (restored != null) restored = ScriptSafe(sent[i].Text, restored);
            if (restored == null || restored == sent[i].Text.Trim()) continue;
            byIndex[i] = new ProofSuggestion(sent[i].Key, restored, kind, why.Length > 160 ? why[..160] + "…" : why);
        }
        return sent.Select((s, i) => byIndex.TryGetValue(i, out var x) ? x : new ProofSuggestion(s.Key, null, "", "")).ToList();
    }

    /// <summary>The JSON array in a reply: past a reasoning model's &lt;think&gt; block, code fences and any prose around it.</summary>
    /// <summary>
    /// A suggestion as the DBVO xEdit script will take it: its plain-punctuation swaps are made (; → , and the tofu
    /// characters), and a suggestion the script would then voice worse than the line as it is (reformat it, mark it
    /// BROKEN or TOO LONG) is dropped (null).
    /// </summary>
    public static string? ScriptSafe(string original, string suggestion)
    {
        var s = Rules.ScriptRules.Detofu(suggestion).Replace(';', ',').Trim();
        while (s.Contains("  ")) s = s.Replace("  ", " ");
        static int Rank(string t) => Rules.ScriptRules.Classify(t, null).Kind switch
        {
            Rules.LineKind.Valid or Rules.LineKind.Empty or Rules.LineKind.SkippedEditorId => 0,
            Rules.LineKind.Fixed => 1,
            _ => 2, // Broken, TooLong
        };
        return Rank(s) <= Rank(original.Trim()) ? s : null;
    }

    private static string? ExtractArray(string reply)
    {
        var s = Regex.Replace(reply, @"<think>[\s\S]*?</think>", "", RegexOptions.IgnoreCase);
        s = Regex.Replace(s.Trim(), @"```[a-z]*", "");
        for (int a = s.IndexOf('['); a >= 0; a = s.IndexOf('[', a + 1))
        {
            // The first [ that starts a whole, valid array (prose such as "line [3]" before it is passed over).
            try
            {
                var reader = new Utf8JsonReader(System.Text.Encoding.UTF8.GetBytes(s[a..]), new JsonReaderOptions { AllowTrailingCommas = true });
                using var doc = JsonDocument.ParseValue(ref reader);
                if (doc.RootElement.ValueKind == JsonValueKind.Array && (doc.RootElement.GetArrayLength() == 0 || doc.RootElement[0].ValueKind == JsonValueKind.Object))
                    return doc.RootElement.GetRawText();
            }
            catch (JsonException) { }
        }
        return null;
    }
}
