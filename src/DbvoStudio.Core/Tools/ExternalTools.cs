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
        if (!File.Exists(exe)) throw new ToolException($"Tool not found: {exe}");
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
            throw new ToolException($"{Path.GetFileName(exe)} timed out or was cancelled.");
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
        if (r.ExitCode != 0 || !File.Exists(output)) throw new ToolException("ffmpeg failed: " + Tail(r.StdErr));
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
            throw new ToolException("FonixData.cdf is not set. Pick it in Settings.");
        var r = await RunAsync(FaceFxWrapperPath, new[] { "Skyrim", "USEnglish", FonixDataPath, wav, resampledWav, lipOut, text }, ct, TimeSpan.FromMinutes(5));
        if (!File.Exists(lipOut)) throw new ToolException("FaceFXWrapper made no .lip: " + Tail(r.StdOut + r.StdErr));
    }

    /// <summary>xWMAEncode /b 48000 in.wav out.xwm (the settings Yakitori uses).</summary>
    public async Task ToXwmaAsync(string wav, string xwmOut, int bitrate = 48000, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(XwmaEncodePath) || !File.Exists(XwmaEncodePath))
            throw new ToolException("xWMAEncode.exe is not set. Pick it in Settings (it comes with Yakitori Audio Converter).");
        var r = await RunAsync(XwmaEncodePath, new[] { "/b", bitrate.ToString(), wav, xwmOut }, ct);
        if (r.ExitCode != 0 || !File.Exists(xwmOut)) throw new ToolException("xWMAEncode failed: " + Tail(r.StdOut + r.StdErr));
    }

    /// <summary>Integrated loudness (LUFS) and true peak (dBTP) of an audio file (ffmpeg ebur128). Null where ffmpeg gave none.</summary>
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
