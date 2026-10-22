using System.IO.Compression;
using System.Text.Json;

namespace DbvoStudio.Core.Books;

/// <summary>A ready-made set of book texts (spoken text, records, who reads each book), shipped with the app.</summary>
public sealed class BuiltInSet
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public int Version { get; set; }
    public string Description { get; set; } = "";
    /// <summary>Credit shown in the app (the texts' author).</summary>
    public string Credit { get; set; } = "";
    public List<SetBook> Books { get; set; } = new();
}

public sealed class SetBook
{
    public string Stem { get; set; } = "";
    public string Title { get; set; } = "";
    public List<BookRecordRef> Records { get; set; } = new();
    public string Spoken { get; set; } = "";
    /// <summary>narrator | npc</summary>
    public string Reader { get; set; } = "narrator";
    public string VoiceType { get; set; } = "";
    public string Author { get; set; } = "";
    public string Kind { get; set; } = "book";
    public List<BookVariant> Variants { get; set; } = new();
    /// <summary>The set version in which this book's text last changed.</summary>
    public int Version { get; set; } = 1;
}

public static class BuiltInSets
{
    private static readonly Dictionary<string, BuiltInSet?> Cache = new();

    /// <summary>The sets built into this build (id, name, version, book count).</summary>
    public static List<BuiltInSet> List()
    {
        var asm = typeof(BuiltInSets).Assembly;
        return asm.GetManifestResourceNames().Where(n => n.StartsWith("DbvoStudio.Core.Books.Sets.") && n.EndsWith(".json.gz"))
            .Select(n => Load(n["DbvoStudio.Core.Books.Sets.".Length..^".json.gz".Length])).Where(s => s != null).Select(s => s!).ToList();
    }

    public static BuiltInSet? Load(string id)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(id, out var c)) return c;
            using var s = typeof(BuiltInSets).Assembly.GetManifestResourceStream($"DbvoStudio.Core.Books.Sets.{id}.json.gz");
            BuiltInSet? set = null;
            if (s != null)
            {
                using var z = new GZipStream(s, CompressionMode.Decompress);
                set = JsonSerializer.Deserialize<BuiltInSet>(z, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            Cache[id] = set;
            return set;
        }
    }

    /// <summary>
    /// Fill a project from a set: new books get the set's text and reader; books already in the project keep the user's
    /// edits, and a book whose set text changed since gets "updated text available" instead of being overwritten.
    /// </summary>
    public static void Apply(BooksProject p, BuiltInSet set)
    {
        var have = p.Books.ToDictionary(b => b.Stem, StringComparer.OrdinalIgnoreCase);
        var list = new List<Book>();
        foreach (var sb in set.Books)
        {
            if (have.TryGetValue(sb.Stem, out var b))
            {
                b.Records = sb.Records;
                b.Title = sb.Title;
                if (sb.Version > b.SetVersion && !string.Equals(b.Spoken, sb.Spoken, StringComparison.Ordinal))
                {
                    if (!b.SpokenEdited && !p.Lines.Any(l => l.Stem == b.Stem && l.Takes.Count > 0)) { b.Spoken = sb.Spoken; b.SetVersion = sb.Version; b.UpdatedText = null; }
                    else { b.UpdatedText = sb.Spoken; b.UpdatedVersion = sb.Version; }
                }
                // Variants are part of the set: refresh them, keeping ids so their takes stay attached.
                foreach (var v in sb.Variants)
                {
                    var old = b.Variants.FirstOrDefault(x => x.Plugin.Equals(v.Plugin, StringComparison.OrdinalIgnoreCase) && x.WhenInstalled == v.WhenInstalled);
                    if (old == null) b.Variants.Add(Clone(v)); else if (old.Spoken != v.Spoken && sb.Version > b.SetVersion) old.Spoken = v.Spoken;
                }
                list.Add(b);
                continue;
            }
            list.Add(new Book
            {
                Stem = sb.Stem, Title = sb.Title, Records = sb.Records, Spoken = sb.Spoken, Reader = sb.Reader, VoiceType = sb.VoiceType,
                Author = sb.Author, AuthorSource = sb.Author.Length > 0 || sb.VoiceType.Length > 0 ? "set" : "", Kind = sb.Kind,
                AuthorCertainty = sb.Author.Length > 0 ? Certainty.Confirmed : Certainty.None,
                VoiceTypeCertainty = sb.VoiceType.Length > 0 ? Certainty.Confirmed : Certainty.None, AuthorConfirmed = sb.Reader == "npc",
                VoiceTypeVia = sb.VoiceType.Length > 0 ? $"{set.Name} (the set's author)" : "",
                Variants = sb.Variants.Select(Clone).ToList(), SetVersion = sb.Version,
            });
        }
        // Books the user added beside the set stay.
        list.AddRange(p.Books.Where(b => !set.Books.Any(s => s.Stem.Equals(b.Stem, StringComparison.OrdinalIgnoreCase))));
        p.Books = list;
        p.SetVersion = set.Version;
    }

    private static BookVariant Clone(BookVariant v) => new() { Id = v.Id, Plugin = v.Plugin, Label = v.Label, WhenInstalled = v.WhenInstalled, Spoken = v.Spoken };

    /// <summary>Add the game's own text (for the editor and the checks) to set books, from a session of the base game plugins.</summary>
    public static void AttachGameText(BooksProject p, BookSession s)
    {
        var byKey = s.Books.Values.ToDictionary(r => (r.Plugin.ToLowerInvariant(), r.Key.Id));
        foreach (var b in p.Books)
        {
            var r = b.Records.Select(x => byKey.TryGetValue((x.Plugin.ToLowerInvariant(), x.FormId), out var raw) ? raw : null).FirstOrDefault(x => x != null);
            if (r == null) continue;
            b.Raw = r.Raw;
            b.InGame = BookText.Clean(r.Raw);
            foreach (var rec in b.Records) if (rec.EditorId.Length == 0 && byKey.TryGetValue((rec.Plugin.ToLowerInvariant(), rec.FormId), out var rr)) rec.EditorId = rr.EditorId;
        }
    }
}
