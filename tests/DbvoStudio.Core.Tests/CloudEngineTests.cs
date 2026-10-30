using System.Net;
using System.Text.Json.Nodes;
using DbvoStudio.Core.Engines;
using DbvoStudio.Core.Projects;
using Xunit;

namespace DbvoStudio.Core.Tests;

public class CloudEngineTests
{
    private sealed class Fake(Func<HttpRequestMessage, string, HttpResponseMessage> f) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Req, JsonNode? Body)> Seen { get; } = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            var body = r.Content == null ? "" : await r.Content.ReadAsStringAsync(ct);
            Seen.Add((r, body.Length > 0 ? JsonNode.Parse(body) : null));
            return f(r, body);
        }
    }

    private static byte[] Pcm(int samples) => new byte[samples * 2];

    [Theory]
    [InlineData("fish", "[British Extremely Baleful] Leave me.", "[British Extremely Baleful] Leave me.", null, null)]
    [InlineData("inworld", "[sigh] Fine.", "[sigh] Fine.", null, null)]
    [InlineData("cartesia", "[British Extremely Baleful] Leave me. [laughs]", "Leave me. [laughter]", "contempt", null)]
    [InlineData("cartesia", "[sad] I know.", "I know.", "sad", null)]
    [InlineData("minimax", "[Thoughtful] Hmm... [laughs] maybe.", "Hmm... (laughs) maybe.", "calm", null)]
    [InlineData("openai", "[British Extremely Baleful] Leave me.", "Leave me.", null, "Deliver this line: British Extremely Baleful.")]
    [InlineData("omnivoice", "[whispers] Quiet [laughs] now.", "Quiet [laughter] now.", null, null)]
    [InlineData("voxcpm", "[whispers] Quiet now.", "Quiet now.", null, null)]
    public void Tags_reach_each_engine_its_own_way(string engine, string spoken, string text, string? emotion, string? instructions)
    {
        var r = TtsEngines.EngineText(engine, spoken, engine == "minimax" ? "speech-2.8-hd" : "");
        Assert.Equal(text, r.Text);
        Assert.Equal(emotion, r.Emotion);
        Assert.Equal(instructions, r.Instructions);
    }

    [Fact]
    public void A_voice_pack_emotion_applies_when_the_tags_name_none_and_whisper_needs_speech_2_6()
    {
        Assert.Equal("calm", TtsEngines.EngineText("cartesia", "Hello.", "", "calm").Emotion);
        Assert.Null(TtsEngines.EngineText("minimax", "[whispers] Hush.", "speech-2.8-hd").Emotion);
        Assert.Equal("whisper", TtsEngines.EngineText("minimax", "[whispers] Hush.", "speech-2.6-hd").Emotion);
    }

    [Fact]
    public async Task Cartesia_sends_voice_emotion_and_raw_pcm_format()
    {
        var h = new Fake((_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Pcm(441)) });
        var v = new VoicePack { Cartesia = { VoiceId = "abc", Speed = 1.2 } };
        var got = await new CloudTtsClient("cartesia", "k", new HttpClient(h)).SpeakAsync("Leave me.", "contempt", null, v, default);
        Assert.Equal(882, got.Pcm44100.Length);
        var (req, body) = h.Seen.Single();
        Assert.Equal("https://api.cartesia.ai/tts/bytes", req.RequestUri!.ToString());
        Assert.Equal("Bearer", req.Headers.Authorization!.Scheme);
        Assert.Equal("abc", (string)body!["voice"]!["id"]!);
        Assert.Equal("contempt", (string)body["generation_config"]!["emotion"]!);
        Assert.Equal(1.2, (double)body["generation_config"]!["speed"]!);
        Assert.Equal("pcm_s16le", (string)body["output_format"]!["encoding"]!);
    }

    [Fact]
    public async Task MiniMax_reads_hex_audio_and_maps_a_balance_error()
    {
        var ok = new Fake((_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"base_resp\":{\"status_code\":0},\"data\":{\"audio\":\"0000\"},\"extra_info\":{\"usage_characters\":9}}") });
        var v = new VoicePack { MiniMax = { VoiceId = "v1" } };
        var got = await new CloudTtsClient("minimax", "k", new HttpClient(ok)).SpeakAsync("Hello.", null, null, v, default);
        Assert.Equal(9, got.BilledCharacters);
        var broke = new Fake((_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"base_resp\":{\"status_code\":1008,\"status_msg\":\"insufficient balance\"}}") });
        var ex = await Assert.ThrowsAsync<CloudTtsException>(() => new CloudTtsClient("minimax", "k", new HttpClient(broke)).SpeakAsync("Hello.", null, null, v, default));
        Assert.Contains("out of credits", ex.Message);
    }

    [Fact]
    public async Task OpenAi_compatible_on_this_pc_needs_no_key_and_rejects_mp3()
    {
        var wav = new Audio.WavFile { SampleRate = 24000, Channels = 1, Samples = new short[240] }.ToBytes();
        var h = new Fake((r, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(wav) });
        var v = new VoicePack { OpenAi = { BaseUrl = "http://127.0.0.1:5123/v1", Voice = "default", ExtraJson = "{\"exaggeration\":0.6}" } };
        var got = await new CloudTtsClient("openai", null, new HttpClient(h)).SpeakAsync("Hi.", null, "Deliver this line: cold.", v, default);
        Assert.Equal(441 * 2, got.Pcm44100.Length); // 10 ms at 24 kHz → 44.1 kHz
        var (req, body) = h.Seen.Single();
        Assert.Equal("http://127.0.0.1:5123/v1/audio/speech", req.RequestUri!.ToString());
        Assert.Null(req.Headers.Authorization);
        Assert.Equal(0.6, (double)body!["exaggeration"]!);
        Assert.Equal("Deliver this line: cold.", (string)body["instructions"]!);

        var mp3 = new Fake((_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { (byte)'I', (byte)'D', (byte)'3', 0, 0 }) });
        var ex = await Assert.ThrowsAsync<CloudTtsException>(() => new CloudTtsClient("openai", null, new HttpClient(mp3)).SpeakAsync("Hi.", null, null, v, default));
        Assert.Contains("MP3", ex.Message);
    }

    [Fact]
    public async Task A_missing_key_or_voice_is_said_before_anything_is_sent()
    {
        var h = new Fake((_, _) => new HttpResponseMessage(HttpStatusCode.OK));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => new CloudTtsClient("fish", null, new HttpClient(h)).SpeakAsync("Hi.", null, null, new VoicePack { Fish = { VoiceId = "x" } }, default));
        Assert.Contains("API key", ex.Message);
        ex = await Assert.ThrowsAsync<InvalidOperationException>(() => new CloudTtsClient("inworld", "k", new HttpClient(h)).SpeakAsync("Hi.", null, null, new VoicePack(), default));
        Assert.Contains("voice ID", ex.Message);
        Assert.Empty(h.Seen);
    }

    [Theory]
    [InlineData("localhost:5123", true)]
    [InlineData("http://172.20.1.5:8000", true)]
    [InlineData("https://api.openai.com", false)]
    public void Local_servers_need_no_key_even_typed_without_http(string url, bool local) => Assert.Equal(local, TtsEngines.IsLocalAddress(url));

    [Fact]
    public void Raw_pcm_starting_like_json_is_still_audio()
    {
        var pcm = new byte[] { (byte)'{', 0x10, 0x20, 0x30 };
        Assert.Equal(4, DbvoStudio.Core.Engines.Pcm.ToMono44100(pcm, 44100).Length);
    }

    [Fact]
    public void Books_voices_on_an_engine_with_a_lower_limit_get_their_own_shorter_parts()
    {
        var p = new Books.BooksProject();
        p.Narrators.Add(new Books.BookNarrator { Engine = "inworld", VoiceId = "b" });
        Assert.Equal(2000, Books.BooksService.VoiceCeiling(p, Books.BooksProject.NarratorKey(p.Narrators[0].Id)));
        Assert.True(Books.BooksService.Ceiling(p) > 2000); // the project's own split is unchanged
    }
}
