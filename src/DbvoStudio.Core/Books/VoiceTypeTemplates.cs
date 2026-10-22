namespace DbvoStudio.Core.Books;

/// <summary>
/// Which vanilla voice types each leveled (Lvl…) or character (LChar…) template stands for
/// (Reference/voicetype-templates.txt, compiled for IVBOS). A template can map to several voice types.
/// </summary>
public sealed class VoiceTypeTemplates
{
    private readonly Dictionary<string, List<string>> _byTemplate = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyCollection<string> VoiceTypes { get; }

    public VoiceTypeTemplates(string text)
    {
        var vts = new List<string>();
        string? cur = null;
        foreach (var raw in text.Split('\n'))
        {
            var l = raw.Trim();
            if (l.StartsWith("[[") && l.EndsWith("]]")) { cur = l.Trim('[', ']', ' '); vts.Add(cur); continue; }
            if (cur == null || !l.StartsWith('-')) continue;
            var t = l.TrimStart('-', ' ').Trim();
            if (t.Length == 0) continue;
            if (!_byTemplate.TryGetValue(t, out var list)) _byTemplate[t] = list = new();
            if (!list.Contains(cur, StringComparer.OrdinalIgnoreCase)) list.Add(cur);
        }
        VoiceTypes = vts;
    }

    /// <summary>Voice types a template (by EditorID) stands for, or none.</summary>
    public IReadOnlyList<string> For(string templateEditorId) =>
        _byTemplate.TryGetValue(templateEditorId, out var l) ? l : Array.Empty<string>();

    public int Count => _byTemplate.Count;

    private static readonly Lazy<VoiceTypeTemplates> _shared = new(() =>
    {
        using var s = typeof(VoiceTypeTemplates).Assembly.GetManifestResourceStream("DbvoStudio.Core.Reference.voicetype-templates.txt");
        if (s == null) return new VoiceTypeTemplates("");
        using var r = new StreamReader(s);
        return new VoiceTypeTemplates(r.ReadToEnd());
    });

    public static VoiceTypeTemplates Shared => _shared.Value;
}
