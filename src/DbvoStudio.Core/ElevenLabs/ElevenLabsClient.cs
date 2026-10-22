using DbvoStudio.Core.Text;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DbvoStudio.Core.ElevenLabs;

public sealed class ElevenVoice
{
    [JsonPropertyName("voice_id")] public string VoiceId { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("category")] public string? Category { get; set; }
}

public sealed class ElevenModel
{
    [JsonPropertyName("model_id")] public string ModelId { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("can_do_text_to_speech")] public bool CanDoTextToSpeech { get; set; } = true;
    [JsonPropertyName("max_characters_request_free_user")] public int? MaxCharsFree { get; set; }
    [JsonPropertyName("max_characters_request_subscribed_user")] public int? MaxCharsSubscribed { get; set; }
    [JsonPropertyName("can_use_style")] public bool CanUseStyle { get; set; }
    [JsonPropertyName("can_use_speaker_boost")] public bool CanUseSpeakerBoost { get; set; }
    [JsonPropertyName("token_cost_factor")] public double TokenCostFactor { get; set; } = 1;
    [JsonPropertyName("can_do_voice_conversion")] public bool CanDoVoiceConversion { get; set; }
}

public sealed class ElevenSubscription
{
    [JsonPropertyName("tier")] public string? Tier { get; set; }
    [JsonPropertyName("character_count")] public long CharacterCount { get; set; }
    [JsonPropertyName("character_limit")] public long CharacterLimit { get; set; }
    [JsonPropertyName("next_character_count_reset_unix")] public long? NextReset { get; set; }
}

/// <summary>Voice settings sent with each request. Null values are omitted.</summary>
public sealed class VoiceSettings
{
    [JsonPropertyName("stability")] public double? Stability { get; set; }
    [JsonPropertyName("similarity_boost")] public double? SimilarityBoost { get; set; }
    [JsonPropertyName("style")] public double? Style { get; set; }
    [JsonPropertyName("use_speaker_boost")] public bool? UseSpeakerBoost { get; set; }
    [JsonPropertyName("speed")] public double? Speed { get; set; }
}

public sealed class PronunciationLocator
{
    [JsonPropertyName("pronunciation_dictionary_id")] public string DictionaryId { get; set; } = "";
    [JsonPropertyName("version_id")] public string? VersionId { get; set; }
}

public sealed class TtsRequest
{
    [JsonPropertyName("text")] public string Text { get; set; } = "";
    [JsonPropertyName("model_id")] public string ModelId { get; set; } = "eleven_v4";
    [JsonPropertyName("voice_settings")] public VoiceSettings? VoiceSettings { get; set; }
    [JsonPropertyName("seed")] public long? Seed { get; set; }
    [JsonPropertyName("language_code")] public string? LanguageCode { get; set; }
    [JsonPropertyName("apply_text_normalization")] public string? ApplyTextNormalization { get; set; }
    [JsonPropertyName("pronunciation_dictionary_locators")] public List<PronunciationLocator>? PronunciationDictionaries { get; set; }
    /// <summary>Request stitching: the text before and after this one, so long readings flow across requests (not on eleven_v3).</summary>
    [JsonPropertyName("previous_text")] public string? PreviousText { get; set; }
    [JsonPropertyName("next_text")] public string? NextText { get; set; }

    /// <summary>Not sent in the body: becomes the output_format query parameter (e.g. mp3_44100_128, pcm_44100).</summary>
    [JsonIgnore] public string OutputFormat { get; set; } = "mp3_44100_128";
    [JsonIgnore] public string VoiceId { get; set; } = "";
}

public sealed class ElevenLabsException : Exception
{
    public ElevenLabsException(HttpStatusCode status, string message, bool retryable) : base(message)
    {
        Status = status;
        Retryable = retryable;
    }

    public HttpStatusCode Status { get; }
    public bool Retryable { get; }
}

/// <summary>Minimal ElevenLabs REST client: voices, models, subscription, text-to-speech, pronunciation dictionaries.</summary>
public sealed class ElevenLabsClient : IDisposable
{
    public const string DefaultBase = "https://api.elevenlabs.io/";
    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    private readonly HttpClient _http;
    private readonly bool _owns;

    public ElevenLabsClient(string apiKey, HttpClient? http = null, string baseUrl = DefaultBase)
    {
        _owns = http == null;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        _http.BaseAddress ??= new Uri(baseUrl);
        _http.DefaultRequestHeaders.Remove("xi-api-key");
        _http.DefaultRequestHeaders.Add("xi-api-key", apiKey);
    }

    public async Task<List<ElevenVoice>> GetVoicesAsync(CancellationToken ct = default)
    {
        var doc = await GetJsonAsync("v1/voices", ct);
        return doc.RootElement.GetProperty("voices").Deserialize<List<ElevenVoice>>() ?? new();
    }

    public async Task<List<ElevenModel>> GetModelsAsync(CancellationToken ct = default)
    {
        var doc = await GetJsonAsync("v1/models", ct);
        return (doc.RootElement.Deserialize<List<ElevenModel>>() ?? new()).Where(m => m.CanDoTextToSpeech).ToList();
    }

    /// <summary>Models that can convert speech to speech (Voice Changer).</summary>
    public async Task<List<ElevenModel>> GetStsModelsAsync(CancellationToken ct = default)
    {
        var doc = await GetJsonAsync("v1/models", ct);
        return (doc.RootElement.Deserialize<List<ElevenModel>>() ?? new()).Where(m => m.CanDoVoiceConversion).ToList();
    }

    /// <summary>
    /// Speech-to-speech: revoice an existing recording with another voice (multipart upload).
    /// Returns raw audio bytes in the requested output format.
    /// </summary>
    public async Task<byte[]> SpeechToSpeechAsync(string voiceId, string modelId, byte[] audio, string fileName, VoiceSettings? settings,
        long? seed, bool removeBackgroundNoise, string outputFormat, CancellationToken ct = default)
    {
        var url = $"v1/speech-to-speech/{Uri.EscapeDataString(voiceId)}?output_format={Uri.EscapeDataString(outputFormat)}";
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(audio);
        file.Headers.ContentType = new MediaTypeHeaderValue(fileName.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) ? "audio/mpeg" : "audio/wav");
        form.Add(file, "audio", fileName);
        form.Add(new StringContent(modelId), "model_id");
        if (settings != null) form.Add(new StringContent(JsonSerializer.Serialize(settings, Json)), "voice_settings");
        if (seed != null) form.Add(new StringContent(seed.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)), "seed");
        if (removeBackgroundNoise) form.Add(new StringContent("true"), "remove_background_noise");
        using var msg = new HttpRequestMessage(HttpMethod.Post, url) { Content = form };
        msg.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("audio/*"));
        using var resp = await _http.SendAsync(msg, ct);
        if (!resp.IsSuccessStatusCode) throw await ErrorAsync(resp, ct);
        return await resp.Content.ReadAsByteArrayAsync(ct);
    }

    public async Task<ElevenSubscription> GetSubscriptionAsync(CancellationToken ct = default)
    {
        var doc = await GetJsonAsync("v1/user/subscription", ct);
        return doc.RootElement.Deserialize<ElevenSubscription>() ?? new();
    }

    /// <summary>Generate speech. Returns raw audio bytes in the requested output format.</summary>
    public async Task<byte[]> TextToSpeechAsync(TtsRequest req, CancellationToken ct = default)
    {
        var url = $"v1/text-to-speech/{Uri.EscapeDataString(req.VoiceId)}?output_format={Uri.EscapeDataString(req.OutputFormat)}";
        using var msg = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(req, options: Json),
        };
        msg.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("audio/*"));
        using var resp = await _http.SendAsync(msg, ct);
        if (!resp.IsSuccessStatusCode) throw await ErrorAsync(resp, ct);
        return await resp.Content.ReadAsByteArrayAsync(ct);
    }

    public sealed record DictionaryRule(string Type, string StringToReplace, string? Alias, string? Phoneme, string? Alphabet);

    /// <summary>Create a pronunciation dictionary from rules (alias or IPA phoneme). Returns (id, versionId).</summary>
    public async Task<(string Id, string VersionId)> CreateDictionaryAsync(string name, IEnumerable<DictionaryRule> rules, CancellationToken ct = default)
    {
        var body = new
        {
            name,
            rules = rules.Select(r => r.Type == "phoneme"
                ? (object)new { type = "phoneme", string_to_replace = r.StringToReplace, phoneme = r.Phoneme, alphabet = r.Alphabet ?? "ipa" }
                : new { type = "alias", string_to_replace = r.StringToReplace, alias = r.Alias }),
        };
        using var resp = await _http.PostAsJsonAsync("v1/pronunciation-dictionaries/add-from-rules", body, ct);
        if (!resp.IsSuccessStatusCode) throw await ErrorAsync(resp, ct);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        return (doc.RootElement.GetProperty("id").GetString()!, doc.RootElement.GetProperty("version_id").GetString()!);
    }

    private async Task<JsonDocument> GetJsonAsync(string path, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(path, ct);
        if (!resp.IsSuccessStatusCode) throw await ErrorAsync(resp, ct);
        return JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
    }

    private static async Task<ElevenLabsException> ErrorAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        string body = "";
        try { body = await resp.Content.ReadAsStringAsync(ct); } catch { }
        string detail = body;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("detail", out var d))
                detail = d.ValueKind == JsonValueKind.Object && d.TryGetProperty("message", out var m) ? m.GetString() ?? body : d.ToString();
        }
        catch { }
        int code = (int)resp.StatusCode;
        bool retry = code == 429 || code >= 500 || body.Contains("too_many_concurrent_requests", StringComparison.OrdinalIgnoreCase);
        string friendly = code switch
        {
            401 => "ElevenLabs rejected the API key.",
            429 => "ElevenLabs is busy or the concurrency limit was reached.",
            _ => $"ElevenLabs error {code}.",
        };
        _ = friendly; _ = detail;
        return new ElevenLabsException(resp.StatusCode, FriendlyError.Http("ElevenLabs", code, body, FriendlyError.ElevenLabsPlace), retry);
    }

    public void Dispose()
    {
        if (_owns) _http.Dispose();
    }
}
