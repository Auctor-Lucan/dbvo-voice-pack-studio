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

    private static readonly Regex Markup = new(@"<[^<>]*>|\((?:[^()<>]|<[^<>]*>)*\)|\[[^\[\]]*\]", RegexOptions.Compiled);
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
            if (restored == null || restored == sent[i].Text.Trim()) continue;
            byIndex[i] = new ProofSuggestion(sent[i].Key, restored, kind, why.Length > 160 ? why[..160] + "…" : why);
        }
        return sent.Select((s, i) => byIndex.TryGetValue(i, out var x) ? x : new ProofSuggestion(s.Key, null, "", "")).ToList();
    }

    private static string? ExtractArray(string reply)
    {
        var s = Regex.Replace(reply.Trim(), @"^```[a-z]*\s*|\s*```$", "");
        int a = s.IndexOf('['), b = s.LastIndexOf(']');
        return a >= 0 && b > a ? s[a..(b + 1)] : null;
    }
}
