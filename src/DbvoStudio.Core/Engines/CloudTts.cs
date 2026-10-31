using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DbvoStudio.Core.Naming;

namespace DbvoStudio.Core.Engines;

// Voice engines besides ElevenLabs and the two local servers, ported from DBVO Understudy. Each one turns a line into
// 16-bit mono PCM at 44.1 kHz, so trimming, LIP, Check takes, Edit take and export work the same for every engine.
// [tags] reach each engine the way it understands them (see EngineText):
//   fish      free-form [cues] in the text (S2 models), spoken as instructions, counted as text
//   inworld   free-form [steering] and non-verbal [laugh] [sigh]… in the text (inworld-tts-2), counted as text
//   cartesia  tags leave the text; a leading tag that names an emotion picks generation_config.emotion; [laughter] stays
//   minimax   tags leave the text; a leading tag picks voice_setting.emotion; on speech-2.8 [laughs] [sighs]… become (laughs)…
//   openai    tags leave the text; their words become the "instructions" (gpt-4o-mini-tts; local servers ignore them)

// ------------------------------------------------------------------------------------------------ settings

public sealed class FishSettings
{
    /// <summary>The voice model's ID in your Fish Audio account (reference_id).</summary>
    public string VoiceId { get; set; } = "";
    public string VoiceName { get; set; } = "";
    /// <summary>s2.1-pro (default) or s2-pro.</summary>
    public string Model { get; set; } = "s2.1-pro";
    public double Speed { get; set; } = 1.0;
    /// <summary>0 = Fish Audio's default.</summary>
    public double Temperature { get; set; }
    public int Concurrency { get; set; } = 2;
}

public sealed class InworldSettings
{
    public string VoiceId { get; set; } = "";
    public string VoiceName { get; set; } = "";
    /// <summary>inworld-tts-2 follows [steering]; inworld-tts-2-flash ignores it (non-verbal tags still work).</summary>
    public string ModelId { get; set; } = "inworld-tts-2";
    public double SpeakingRate { get; set; } = 1.0;
    public int Concurrency { get; set; } = 2;
}

public sealed class CartesiaSettings
{
    public string VoiceId { get; set; } = "";
    public string VoiceName { get; set; } = "";
    public string ModelId { get; set; } = "sonic-3.6";
    /// <summary>"en", or a locale like "en-GB" (sent as locale).</summary>
    public string Language { get; set; } = "en";
    /// <summary>0.6–1.5 (Cartesia treats it as guidance).</summary>
    public double Speed { get; set; } = 1.0;
    /// <summary>0.5–2.0.</summary>
    public double Volume { get; set; } = 1.0;
    /// <summary>Emotion for lines whose tags name none; empty: the model reads the text.</summary>
    public string Emotion { get; set; } = "";
    public int Concurrency { get; set; } = 2;
}

public sealed class MiniMaxSettings
{
    public string VoiceId { get; set; } = "";
    public string VoiceName { get; set; } = "";
    /// <summary>speech-2.8-hd (default), speech-2.8-turbo, speech-2.6-hd / -turbo.</summary>
    public string Model { get; set; } = "speech-2.8-hd";
    public double Speed { get; set; } = 1.0;
    public double Volume { get; set; } = 1.0;
    public int Pitch { get; set; }
    /// <summary>Emotion for lines whose tags name none; empty: the model reads the text.</summary>
    public string Emotion { get; set; } = "";
    public int Concurrency { get; set; } = 2;
}

public sealed class OpenAiTtsSettings
{
    /// <summary>https://api.openai.com, or an OpenAI-compatible server such as http://127.0.0.1:5123 (Chatterbox TTS API).</summary>
    public string BaseUrl { get; set; } = "https://api.openai.com";
    /// <summary>gpt-4o-mini-tts on OpenAI; whatever the server expects otherwise.</summary>
    public string Model { get; set; } = "gpt-4o-mini-tts";
    /// <summary>A built-in voice (marin, cedar…) or the server's voice name.</summary>
    public string Voice { get; set; } = "marin";
    /// <summary>Delivery instructions for every line; the words of a line's [tags] are added to them.</summary>
    public string Instructions { get; set; } = "";
    public double Speed { get; set; } = 1.0;
    /// <summary>Extra JSON fields for local servers, sent as they are (Chatterbox: {"exaggeration":0.6,"cfg_weight":0.5}).</summary>
    public string ExtraJson { get; set; } = "";
    public int Concurrency { get; set; } = 2;
}

// ------------------------------------------------------------------------------------------------ catalog

/// <summary>What an engine does with [tags], in words the UI and guide share.</summary>
public enum TagUse { Performed, InText, SomeSounds, Emotion, Instructions, None }

public sealed record EngineInfo(string Id, string Name, bool Cloud, TagUse Tags, string KeyHint);

public static class TtsEngines
{
    public static readonly EngineInfo[] All =
    {
        new("elevenlabs", "ElevenLabs", true, TagUse.Performed, ""),
        new("fish", "Fish Audio", true, TagUse.InText, "fish.audio → API Keys"),
        new("inworld", "Inworld", true, TagUse.InText, "Inworld portal → API keys: the Base64 credential"),
        new("cartesia", "Cartesia", true, TagUse.Emotion, "play.cartesia.ai → API Keys"),
        new("minimax", "MiniMax", true, TagUse.Emotion, "platform.minimax.io → API Keys"),
        new("openai", "OpenAI-compatible", true, TagUse.Instructions, "platform.openai.com → API keys (none needed for a server on this PC)"),
        new("omnivoice", "OmniVoice (local)", false, TagUse.SomeSounds, ""),
        new("voxcpm", "VoxCPM (local)", false, TagUse.None, ""),
    };

    /// <summary>The engines this file adds (they need an API key, except an OpenAI-compatible server on this PC).</summary>
    public static readonly string[] CloudIds = { "fish", "inworld", "cartesia", "minimax", "openai" };

    public static bool IsCloud(string? engine) => CloudIds.Contains(engine ?? "");

    public static string Name(string? engine) => All.FirstOrDefault(e => e.Id == engine)?.Name ?? "ElevenLabs";

    /// <summary>Whether an OpenAI-compatible address is this PC or the local network (no key needed).</summary>
    public static bool IsLocalAddress(string url) =>
        Uri.TryCreate(WithScheme(url), UriKind.Absolute, out var u) && (u.IsLoopback || u.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            || u.Host.StartsWith("192.168.") || u.Host.StartsWith("10.")
            || System.Text.RegularExpressions.Regex.IsMatch(u.Host, @"^172\.(1[6-9]|2[0-9]|3[01])\."));

    /// <summary>"localhost:5123" as typed → http://localhost:5123.</summary>
    public static string WithScheme(string url)
    {
        var t = (url ?? "").Trim();
        return t.Length == 0 || t.Contains("://") ? t : "http://" + t;
    }

    /// <summary>Cartesia's emotion values (docs.cartesia.ai, Volume, Speed and Emotion; English only).</summary>
    public static readonly string[] CartesiaEmotions =
    {
        "neutral", "happy", "excited", "enthusiastic", "elated", "euphoric", "triumphant", "amazed", "surprised", "flirtatious",
        "curious", "content", "peaceful", "serene", "calm", "grateful", "affectionate", "trust", "sympathetic", "anticipation",
        "mysterious", "angry", "mad", "outraged", "frustrated", "agitated", "threatened", "disgusted", "contempt", "envious",
        "sarcastic", "ironic", "sad", "dejected", "melancholic", "disappointed", "hurt", "guilty", "bored", "tired", "rejected",
        "nostalgic", "wistful", "apologetic", "hesitant", "insecure", "confused", "resigned", "anxious", "panicked", "alarmed",
        "scared", "proud", "confident", "distant", "skeptical", "contemplative", "determined",
    };

    /// <summary>MiniMax's emotion values; fluent and whisper only on the speech-2.6 models.</summary>
    public static readonly string[] MiniMaxEmotions = { "happy", "sad", "angry", "fearful", "disgusted", "surprised", "calm", "fluent", "whisper" };

    /// <summary>Common tag words to an engine's nearest emotion, for tags that aren't an emotion name themselves.</summary>
    private static readonly Dictionary<string, string> CartesiaNear = new(StringComparer.OrdinalIgnoreCase)
    {
        ["flirty"] = "flirtatious", ["seductive"] = "flirtatious", ["tender"] = "affectionate", ["warm"] = "affectionate",
        ["menacing"] = "threatened", ["sinister"] = "threatened", ["baleful"] = "contempt", ["mocking"] = "sarcastic",
        ["whispers"] = "mysterious", ["whispering"] = "mysterious", ["tearful"] = "sad", ["crying"] = "sad", ["urgent"] = "alarmed",
        ["shouting"] = "outraged", ["shouts"] = "outraged", ["nervous"] = "anxious", ["pleading"] = "scared", ["weary"] = "tired",
        ["sighs"] = "resigned", ["thoughtful"] = "contemplative", ["laughs"] = "happy", ["amused"] = "happy", ["cheerful"] = "happy",
        ["cold"] = "distant", ["fearful"] = "scared", ["afraid"] = "scared", ["annoyed"] = "frustrated", ["bitter"] = "contempt",
    };

    private static readonly Dictionary<string, string> MiniMaxNear = new(StringComparer.OrdinalIgnoreCase)
    {
        ["flirty"] = "happy", ["amused"] = "happy", ["cheerful"] = "happy", ["excited"] = "happy", ["laughs"] = "happy",
        ["tearful"] = "sad", ["crying"] = "sad", ["weary"] = "sad", ["pleading"] = "sad", ["sighs"] = "sad",
        ["menacing"] = "angry", ["sinister"] = "angry", ["baleful"] = "angry", ["shouting"] = "angry", ["annoyed"] = "angry",
        ["nervous"] = "fearful", ["scared"] = "fearful", ["afraid"] = "fearful", ["urgent"] = "fearful",
        ["mocking"] = "disgusted", ["sarcastic"] = "disgusted", ["bitter"] = "disgusted",
        ["gasps"] = "surprised", ["tender"] = "calm", ["thoughtful"] = "calm", ["whispers"] = "whisper", ["whispering"] = "whisper",
    };

    // OmniVoice's documented non-verbal tags, and the tag words Studio users write for them.
    private static readonly Dictionary<string, string> OmniSounds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["laughter"] = "[laughter]", ["laughs"] = "[laughter]", ["laugh"] = "[laughter]", ["laughing"] = "[laughter]",
        ["sigh"] = "[sigh]", ["sighs"] = "[sigh]", ["sighing"] = "[sigh]",
    };

    // MiniMax speech-2.8 interjections, written in parentheses where the sound should be.
    private static readonly Dictionary<string, string> MiniMaxSounds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["laughs"] = "(laughs)", ["laugh"] = "(laughs)", ["laughter"] = "(laughs)", ["chuckles"] = "(chuckle)", ["chuckle"] = "(chuckle)",
        ["sighs"] = "(sighs)", ["sigh"] = "(sighs)", ["gasps"] = "(gasps)", ["gasp"] = "(gasps)", ["clears throat"] = "(clears throat)",
        ["coughs"] = "(coughs)", ["cough"] = "(coughs)", ["sneezes"] = "(sneezes)",
    };

    private static readonly Regex Tag = new(@"\[([^\]]*)\]");
    private static readonly Regex Leading = new(@"^\s*((?:\[[^\]]*\]\s*)+)");

    /// <summary>The words of a text's tags: "[British Extremely Baleful] [Thoughtful]" → "British Extremely Baleful, Thoughtful".</summary>
    public static string TagWords(string text) => string.Join(", ", Tag.Matches(text).Select(m => m.Groups[1].Value.Trim()).Where(w => w.Length > 0));

    /// <summary>
    /// What an engine gets for a line's spoken text: the text itself (tags kept, translated or removed), and the emotion or
    /// instructions the tags turned into. defaultEmotion: the voice pack's own, used when the tags name none.
    /// </summary>
    public static (string Text, string? Emotion, string? Instructions) EngineText(string engine, string spoken, string model = "", string defaultEmotion = "", string instructions = "")
    {
        switch (engine)
        {
            case "fish":
            case "inworld":
                return (spoken, null, null);
            case "omnivoice":
                return (Clean(Tag.Replace(spoken, m => OmniSounds.TryGetValue(m.Groups[1].Value.Trim(), out var s) ? " " + s + " " : " ")), null, null);
            case "cartesia":
            {
                var emotion = EmotionFrom(spoken, CartesiaEmotions, CartesiaNear) ?? Nz(defaultEmotion);
                var text = Tag.Replace(spoken, m => IsLaugh(m.Groups[1].Value) ? " [laughter] " : " ");
                return (Clean(text), emotion, null);
            }
            case "minimax":
            {
                bool v26 = model.StartsWith("speech-2.6", StringComparison.OrdinalIgnoreCase);
                var emotion = EmotionFrom(spoken, MiniMaxEmotions, MiniMaxNear) ?? Nz(defaultEmotion);
                if (emotion is "fluent" or "whisper" && !v26) emotion = null; // only the 2.6 models have these
                bool sounds = model.StartsWith("speech-2.8", StringComparison.OrdinalIgnoreCase);
                var text = Tag.Replace(spoken, m => sounds && MiniMaxSounds.TryGetValue(m.Groups[1].Value.Trim(), out var s) ? " " + s + " " : " ");
                return (Clean(text), emotion, null);
            }
            case "openai":
            {
                var words = TagWords(spoken);
                var instr = string.Join(" ", new[] { instructions.Trim(), words.Length > 0 ? $"Deliver this line: {words}." : "" }.Where(x => x.Length > 0));
                return (SpokenText.StripTags(spoken), null, instr.Length > 0 ? instr : null);
            }
            default:
                return (SpokenText.StripTags(spoken), null, null);
        }
    }

    private static string? Nz(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static bool IsLaugh(string w) => w.Trim().ToLowerInvariant() is "laughter" or "laughs" or "laugh" or "laughing";

    private static string Clean(string t) => SpokenText.StripTags(t.Replace("[laughter]", "\u0001L\u0001").Replace("[sigh]", "\u0001S\u0001"))
        .Replace("\u0001L\u0001", "[laughter]").Replace("\u0001S\u0001", "[sigh]");

    /// <summary>The first emotion the leading tags name: an emotion itself, or a word close to one ("Baleful" → contempt).</summary>
    private static string? EmotionFrom(string spoken, string[] emotions, Dictionary<string, string> near)
    {
        var m = Leading.Match(spoken);
        if (!m.Success) return null;
        foreach (Match t in Tag.Matches(m.Groups[1].Value))
            foreach (var word in Regex.Split(t.Groups[1].Value, @"[\s,]+").Where(w => w.Length > 0).Reverse()) // "Extremely Baleful": the last word matters
            {
                var e = emotions.FirstOrDefault(x => x.Equals(word, StringComparison.OrdinalIgnoreCase));
                if (e != null) return e;
                if (near.TryGetValue(word, out var n)) return n;
            }
        return null;
    }
}

// ------------------------------------------------------------------------------------------------ clients

/// <summary>An engine's HTTP error. Retryable when nothing was made (busy, unavailable).</summary>
public sealed class CloudTtsException : Exception
{
    public CloudTtsException(string message, bool retryable) : base(message) { Retryable = retryable; }
    public bool Retryable { get; }
}

/// <summary>One take from a cloud engine: 16-bit mono PCM at 44.1 kHz, and the characters billed when the engine says.</summary>
public sealed record CloudAudio(byte[] Pcm44100, int? BilledCharacters);

/// <summary>Fish Audio, Inworld, Cartesia, MiniMax and OpenAI-compatible text-to-speech.</summary>
public sealed class CloudTtsClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _engine;
    private readonly string? _key;
    private const string KeyPlace = "Global Settings → General & tools → Voice engines";

    public CloudTtsClient(string engine, string? key, HttpClient? http = null)
    {
        _engine = engine;
        _key = string.IsNullOrWhiteSpace(key) ? null : key.Trim();
        // OpenAI-compatible servers are often local ones on a CPU or small GPU: a long Books part can take a while.
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(engine == "openai" ? 15 : 3) };
    }

    public void Dispose() => _http.Dispose();

    private string Name => TtsEngines.Name(_engine);

    private string Need() => _key ?? throw new InvalidOperationException($"{Name} needs your API key, and none is saved. Add it in {KeyPlace}.");

    /// <summary>The line's text as EngineText made it, plus the emotion or instructions from its tags.</summary>
    public Task<CloudAudio> SpeakAsync(string text, string? emotion, string? instructions, Projects.VoicePack v, CancellationToken ct) => _engine switch
    {
        "fish" => Fish(text, v.Fish, ct),
        "inworld" => Inworld(text, v.Inworld, ct),
        "cartesia" => Cartesia(text, emotion, v.Cartesia, ct),
        "minimax" => MiniMax(text, emotion, v.MiniMax, ct),
        "openai" => OpenAi(text, instructions, v.OpenAi, ct),
        _ => throw new InvalidOperationException($"Unknown voice engine '{_engine}'."),
    };

    private static string Base(string? url, string fallback) => TtsEngines.WithScheme(string.IsNullOrWhiteSpace(url) ? fallback : url.Trim()).TrimEnd('/');

    private async Task<HttpResponseMessage> PostAsync(string url, JsonNode body, Action<HttpRequestMessage> headers, CancellationToken ct)
    {
        for (int attempt = 1; ; attempt++)
        {
            using var msg = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
            headers(msg);
            HttpResponseMessage resp;
            try { resp = await _http.SendAsync(msg, ct); }
            // Only failures before anything was sent (no such host, refused, TLS): a dropped connection mid-request may
            // already have been billed, so it isn't sent again.
            catch (HttpRequestException ex) when (attempt < 4 && NotSent(ex))
            {
                await Task.Delay(TimeSpan.FromSeconds(2 * attempt), ct);
                continue;
            }
            catch (HttpRequestException ex)
            {
                throw new CloudTtsException($"{Name}: couldn't reach {new Uri(url).Host} ({ex.Message}).", false);
            }
            if (resp.IsSuccessStatusCode) return resp;
            var text = "";
            try { text = await resp.Content.ReadAsStringAsync(ct); } catch { }
            int code = (int)resp.StatusCode;
            // 429 (busy) and 503 (unavailable) mean nothing was made; a 500/502/504 may come after the audio was billed.
            bool retry = code is 429 or 503;
            resp.Dispose();
            if (retry && attempt < 4) { await Task.Delay(TimeSpan.FromSeconds(2 * attempt), ct); continue; }
            throw new CloudTtsException(Explain(code, text), retry);
        }
    }

    internal static bool NotSent(HttpRequestException ex) =>
        ex.HttpRequestError is HttpRequestError.NameResolutionError or HttpRequestError.SecureConnectionError
        || ex.InnerException is System.Net.Sockets.SocketException { SocketErrorCode: System.Net.Sockets.SocketError.ConnectionRefused or System.Net.Sockets.SocketError.HostUnreachable or System.Net.Sockets.SocketError.NetworkUnreachable or System.Net.Sockets.SocketError.HostNotFound };

    /// <summary>A billed-characters figure, whatever form the reply gives it in (never fails a take that was paid for).</summary>
    private static int? Num(JsonNode? n)
    {
        if (n is not JsonValue v) return null;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<long>(out var l)) return (int)Math.Min(int.MaxValue, l);
        if (v.TryGetValue<double>(out var d)) return (int)Math.Round(d);
        if (v.TryGetValue<string>(out var s) && int.TryParse(s, out var p)) return p;
        return null;
    }

    private string Explain(int code, string body)
    {
        var detail = body.Length > 300 ? body[..300] + "…" : body;
        return code switch
        {
            401 or 403 => $"{Name}: the API key was refused ({code}). Check it in {KeyPlace}. {detail}",
            402 => $"{Name}: the account is out of credits, or the plan doesn't allow this ({code}). {detail}",
            404 => $"{Name}: couldn't find the voice or model ({code}). Check the voice ID and model in the Phase 2 toolbar. {detail}",
            422 or 400 => $"{Name}: the request was refused ({code}), usually a setting outside the engine's range or a wrong voice ID. {detail}",
            429 => $"{Name}: too many requests at once ({code}); lower Concurrent in the Phase 2 toolbar. {detail}",
            _ => $"{Name}: HTTP {code}. {detail}",
        };
    }

    private async Task<CloudAudio> Fish(string text, FishSettings s, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(s.VoiceId)) throw new InvalidOperationException("Enter the Fish Audio voice model ID for this voice pack (Phase 2 toolbar).");
        var key = Need();
        var body = new JsonObject
        {
            ["text"] = text,
            ["reference_id"] = s.VoiceId.Trim(),
            ["format"] = "pcm",
            ["sample_rate"] = 44100,
            ["latency"] = "normal",
        };
        if (Math.Abs(s.Speed - 1) > 0.001) body["prosody"] = new JsonObject { ["speed"] = s.Speed };
        if (s.Temperature > 0) body["temperature"] = s.Temperature;
        using var resp = await PostAsync("https://api.fish.audio/v1/tts", body, m =>
        {
            m.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            m.Headers.Add("model", string.IsNullOrWhiteSpace(s.Model) ? "s2.1-pro" : s.Model.Trim());
        }, ct);
        return new CloudAudio(Pcm.ToMono44100(await resp.Content.ReadAsByteArrayAsync(ct), 44100, Name), null);
    }

    private async Task<CloudAudio> Inworld(string text, InworldSettings s, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(s.VoiceId)) throw new InvalidOperationException("Enter the Inworld voice ID for this voice pack (Phase 2 toolbar).");
        var key = Need();
        var audio = new JsonObject { ["audioEncoding"] = "LINEAR16", ["sampleRateHertz"] = 44100 };
        if (Math.Abs(s.SpeakingRate - 1) > 0.001) audio["speakingRate"] = s.SpeakingRate;
        var body = new JsonObject
        {
            ["text"] = text,
            ["voiceId"] = s.VoiceId.Trim(),
            ["modelId"] = string.IsNullOrWhiteSpace(s.ModelId) ? "inworld-tts-2" : s.ModelId.Trim(),
            ["audioConfig"] = audio,
        };
        using var resp = await PostAsync("https://api.inworld.ai/tts/v1/voice", body, m => m.Headers.Authorization = new AuthenticationHeaderValue("Basic", key), ct);
        var json = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct));
        var b64 = json?["audioContent"]?.GetValue<string>() ?? throw new CloudTtsException("Inworld: no audio in the reply.", false); // answered (maybe billed): not retried
        int? used = Num(json?["usage"]?["processedCharactersCount"]);
        return new CloudAudio(Pcm.ToMono44100(Convert.FromBase64String(b64), 44100, Name), used);
    }

    private async Task<CloudAudio> Cartesia(string text, string? emotion, CartesiaSettings s, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(s.VoiceId)) throw new InvalidOperationException("Enter the Cartesia voice ID for this voice pack (Phase 2 toolbar).");
        var key = Need();
        var gen = new JsonObject();
        if (Math.Abs(s.Speed - 1) > 0.001) gen["speed"] = Math.Clamp(s.Speed, 0.6, 1.5);
        if (Math.Abs(s.Volume - 1) > 0.001) gen["volume"] = Math.Clamp(s.Volume, 0.5, 2.0);
        if (emotion != null) gen["emotion"] = emotion;
        var body = new JsonObject
        {
            ["model_id"] = string.IsNullOrWhiteSpace(s.ModelId) ? "sonic-3.6" : s.ModelId.Trim(),
            ["transcript"] = text,
            ["voice"] = new JsonObject { ["id"] = s.VoiceId.Trim() },
            ["output_format"] = new JsonObject { ["container"] = "raw", ["encoding"] = "pcm_s16le", ["sample_rate"] = 44100 },
        };
        var lang = (s.Language ?? "").Trim();
        if (lang.Length > 0) body[lang.Contains('-') ? "locale" : "language"] = lang;
        if (gen.Count > 0) body["generation_config"] = gen;
        using var resp = await PostAsync("https://api.cartesia.ai/tts/bytes", body, m =>
        {
            m.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            m.Headers.Add("Cartesia-Version", "2026-08-14");
        }, ct);
        return new CloudAudio(Pcm.ToMono44100(await resp.Content.ReadAsByteArrayAsync(ct), 44100, Name), null);
    }

    private async Task<CloudAudio> MiniMax(string text, string? emotion, MiniMaxSettings s, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(s.VoiceId)) throw new InvalidOperationException("Enter the MiniMax voice ID for this voice pack (Phase 2 toolbar).");
        var key = Need();
        var vs = new JsonObject { ["voice_id"] = s.VoiceId.Trim() };
        if (Math.Abs(s.Speed - 1) > 0.001) vs["speed"] = Math.Clamp(s.Speed, 0.5, 2.0);
        if (Math.Abs(s.Volume - 1) > 0.001) vs["vol"] = Math.Clamp(s.Volume, 0.1, 10);
        if (s.Pitch != 0) vs["pitch"] = Math.Clamp(s.Pitch, -12, 12);
        if (emotion != null) vs["emotion"] = emotion;
        var body = new JsonObject
        {
            ["model"] = string.IsNullOrWhiteSpace(s.Model) ? "speech-2.8-hd" : s.Model.Trim(),
            ["text"] = text,
            ["stream"] = false,
            ["voice_setting"] = vs,
            ["audio_setting"] = new JsonObject { ["sample_rate"] = 44100, ["format"] = "pcm", ["channel"] = 1 },
        };
        JsonNode? json;
        int status;
        for (int attempt = 1; ; attempt++)
        {
            using var resp = await PostAsync("https://api.minimax.io/v1/t2a_v2", body, m => m.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key), ct);
            json = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct));
            status = json?["base_resp"]?["status_code"]?.GetValue<int>() ?? 0;
            // MiniMax answers rate limits with HTTP 200 and a status code.
            if (status is 1002 or 1039 && attempt < 4) { await Task.Delay(TimeSpan.FromSeconds(2 * attempt), ct); continue; }
            break;
        }
        if (status != 0)
        {
            var msg = json?["base_resp"]?["status_msg"]?.GetValue<string>() ?? "";
            // 1004: authentication failed; 1008: insufficient balance; 1002/1039: rate limits.
            int http = status switch { 1004 => 401, 1008 => 402, 1002 or 1039 => 429, _ => 400 };
            throw new CloudTtsException(Explain(http, $"MiniMax status {status}: {msg}"), http == 429);
        }
        var hex = json?["data"]?["audio"]?.GetValue<string>() ?? throw new CloudTtsException("MiniMax: no audio in the reply.", false); // answered (maybe billed): not retried
        int? used = Num(json?["extra_info"]?["usage_characters"]);
        return new CloudAudio(Pcm.ToMono44100(Convert.FromHexString(hex), 44100, Name), used);
    }

    private async Task<CloudAudio> OpenAi(string text, string? instructions, OpenAiTtsSettings s, CancellationToken ct)
    {
        var baseUrl = Base(s.BaseUrl, "https://api.openai.com");
        if (_key == null && !TtsEngines.IsLocalAddress(baseUrl)) Need();
        var body = new JsonObject
        {
            ["model"] = string.IsNullOrWhiteSpace(s.Model) ? "gpt-4o-mini-tts" : s.Model.Trim(),
            ["input"] = text,
            ["voice"] = string.IsNullOrWhiteSpace(s.Voice) ? "marin" : s.Voice.Trim(),
            ["response_format"] = "wav",
        };
        if (!string.IsNullOrWhiteSpace(instructions)) body["instructions"] = instructions;
        if (Math.Abs(s.Speed - 1) > 0.001) body["speed"] = s.Speed;
        if (!string.IsNullOrWhiteSpace(s.ExtraJson))
        {
            JsonObject extra;
            try { extra = JsonNode.Parse(s.ExtraJson) as JsonObject ?? throw new JsonException(); }
            catch (JsonException) { throw new InvalidOperationException("OpenAI-compatible: Extra fields must be a JSON object, like {\"exaggeration\": 0.6}."); }
            foreach (var (k, v) in extra) body[k] = v?.DeepClone();
        }
        // A base address that already ends in /v1 (as many local servers print it), or is the whole endpoint, isn't doubled.
        var b = System.Text.RegularExpressions.Regex.Replace(baseUrl.TrimEnd('/'), @"(/v1)?/audio/speech$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var url = b.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? b + "/audio/speech" : b + "/v1/audio/speech";
        // The saved key is only sent to servers outside this PC and network: a local server never gets the OpenAI key.
        bool local = TtsEngines.IsLocalAddress(baseUrl);
        using var resp = await PostAsync(url, body, m =>
        {
            if (_key != null && !local) m.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _key);
        }, ct);
        var audio = await resp.Content.ReadAsByteArrayAsync(ct);
        Pcm.RequireWav(audio, Name);
        return new CloudAudio(Pcm.ToMono44100(audio, 24000, Name), null);
    }
}

/// <summary>Any engine's audio to raw 16-bit mono PCM at 44.1 kHz: WAV (16/24/32-bit or float, any rate or channels) or raw 16-bit.</summary>
public static class Pcm
{
    public static byte[] ToMono44100(byte[] data, int rawRate, string engine = "The engine")
    {
        if (data.Length < 2) throw new CloudTtsException($"{engine} sent no audio.", true);
        // A JSON error in place of audio (raw PCM may start with the byte of '{' too, so only when it really is JSON)
        if ((data[0] == '{' || data[0] == '[') && data.Length < 100_000 && LooksLikeJson(data))
            throw new CloudTtsException($"{engine} sent text instead of audio: {Encoding.UTF8.GetString(data, 0, Math.Min(data.Length, 200))}", false);
        float[] mono;
        int rate;
        if (data.Length >= 12 && data[0] == 'R' && data[1] == 'I' && data[2] == 'F' && data[3] == 'F' && data[8] == 'W') (mono, rate) = ReadWav(data);
        else
        {
            mono = new float[data.Length / 2];
            for (int i = 0; i < mono.Length; i++) mono[i] = BitConverter.ToInt16(data, 2 * i) / 32768f;
            rate = rawRate;
        }
        if (rate != 44100) mono = Resample(mono, rate, 44100);
        var o = new byte[mono.Length * 2];
        for (int i = 0; i < mono.Length; i++)
        {
            int s = (int)Math.Round(Math.Clamp(mono[i], -1f, 1f) * 32767f);
            o[2 * i] = (byte)s; o[2 * i + 1] = (byte)(s >> 8);
        }
        return o;
    }

    private static bool LooksLikeJson(byte[] data)
    {
        try { using var _ = JsonDocument.Parse(data); return true; } catch (JsonException) { return false; }
    }

    public static (float[] Mono, int Rate) ReadWav(byte[] b)
    {
        int fmt = 0, channels = 1, rate = 0, bits = 16;
        long o = 12;
        while (o + 8 <= b.Length)
        {
            var id = Encoding.ASCII.GetString(b, (int)o, 4);
            long size = BitConverter.ToUInt32(b, (int)o + 4);
            long body = o + 8;
            if (id == "fmt ")
            {
                if (size < 16 || body + 16 > b.Length) throw new InvalidDataException("WAV with a short fmt chunk.");
                fmt = BitConverter.ToUInt16(b, (int)body);
                channels = BitConverter.ToUInt16(b, (int)body + 2);
                rate = BitConverter.ToInt32(b, (int)body + 4);
                bits = BitConverter.ToUInt16(b, (int)body + 14);
                if (fmt == 0xFFFE && size >= 26 && body + 26 <= b.Length) fmt = BitConverter.ToUInt16(b, (int)body + 24);  // WAVE_FORMAT_EXTENSIBLE
            }
            else if (id == "data")
            {
                // Streaming servers write 0 or 0xFFFFFFFF as the size: take what's there.
                long len = size == 0 || body + size > b.Length ? b.Length - body : size;
                int frame = channels * bits / 8;
                if (frame <= 0 || rate <= 0 || channels > 16) throw new InvalidDataException("WAV without a usable fmt chunk.");
                int n = (int)(len / frame);
                var mono = new float[n];
                for (int i = 0; i < n; i++)
                {
                    float sum = 0;
                    for (int c = 0; c < channels; c++)
                    {
                        int p = (int)(body + (long)i * frame + c * bits / 8);
                        sum += (fmt, bits) switch
                        {
                            (3, 32) => BitConverter.ToSingle(b, p),
                            (1, 16) => BitConverter.ToInt16(b, p) / 32768f,
                            (1, 24) => ((b[p] | (b[p + 1] << 8) | (b[p + 2] << 16)) << 8 >> 8) / 8388608f,
                            (1, 32) => BitConverter.ToInt32(b, p) / 2147483648f,
                            (1, 8) => (b[p] - 128) / 128f,
                            _ => throw new InvalidDataException($"WAV format {fmt}/{bits}-bit isn't supported."),
                        };
                    }
                    mono[i] = sum / channels;
                }
                return (mono, rate);
            }
            long next = body + size + (size & 1);
            if (next <= o) break;  // a corrupt size can't send the walk backwards
            o = next;
        }
        throw new InvalidDataException("WAV without a data chunk.");
    }

    /// <summary>An engine that should send WAV sent something else (MP3, Ogg, FLAC or a JSON error): say so instead of saving noise.</summary>
    public static void RequireWav(byte[] data, string engine)
    {
        if (data.Length >= 12 && data[0] == 'R' && data[1] == 'I' && data[2] == 'F' && data[3] == 'F') return;
        string? what = data.Length == 0 ? "no audio"
            : data[0] == '{' || data[0] == '[' ? "JSON (" + Encoding.UTF8.GetString(data, 0, Math.Min(data.Length, 200)) + ")"
            : data.Length >= 3 && data[0] == 'I' && data[1] == 'D' && data[2] == '3' || data.Length >= 2 && data[0] == 0xFF && (data[1] & 0xE0) == 0xE0 ? "MP3"
            : data.Length >= 4 && data[0] == 'O' && data[1] == 'g' && data[2] == 'g' ? "Ogg"
            : data.Length >= 4 && data[0] == 'f' && data[1] == 'L' && data[2] == 'a' && data[3] == 'C' ? "FLAC"
            : null;
        if (what != null) throw new CloudTtsException($"{engine}: expected WAV audio but got {what}. Does the server support response_format \"wav\"?", false);
    }

    /// <summary>Windowed-sinc resampling (Blackman window, 16 zero crossings each side, low-passed when downsampling).</summary>
    public static float[] Resample(float[] x, int from, int to)
    {
        if (x.Length == 0 || from == to) return x;
        double ratio = (double)to / from;
        double cutoff = Math.Min(1.0, ratio);
        const int zc = 16;
        int n = (int)Math.Round(x.Length * ratio);
        var y = new float[n];
        double half = zc / cutoff;
        for (int i = 0; i < n; i++)
        {
            double t = i / ratio;
            int lo = (int)Math.Ceiling(t - half), hi = (int)Math.Floor(t + half);
            double acc = 0, wsum = 0;
            for (int j = Math.Max(lo, 0); j <= Math.Min(hi, x.Length - 1); j++)
            {
                double d = (t - j) * cutoff;
                double sinc = d == 0 ? 1 : Math.Sin(Math.PI * d) / (Math.PI * d);
                double w = 0.42 + 0.5 * Math.Cos(Math.PI * d / zc) + 0.08 * Math.Cos(2 * Math.PI * d / zc);
                double k = sinc * w;
                acc += x[j] * k; wsum += k;
            }
            y[i] = (float)(wsum == 0 ? 0 : acc / wsum);
        }
        return y;
    }
}
