using System.Text.RegularExpressions;
using DbvoStudio.Core.Reference;
using DbvoStudio.Core.Revoice;

namespace DbvoStudio.Core.Books;

/// <summary>What the author lookup found for a book.</summary>
public sealed class AuthorFinding
{
    public string Name { get; set; } = "";
    /// <summary>by-line | signature | uesp | title</summary>
    public string Source { get; set; } = "";
    public Certainty Certainty { get; set; }
    public NpcInfo? Npc { get; set; }
    /// <summary>Several NPCs carry this name (not the same voice type): the first is shown.</summary>
    public int NpcMatches { get; set; }
    public string VoiceType { get; set; } = "";
    public List<string> Candidates { get; set; } = new();
    public string Via { get; set; } = "";
    /// <summary>The voice type is a vanilla one (Skyrim, Update, Dawnguard, HearthFires, Dragonborn).</summary>
    public bool Vanilla { get; set; }
    public Certainty VoiceTypeCertainty { get; set; }
}

/// <summary>
/// Finds who wrote a book and which voice should read it, the IVBOS way: an explicit "By" line or a letter's signature
/// names the author; UESP's book pages add authors linked to NPC pages; a title like "Brand-Shei's Note" is only a hint.
/// The name is then looked up among the NPC records, and the NPC's voice type comes from its record (VTCK), its template
/// (TPLT, with leveled lists and the IVBOS template table) or not at all. Only the author or sender counts: recipients,
/// people mentioned and nearby corpses never do.
/// </summary>
public sealed partial class BookAuthors
{
    private readonly DialogueIndex _idx;
    private readonly Dictionary<FormKey, string> _templateNames;
    private readonly UespReference? _uesp;
    private readonly VoiceTypeTemplates _templates;
    private readonly Dictionary<string, List<NpcInfo>> _byName;

    public BookAuthors(DialogueIndex idx, Dictionary<FormKey, string> templateNames, UespReference? uesp = null, VoiceTypeTemplates? templates = null)
    {
        _idx = idx;
        _templateNames = templateNames;
        _uesp = uesp;
        _templates = templates ?? VoiceTypeTemplates.Shared;
        _byName = idx.Npcs.Values.Where(n => n.Name.Length > 0)
            .GroupBy(n => Key(n.Name)).ToDictionary(g => g.Key, g => g.ToList());
    }

    private static string Key(string name) => Regex.Replace(name.ToLowerInvariant().Replace('’', '\''), @"[^\p{L}\p{N}' ]+", " ").Trim();

    public static bool IsVanillaPlugin(string plugin) => RevoiceService.BaseMasters.Contains(plugin, StringComparer.OrdinalIgnoreCase);

    [GeneratedRegex(@"^(?<name>[A-Z][\p{L}\-]+(?:[ \-][A-Z][\p{L}\-]+){0,2})['’]s?\s+(?:Journal|Diary|Note|Notes|Letter|Log|Orders|Plan|Ledger|Request|Reply|Message|Will|Missive|Report|Confession|Instructions|Research|Writ|List)\b")]
    private static partial Regex TitleOwnerRx();
    [GeneratedRegex(@"^(?:Letter|Note|Message|Orders|Missive|Request|Reply|Plea|Warning)\s+from\s+(?<name>[A-Z][\p{L}\-]+(?:\s+[A-Z][\p{L}\-]+){0,2})\b")]
    private static partial Regex TitleFromRx();

    public AuthorFinding Find(Book b)
    {
        var f = new AuthorFinding();
        var (byLine, rest) = BookText.SplitByLine(b.InGame, b.Title);
        var (sender, _) = BookText.SplitSignature(rest);
        UespReference.Book? ub = null;
        if (_uesp != null && b.First is { } r0 && IsVanillaPlugin(r0.Plugin)) ub = b.Records.Select(r => _uesp.BookById(r.Plugin, r.FormId)).FirstOrDefault(x => x != null);

        if (sender != null) { f.Name = CleanName(sender); f.Source = "signature"; f.Certainty = Certainty.Unverified; }
        else if (byLine != null) { f.Name = CleanName(byLine); f.Source = "by-line"; f.Certainty = Certainty.Unverified; }
        else if (ub != null && ub.Author.Length > 0 && !Regex.IsMatch(ub.Author, @"^(unknown|anonymous|various|none)\b", RegexOptions.IgnoreCase))
        { f.Name = CleanName(ub.Author); f.Source = "uesp"; f.Certainty = Certainty.Unverified; }
        else
        {
            var m = TitleOwnerRx().Match(b.Title);
            if (!m.Success) m = TitleFromRx().Match(b.Title);
            if (m.Success) { f.Name = m.Groups["name"].Value; f.Source = "title"; f.Certainty = Certainty.Inferred; }
        }
        if (f.Name.Length == 0) return f;

        // UESP links the author to an NPC page with the NPC's base ID: that settles which NPC wrote it.
        NpcInfo? npc = null;
        if (ub != null && ub.AuthorPage.Length > 0 && Same(ub.Author, f.Name) && _uesp!.NpcByPage(ub.AuthorPage) is { BaseId.Length: > 0 } un)
        {
            var key = new FormKey(un.Plugin.ToLowerInvariant(), Convert.ToUInt32(un.BaseId, 16));
            if (_idx.Npcs.TryGetValue(key, out var n)) { npc = n; f.Certainty = Certainty.Confirmed; f.Via = "UESP book page links the author's NPC page"; }
        }
        if (npc == null)
        {
            var matches = Lookup(f.Name);
            if (matches.Count > 0)
            {
                // Prefer unique, named NPCs over generic copies with the same name.
                var vtSets = matches.Select(n => string.Join(",", VoiceTypes(n).Select(v => v.Name).OrderBy(x => x))).Distinct().ToList();
                npc = matches.OrderBy(n => n.Template != null ? 1 : 0).ThenBy(n => n.EditorId.Length).First();
                f.NpcMatches = vtSets.Count > 1 ? matches.Count : 1;
            }
        }
        f.Npc = npc;
        if (npc == null) return f;

        var vts = VoiceTypes(npc);
        f.Via = (f.Via.Length > 0 ? f.Via + "; " : "") + (vts.Count > 0 ? vts[0].Via : "no voice type on the NPC record or its template");
        var names = vts.Select(v => v.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (names.Count == 1)
        {
            f.VoiceType = names[0];
            f.Vanilla = vts[0].Vanilla;
            f.VoiceTypeCertainty = f.NpcMatches > 1 ? Certainty.Inferred : f.Certainty == Certainty.Inferred ? Certainty.Inferred : Certainty.Unverified;
            if (f.Certainty == Certainty.Confirmed) f.VoiceTypeCertainty = Certainty.Confirmed;
        }
        else if (names.Count > 1)
        {
            f.Candidates = names;
            f.Vanilla = vts.All(v => v.Vanilla);
            f.VoiceTypeCertainty = Certainty.Inferred;
        }
        return f;
    }

    private static bool Same(string a, string b) => Key(a) == Key(b);

    private static string CleanName(string s)
    {
        s = Regex.Replace(s, @"^(?:by|from|signed|your\s+\w+,?)\s+", "", RegexOptions.IgnoreCase).Trim().TrimEnd('.', ',', '!');
        return s;
    }

    /// <summary>NPC records with exactly this name; a first name alone matches when only one NPC has it.</summary>
    public List<NpcInfo> Lookup(string name)
    {
        var k = Key(name);
        if (_byName.TryGetValue(k, out var exact)) return exact;
        // "Brynjolf" for "Brynjolf the Thief"? Only exact or unique first-name matches are accepted.
        var first = k.Split(' ')[0];
        if (first.Length >= 4 && k.Split(' ').Length == 1)
        {
            var hits = _byName.Where(kv => kv.Key.Split(' ')[0] == first).SelectMany(kv => kv.Value).ToList();
            if (hits.Select(h => h.Name).Distinct().Count() == 1) return hits;
        }
        return new();
    }

    public sealed record VoiceTypeHit(string Name, bool Vanilla, string Via);

    /// <summary>
    /// The voice types an NPC can have, with how each was found: the NPC's own VTCK; its template's (TPLT, followed through
    /// templates and leveled lists in the plugins); and, for Lvl…/LChar… templates, the IVBOS template table.
    /// </summary>
    public List<VoiceTypeHit> VoiceTypes(NpcInfo n)
    {
        var hits = new List<VoiceTypeHit>();
        void Add(FormKey vt, string via)
        {
            if (!_idx.VoiceTypes.TryGetValue(vt, out var v)) return;
            if (hits.Any(h => string.Equals(h.Name, v.EditorId, StringComparison.OrdinalIgnoreCase))) return;
            hits.Add(new VoiceTypeHit(v.EditorId, IsVanillaPlugin(vt.Plugin) || IsVanillaPluginLower(vt.Plugin), via));
        }
        bool fromTemplate = n.UseTraits && n.Template != null;
        if (!fromTemplate && n.VoiceType is { } direct) { Add(direct, "NPC record (VTCK)"); return hits; }
        if (n.Template is { } t)
        {
            var tname = _templateNames.TryGetValue(t, out var tn) ? tn : t.ToString();
            foreach (var vt in _idx.VoiceTypesOf(t)) Add(vt, $"template {tname}");
            if (hits.Count == 0 || hits.Count > 1)
            {
                // The IVBOS table for Lvl…/LChar… templates (exact EditorID match only).
                var listed = _templates.For(tname);
                if (listed.Count > 0)
                {
                    var byName = _idx.VoiceTypes.Values.ToDictionary(v => v.EditorId, v => v.Key, StringComparer.OrdinalIgnoreCase);
                    var keep = hits.Count > 1 ? hits.Where(h => listed.Contains(h.Name, StringComparer.OrdinalIgnoreCase)).ToList() : new List<VoiceTypeHit>();
                    if (hits.Count == 0 || keep.Count > 0)
                    {
                        hits.Clear();
                        hits.AddRange(keep.Select(h => h with { Via = $"template {tname} (IVBOS table)" }));
                        foreach (var name in listed) if (byName.TryGetValue(name, out var key)) Add(key, $"template {tname} (IVBOS table)");
                    }
                }
            }
        }
        if (hits.Count == 0 && n.VoiceType is { } fallback) Add(fallback, "NPC record (VTCK)");
        return hits;
    }

    private static bool IsVanillaPluginLower(string plugin) => RevoiceService.BaseMasters.Any(m => m.Equals(plugin, StringComparison.OrdinalIgnoreCase));
}
