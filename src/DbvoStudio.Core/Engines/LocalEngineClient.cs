using DbvoStudio.Core.Text;
using System.Net.Http.Json;
using System.Text.Json;
using DbvoStudio.Core.Projects;

namespace DbvoStudio.Core.Engines;

public sealed record EngineHealth(bool Ok, string Phase, string? Device, string? Error, double? Progress, string? Model);

/// <summary>
/// Client for the local VoxCPM and OmniVoice servers used by DBVO Voice Pack Maker 2.7.0
/// (same endpoints and request fields: /health, /voices, /load_model, /tts_to_audio/).
/// </summary>
public sealed class LocalEngineClient : IDisposable
{
    public const string VoxCpm = "voxcpm";
    public const string OmniVoice = "omnivoice";

    private readonly HttpClient _http;
    private readonly string _base;

    public LocalEngineClient(string baseUrl, HttpClient? http = null)
    {
        _base = (string.IsNullOrWhiteSpace(baseUrl) ? "http://localhost:7861" : baseUrl.Trim()).TrimEnd('/');
        if (!_base.Contains("://")) _base = "http://" + _base; // "localhost:7862" or "127.0.0.1:7862" as typed
        // DBReV shows its full endpoint (http://localhost:8001/v1/audio/speech); the server address is the part before it.
        foreach (var tail in new[] { "/v1/audio/speech", "/tts_to_audio", "/health", "/voices", "/docs" })
            if (_base.EndsWith(tail, StringComparison.OrdinalIgnoreCase)) { _base = _base[..^tail.Length]; break; }
        // Python servers usually listen on IPv4 only, and on Windows "localhost" tries IPv6 first (a refused try there
        // costs about 2 s each time), so localhost goes straight to 127.0.0.1.
        if (Uri.TryCreate(_base, UriKind.Absolute, out var u) && u.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            _base = new UriBuilder(u) { Host = "127.0.0.1" }.Uri.ToString().TrimEnd('/');
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
    }

    public Task<EngineHealth> HealthAsync(CancellationToken ct = default) => HealthAtAsync(_base, ct);

    private async Task<EngineHealth> HealthAtAsync(string baseUrl, CancellationToken ct)
    {
        string body;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(8));
            using var resp = await _http.GetAsync(baseUrl + "/health", cts.Token);
            if (!resp.IsSuccessStatusCode)
                return Offline($"{baseUrl} answers, but {baseUrl}/health gave HTTP {(int)resp.StatusCode}. DBVO Studio needs the OmniVoice / VoxCPM server from DBVO Voice Pack Maker or DBReV Voice Pack Maker (DBReV's runs on port 8001), not a Gradio web page or another server.");
            body = await resp.Content.ReadAsStringAsync(cts.Token);
        }
        catch (HttpRequestException ex)
        {
            return Offline($"Nothing answers at {baseUrl} ({ex.Message}). Is the server running, and on this port? Check the port in the server's console window.");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return Offline($"{baseUrl} didn't answer within 8 s. The server may still be loading its model; try Check again in a minute.");
        }
        catch (Exception ex) when (ex is UriFormatException or NotSupportedException or InvalidOperationException)
        {
            return Offline($"\"{baseUrl}\" isn't a valid address. Use something like http://127.0.0.1:7862.");
        }
        try
        {
            using var doc = JsonDocument.Parse(body);
            var r = doc.RootElement;
            // Voice Pack Maker 2.7.0 sends model_loaded; DBReV's OmniVoice server sends {"status":"ok","voices":n} instead.
            bool loaded = r.TryGetProperty("model_loaded", out var ml)
                ? ml.ValueKind == JsonValueKind.True
                : r.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.String && st.GetString() is "ok" or "ready";
            string phase = loaded ? "ready" : r.TryGetProperty("phase", out var ph) && ph.ValueKind == JsonValueKind.String ? ph.GetString() ?? "starting" : "starting";
            return new EngineHealth(loaded, phase,
                r.TryGetProperty("device", out var d) ? d.ToString() : null,
                r.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null,
                r.TryGetProperty("progress", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetDouble() : null,
                r.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null);
        }
        catch (JsonException)
        {
            return Offline($"{baseUrl}/health answers, but not with JSON: that's a web page or another server, not the DBVO Voice Pack Maker OmniVoice / VoxCPM server.");
        }
    }

    private static EngineHealth Offline(string why) => new(false, "offline", null, why, null, null);

    public async Task<List<string>> VoicesAsync(CancellationToken ct = default)
    {
        using var doc = JsonDocument.Parse(await _http.GetStringAsync(_base + "/voices", ct));
        if (!doc.RootElement.TryGetProperty("voices", out var v) || v.ValueKind != JsonValueKind.Array) return new();
        return v.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString()! : x.TryGetProperty("name", out var n) ? n.GetString()! : x.ToString()).ToList();
    }

    public async Task LoadModelAsync(string model, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsJsonAsync(_base + "/load_model", new { model }, ct);
        resp.EnsureSuccessStatusCode();
    }

    /// <summary>VoxCPM text-to-speech; returns WAV bytes.</summary>
    public Task<byte[]> VoxCpmAsync(string text, VoxCpmSettings s, CancellationToken ct = default) =>
        PostTtsAsync(new Dictionary<string, object?>
        {
            ["text"] = text,
            ["speaker_wav"] = s.Voice,
            ["language"] = string.IsNullOrWhiteSpace(s.Language) ? "en" : s.Language,
            ["save_path"] = s.Voice + ".wav",
            ["reference_mode"] = s.ReferenceMode,
            ["cfg_value"] = s.ReferenceMode ? s.ControlCfgValue : s.CfgValue,
            ["inference_timesteps"] = s.InferenceTimesteps,
            ["normalize"] = s.Normalize,
            ["retry_badcase"] = s.RetryBadcase,
        }, ct);

    // Servers known to speak DBReV's OpenAI-style API (POST /v1/audio/speech), by address.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> OpenAiStyle = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// OmniVoice text-to-speech; returns WAV bytes. Works with Voice Pack Maker 2.7.0's server (POST /tts_to_audio/) and
    /// with DBReV Voice Pack Maker's omnivoice_server.py (POST /v1/audio/speech, port 8001): the first answers 404 on the
    /// other's endpoint, so a 404 switches to the other API and the address is remembered.
    /// </summary>
    public async Task<byte[]> OmniVoiceAsync(string text, OmniVoiceSettings s, CancellationToken ct = default)
    {
        var old = new Dictionary<string, object?>
        {
            ["text"] = text,
            ["speaker_wav"] = s.Voice,
            ["inference_timesteps"] = s.NumStep,
            ["speed"] = s.Speed,
            ["cfg_value"] = s.GuidanceScale,
        };
        var dbrev = new Dictionary<string, object?>
        {
            ["input"] = text,
            ["voice"] = s.Voice,
            ["num_step"] = s.NumStep,
            ["speed"] = s.Speed,
            ["guidance_scale"] = s.GuidanceScale,
            ["denoise"] = s.Denoise,
            ["response_format"] = "wav", // as OpenAI-style servers default to MP3 (ignored where it isn't known)
        };
        if (!string.IsNullOrWhiteSpace(s.Language)) dbrev["language"] = s.Language.Trim();
        bool openAi = OpenAiStyle.TryGetValue(_base, out var known) && known;
        try
        {
            return openAi ? await PostTtsAsync(dbrev, ct, "/v1/audio/speech") : await PostTtsAsync(old, ct);
        }
        catch (EndpointMissingException)
        {
            OpenAiStyle[_base] = !openAi;
            return !openAi ? await PostTtsAsync(dbrev, ct, "/v1/audio/speech") : await PostTtsAsync(old, ct);
        }
    }

    private sealed class EndpointMissingException() : Exception("The local voice engine at this address has no text-to-speech endpoint Studio knows (/tts_to_audio/ or /v1/audio/speech). Check the address in Global Settings → General & tools → Local voice engines.");

    private async Task<byte[]> PostTtsAsync(Dictionary<string, object?> body, CancellationToken ct, string path = "/tts_to_audio/")
    {
        using var msg = new HttpRequestMessage(HttpMethod.Post, _base + path) { Content = JsonContent.Create(body) };
        msg.Headers.Accept.ParseAdd("audio/wav");
        using var resp = await _http.SendAsync(msg, ct);
        if (!resp.IsSuccessStatusCode)
        {
            // FastAPI's own 404 for a path it doesn't have (not DBReV's "Voice '…' not found")
            if ((int)resp.StatusCode is 404 or 405)
            {
                var d = "";
                try { d = await resp.Content.ReadAsStringAsync(ct); } catch { }
                if (d.Contains("\"Not Found\"") || d.Contains("Method Not Allowed")) throw new EndpointMissingException();
                throw new InvalidOperationException(FriendlyError.Http("The local voice engine", (int)resp.StatusCode, d, "Global Settings → General & tools → Local voice engines"));
            }
            var detail = "";
            try { detail = await resp.Content.ReadAsStringAsync(ct); } catch { }
            throw new InvalidOperationException(FriendlyError.Http("The local voice engine", (int)resp.StatusCode, detail, "Global Settings → General & tools → Local voice engines"));
        }
        return await resp.Content.ReadAsByteArrayAsync(ct);
    }

    public void Dispose() => _http.Dispose();
}
