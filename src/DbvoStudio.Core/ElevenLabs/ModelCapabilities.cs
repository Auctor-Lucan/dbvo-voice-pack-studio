namespace DbvoStudio.Core.ElevenLabs;

/// <summary>Which settings each ElevenLabs model family accepts, so the toolbar only shows what applies.</summary>
public static class ModelCapabilities
{
    public static bool IsTagModel(string modelId) =>
        modelId.StartsWith("eleven_v3", StringComparison.OrdinalIgnoreCase) ||
        modelId.StartsWith("eleven_v4", StringComparison.OrdinalIgnoreCase);

    /// <summary>v3/v4 take three stability values: Creative 0, Natural 0.5, Robust 1.</summary>
    public static bool HasDiscreteStability(string modelId) => IsTagModel(modelId);

    public static bool SupportsStyle(string modelId) => !IsTagModel(modelId) && !modelId.Contains("flash", StringComparison.OrdinalIgnoreCase);

    public static bool SupportsSpeakerBoost(string modelId) => !IsTagModel(modelId);

    public static bool SupportsSimilarity(string modelId) => true;

    public static bool SupportsSpeed(string modelId) => true;

    /// <summary>Inline IPA between slashes and IPA phoneme dictionary rules.</summary>
    public static bool SupportsPhonemeRules(string modelId) =>
        IsTagModel(modelId) || modelId.Equals("eleven_flash_v2", StringComparison.OrdinalIgnoreCase);

    public static int MaxCharacters(string modelId) => modelId switch
    {
        var m when m.StartsWith("eleven_v4", StringComparison.OrdinalIgnoreCase) => 10_000,
        var m when m.StartsWith("eleven_v3", StringComparison.OrdinalIgnoreCase) => 5_000,
        var m when m.StartsWith("eleven_flash_v2_5", StringComparison.OrdinalIgnoreCase) => 40_000,
        var m when m.StartsWith("eleven_flash_v2", StringComparison.OrdinalIgnoreCase) => 30_000,
        _ => 10_000,
    };

    /// <summary>Snap a stability value to the nearest v3/v4 step.</summary>
    public static double SnapStability(double v) => v < 0.25 ? 0 : v < 0.75 ? 0.5 : 1;

    /// <summary>Build the voice settings for a request, dropping anything the model does not support.</summary>
    public static VoiceSettings Build(string modelId, double stability, double similarity, double style, bool speakerBoost, double speed)
    {
        var s = new VoiceSettings
        {
            Stability = HasDiscreteStability(modelId) ? SnapStability(stability) : stability,
            SimilarityBoost = similarity,
            Speed = speed,
        };
        if (SupportsStyle(modelId)) s.Style = style;
        if (SupportsSpeakerBoost(modelId)) s.UseSpeakerBoost = speakerBoost;
        return s;
    }
}
