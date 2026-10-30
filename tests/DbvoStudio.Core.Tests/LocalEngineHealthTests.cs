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
}
