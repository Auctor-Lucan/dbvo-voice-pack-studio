using DbvoStudio.Core.Audio;
using DbvoStudio.Core.Tools;

namespace DbvoStudio.Core.Projects;

/// <summary>
/// Effects (reverb, filters, voices) on Phase 2, NPC Revoice and book takes: the take's WAV gets the effect and its dry
/// original is kept under &lt;folder&gt;\dry, so the effect can be changed or taken off without a new take. The .lip stays
/// (it was made from the words, and the timing doesn't change).
/// </summary>
public static class TakeEffects
{
    public static string DryPath(string root, Take t) => Path.Combine(root, "dry", t.Id + ".wav");

    /// <summary>Sets a line's effect and puts it on (or takes it off) every take of the line. Returns the takes changed.</summary>
    public static async Task<int> SetAsync(string root, Phase2Line line, string? effectId, IEnumerable<EffectPreset>? presets, ExternalTools? tools, CancellationToken ct = default)
    {
        line.Effect = effectId ?? "";
        int n = 0;
        foreach (var t in line.Takes) if (await ApplyAsync(root, t, line.Effect, presets, tools, ct)) n++;
        return n;
    }

    /// <summary>One take to the given effect ("" = dry). False when nothing changed.</summary>
    public static async Task<bool> ApplyAsync(string root, Take t, string effectId, IEnumerable<EffectPreset>? presets, ExternalTools? tools, CancellationToken ct = default)
    {
        if (t.Effect == effectId) return false;
        var wav = Path.Combine(root, t.WavPath);
        if (string.IsNullOrEmpty(t.WavPath) || !File.Exists(wav)) return false;
        var dry = DryPath(root, t);
        if (t.Effect.Length > 0)
        {
            if (File.Exists(dry))
            {
                // A new file in place (not written through: take files can be hard links to the WAV + LIP folder).
                var back = wav + ".dry-tmp";
                File.Copy(dry, back, true);
                File.Move(back, wav, true);
                File.Delete(dry);
            }
            t.OriginalFuz = t.DryOriginalFuz ?? t.OriginalFuz;
            t.DryOriginalFuz = null;
            t.Effect = "";
            t.DurationSeconds = Grunts.GruntService.ProbeWavHeader(wav)?.Seconds ?? t.DurationSeconds;
        }
        if (effectId.Length == 0) return true;
        var fx = Effects.Find(presets, effectId) ?? throw new InvalidOperationException($"The effect \"{effectId}\" doesn't exist.");
        Directory.CreateDirectory(Path.GetDirectoryName(dry)!);
        File.Copy(wav, dry, true);
        await Effects.ApplyAsync(dry, wav, fx, tools, ct);
        t.Effect = fx.Id;
        t.DryOriginalFuz = t.OriginalFuz;
        t.OriginalFuz = null;
        t.DurationSeconds = Grunts.GruntService.ProbeWavHeader(wav)?.Seconds ?? t.DurationSeconds;
        return true;
    }
}
