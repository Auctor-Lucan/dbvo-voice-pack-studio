using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace DbvoStudio.Core.Projects;

/// <summary>Import and export of pronunciation lists: CSV, W3C .pls lexicons (ElevenLabs' format) and DBVO Voice Pack Maker 2.7.0 settings.</summary>
public static class PronunciationIO
{
    private static readonly XNamespace Pls = "http://www.w3.org/2005/01/pronunciation-lexicon";

    /// <summary>Read any supported file, chosen by extension (.csv, .pls/.xml, .json).</summary>
    public static List<Pronunciation> Import(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".csv" or ".txt" => ReadCsv(File.ReadAllText(path)),
            ".pls" or ".xml" => ReadPls(File.ReadAllText(path)),
            ".json" => ReadVpm27(File.ReadAllText(path)),
            _ => throw new InvalidOperationException("Choose a .csv, .pls or 2.7.0 settings.json file."),
        };
    }

    public static void Export(string path, IEnumerable<Pronunciation> list)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".pls" or ".xml") File.WriteAllText(path, WritePls(list), new UTF8Encoding(false));
        else File.WriteAllText(path, WriteCsv(list), new UTF8Encoding(true));
    }

    // ---- CSV: word,replacement,type,whole_word,case_sensitive (header optional)

    public static string WriteCsv(IEnumerable<Pronunciation> list)
    {
        var sb = new StringBuilder("word,replacement,type,whole_word,case_sensitive\r\n");
        foreach (var p in list)
            sb.Append(Csv(p.Word)).Append(',').Append(Csv(p.Replacement)).Append(',').Append(p.Kind == "phoneme" ? "ipa" : "alias")
              .Append(',').Append(p.WholeWord ? "yes" : "no").Append(',').Append(p.CaseSensitive ? "yes" : "no").Append("\r\n");
        return sb.ToString();
    }

    private static string Csv(string s) => s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    public static List<Pronunciation> ReadCsv(string text)
    {
        var list = new List<Pronunciation>();
        foreach (var row in ParseCsv(text))
        {
            if (row.Count < 2 || string.IsNullOrWhiteSpace(row[0])) continue;
            if (row[0].Equals("word", StringComparison.OrdinalIgnoreCase) && row[1].Equals("replacement", StringComparison.OrdinalIgnoreCase)) continue;
            bool Yes(int i, bool def) => row.Count > i && !string.IsNullOrWhiteSpace(row[i]) ? row[i].Trim().ToLowerInvariant() is "yes" or "true" or "1" or "y" : def;
            var type = row.Count > 2 ? row[2].Trim().ToLowerInvariant() : "alias";
            list.Add(new Pronunciation
            {
                Word = row[0].Trim(),
                Replacement = row[1].Trim(),
                Kind = type is "ipa" or "phoneme" ? "phoneme" : "alias",
                WholeWord = Yes(3, true),
                CaseSensitive = Yes(4, false),
            });
        }
        return list;
    }

    private static IEnumerable<List<string>> ParseCsv(string text)
    {
        var row = new List<string>();
        var cell = new StringBuilder();
        bool quoted = false;
        char sep = text.Split('\n')[0].Count(c => c == ';') > text.Split('\n')[0].Count(c => c == ',') ? ';' : ',';
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else cell.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == sep) { row.Add(cell.ToString()); cell.Clear(); }
            else if (c == '\n')
            {
                row.Add(cell.ToString().TrimEnd('\r'));
                cell.Clear();
                yield return row;
                row = new List<string>();
            }
            else cell.Append(c);
        }
        if (cell.Length > 0 || row.Count > 0) { row.Add(cell.ToString().TrimEnd('\r')); yield return row; }
    }

    // ---- PLS (W3C Pronunciation Lexicon), as uploaded to ElevenLabs

    public static string WritePls(IEnumerable<Pronunciation> list)
    {
        var root = new XElement(Pls + "lexicon",
            new XAttribute("version", "1.0"),
            new XAttribute("alphabet", "ipa"),
            new XAttribute(XNamespace.Xml + "lang", "en-US"));
        foreach (var p in list.Where(p => !string.IsNullOrWhiteSpace(p.Word)))
            root.Add(new XElement(Pls + "lexeme",
                new XElement(Pls + "grapheme", p.Word),
                p.Kind == "phoneme" ? new XElement(Pls + "phoneme", p.Replacement.Trim('/')) : new XElement(Pls + "alias", p.Replacement)));
        return new XDocument(new XDeclaration("1.0", "UTF-8", null), root).Declaration + "\n" + root;
    }

    public static List<Pronunciation> ReadPls(string text)
    {
        var doc = XDocument.Parse(text);
        var list = new List<Pronunciation>();
        foreach (var lex in doc.Descendants().Where(e => e.Name.LocalName == "lexeme"))
        {
            var alias = lex.Elements().FirstOrDefault(e => e.Name.LocalName == "alias")?.Value;
            var phoneme = lex.Elements().FirstOrDefault(e => e.Name.LocalName == "phoneme")?.Value;
            foreach (var g in lex.Elements().Where(e => e.Name.LocalName == "grapheme"))
            {
                if (string.IsNullOrWhiteSpace(g.Value)) continue;
                list.Add(alias != null
                    ? new Pronunciation { Word = g.Value.Trim(), Replacement = alias.Trim(), Kind = "alias", WholeWord = true, CaseSensitive = true }
                    : new Pronunciation { Word = g.Value.Trim(), Replacement = (phoneme ?? "").Trim(), Kind = "phoneme", WholeWord = true, CaseSensitive = true });
            }
        }
        return list;
    }

    // ---- DBVO Voice Pack Maker 2.7.0: settings.json { "pronunciations": [ { "from": "...", "to": "..." } ] }
    // 2.7.0 replaces whole words, case-sensitively.

    public static List<Pronunciation> ReadVpm27(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("pronunciations", out var arr) || arr.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("This file has no 2.7.0 pronunciation list (no \"pronunciations\" entry).");
        var list = new List<Pronunciation>();
        foreach (var e in arr.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Object) continue;
            var from = e.TryGetProperty("from", out var f) ? f.GetString() : null;
            var to = e.TryGetProperty("to", out var t) ? t.GetString() : null;
            if (string.IsNullOrWhiteSpace(from)) continue;
            list.Add(new Pronunciation { Word = from.Trim(), Replacement = to ?? "", Kind = "alias", WholeWord = true, CaseSensitive = true });
        }
        return list;
    }

    /// <summary>Likely locations of 2.7.0's settings.json (Tauri app data folders), newest first.</summary>
    public static List<string> FindVpm27Settings()
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        }.Where(r => !string.IsNullOrEmpty(r) && Directory.Exists(r));
        var found = new List<string>();
        foreach (var root in roots)
        {
            IEnumerable<string> dirs;
            try { dirs = Directory.EnumerateDirectories(root); } catch { continue; }
            foreach (var d in dirs)
            {
                var name = Path.GetFileName(d).ToLowerInvariant();
                if (!(name.Contains("dbvo") || name.Contains("vpm") || name.Contains("voice"))) continue;
                try
                {
                    foreach (var f in Directory.EnumerateFiles(d, "settings.json", SearchOption.AllDirectories).Take(10))
                    {
                        try
                        {
                            var text = File.ReadAllText(f);
                            if (text.Contains("\"pronunciations\"")) found.Add(f);
                        }
                        catch { }
                    }
                }
                catch { }
            }
        }
        return found.OrderByDescending(File.GetLastWriteTimeUtc).ToList();
    }

    /// <summary>Merge imported entries into a list: same word (ignoring case) is replaced, new words are added.</summary>
    public static (int Added, int Replaced) Merge(List<Pronunciation> target, IEnumerable<Pronunciation> incoming)
    {
        int added = 0, replaced = 0;
        foreach (var p in incoming)
        {
            int i = target.FindIndex(x => string.Equals(x.Word.Trim(), p.Word.Trim(), StringComparison.OrdinalIgnoreCase));
            if (i >= 0) { target[i] = p; replaced++; }
            else { target.Add(p); added++; }
        }
        return (added, replaced);
    }
}
