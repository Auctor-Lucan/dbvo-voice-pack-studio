using System.Net.Http;
using System.Net.Sockets;
using DbvoStudio.Core.Projects;
using DbvoStudio.Core.Text;
using Xunit;

namespace DbvoStudio.Core.Tests;

public class Fixes71Tests
{
    [Fact]
    public void Missing_api_key_reads_as_a_missing_key_and_names_the_setting()
    {
        var body = """{ "error": { "message": "You didn't provide an API key. You need to provide your API key in an Authorization header using Bearer auth (i.e. Authorization: Bearer YOUR_KEY)", "type": "invalid_request_error" } }""";
        var m = FriendlyError.Http("Your AI provider", 401, body, FriendlyError.AiPlace);
        Assert.Contains("needs an API key, and none is set", m);
        Assert.Contains("AI auto-tagging", m);
        Assert.DoesNotContain("Bearer", m);
        Assert.DoesNotContain("401", m);
    }

    [Fact]
    public void Wrong_key_rate_limit_server_error_and_quota_are_told_apart()
    {
        Assert.Contains("didn't accept the API key", FriendlyError.Http("ElevenLabs", 401, """{"detail":{"status":"invalid_api_key","message":"Invalid API key"}}""", FriendlyError.ElevenLabsPlace));
        Assert.Contains("busy", FriendlyError.Http("ElevenLabs", 429, """{"detail":{"status":"too_many_concurrent_requests"}}""", FriendlyError.ElevenLabsPlace));
        Assert.Contains("on its end", FriendlyError.Http("ElevenLabs", 503, "<html>Service Unavailable</html>", FriendlyError.ElevenLabsPlace));
        Assert.Contains("out of credits", FriendlyError.Http("ElevenLabs", 401, """{"detail":{"status":"quota_exceeded","message":"This request exceeds your quota."}}""", FriendlyError.ElevenLabsPlace)); // ElevenLabs answers 401 when credits run out
        Assert.Contains("out of credits", FriendlyError.Http("ElevenLabs", 402, "{}", FriendlyError.ElevenLabsPlace));
        Assert.Contains("model", FriendlyError.Http("Your AI provider", 404, """{"error":{"message":"The model `gpt-9` does not exist"}}""", FriendlyError.AiPlace));
    }

    [Fact]
    public void Common_exceptions_become_plain_sentences()
    {
        Assert.Contains("open in another program", FriendlyError.From(new IOException("The process cannot access the file 'D:\\a.fuz' because it is being used by another process.")));
        Assert.Contains("D:\\a.fuz", FriendlyError.From(new IOException("The process cannot access the file 'D:\\a.fuz' because it is being used by another process.")));
        Assert.Contains("isn't running", FriendlyError.From(new HttpRequestException("x", new SocketException((int)SocketError.ConnectionRefused)), "The local voice engine"));
        Assert.Contains("too long for Windows", FriendlyError.From(new PathTooLongException()));
        Assert.Equal("Pick a voice first.", FriendlyError.From(new InvalidOperationException("Pick a voice first.")));
    }

    [Fact]
    public void Choosing_another_take_clears_the_review_approval()
    {
        var line = new Phase2Line { LineId = "a", Takes = { new Take { Id = "t1" }, new Take { Id = "t2" } } };
        line.SelectedTakeId = "t1";
        line.Approved = true;
        line.SelectedTakeId = "t1";
        Assert.True(line.Approved);
        line.SelectedTakeId = "t2";
        Assert.False(line.Approved);
    }

    [Fact]
    public void A_loaded_patch_keeps_its_approvals()
    {
        var line = new Phase2Line { LineId = "a", Takes = { new Take { Id = "t1" } } };
        line.SelectedTakeId = "t1";
        line.Approved = true;
        var json = System.Text.Json.JsonSerializer.Serialize(line);
        var back = System.Text.Json.JsonSerializer.Deserialize<Phase2Line>(json)!;
        Assert.True(back.Approved);
        Assert.Equal("t1", back.SelectedTakeId);
    }
}
