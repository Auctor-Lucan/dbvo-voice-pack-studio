using System.Text.RegularExpressions;

namespace DbvoStudio.Core.Books;

/// <summary>The AI "spoken formatting" pass for a book (IVBOS rules), through the AI provider set up for auto-tagging.</summary>
public static class BookAi
{
    public const string SystemPrompt = """
        You prepare the text of a Skyrim book, letter or journal for a text-to-speech voice (ElevenLabs).
        This is spoken FORMATTING, not proofreading. Follow these rules exactly:

        - Keep every word as written. Do not correct spelling, grammar, lore, dialect or style. Do not add or remove sentences.
        - Keep alias markup such as <Alias=Player> or <Alias.Pronoun=Player> exactly as it is, in the same place.
        - Keep the separators: " ---- By Author ----- " after a title, " ----- " between title and body, " ----- Sender" at the end.
        - Pacing: be generous in turning suitable sentence-final periods into "..." for hesitation, suspense, breath and dramatic
          beats. Do not touch periods of abbreviations (Vol., Mr., Dr., St.). Keep existing ellipses.
        - Emphasis: write a few strongly stressed words or short phrases in ALL CAPS (threats, revelations, climaxes).
          Never make a new ALL-CAPS word shorter than four letters. Keep ALL CAPS that is already there.
        - Asides in (parentheses) or [brackets] become " - aside - " (sentence-final: " - aside." without the closing dash).
        - Keep paragraph breaks (blank lines) where they are.
        - Reply with the formatted text only. No quotes, no notes, no explanations.
        """;

    public static string UserPrompt(string title, string text) => $"Title: {title}\n\nText:\n{text}";

    /// <summary>Strip code fences or quotes some models wrap the answer in.</summary>
    public static string Clean(string reply)
    {
        var s = reply.Trim();
        s = Regex.Replace(s, @"^```[a-z]*\s*|\s*```$", "");
        if (s.Length > 1 && s[0] == '"' && s[^1] == '"') s = s[1..^1];
        return s.Trim();
    }
}
