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
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
    }

    public async Task<EngineHealth> HealthAsync(CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(4));
            using var doc = JsonDocument.Parse(await _http.GetStringAsync(_base + "/health", cts.Token));
            var r = doc.RootElement;
            bool loaded = r.TryGetProperty("model_loaded", out var ml) && ml.ValueKind == JsonValueKind.True;
            string phase = loaded ? "ready" : r.TryGetProperty("phase", out var ph) ? ph.GetString() ?? "starting" : "starting";
            return new EngineHealth(loaded, phase,
                r.TryGetProperty("device", out var d) ? d.ToString() : null,
                r.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null,
                r.TryGetProperty("progress", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetDouble() : null,
                r.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return new EngineHealth(false, "offline", null, null, null, null);
        }
    }

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

    /// <summary>OmniVoice text-to-speech; returns WAV bytes.</summary>
    public Task<byte[]> OmniVoiceAsync(string text, OmniVoiceSettings s, CancellationToken ct = default) =>
        PostTtsAsync(new Dictionary<string, object?>
        {
            ["text"] = text,
            ["speaker_wav"] = s.Voice,
            ["inference_timesteps"] = s.NumStep,
            ["speed"] = s.Speed,
            ["cfg_value"] = s.GuidanceScale,
        }, ct);

    private async Task<byte[]> PostTtsAsync(Dictionary<string, object?> body, CancellationToken ct)
    {
        using var msg = new HttpRequestMessage(HttpMethod.Post, _base + "/tts_to_audio/") { Content = JsonContent.Create(body) };
        msg.Headers.Accept.ParseAdd("audio/wav");
        using var resp = await _http.SendAsync(msg, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var detail = "";
            try { detail = await resp.Content.ReadAsStringAsync(ct); } catch { }
            throw new HttpRequestException($"Local engine error {(int)resp.StatusCode}. {detail}".Trim());
        }
        return await resp.Content.ReadAsByteArrayAsync(ct);
    }

    public void Dispose() => _http.Dispose();
}
