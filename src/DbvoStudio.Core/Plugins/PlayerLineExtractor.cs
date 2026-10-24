using DbvoStudio.Core.Rules;

namespace DbvoStudio.Core.Plugins;

/// <summary>Which text field a player line came from.</summary>
public enum LineField
{
    /// <summary>DIAL FULL: the topic text shown in the dialogue menu.</summary>
    TopicText,
    /// <summary>INFO RNAM: the response's own prompt text.</summary>
    Prompt,
}

/// <summary>One player line found in a plugin, with the script's verdict.</summary>
public sealed class ExtractedLine
{
    public required uint FormId { get; init; }
    public required LineField Field { get; init; }
    public required string? EditorId { get; init; }
    public required string Original { get; init; }
    public required LineKind Kind { get; init; }
    /// <summary>The script's suggested text (Valid: the original; Fixed/TooLong: the fix; Broken: null).</summary>
    public required string? Suggested { get; init; }
    /// <summary>Parent topic FormID for prompts; the topic itself for topic text.</summary>
    public required uint TopicFormId { get; init; }
    /// <summary>
    /// Topic text whose every response in this plugin has its own prompt, so the game never shows it.
    /// Shown as skipped by default, with a per-line include switch.
    /// </summary>
    public bool HiddenByPrompts { get; init; }
    /// <summary>File order, for stable display.</summary>
    public required int Order { get; init; }

    public string RecordLabel => Field == LineField.TopicText ? "DIAL FULL" : "INFO RNAM";
}

public sealed class ExtractionResult
{
    public required PluginFile Plugin { get; init; }
    public required List<ExtractedLine> Lines { get; init; }
    /// <summary>Count of localized strings that could not be resolved (strings files missing).</summary>
    public int UnresolvedStrings { get; init; }

    /// <summary>Topic file lines from Valid and Fixed rows, de-duplicated ignoring case, in order.</summary>
    public List<string> TopicLines(bool includeHiddenByPrompts = false)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<string>();
        foreach (var l in Lines)
        {
            if (l.Kind is not (LineKind.Valid or LineKind.Fixed) || l.Suggested == null) continue;
            if (l.HiddenByPrompts && !includeHiddenByPrompts) continue;
            if (seen.Add(l.Suggested)) list.Add(l.Suggested);
        }
        return list;
    }

    public bool NeedsFixPlugin => Lines.Any(l => l.Kind == LineKind.Fixed && !l.HiddenByPrompts);
}

/// <summary>
/// Phase 1 extraction: the xEdit script's Process() over one plugin, in isolation (no load order).
/// DIAL (category Custom or Rumors) topic text and INFO prompt text are checked with <see cref="ScriptRules"/>.
/// </summary>
public static class PlayerLineExtractor
{
    public static ExtractionResult Extract(PluginFile plugin)
    {
        var infosByTopic = plugin.Records
            .Where(r => r.Signature == "INFO" && !r.IsDeleted && r.ParentTopic.HasValue)
            .GroupBy(r => r.ParentTopic!.Value)
            .ToDictionary(g => g.Key, g => g.ToList());

        var lines = new List<ExtractedLine>();
        int unresolved = 0;

        foreach (var rec in plugin.Records)
        {
            if (rec.IsDeleted) continue;
            string? edid = rec.EditorId;

            if (rec.Signature == "DIAL")
            {
                var snam = rec.Get("SNAM");
                string sub = snam == null ? "" : Subrecord.Ascii(snam.Data.AsSpan(0, Math.Min(4, snam.Data.Length)));
                if (sub != "CUST" && sub != "RUMO") continue;

                var full = rec.Get("FULL");
                string? text = full == null ? string.Empty : plugin.DecodeLString(full.Data);
                bool hidden = text != null && IsHiddenByPrompts(plugin, rec, text, infosByTopic);
                if (text == null) { unresolved++; continue; }
                Add(lines, rec, LineField.TopicText, text, edid, rec.FormId, hidden);
            }
            else if (rec.Signature == "INFO")
            {
                var rnam = rec.Get("RNAM");
                if (rnam == null) continue;
                string? text = plugin.DecodeLString(rnam.Data);
                if (text == null) { unresolved++; continue; }
                Add(lines, rec, LineField.Prompt, text, edid, rec.ParentTopic ?? 0, false);
            }
        }

        return new ExtractionResult { Plugin = plugin, Lines = lines, UnresolvedStrings = unresolved };
    }

    /// <summary>
    /// The topic text never shows in game when every response of the topic shows its own prompt instead.
    /// Only said when this plugin holds every response (TIFC, the topic's response count, is not more than the
    /// responses here: a patch that overrides a few responses of a master's topic can't see the rest), every one of
    /// them has a prompt with text (an empty prompt shows the topic text), and no prompt reads the same as the topic.
    /// When in doubt the line is kept: voicing an extra line is harmless, a missing one is silent.
    /// </summary>
    internal static bool IsHiddenByPrompts(PluginFile plugin, PluginRecord dial, string topicText, Dictionary<uint, List<PluginRecord>> infosByTopic)
    {
        var tifc = dial.Get("TIFC");
        if (tifc == null || tifc.Data.Length < 4) return false;
        if (!infosByTopic.TryGetValue(dial.FormId, out var kids) || kids.Count == 0) return false;
        if (BitConverter.ToUInt32(tifc.Data, 0) > kids.Count) return false;
        foreach (var k in kids)
        {
            var rnam = k.Get("RNAM");
            if (rnam == null) return false;
            var prompt = plugin.DecodeLString(rnam.Data);
            if (string.IsNullOrWhiteSpace(prompt)) return false;
            if (string.Equals(prompt.Trim(), topicText.Trim(), StringComparison.Ordinal)) return false;
        }
        return true;
    }

    private static void Add(List<ExtractedLine> lines, PluginRecord rec, LineField field, string text, string? edid, uint topic, bool hidden)
    {
        var r = ScriptRules.Classify(text, edid);
        if (r.Kind == LineKind.Empty) return;
        lines.Add(new ExtractedLine
        {
            FormId = rec.FormId,
            Field = field,
            EditorId = edid,
            Original = text,
            Kind = r.Kind,
            Suggested = r.Final,
            TopicFormId = topic,
            HiddenByPrompts = hidden,
            Order = lines.Count,
        });
    }
}
