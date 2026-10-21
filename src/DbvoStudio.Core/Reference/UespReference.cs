using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DbvoStudio.Core.Reference;

/// <summary>Reference data from UESP (en.uesp.net, CC BY-SA 2.5): NPCs, quoted dialogue and books. See Reference/README.md.</summary>
public sealed class UespReference
{
    public sealed class Npc
    {
        public string Name { get; set; } = "";
        public string Page { get; set; } = "";
        public string Url { get; set; } = "";
        /// <summary>Plugin the NPC comes from (empty for Creation Club pages).</summary>
        public string Plugin { get; set; } = "";
        /// <summary>File-local base ID (6 hex digits), empty when the page has none.</summary>
        public string BaseId { get; set; } = "";
        public string RefId { get; set; } = "";
        public string VoiceType { get; set; } = "";
        public string Race { get; set; } = "";
        public string Gender { get; set; } = "";
    }

    public sealed class Quote
    {
        public string Text { get; set; } = "";
        /// <summary>Indexes into <see cref="Npcs"/> of the NPCs UESP quotes saying it.</summary>
        public List<int> Npcs { get; set; } = new();
        /// <summary>Speaker labels that could not be matched to an NPC page.</summary>
        public List<string>? Labels { get; set; }
    }

    public sealed class Book
    {
        public string Title { get; set; } = "";
        public string Url { get; set; } = "";
        public string Plugin { get; set; } = "";
        /// <summary>File-local form ID (6 hex digits).</summary>
        public string FormId { get; set; } = "";
        public string Author { get; set; } = "";
        /// <summary>The author's UESP page ("Skyrim:Aeri"), when the author is linked.</summary>
        public string AuthorPage { get; set; } = "";
        public string Found { get; set; } = "";
        public string Quest { get; set; } = "";
    }

    public sealed class SourceInfo
    {
        public string Name { get; set; } = "";
        public string Url { get; set; } = "";
        public string License { get; set; } = "";
        public string LicenseUrl { get; set; } = "";
        public int Pages { get; set; }
        public string RevisionsFrom { get; set; } = "";
        public string RevisionsTo { get; set; } = "";
    }

    public SourceInfo Source { get; set; } = new();
    public List<Npc> Npcs { get; set; } = new();
    public List<Quote> Quotes { get; set; } = new();
    public List<Book> Books { get; set; } = new();

    private Dictionary<string, List<Npc>>? _byQuote;
    private Dictionary<(string, uint), Npc>? _byBaseId;
    private Dictionary<string, Npc>? _byPage;
    private Dictionary<(string, uint), Book>? _bookById;

    /// <summary>Plugins UESP describes (quotes and NPCs only apply to lines from these).</summary>
    public static readonly HashSet<string> BaseGamePlugins = new(StringComparer.OrdinalIgnoreCase)
        { "Skyrim.esm", "Update.esm", "Dawnguard.esm", "HearthFires.esm", "Dragonborn.esm" };

    private static readonly Lazy<UespReference?> _shared = new(() => { try { return LoadEmbedded(); } catch { return null; } });

    /// <summary>The data built into the app, or null if it could not be read.</summary>
    public static UespReference? Shared => _shared.Value;

    public static UespReference LoadEmbedded()
    {
        using var s = typeof(UespReference).Assembly.GetManifestResourceStream("DbvoStudio.Core.Reference.uesp-skyrim.json.gz")
            ?? throw new InvalidOperationException("UESP reference data is missing from the build.");
        return Load(s);
    }

    public static UespReference Load(Stream gz)
    {
        using var z = new GZipStream(gz, CompressionMode.Decompress);
        return JsonSerializer.Deserialize<UespReference>(z, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }

    /// <summary>Lowercase letters and digits only, single spaces: how quotes and subtitles are compared.</summary>
    public static string Normalize(string text)
    {
        var sb = new StringBuilder(text.Length);
        bool space = false;
        foreach (var ch in text.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch)) { if (space && sb.Length > 0) sb.Append(' '); sb.Append(ch); space = false; }
            else if (char.IsWhiteSpace(ch)) space = true;
        }
        return sb.ToString();
    }

    /// <summary>NPCs UESP quotes saying this line (exact text after normalizing).</summary>
    public IReadOnlyList<Npc> Speakers(string line)
    {
        _byQuote ??= Quotes.GroupBy(q => Normalize(q.Text))
            .ToDictionary(g => g.Key, g => g.SelectMany(q => q.Npcs).Distinct().Where(i => i >= 0 && i < Npcs.Count).Select(i => Npcs[i]).ToList());
        return _byQuote.TryGetValue(Normalize(line), out var r) ? r : Array.Empty<Npc>();
    }

    /// <summary>The UESP NPC with this base ID in this plugin.</summary>
    public Npc? NpcByBaseId(string plugin, uint id)
    {
        _byBaseId ??= Npcs.Where(n => n.BaseId.Length > 0 && n.Plugin.Length > 0)
            .GroupBy(n => (n.Plugin.ToLowerInvariant(), Convert.ToUInt32(n.BaseId, 16)))
            .ToDictionary(g => g.Key, g => g.First());
        return _byBaseId.TryGetValue((plugin.ToLowerInvariant(), id & 0xFFFFFF), out var n) ? n : null;
    }

    public Npc? NpcByPage(string page)
    {
        _byPage ??= Npcs.GroupBy(n => n.Page, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        return _byPage.TryGetValue(page, out var n) ? n : null;
    }

    /// <summary>The UESP book page for this BOOK record.</summary>
    public Book? BookById(string plugin, uint id)
    {
        _bookById ??= Books.Where(b => b.FormId.Length > 0 && b.Plugin.Length > 0)
            .GroupBy(b => (b.Plugin.ToLowerInvariant(), Convert.ToUInt32(b.FormId, 16)))
            .ToDictionary(g => g.Key, g => g.First());
        return _bookById.TryGetValue((plugin.ToLowerInvariant(), id & 0xFFFFFF), out var b) ? b : null;
    }
}
