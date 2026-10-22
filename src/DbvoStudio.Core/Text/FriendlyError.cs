using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DbvoStudio.Core.Text;

/// <summary>
/// Turns technical failures (HTTP status codes, provider JSON, .NET exceptions) into a sentence a user can act on:
/// what went wrong, in plain words, and what to do about it.
/// </summary>
public static class FriendlyError
{
    public const string ElevenLabsPlace = "Global Settings → General & tools → ElevenLabs";
    public const string AiPlace = "Global Settings → General & tools → AI auto-tagging";
    public const string ToolsPlace = "Global Settings → General & tools → Tools";

    /// <summary>The message an online service gave, pulled out of its JSON reply when there is one.</summary>
    public static string ProviderMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "";
        try
        {
            using var doc = JsonDocument.Parse(body);
            var r = doc.RootElement;
            foreach (var key in new[] { "error", "detail" })
            {
                if (!r.TryGetProperty(key, out var e)) continue;
                if (e.ValueKind == JsonValueKind.String) return e.GetString() ?? "";
                if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String) return m.GetString() ?? "";
                if (e.ValueKind == JsonValueKind.Array && e.GetArrayLength() > 0 && e[0].ValueKind == JsonValueKind.Object && e[0].TryGetProperty("msg", out var mm)) return mm.GetString() ?? "";
            }
            if (r.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.String) return msg.GetString() ?? "";
        }
        catch (JsonException) { }
        // Not JSON (an HTML error page, plain text): keep it short or drop it.
        var plain = Regex.Replace(body, "<[^>]+>", " ");
        plain = Regex.Replace(plain, @"\s+", " ").Trim();
        return plain.Length > 160 ? "" : plain;
    }

    /// <summary>A failed reply from an online service, e.g. Http("ElevenLabs", 401, body, ElevenLabsPlace).</summary>
    public static string Http(string service, int code, string body, string settingsPlace)
    {
        var said = ProviderMessage(body);
        var low = (said + " " + body).ToLowerInvariant();
        string Quote() => string.IsNullOrWhiteSpace(said) ? "" : $" ({service} said: \"{Short(said)}\")";

        if (code == 402 || low.Contains("quota_exceeded") || low.Contains("insufficient_quota") || (low.Contains("credits") && low.Contains("not enough")))
            return $"Your {service} account is out of credits for this{Quote()}. Wait for them to renew, or top up, then try again.";
        if (code == 401 || code == 403 || low.Contains("invalid_api_key") || low.Contains("api key"))
        {
            if (low.Contains("didn't provide") || low.Contains("did not provide") || low.Contains("missing") || low.Contains("no api key"))
                return $"{service} needs an API key, and none is set. Add it in {settingsPlace}.";
            if (code == 403 && !low.Contains("key"))
                return $"{service} refused this request for your account{Quote()}. Your plan may not include this feature.";
            return $"{service} didn't accept the API key. Check that it is copied in full, and still valid, in {settingsPlace}.";
        }
        if (code == 429 || low.Contains("too_many_concurrent_requests") || low.Contains("rate limit"))
            return $"{service} is busy, or your plan's limit on requests at once was reached. Wait a minute and try again, or lower Concurrent.";
        if (code == 404)
            return low.Contains("model")
                ? $"{service} doesn't know the model that was asked for{Quote()}. Pick another model in {settingsPlace}."
                : $"{service} couldn't find what was asked for{Quote()}. Check the address and model in {settingsPlace}.";
        if (code == 413) return $"The file is too large for {service}. Use a shorter clip.";
        if (code >= 500) return $"{service} had a problem on its end (error {code}). This is usually temporary; try again in a few minutes.";
        if (code == 400 || code == 422)
            return $"{service} didn't accept the request{Quote()}.";
        return $"{service} answered with error {code}{Quote()}.";
    }

    /// <summary>Any exception, as a sentence a user can act on. Messages written for users pass through unchanged.</summary>
    public static string From(Exception ex, string? service = null)
    {
        if (ex is AggregateException ag && ag.InnerExceptions.Count == 1) ex = ag.InnerExceptions[0];
        var who = service ?? "The server";
        switch (ex)
        {
            case OperationCanceledException oce when oce.InnerException is TimeoutException || !oce.CancellationToken.IsCancellationRequested:
                return $"{who} took too long to answer, so the request was stopped. Check your connection and try again.";
            case OperationCanceledException:
                return "Cancelled.";
            case HttpRequestException hre:
                if (hre.StatusCode is HttpStatusCode sc) return Http(service ?? "The server", (int)sc, hre.Message, "Global Settings");
                if (hre.InnerException is SocketException se && se.SocketErrorCode == SocketError.ConnectionRefused)
                    return $"{who} isn't running or isn't listening at that address. Start it, or check the address in Global Settings.";
                if (hre.InnerException is SocketException se2 && (se2.SocketErrorCode == SocketError.HostNotFound || se2.SocketErrorCode == SocketError.TryAgain))
                    return $"Couldn't reach {(service ?? "the server")}: the address wasn't found. Check your internet connection and the address in Global Settings.";
                return $"Couldn't reach {(service ?? "the server")}. Check your internet connection and try again.";
            case PathTooLongException:
                return "A file path is too long for Windows (over 260 characters). Choose a shorter folder, closer to the drive root, such as D:\\DBVO.";
            case UnauthorizedAccessException:
                return "Windows refused access to a file or folder" + PathIn(ex.Message) + ". It may be read-only, open in another program, or in a protected folder such as Program Files.";
            case FileNotFoundException fnf:
                return "A file is missing" + (fnf.FileName != null ? $": {fnf.FileName}" : PathIn(ex.Message)) + ". It may have been moved, renamed or deleted.";
            case DirectoryNotFoundException:
                return "A folder is missing" + PathIn(ex.Message) + ". It may have been moved, renamed or deleted, or the drive isn't connected.";
            case IOException io when (io.HResult & 0xFFFF) == 112 || io.Message.Contains("not enough space", StringComparison.OrdinalIgnoreCase):
                return "The drive is full. Free some space and try again.";
            case IOException io when io.Message.Contains("being used by another process", StringComparison.OrdinalIgnoreCase):
                return "A file is open in another program" + PathIn(io.Message) + ", such as the game, Mod Organizer, an audio player or Explorer's preview. Close it and try again.";
            case JsonException:
                return "A settings or project file couldn't be read: its contents are damaged or in an unexpected format. Restoring a backup (Global Settings → Library) usually fixes this.";
            case InvalidDataException:
            case FormatException:
                return "A file isn't in the expected format: " + ex.Message;
            case OutOfMemoryException:
                return "Windows ran out of memory. Close other programs and try again, or work on fewer lines at a time.";
        }
        return ex.Message;
    }

    private static string PathIn(string message)
    {
        var m = Regex.Match(message, "'([A-Za-z]:\\\\[^']+|\\\\\\\\[^']+)'");
        return m.Success ? $" ({m.Groups[1].Value})" : "";
    }

    private static string Short(string s)
    {
        s = Regex.Replace(s, @"\s+", " ").Trim();
        return s.Length > 160 ? s[..160].TrimEnd() + "…" : s;
    }
}
