using System.Diagnostics;
using System.Text;

namespace DbvoStudio.Core.Tools;

public sealed record ToolResult(int ExitCode, string StdOut, string StdErr);

public sealed class ToolException : Exception
{
    public ToolException(string message) : base(message) { }
}

/// <summary>
/// Runs ffmpeg, FaceFXWrapper and xWMAEncode. These tools fail on long paths, so callers give them
/// files in a short work folder (see <see cref="WorkFolder"/>) and rename the results afterwards.
/// </summary>
public sealed class ExternalTools
{
    public required string FfmpegPath { get; init; }
    public required string FaceFxWrapperPath { get; init; }
    public string? XwmaEncodePath { get; init; }
    public string? FonixDataPath { get; init; }

    public static async Task<ToolResult> RunAsync(string exe, IEnumerable<string> args, CancellationToken ct = default, TimeSpan? timeout = null)
    {
        if (!File.Exists(exe)) throw new ToolException($"{Path.GetFileName(exe)} isn't where it was set ({exe}). Pick it again in Global Settings → General & tools → Tools.");
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = new Process { StartInfo = psi };
        var o = new StringBuilder();
        var e = new StringBuilder();
        p.OutputDataReceived += (_, d) => { if (d.Data != null) o.AppendLine(d.Data); };
        p.ErrorDataReceived += (_, d) => { if (d.Data != null) e.AppendLine(d.Data); };
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout ?? TimeSpan.FromMinutes(5));
        try
        {
            await p.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(true); } catch { }
            throw new ToolException(ct.IsCancellationRequested ? "Cancelled." : $"{Path.GetFileName(exe)} took longer than {(int)(timeout ?? TimeSpan.FromMinutes(5)).TotalMinutes} minutes and was stopped. The file may be very long or damaged.");
        }
        return new ToolResult(p.ExitCode, o.ToString(), e.ToString());
    }

    /// <summary>Any audio to mono 16-bit PCM WAV at the given rate. Optional EBU R128 loudness normalization and gain.</summary>
    public async Task ToWavAsync(string input, string output, int sampleRate = 44100, bool loudnorm = false, double gainDb = 0, CancellationToken ct = default)
    {
        var filters = new List<string>();
        if (loudnorm) filters.Add("loudnorm=I=-16:TP=-1.5:LRA=11");
        if (Math.Abs(gainDb) > 0.001) filters.Add(FormattableString.Invariant($"volume={gainDb:0.##}dB"));
        var args = new List<string> { "-y", "-hide_banner", "-loglevel", "error", "-i", input };
        if (filters.Count > 0) { args.Add("-af"); args.Add(string.Join(",", filters)); }
        args.AddRange(new[] { "-ac", "1", "-ar", sampleRate.ToString(), "-acodec", "pcm_s16le", "-map_metadata", "-1", output });
        var r = await RunAsync(FfmpegPath, args, ct);
        if (r.ExitCode != 0 || !File.Exists(output)) throw new ToolException($"ffmpeg couldn't convert {Path.GetFileName(input)}. The file may be damaged or not audio. ({Tail(r.StdErr)})");
    }

    /// <summary>Raw 16-bit little-endian mono PCM (ElevenLabs pcm_* output) to a WAV file.</summary>
    public static void PcmToWav(byte[] pcm, int sampleRate, string output)
    {
        var samples = new short[pcm.Length / 2];
        Buffer.BlockCopy(pcm, 0, samples, 0, samples.Length * 2);
        new Audio.WavFile { SampleRate = sampleRate, Channels = 1, Samples = samples }.Write(output);
    }

    /// <summary>FaceFXWrapper "Skyrim" "USEnglish" FonixData wav resampledWav lip "text".</summary>
    public async Task MakeLipAsync(string wav, string resampledWav, string lipOut, string text, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(FonixDataPath) || !File.Exists(FonixDataPath))
            throw new ToolException("Making .lip files needs FonixData.cdf, and it isn't set. Pick it in Global Settings → General & tools → Tools.");
        var r = await RunAsync(FaceFxWrapperPath, new[] { "Skyrim", "USEnglish", FonixDataPath, wav, resampledWav, lipOut, text }, ct, TimeSpan.FromMinutes(5));
        if (!File.Exists(lipOut)) throw new ToolException($"FaceFXWrapper made no .lip file for {Path.GetFileName(wav)}. Usual causes: the text is empty or has only symbols, the audio is silent, or FonixData.cdf is the wrong file.{Details(r.StdOut + r.StdErr)}");
    }

    /// <summary>xWMAEncode /b 48000 in.wav out.xwm (the settings Yakitori uses).</summary>
    public async Task ToXwmaAsync(string wav, string xwmOut, int bitrate = 48000, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(XwmaEncodePath) || !File.Exists(XwmaEncodePath))
            throw new ToolException("Building .fuz files needs xWMAEncode.exe, and it isn't set. Pick it in Global Settings → General & tools → Tools (it comes with Yakitori Audio Converter).");
        var r = await RunAsync(XwmaEncodePath, new[] { "/b", bitrate.ToString(), wav, xwmOut }, ct);
        if (r.ExitCode != 0 || !File.Exists(xwmOut)) throw new ToolException($"xWMAEncode couldn't encode {Path.GetFileName(wav)}. The WAV may be damaged or empty.{Details(r.StdOut + r.StdErr)}");
    }

    /// <summary>Integrated loudness (LUFS) and true peak (dBTP) of an audio file (ffmpeg ebur128). Null where ffmpeg gave none.</summary>
    /// <summary>
    /// One ffmpeg run for Check takes: measures loudness and true peak (EBU R128) and, when <paramref name="wav16"/> is given,
    /// writes the 16 kHz mono copy Whisper reads, so each take starts ffmpeg once instead of twice.
    /// </summary>
    public async Task<(double? Lufs, double? TruePeak)> PrepareForCheckAsync(string input, string? wav16, CancellationToken ct = default)
    {
        var args = new List<string> { "-y", "-hide_banner", "-nostats", "-i", input, "-af", "ebur128=peak=true" };
        if (wav16 == null) args.AddRange(new[] { "-f", "null", "-" });
        else args.AddRange(new[] { "-ac", "1", "-ar", "16000", "-acodec", "pcm_s16le", "-map_metadata", "-1", wav16 });
        var r = await RunAsync(FfmpegPath, args, ct);
        if (wav16 != null && (r.ExitCode != 0 || !File.Exists(wav16))) throw new ToolException($"ffmpeg couldn't read {Path.GetFileName(input)}. ({Tail(r.StdErr)})");
        return ParseEbur128(r.StdErr);
    }

    public async Task<(double? Lufs, double? TruePeak)> MeasureAsync(string input, CancellationToken ct = default)
    {
        var r = await RunAsync(FfmpegPath, new[] { "-hide_banner", "-nostats", "-i", input, "-af", "ebur128=peak=true", "-f", "null", "-" }, ct);
        return ParseEbur128(r.StdErr);
    }

    /// <summary>The Summary block of ffmpeg's ebur128 filter: "I: -18.3 LUFS" and, under True peak, "Peak: -1.4 dBFS".</summary>
    public static (double? Lufs, double? TruePeak) ParseEbur128(string stderr)
    {
        int at = stderr.LastIndexOf("Summary:", StringComparison.Ordinal);
        var sum = at >= 0 ? stderr[at..] : stderr;
        static double? Num(System.Text.RegularExpressions.Match m) =>
            m.Success && double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v
            : m.Success && m.Groups[1].Value.Contains("inf") ? double.NegativeInfinity : null;
        var i = Num(System.Text.RegularExpressions.Regex.Match(sum, @"I:\s+(-?[\d.]+|-inf) LUFS"));
        var tp = Num(System.Text.RegularExpressions.Regex.Match(sum, @"Peak:\s+(-?[\d.]+|-inf) dBFS"));
        return (i, tp);
    }

    private static string Details(string s) { var t = Tail(s); return t.Length == 0 ? "" : $" ({t})"; }

    private static string Tail(string s)
    {
        var lines = s.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join(" ", lines.TakeLast(3));
    }
}

/// <summary>A short-path scratch folder (e.g. C:\Users\x\AppData\Local\DBVOStudio\w\a1b2c3) with short file names.</summary>
public sealed class WorkFolder : IDisposable
{
    public WorkFolder(string root)
    {
        Path = System.IO.Path.Combine(root, Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try { Directory.Delete(Path, true); } catch { }
    }
}
