using System.IO;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using DbvoStudio.Core.Naming;
using DbvoStudio.Core.Projects;
using DbvoStudio.Core.Tools;
using Whisper.net;
using Whisper.net.Ggml;
using Whisper.net.LibraryLoader;

namespace DbvoStudio.App.Tools;

public sealed record WavToLipEvent(string Kind, string Text, int Index = 0, int Total = 0);

/// <summary>
/// The WAV to LIP tool: transcribe each WAV with Whisper, then make a .lip next to it with FaceFXWrapper using the
/// transcript as the text. A port of the WAV_to_LIP Python app: same FaceFXWrapper call, temporary files in
/// %TEMP%\WAV_to_LIP, stale-.lip check, CUDA-to-CPU fallback. Two engines: Whisper.net built in (whisper.cpp, GPU via
/// Vulkan or CPU), or the user's own faster-whisper Python environment (CUDA, exactly as before).
/// </summary>
public sealed class WavToLip
{
    private readonly WavToLipSettings _s;
    private readonly ExternalTools _tools;
    private readonly string _modelsFolder;
    private readonly Action<WavToLipEvent> _emit;

    public WavToLip(WavToLipSettings settings, ExternalTools tools, string modelsFolder, Action<WavToLipEvent> emit)
    {
        _s = settings;
        _tools = tools;
        _modelsFolder = modelsFolder;
        _emit = emit;
    }

    private static readonly string TempRoot = Path.Combine(Path.GetTempPath(), "WAV_to_LIP");

    private void Log(string t) => _emit(new WavToLipEvent("log", t));
    private void Status(string t, int i = 0, int n = 0) => _emit(new WavToLipEvent("status", t, i, n));

    public async Task<(int Success, int Failed)> RunAsync(IReadOnlyList<string> files, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_tools.FonixDataPath) || !File.Exists(_tools.FonixDataPath))
            throw new InvalidOperationException("FonixData.cdf is not set. Pick it in Settings → General & tools. The official FaceFXWrapper needs this dictionary.");
        if (!File.Exists(_tools.FaceFxWrapperPath))
            throw new InvalidOperationException("FaceFXWrapper.exe was not found. Pick it in Settings → General & tools.");
        Directory.CreateDirectory(TempRoot);

        Func<string, CancellationToken, Task<string>> transcribe;
        IAsyncDisposable? engine = null;
        if (_s.UseFilename)
        {
            transcribe = (wav, _) => Task.FromResult(TextFromFilename(wav));
            Log("Using each file's name as the text (no transcription).");
        }
        else if (_s.Engine == "faster-whisper")
        {
            var fw = new FasterWhisperProcess(_s, Log);
            await fw.StartAsync(ct);
            engine = fw;
            transcribe = fw.TranscribeAsync;
        }
        else
        {
            var bw = await BuiltInWhisper.CreateAsync(_s, _modelsFolder, _tools, Log, Status, ct);
            engine = bw;
            transcribe = bw.TranscribeAsync;
        }

        int success = 0, failures = 0, total = files.Count;
        try
        {
            for (int i = 0; i < total; i++)
            {
                ct.ThrowIfCancellationRequested();
                var wav = files[i];
                var name = Path.GetFileName(wav);
                string? tempTxt = null;
                var resampled = Path.Combine(TempRoot, Path.GetFileNameWithoutExtension(wav) + "_resampled.wav");
                Status($"[{i + 1}/{total}] Transcribing {name}...", i, total);
                try
                {
                    var transcript = (await transcribe(wav, ct)).Trim();
                    if (transcript.Length == 0) throw new InvalidOperationException("Whisper returned an empty transcript.");
                    // The temporary transcript is the source of the exact text passed to FaceFXWrapper.
                    tempTxt = Path.Combine(TempRoot, Path.GetFileNameWithoutExtension(wav) + "_" + Environment.CurrentManagedThreadId + ".txt");
                    await File.WriteAllTextAsync(tempTxt, transcript, new UTF8Encoding(false), ct);
                    var facefxText = (await File.ReadAllTextAsync(tempTxt, ct)).Trim();
                    var lip = Path.ChangeExtension(wav, ".lip");
                    DateTime? previous = File.Exists(lip) ? File.GetLastWriteTimeUtc(lip) : null;
                    Status($"[{i + 1}/{total}] Generating {Path.GetFileName(lip)} using the transcript...", i, total);
                    var r = await ExternalTools.RunAsync(_tools.FaceFxWrapperPath,
                        new[] { "Skyrim", "USEnglish", _tools.FonixDataPath!, wav, resampled, lip, facefxText }, ct, TimeSpan.FromSeconds(180));
                    // Official FaceFXWrapper's primary success condition is the output LIP existing.
                    if (!File.Exists(lip) || new FileInfo(lip).Length == 0)
                    {
                        var details = (r.StdOut + "\n" + r.StdErr);
                        throw new InvalidOperationException($"FaceFXWrapper did not create a non-empty .lip (exit code {r.ExitCode}). " + details[^Math.Min(1200, details.Length)..]);
                    }
                    if (previous != null && File.GetLastWriteTimeUtc(lip) == previous)
                        throw new InvalidOperationException("The existing .lip file was not updated; refusing to count a stale file as success.");
                    success++;
                    Log($"SUCCESS: {name} -> {Path.GetFileName(lip)}   \"{facefxText}\"");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    failures++;
                    Log($"FAILED: {name}: {ex.Message}");
                }
                finally
                {
                    if (tempTxt != null) try { File.Delete(tempTxt); } catch { }
                    try { if (File.Exists(resampled)) File.Delete(resampled); } catch { }
                }
            }
            if (engine is BuiltInWhisper b) Log($"Transcription device used: {b.DeviceUsed}");
            if (engine is FasterWhisperProcess f && f.DeviceUsed != null) Log($"Transcription device used: {f.DeviceUsed}");
        }
        finally
        {
            if (engine != null) await engine.DisposeAsync();
        }
        return (success, failures);
    }

    /// <summary>
    /// A transcriber using the WAV to LIP engine settings (built-in Whisper or faster-whisper), for other tools
    /// such as the NPC Revoice orphan matching. Dispose the returned engine when done.
    /// </summary>
    public async Task<(Func<string, CancellationToken, Task<string>> Transcribe, IAsyncDisposable Engine)> CreateTranscriberAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(TempRoot);
        if (_s.Engine == "faster-whisper")
        {
            var fw = new FasterWhisperProcess(_s, Log);
            await fw.StartAsync(ct);
            return (fw.TranscribeAsync, fw);
        }
        var bw = await BuiltInWhisper.CreateAsync(_s, _modelsFolder, _tools, Log, Status, ct);
        return (bw.TranscribeAsync, bw);
    }

    /// <summary>Legacy names are the line itself: underscores back to spaces.</summary>
    public static string TextFromFilename(string wav)
    {
        var stem = Path.GetFileNameWithoutExtension(wav);
        return stem.Replace('_', ' ').Trim();
    }

    // ------------------------------------------------------------------ built-in Whisper.net

    private sealed class BuiltInWhisper : IAsyncDisposable
    {
        private WhisperFactory _factory = null!;
        private WhisperProcessor _processor = null!;
        private readonly WavToLipSettings _s;
        private readonly string _modelPath;
        private readonly Action<string> _log;
        private readonly ExternalTools _tools;
        public string DeviceUsed { get; private set; } = "";

        private BuiltInWhisper(WavToLipSettings s, string modelPath, ExternalTools tools, Action<string> log)
        {
            _s = s;
            _modelPath = modelPath;
            _tools = tools;
            _log = log;
        }

        public static async Task<BuiltInWhisper> CreateAsync(WavToLipSettings s, string modelsFolder, ExternalTools tools, Action<string> log, Action<string, int, int> status, CancellationToken ct)
        {
            var (type, q, file) = ModelFile(s.Model, s.Compute);
            Directory.CreateDirectory(modelsFolder);
            var path = Path.Combine(modelsFolder, file);
            if (!File.Exists(path))
            {
                status($"Downloading the Whisper {s.Model} model ({file}). This happens once.", 0, 0);
                log($"Downloading {file} to {modelsFolder}");
                var tmp = path + ".part";
                await using (var src = await WhisperGgmlDownloader.Default.GetGgmlModelAsync(type, q, ct))
                await using (var dst = File.Create(tmp))
                    await src.CopyToAsync(dst, ct);
                File.Move(tmp, path, true);
            }
            var w = new BuiltInWhisper(s, path, tools, log);
            w.Load(s.Device == "cuda" || s.Device == "gpu");
            return w;
        }

        private void Load(bool gpu)
        {
            if (gpu)
            {
                try
                {
                    Build(true);
                    DeviceUsed = $"GPU ({RuntimeOptions.LoadedLibrary})";
                    _log($"Loaded Whisper {_s.Model} on the GPU ({RuntimeOptions.LoadedLibrary}, {_s.Compute}).");
                    return;
                }
                catch (Exception ex)
                {
                    if (!_s.CpuFallback) throw;
                    _log($"GPU initialization failed: {ex.Message}");
                    _log("Retrying the model on CPU.");
                }
            }
            Build(false);
            DeviceUsed = "CPU";
            _log($"Loaded Whisper {_s.Model} on the CPU ({_s.Compute}).");
        }

        private void Build(bool gpu)
        {
            _processor?.Dispose();
            _factory?.Dispose();
            _factory = WhisperFactory.FromPath(_modelPath, new WhisperFactoryOptions { UseGpu = gpu });
            var b = _factory.CreateBuilder();
            b = _s.Language == "auto" ? b.WithLanguageDetection() : b.WithLanguage(_s.Language);
            b = b.WithBeamSearchSamplingStrategy(o => o.WithBeamSize(5));
            _processor = b.Build();
        }

        public async Task<string> TranscribeAsync(string wav, CancellationToken ct)
        {
            // whisper.cpp wants 16 kHz mono 16-bit: convert in the temp folder with ffmpeg.
            var tmp = Path.Combine(TempRoot, Guid.NewGuid().ToString("N")[..8] + "_16k.wav");
            try
            {
                await _tools.ToWavAsync(wav, tmp, 16000, false, 0, ct);
                try { return await Run(tmp, ct); }
                catch (Exception ex) when (DeviceUsed.StartsWith("GPU") && _s.CpuFallback && ex is not OperationCanceledException)
                {
                    // The GPU can fail on the first inference even if loading succeeded.
                    _log($"GPU inference failed on {Path.GetFileName(wav)}: {ex.Message}");
                    _log("Retrying this file on CPU.");
                    Build(false);
                    DeviceUsed = "CPU";
                    return await Run(tmp, ct);
                }
            }
            finally { try { File.Delete(tmp); } catch { } }
        }

        private async Task<string> Run(string wav16, CancellationToken ct)
        {
            var sb = new StringBuilder();
            await using var fs = File.OpenRead(wav16);
            await foreach (var seg in _processor.ProcessAsync(fs, ct))
            {
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(seg.Text.Trim());
            }
            return sb.ToString().Trim();
        }

        public ValueTask DisposeAsync()
        {
            _processor?.Dispose();
            _factory?.Dispose();
            return ValueTask.CompletedTask;
        }

        /// <summary>Model + compute type to the ggml file: float16 = unquantized, int8 / int8_float16 = q8_0.</summary>
        public static (GgmlType, QuantizationType, string) ModelFile(string model, string compute)
        {
            var type = model switch
            {
                "medium" => GgmlType.Medium,
                "small" => GgmlType.Small,
                "base" => GgmlType.Base,
                _ => GgmlType.LargeV3,
            };
            var q = compute is "int8" or "int8_float16" ? QuantizationType.Q8_0 : QuantizationType.NoQuantization;
            return (type, q, $"ggml-{model}{(q == QuantizationType.Q8_0 ? "-q8_0" : "")}.bin");
        }
    }

    // ------------------------------------------------------------------ the user's faster-whisper environment

    /// <summary>Runs the bundled helper script with the user's Python (faster-whisper installed), one file per request.</summary>
    private sealed class FasterWhisperProcess : IAsyncDisposable
    {
        private readonly WavToLipSettings _s;
        private readonly Action<string> _log;
        private Process? _p;
        public string? DeviceUsed { get; private set; }

        public FasterWhisperProcess(WavToLipSettings s, Action<string> log)
        {
            _s = s;
            _log = log;
        }

        public async Task StartAsync(CancellationToken ct)
        {
            if (!File.Exists(_s.PythonPath))
                throw new InvalidOperationException("Set python.exe of your faster-whisper environment in Settings → Tools (e.g. WAV_to_LIP_App\\.venv\\Scripts\\python.exe).");
            var script = Path.Combine(AppContext.BaseDirectory, "Tools", "faster_whisper_server.py");
            var psi = new ProcessStartInfo(_s.PythonPath)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardInputEncoding = new UTF8Encoding(false),
            };
            psi.ArgumentList.Add("-u");
            psi.ArgumentList.Add(script);
            psi.ArgumentList.Add(JsonSerializer.Serialize(new { model = _s.Model, device = _s.Device, compute = _s.Compute, language = _s.Language, fallback = _s.CpuFallback }));
            psi.Environment["PYTHONIOENCODING"] = "utf-8";
            if (!string.IsNullOrWhiteSpace(_s.CudaLibsFolder))
                psi.Environment["PATH"] = _s.CudaLibsFolder + ";" + Environment.GetEnvironmentVariable("PATH");
            _p = Process.Start(psi) ?? throw new InvalidOperationException("Could not start Python.");
            _ = Task.Run(async () =>
            {
                string? line;
                while ((line = await _p.StandardError.ReadLineAsync()) != null)
                    if (!string.IsNullOrWhiteSpace(line) && !line.Contains("%|")) _log("python: " + line);
            });
            var ready = await ReadAsync(ct);
            if (ready.GetProperty("type").GetString() == "fatal") throw new InvalidOperationException(ready.GetProperty("message").GetString());
            DeviceUsed = ready.TryGetProperty("device", out var d) ? d.GetString() : null;
        }

        private async Task<JsonElement> ReadAsync(CancellationToken ct)
        {
            while (true)
            {
                var line = await _p!.StandardOutput.ReadLineAsync(ct) ?? throw new InvalidOperationException("The faster-whisper helper stopped. See the log for Python's error.");
                if (!line.StartsWith("{")) continue;
                var el = JsonDocument.Parse(line).RootElement.Clone();
                if (el.GetProperty("type").GetString() == "log") { _log(el.GetProperty("message").GetString() ?? ""); continue; }
                return el;
            }
        }

        public async Task<string> TranscribeAsync(string wav, CancellationToken ct)
        {
            await _p!.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { file = wav }));
            await _p.StandardInput.FlushAsync(ct);
            var r = await ReadAsync(ct);
            if (r.TryGetProperty("device", out var d)) DeviceUsed = d.GetString();
            if (r.GetProperty("type").GetString() == "error") throw new InvalidOperationException(r.GetProperty("message").GetString());
            return r.GetProperty("text").GetString() ?? "";
        }

        public async ValueTask DisposeAsync()
        {
            if (_p == null) return;
            try
            {
                if (!_p.HasExited)
                {
                    await _p.StandardInput.WriteLineAsync("{\"quit\":true}");
                    await _p.StandardInput.FlushAsync();
                    if (!_p.WaitForExit(3000)) _p.Kill(true);
                }
            }
            catch { }
            _p.Dispose();
        }
    }
}
