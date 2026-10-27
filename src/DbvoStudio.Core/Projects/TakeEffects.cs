using DbvoStudio.Core.Audio;
using DbvoStudio.Core.Tools;

namespace DbvoStudio.Core.Projects;

/// <summary>
/// Effects (reverb, filters, voices) and Soften peaks on Phase 2, NPC Revoice and book takes: the take's WAV gets the effect and its dry
/// original is kept under &lt;folder&gt;\dry, so the effect can be changed or taken off without a new take. The .lip stays
/// (it was made from the words, and the timing doesn't change).
/// </summary>
public static class TakeEffects
{
    public static string DryPath(string root, Take t) => Path.Combine(root, "dry", t.Id + ".wav");

    /// <summary>
    /// Sets a line's effect and/or Soften peaks (null = leave as it is) and puts them on every take of the line.
    /// Returns the takes changed.
    /// </summary>
    public static async Task<int> SetAsync(string root, Phase2Line line, string? effectId, IEnumerable<EffectPreset>? presets, ExternalTools? tools, CancellationToken ct = default, string? soften = null)
    {
        if (effectId != null) line.Effect = effectId;
        if (soften != null) line.Soften = soften;
        int n = 0;
        foreach (var t in line.Takes)
            if (await RenderAsync(root, t, effectId ?? t.Effect, soften ?? t.Soften, presets, tools, ct)) n++;
        return n;
    }

    /// <summary>One take to the given effect ("" = dry), keeping its Soften peaks. False when nothing changed.</summary>
    public static Task<bool> ApplyAsync(string root, Take t, string effectId, IEnumerable<EffectPreset>? presets, ExternalTools? tools, CancellationToken ct = default) =>
        RenderAsync(root, t, effectId, t.Soften, presets, tools, ct);

    /// <summary>
    /// One take to the given effect and Soften peaks ("" = off for either): back to the dry original first, then the
    /// effect, then the softening. False when nothing changed.
    /// </summary>
    public static async Task<bool> RenderAsync(string root, Take t, string effectId, string soften, IEnumerable<EffectPreset>? presets, ExternalTools? tools, CancellationToken ct = default)
    {
        effectId ??= ""; soften = PeakSoftener.IsMode(soften) ? soften : "";
        if (t.Effect == effectId && t.Soften == soften) return false;
        var wav = Path.Combine(root, t.WavPath);
        if (string.IsNullOrEmpty(t.WavPath) || !File.Exists(wav)) return false;
        var dry = DryPath(root, t);
        if (t.Effect.Length > 0 || t.Soften.Length > 0)
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
            t.Soften = "";
            t.DurationSeconds = Grunts.GruntService.ProbeWavHeader(wav)?.Seconds ?? t.DurationSeconds;
        }
        if (effectId.Length == 0 && soften.Length == 0) return true;
        var fx = effectId.Length == 0 ? null : Effects.Find(presets, effectId) ?? throw new InvalidOperationException($"The effect \"{effectId}\" doesn't exist.");
        Directory.CreateDirectory(Path.GetDirectoryName(dry)!);
        File.Copy(wav, dry, true);
        var tmp = wav + ".fx-tmp.wav";
        try
        {
            string cur = dry;
            if (fx != null) { await Effects.ApplyAsync(cur, tmp, fx, tools, ct); cur = tmp; }
            if (soften.Length > 0) { var t2 = wav + ".sp-tmp.wav"; await PeakSoftener.ApplyAsync(cur, t2, soften, tools, ct); File.Move(t2, tmp, true); cur = tmp; }
            File.Move(tmp, wav, true); // a new file in place, as above
        }
        finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch { } }
        t.Effect = fx?.Id ?? "";
        t.Soften = soften;
        t.DryOriginalFuz = t.OriginalFuz;
        t.OriginalFuz = null;
        t.DurationSeconds = Grunts.GruntService.ProbeWavHeader(wav)?.Seconds ?? t.DurationSeconds;
        return true;
    }
}
