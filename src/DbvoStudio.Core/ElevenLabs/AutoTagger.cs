using DbvoStudio.Core.Text;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using DbvoStudio.Core.Naming;

namespace DbvoStudio.Core.ElevenLabs;

public sealed record AutoTagResult(string LineId, string? Suggestion, string? Error);

/// <summary>
/// Adds ElevenLabs v3/v4 audio tags to lines the way the ElevenLabs "Enhance" button does: an AI language model
/// rewrites the text with tags only. ElevenLabs has no public API for this, so it runs through an
/// OpenAI-compatible endpoint (OpenAI, or a local model through Ollama). Any result that changes the words
/// is rejected.
/// </summary>
public sealed class AutoTagger : IDisposable
{
    public const string SystemPrompt = """
        You add audio tags for the ElevenLabs v4 text-to-speech model to single lines of video game dialogue.
        The lines are spoken by the player character in Skyrim.

        Rules:
        - DO NOT alter, add, or remove any words of the line. Keep spelling, capitalization and punctuation.
        - DO NOT invent new dialogue.
        - Only insert audio tags in square brackets, such as [curious], [sighs], [whispers], [laughs], [sarcastic],
          [excited], [nervous], [angry], [warm], [tired], [determined], [hesitant], [softly].
        - Tags describe the voice and delivery only. No sound effects, no music, no stage directions.
        - Place a tag immediately before the words it affects, or right after a sentence for a reaction ([sighs]).
        - Use at most two tags per line. If the line is neutral, add one fitting tag at the start.
        - Keep tags that are already in the line.
        - Reply ONLY with the tagged line. No quotes, no explanations.
        """;

    private readonly HttpClient _http;
    private readonly string _model;

    public AutoTagger(string endpoint, string apiKey, string model, HttpClient? http = null)
    {
        if (string.IsNullOrWhiteSpace(endpoint)) throw new InvalidOperationException("AI Auto Tag needs an AI provider, and none is set up yet. Choose one in " + FriendlyError.AiPlace + ".");
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(180) };
        _http.BaseAddress = new Uri(endpoint.TrimEnd('/') + "/");
        if (!string.IsNullOrWhiteSpace(apiKey))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        _model = string.IsNullOrWhiteSpace(model) ? "gpt-4o-mini" : model;
    }

    /// <summary>Suggest a tagged version of <paramref name="spoken"/>. Returns null suggestion if the model changed words.</summary>
    public async Task<AutoTagResult> TagAsync(string lineId, string spoken, string? context, IEnumerable<string> preferredTags, CancellationToken ct = default)
    {
        try
        {
            var user = $"Preferred tags for this voice: {string.Join(" ", preferredTags)}\n" +
                       (string.IsNullOrWhiteSpace(context) ? "" : $"Context: {context}\n") +
                       $"Line: {spoken}";
            var body = new
            {
                model = _model,
                temperature = 0.4,
                messages = new object[]
                {
                    new { role = "system", content = SystemPrompt },
                    new { role = "user", content = user },
                },
            };
            using var resp = await _http.PostAsJsonAsync("chat/completions", body, ct);
            var text = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode) return new AutoTagResult(lineId, null, FriendlyError.Http("Your AI provider", (int)resp.StatusCode, text, FriendlyError.AiPlace));
            using var doc = JsonDocument.Parse(text);
            var content = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()?.Trim() ?? "";
            content = content.Trim('"', '“', '”').Trim();
            if (SpokenText.WordsOnly(content) != SpokenText.WordsOnly(spoken))
                return new AutoTagResult(lineId, null, "The AI changed the words, so its suggestion was discarded.");
            return new AutoTagResult(lineId, content, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new AutoTagResult(lineId, null, ex is JsonException or KeyNotFoundException or IndexOutOfRangeException
                ? "The AI provider answered in a form DBVO Studio doesn't understand. Check that the endpoint is OpenAI-compatible (it usually ends in /v1)."
                : FriendlyError.From(ex, "Your AI provider"));
        }
    }

    /// <summary>A plain chat request (system + user message) to the same AI provider. Returns the reply text or an error.</summary>
    public async Task<(string? Text, string? Error)> ChatAsync(string system, string user, double temperature, CancellationToken ct = default)
    {
        try
        {
            var body = new { model = _model, temperature, messages = new object[] { new { role = "system", content = system }, new { role = "user", content = user } } };
            using var resp = await _http.PostAsJsonAsync("chat/completions", body, ct);
            var text = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode) return (null, FriendlyError.Http("Your AI provider", (int)resp.StatusCode, text, FriendlyError.AiPlace));
            using var doc = JsonDocument.Parse(text);
            return (doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()?.Trim() ?? "", null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (null, ex is JsonException or KeyNotFoundException or IndexOutOfRangeException
                ? "The AI provider answered in a form DBVO Studio doesn't understand. Check that the endpoint is OpenAI-compatible (it usually ends in /v1)."
                : FriendlyError.From(ex, "Your AI provider"));
        }
    }

    private static string Trim(string s) => s.Length > 200 ? s[..200] + "…" : s;

    public void Dispose() => _http.Dispose();
}
