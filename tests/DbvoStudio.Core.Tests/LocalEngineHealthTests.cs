using System.Net;
using DbvoStudio.Core.Engines;
using Xunit;

namespace DbvoStudio.Core.Tests;

public class LocalEngineHealthTests
{
    private sealed class Fake(Func<HttpRequestMessage, HttpResponseMessage> f) : HttpMessageHandler
    {
        public List<string> Urls { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) { Urls.Add(r.RequestUri!.ToString()); return Task.FromResult(f(r)); }
    }

    [Fact]
    public async Task An_address_typed_without_http_works()
    {
        var h = new Fake(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"model_loaded\":true,\"device\":\"cuda\"}") });
        var r = await new LocalEngineClient("localhost:7862", new HttpClient(h)).HealthAsync();
        Assert.True(r.Ok);
        Assert.Equal("http://127.0.0.1:7862/health", h.Urls[0]);
    }

    [Fact]
    public async Task A_server_without_health_says_it_is_the_wrong_server()
    {
        var h = new Fake(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var r = await new LocalEngineClient("http://localhost:7860", new HttpClient(h)).HealthAsync();
        Assert.Equal("offline", r.Phase);
        Assert.Contains("HTTP 404", r.Error);
    }

    [Fact]
    public async Task A_dbrev_style_health_answer_counts_as_ready()
    {
        var h = new Fake(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"status\":\"ok\",\"voices\":0,\"failed_voices\":0}") });
        Assert.True((await new LocalEngineClient("http://127.0.0.1:8001", new HttpClient(h)).HealthAsync()).Ok);
    }

    [Fact]
    public async Task OmniVoice_switches_to_dbrevs_api_when_tts_to_audio_is_missing()
    {
        var h = new Fake(q => q.RequestUri!.AbsolutePath == "/v1/audio/speech"
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 1, 2, 3 }) }
            : new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{\"detail\":\"Not Found\"}") });
        var c = new LocalEngineClient("http://localhost:8002/v1/audio/speech", new HttpClient(h));
        var wav = await c.OmniVoiceAsync("Hello.", new Core.Projects.OmniVoiceSettings { Voice = "Danis" });
        Assert.Equal(3, wav.Length);
        Assert.Equal("http://127.0.0.1:8002/tts_to_audio/", h.Urls[0]);
        Assert.Equal("http://127.0.0.1:8002/v1/audio/speech", h.Urls[1]);
        await c.OmniVoiceAsync("Again.", new Core.Projects.OmniVoiceSettings { Voice = "Danis" });
        Assert.Equal("http://127.0.0.1:8002/v1/audio/speech", h.Urls[2]); // remembered
    }
}
