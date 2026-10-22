using System.IO;
using System.Text.Json;
using DbvoStudio.Core.Books;
using DbvoStudio.Core.ElevenLabs;
using DbvoStudio.Core.Naming;
using DbvoStudio.Core.Projects;
using DbvoStudio.Core.Revoice;
using DbvoStudio.Core.Text;
using DbvoStudio.Core.Tools;

namespace DbvoStudio.App;

/// <summary>Bridge methods for the Voiced Books Tool (Other Tools).</summary>
public sealed partial class Backend
{
    private readonly Dictionary<string, BooksProject> _bk = new();
    private readonly Dictionary<string, BookSession> _bkSessions = new();
    /// <summary>Text checks per book, cached by the texts they were made from.</summary>
    private readonly Dictionary<string, (int Hash, List<BookText.Issue> Issues)> _bkChecks = new();

    private BooksProject LoadBk(string id)
    {
        lock (_bk)
        {
            if (_bk.TryGetValue(id, out var p)) return p;
            p = _lib.LoadBooksProject(id) ?? throw new InvalidOperationException("Voiced Books project not found.");
            _bk[id] = p;
            return p;
        }
    }

    private BooksService BkService() => new(_lib, Runtime());

    private BookSession BkSession(BooksProject p, IProgress<string>? progress = null)
    {
        var sig = BooksService.SessionSignature(p);
        lock (_bkSessions) { if (_bkSessions.TryGetValue(p.Id, out var s) && s.Signature == sig) return s; }
        var fresh = BkService().Open(p, progress);
        lock (_bkSessions) _bkSessions[p.Id] = fresh;
        return fresh;
    }

    /// <summary>Tools for books: ffmpeg and xWMAEncode (no .lip files, so FaceFXWrapper isn't needed).</summary>
    private ExternalTools BooksTools()
    {
        if (!File.Exists(_settings.FfmpegPath)) throw new InvalidOperationException("ffmpeg.exe wasn't found. Pick it in Global Settings → General & tools → Tools.");
        return new ExternalTools { FfmpegPath = _settings.FfmpegPath, FaceFxWrapperPath = _settings.FaceFxWrapperPath ?? "", XwmaEncodePath = _settings.XwmaEncodePath, FonixDataPath = _settings.FonixDataPath };
    }

    private List<BookText.Issue> BkIssues(Book b)
    {
        int hash = HashCode.Combine(b.Spoken, b.InGame, b.Title);
        lock (_bkChecks)
            if (_bkChecks.TryGetValue(b.Stem + "\u0001" + b.Raw.Length, out var c) && c.Hash == hash) return c.Issues;
        var issues = BookText.Check(b.InGame, b.Spoken, b.Raw.Length > 0 ? b.Raw : null, b.Title);
        lock (_bkChecks) _bkChecks[b.Stem + "\u0001" + b.Raw.Length] = (hash, issues);
        return issues;
    }

    private async Task<object?> DispatchBooks(string m, JsonElement p, List<string> dropped)
    {
        switch (m)
        {
            case "books.sets":
                return BuiltInSets.List().Select(s => new { s.Id, s.Name, s.Version, s.Description, s.Credit, books = s.Books.Count, npc = s.Books.Count(b => b.Reader == "npc") }).ToList();
            case "books.list":
                return await Locked(() => _lib.LoadBooksProjects().Select(x => new
                {
                    x.Id, x.Name, x.UpdatedUtc, x.Source, books = x.Books.Count(b => !b.Excluded && !b.Gone), narrators = x.Narrators.Count,
                    lines = x.Lines.Count(l => !l.Excluded), generated = x.Lines.Count(l => !l.Excluded && l.SelectedTakeId != null),
                    targets = x.Plugins.Where(t => t.Target && !t.BaseGame).Select(t => t.FileName).ToList(), x.LastExportUtc,
                }).ToList());
            case "books.create":
                return await Locked(() =>
                {
                    var proj = new BooksProject { Name = S(p, "name", "Voiced books").Trim(), Source = S(p, "source", "plugins") };
                    proj.ModName = S(p, "modName", proj.Source == "plugins" ? proj.Name : "SKYRIM AND DLC").Trim();
                    if (proj.Source != "plugins")
                    {
                        var set = BuiltInSets.Load(proj.Source) ?? throw new InvalidOperationException("That built-in set isn't in this build.");
                        BuiltInSets.Apply(proj, set);
                        proj.IniName = "!VanillaBooks";
                        proj.ArchivePrefix = "VBOS";
                    }
                    proj.Narrators.Add(new BookNarrator { Name = S(p, "narrator", "Narrator").Trim() is { Length: > 0 } n ? n : "Narrator" });
                    BkService().ApplyBaseGame(proj);
                    BooksService.RebuildLines(proj);
                    _lib.Save(proj);
                    lock (_bk) _bk[proj.Id] = proj;
                    return new { id = proj.Id };
                });
            case "books.delete":
                return await Locked(() =>
                {
                    if (_jobCts != null) throw new InvalidOperationException("Another task is still running (see the bar at the top). Press Cancel there first.");
                    var id = S(p, "id");
                    _lib.Backup("before-delete-books", _settings.BackupsToKeep);
                    lock (_bk) _bk.Remove(id);
                    lock (_bkSessions) _bkSessions.Remove(id);
                    var dir = _lib.BooksFolder(id);
                    if (Directory.Exists(dir)) Directory.Delete(dir, true);
                    return true;
                });
            case "books.get": return await Locked(() => BkDto(LoadBk(S(p, "id"))));
            case "books.save": return await Locked(() => BkSave(p));
            case "books.addFiles":
                return await Locked(() =>
                {
                    var proj = LoadBk(S(p, "id"));
                    var paths = Strings(p, "paths");
                    if (B(p, "dropped")) paths.AddRange(dropped);
                    var added = new List<string>();
                    foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
                    {
                        var files = Directory.Exists(path) ? Directory.EnumerateFiles(path).ToList() : new List<string> { path };
                        foreach (var f in files.Where(f => Path.GetExtension(f).ToLowerInvariant() is ".esp" or ".esm" or ".esl"))
                        {
                            if (proj.Plugins.Any(x => x.Path.Equals(f, StringComparison.OrdinalIgnoreCase))) continue;
                            proj.Plugins.Add(new RevoicePlugin { Path = f, FileName = Path.GetFileName(f), Target = true });
                            added.Add(f);
                        }
                        foreach (var b in files.Where(f => f.EndsWith(".bsa", StringComparison.OrdinalIgnoreCase)))
                            if (!proj.Sources.Any(x => x.Path.Equals(b, StringComparison.OrdinalIgnoreCase))) { proj.Sources.Add(new RevoiceSource { Kind = "bsa", Path = b }); added.Add(b); }
                    }
                    if (proj.ModName.Trim().Length == 0 && proj.Plugins.FirstOrDefault(x => x.Target && !x.BaseGame) is { } t) proj.ModName = Path.GetFileNameWithoutExtension(t.FileName);
                    if (proj.IniName.Trim().Length == 0 && proj.Plugins.FirstOrDefault(x => x.Target && !x.BaseGame) is { } t2) proj.IniName = Path.GetFileNameWithoutExtension(t2.FileName);
                    _lib.Save(proj);
                    return new { added, project = BkDto(proj) };
                });
            case "books.plugin":
                return await Locked(() =>
                {
                    var proj = LoadBk(S(p, "id"));
                    var pl = proj.Plugins.FirstOrDefault(x => x.Path == S(p, "path")) ?? throw new InvalidOperationException("Plugin not found.");
                    if (B(p, "remove")) proj.Plugins.Remove(pl);
                    else if (NB(p, "target") is { } t) pl.Target = t;
                    _lib.Save(proj);
                    return BkDto(proj);
                });
            case "books.scan": return StartBkScan(S(p, "id"));
            case "books.book": return await Locked(() => BkBook(p));
            case "books.bookGet": return await Locked(() => BkBookDetail(LoadBk(S(p, "id")), S(p, "stem")));
            case "books.bulk": return await Locked(() => BkBulk(p));
            case "books.voice": return await Locked(() => BkVoice(p));
            case "books.line": return await Locked(() => BkLine(p));
            case "books.voiceTypes": return BkVoiceTypeList(LoadBk(S(p, "id")));
            case "books.estimate": return await Locked(() => BkEstimate(p));
            case "books.generate": return StartBkGenerate(p);
            case "books.export": return StartBkExport(p);
            case "books.join":
            {
                var proj = LoadBk(S(p, "id"));
                var b = proj.Books.FirstOrDefault(x => x.Stem == S(p, "stem")) ?? throw new InvalidOperationException("Book not found.");
                var voice = S(p, "voice");
                var folder = _lib.BooksFolder(proj.Id);
                var j = await BookJoiner.JoinAsync(proj, folder, voice, b, S(p, "variant"), BooksTools(), encode: false, CancellationToken.None)
                    ?? throw new InvalidOperationException("Every part needs a take first.");
                await Locked(() => { _lib.Save(proj); return null; });
                return new { url = "https://media.dbvo/" + Path.GetRelativePath(_lib.Root, Path.Combine(folder, j.Wav)).Replace('\\', '/') + "?t=" + DateTime.UtcNow.Ticks, j.Seconds, j.Lufs, j.PeakDb };
            }
            case "books.aiFormat":
            {
                var proj = LoadBk(S(p, "id"));
                var b = proj.Books.FirstOrDefault(x => x.Stem == S(p, "stem")) ?? throw new InvalidOperationException("Book not found.");
                var rt = Runtime();
                using var tagger = new AutoTagger(rt.AiProvider.Endpoint, rt.AiProvider.ApiKey, rt.AiProvider.Model);
                var text = S(p, "text", b.Spoken);
                var r = await tagger.ChatAsync(BookAi.SystemPrompt, BookAi.UserPrompt(b.Title, text), 0.3, CancellationToken.None);
                if (r.Error != null) throw new InvalidOperationException(r.Error);
                var suggestion = BookAi.Clean(r.Text!);
                return new { suggestion, issues = BookText.Check(b.InGame, suggestion, b.Raw.Length > 0 ? b.Raw : null, b.Title) };
            }
            case "books.openFolder":
            {
                var dir = _lib.BooksFolder(S(p, "id"));
                var sub = Path.Combine(dir, S(p, "which") == "joined" ? "joined" : "takes");
                OpenPath(Directory.Exists(sub) ? sub : dir);
                return true;
            }
            case "books.snap":
            {
                var proj = LoadBk(S(p, "id"));
                var b = proj.Books.FirstOrDefault(x => x.Stem == S(p, "stem")) ?? throw new InvalidOperationException("Book not found.");
                return BookSplitter.Boundaries(S(p, "text", b.Spoken)).Select(x => new { at = x.At, para = x.Paragraph }).ToList();
            }
            default: return NotHandled;
        }
    }

    // ------------------------------------------------------------------ dto

    private object BkDto(BooksProject proj)
    {
        var set = proj.Source != "plugins" ? BuiltInSets.Load(proj.Source) : null;
        var voiceStats = BkVoiceStats(proj);
        return new
        {
            proj.Id, proj.Name, proj.Source, proj.SetVersion, setVersion = set?.Version, setName = set?.Name, setCredit = set?.Credit,
            proj.ModName, proj.ArchivePrefix, proj.IniName, iniFile = BooksExporter.IniFileName(proj), proj.OutputFolder, proj.Packaging, proj.NpcVoices, proj.WriteIni,
            proj.PartTarget, proj.PartMin, proj.PartMax, proj.SendContext, proj.AutoSpokenRules, proj.UseBaseGame, proj.Language, audio = proj.Audio, voice = proj.Voice,
            ceiling = BooksService.Ceiling(proj),
            archives = BooksExporter.Groups(proj).Select(g => BooksExporter.ArchiveName(proj, g.Group)).ToList(),
            plugins = proj.Plugins.Select(x => new { x.Path, x.FileName, x.Target, x.BaseGame, exists = File.Exists(x.Path) }),
            sources = proj.Sources.Select(x => new { x.Id, x.Kind, x.Path, x.BaseGame, exists = File.Exists(x.Path) }),
            baseGameFolder = RevoiceService.DataFolder(_settings.BaseGameFolder),
            xwma = !string.IsNullOrWhiteSpace(_settings.XwmaEncodePath) && File.Exists(_settings.XwmaEncodePath),
            proj.LastScan, proj.LastExportUtc,
            narrators = proj.Narrators.Select(n => new { n.Id, n.Name, n.Engine, n.VoiceId, n.VoiceName, n.Female, n.Level, key = BooksProject.NarratorKey(n.Id), stats = voiceStats.GetValueOrDefault(BooksProject.NarratorKey(n.Id)) }),
            voiceTypes = BkVoiceTypeRows(proj, voiceStats),
            mediaBase = "https://media.dbvo/" + Path.GetRelativePath(_lib.Root, _lib.BooksFolder(proj.Id)).Replace('\\', '/') + "/",
            books = proj.Books.Select(b => BkBookDto(proj, b)).ToList(),
        };
    }

    private sealed record VoiceStat(int Books, int Done, int Lines, int Generated, int Characters, int CharactersLeft, int Stale);

    private static Dictionary<string, VoiceStat> BkVoiceStats(BooksProject proj)
    {
        var res = new Dictionary<string, VoiceStat>();
        foreach (var g in proj.Lines.Where(l => !l.Excluded).GroupBy(l => l.Voice))
        {
            var lines = g.ToList();
            var books = lines.GroupBy(l => l.Stem + "|" + l.Variant).ToList();
            res[g.Key] = new VoiceStat(books.Count, books.Count(b => b.All(l => l.SelectedTake != null && l.Status == Phase2Status.Generated)), lines.Count,
                lines.Count(l => l.SelectedTake != null), lines.Sum(l => l.Spoken.Length),
                lines.Where(l => l.SelectedTake == null || l.Status is Phase2Status.Stale or Phase2Status.Failed or Phase2Status.Pending).Sum(l => l.Spoken.Length),
                lines.Count(l => l.Status == Phase2Status.Stale));
        }
        return res;
    }

    private static bool IsFemaleVt(string vt) => vt.StartsWith("Female", StringComparison.OrdinalIgnoreCase) || vt.Contains("Female", StringComparison.Ordinal);

    private static List<object> BkVoiceTypeRows(BooksProject proj, Dictionary<string, VoiceStat> stats)
    {
        var used = proj.Books.Where(b => !b.Excluded && !b.Gone && b.Reader == "npc" && b.VoiceType.Length > 0).GroupBy(b => b.VoiceType, StringComparer.OrdinalIgnoreCase);
        var rows = new List<object>();
        foreach (var g in used.OrderBy(g => g.Key))
        {
            proj.VoiceTypes.TryGetValue(g.Key, out var a);
            var key = BooksProject.VoiceTypeKey(g.Key);
            rows.Add(new
            {
                voiceType = g.Key, key, books = g.Count(), engine = a?.Engine ?? "elevenlabs", voiceId = a?.VoiceId, voiceName = a?.VoiceName,
                female = a?.Female ?? IsFemaleVt(g.Key), level = a?.Level ?? "loud", useNarrator = a?.UseNarrator ?? false,
                titles = g.Select(b => b.Title).Take(6).ToList(), stats = stats.GetValueOrDefault(key),
            });
        }
        return rows;
    }

    private object BkBookDto(BooksProject proj, Book b)
    {
        var voices = BooksService.VoicesFor(proj, b);
        var issues = b.Excluded ? new List<BookText.Issue>() : BkIssues(b);
        var lines = proj.Lines.Where(l => l.Stem == b.Stem && !l.Excluded).ToList();
        return new
        {
            stem = b.Stem, title = b.Title, kind = b.Kind, reader = b.Reader, vt = b.VoiceType, vtCert = b.VoiceTypeCertainty.ToString(), cands = b.VoiceTypeCandidates,
            via = b.VoiceTypeVia, author = b.Author, authorSrc = b.AuthorSource, authorCert = b.AuthorCertainty.ToString(), npcName = b.AuthorNpcName, confirmed = b.AuthorConfirmed,
            excl = b.Excluded, exReason = b.ExcludeReason, suggest = b.SuggestInclude, gone = b.Gone, marked = b.Marked, updated = b.UpdatedText != null,
            chars = b.Spoken.Length, edited = b.SpokenEdited, records = b.Records.Count, edid = b.First?.EditorId ?? "", plugin = b.First?.Plugin ?? "",
            variants = b.Variants.Count, parts = lines.Count(l => l.Voice == voices.FirstOrDefault() && l.Variant.Length == 0),
            voices = voices.Select(v => new { v, status = BooksService.StatusOf(BooksService.LinesOf(proj, v, b.Stem)) }).ToList(),
            serious = issues.Count(i => i.Serious), notes = issues.Count(i => !i.Serious),
            hasText = b.InGame.Length > 0,
        };
    }

    private object BkBookDetail(BooksProject proj, string stem)
    {
        var b = proj.Books.FirstOrDefault(x => x.Stem == stem) ?? throw new InvalidOperationException("Book not found.");
        var voices = BooksService.VoicesFor(proj, b);
        var parts = BooksService.PartsOf(proj, b);
        return new
        {
            book = BkBookDto(proj, b), inGame = b.InGame, spoken = b.Spoken, updatedText = b.UpdatedText, raw = b.Raw.Length > 0,
            records = b.Records.Select(r => new { r.Plugin, formId = r.Hex, r.EditorId }),
            breaks = parts.Skip(1).Select(x => x.Start).ToList(), manual = b.ManualBreaks,
            partRanges = parts.Select(x => new { start = x.Start, end = x.End, para = x.ParagraphEnd }).ToList(),
            issues = BkIssues(b).Select(i => new { i.Kind, i.Message, i.Serious }),
            variantsList = b.Variants.Select(v => new { v.Id, v.Plugin, v.Label, v.WhenInstalled, v.Spoken }),
            voices = voices.Select(v => new
            {
                key = v, name = BooksService.VoiceName(proj, v), hasVoice = BooksService.VoicePackFor(proj, v) != null,
                lines = BooksService.LinesOf(proj, v, b.Stem).Select(BkLineDto).ToList(),
                variants = b.Variants.Select(x => new { id = x.Id, lines = BooksService.LinesOf(proj, v, b.Stem, x.Id).Select(BkLineDto).ToList() }).ToList(),
                join = proj.Joins.TryGetValue(BookJoiner.JoinKey(v, b.Stem, ""), out var j) ? new { j.Seconds, j.Lufs, j.PeakDb, j.BuiltUtc } : null,
            }).ToList(),
            npcName = b.AuthorNpcName, author = b.Author,
        };
    }

    private static object BkLineDto(BookLine l) => new
    {
        id = l.LineId, part = l.Part, voice = l.Voice, variant = l.Variant, spoken = l.Spoken, start = l.Start, end = l.End, para = l.ParagraphEnd,
        status = l.Status.ToString(), err = l.LastError, sel = l.SelectedTakeId, marked = l.Marked,
        takes = l.Takes.Select(t => new { t.Id, t.Slot, t.CreatedUtc, t.SpokenText, engine = t.Settings.Engine, model = t.Settings.ModelId, wav = t.WavPath.Replace('\\', '/'), t.DurationSeconds }),
    };

    // ------------------------------------------------------------------ edits

    private object BkSave(JsonElement p)
    {
        var proj = LoadBk(S(p, "id"));
        bool relines = false;
        proj.Name = S(p, "name", proj.Name);
        proj.ModName = S(p, "modName", proj.ModName);
        proj.ArchivePrefix = S(p, "archivePrefix", proj.ArchivePrefix);
        proj.IniName = S(p, "iniName", proj.IniName);
        proj.OutputFolder = S(p, "outputFolder", proj.OutputFolder);
        proj.Packaging = S(p, "packaging", proj.Packaging);
        proj.Language = S(p, "language", proj.Language);
        if (NB(p, "npcVoices") is { } nv && nv != proj.NpcVoices) { proj.NpcVoices = nv; relines = true; }
        if (NB(p, "writeIni") is { } wi) proj.WriteIni = wi;
        if (NB(p, "sendContext") is { } sc) proj.SendContext = sc;
        if (NB(p, "autoSpokenRules") is { } ar) proj.AutoSpokenRules = ar;
        if (NB(p, "useBaseGame") is { } bg && bg != proj.UseBaseGame) { proj.UseBaseGame = bg; BkService().ApplyBaseGame(proj); }
        if (B(p, "refreshBaseGame")) BkService().ApplyBaseGame(proj);
        foreach (var (k, set) in new (string, Action<int>)[] { ("partTarget", v => proj.PartTarget = v), ("partMax", v => proj.PartMax = v), ("partMin", v => proj.PartMin = v) })
            if (p.TryGetProperty(k, out var n) && n.ValueKind == JsonValueKind.Number) { set(Math.Clamp(n.GetInt32(), 200, 40000)); relines = true; }
        if (proj.PartTarget > proj.PartMax) proj.PartTarget = proj.PartMax;
        if (p.TryGetProperty("audio", out var au) && au.ValueKind == JsonValueKind.Object) proj.Audio = au.Deserialize<BookAudioSettings>(JsonOptions) ?? proj.Audio;
        if (p.TryGetProperty("voice", out var vs) && vs.ValueKind == JsonValueKind.Object)
        {
            var v = vs.Deserialize<VoicePack>(JsonOptions)!;
            v.Name = "Voiced books"; v.Naming = NamingMode.Legacy;
            if (v.Eleven.ModelId != proj.Voice.Eleven.ModelId || v.Engine != proj.Voice.Engine) relines = true; // the longest part depends on the model
            proj.Voice = v;
        }
        if (relines) BooksService.RebuildLines(proj);
        _lib.Save(proj);
        return BkDto(proj);
    }

    private object BkBook(JsonElement p)
    {
        var proj = LoadBk(S(p, "id"));
        var b = proj.Books.FirstOrDefault(x => x.Stem == S(p, "stem")) ?? throw new InvalidOperationException("Book not found.");
        BkApply(proj, b, S(p, "action"), p);
        BooksService.RebuildLines(proj);
        _lib.Save(proj);
        return BkBookDetail(proj, b.Stem);
    }

    private static void BkApply(BooksProject proj, Book b, string action, JsonElement p)
    {
        switch (action)
        {
            case "spoken":
            {
                var t = S(p, "value");
                if (t == b.Spoken) break;
                b.Spoken = t;
                b.SpokenEdited = true;
                // Stored breaks that no longer fall inside the text are dropped; the rest stay where they were.
                if (b.ManualBreaks) b.Breaks = b.Breaks.Where(x => x > 0 && x < t.Length).ToList();
                break;
            }
            case "variantSpoken":
            {
                var v = b.Variants.FirstOrDefault(x => x.Id == S(p, "variant")) ?? throw new InvalidOperationException("Variant not found.");
                v.Spoken = S(p, "value");
                break;
            }
            case "restore": b.Spoken = BookText.DefaultSpoken(b.Title, b.InGame, out _, out _); b.SpokenEdited = false; b.ManualBreaks = false; b.Breaks.Clear(); break;
            case "applyRules": b.Spoken = BookText.ApplyRules(b.Spoken); b.SpokenEdited = true; break;
            case "useUpdated":
                if (b.UpdatedText != null) { b.Spoken = b.UpdatedText; b.SetVersion = b.UpdatedVersion; b.UpdatedText = null; b.SpokenEdited = false; b.ManualBreaks = false; b.Breaks.Clear(); }
                break;
            case "keepMine": b.UpdatedText = null; b.SetVersion = Math.Max(b.SetVersion, b.UpdatedVersion); break;
            case "breaks":
            {
                var list = p.TryGetProperty("value", out var arr) && arr.ValueKind == JsonValueKind.Array ? arr.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Number).Select(x => x.GetInt32()).ToList() : new List<int>();
                b.Breaks = list.Where(x => x > 0 && x < b.Spoken.Length).Distinct().OrderBy(x => x).ToList();
                b.ManualBreaks = true;
                break;
            }
            case "autoBreaks": b.ManualBreaks = false; b.Breaks.Clear(); break;
            case "reader":
            {
                var r = S(p, "value");
                b.Reader = r == "npc" ? "npc" : "narrator";
                if (S(p, "voiceType") is { Length: > 0 } vt) { b.VoiceType = vt; b.VoiceTypeCertainty = Certainty.Confirmed; b.VoiceTypeVia = "chosen by you"; }
                b.AuthorConfirmed = true;
                b.AuthorSource = b.AuthorSource.Length == 0 ? "user" : b.AuthorSource;
                break;
            }
            case "confirm": b.AuthorConfirmed = true; if (b.VoiceType.Length > 0) b.VoiceTypeCertainty = Certainty.Confirmed; if (b.Author.Length > 0) b.AuthorCertainty = Certainty.Confirmed; break;
            case "unconfirm": b.AuthorConfirmed = false; break;
            case "author": b.Author = S(p, "value").Trim(); b.AuthorSource = "user"; b.AuthorCertainty = Certainty.Confirmed; break;
            case "exclude": b.Excluded = true; if (b.ExcludeReason.Length == 0) b.ExcludeReason = "user"; break;
            case "include": b.Excluded = false; if (b.ExcludeReason == "user") b.ExcludeReason = ""; break;
            case "mark": b.Marked = !b.Marked; break;
            case "markOn": b.Marked = true; break;
            case "markOff": b.Marked = false; break;
            case "notes": b.Notes = S(p, "value"); break;
            default: throw new InvalidOperationException("Unknown book action: " + action);
        }
    }

    private object BkBulk(JsonElement p)
    {
        var proj = LoadBk(S(p, "id"));
        var stems = Strings(p, "stems").ToHashSet();
        var action = S(p, "action");
        int n = 0;
        foreach (var b in proj.Books.Where(b => stems.Contains(b.Stem)))
        {
            if (action == "narrator") { b.Reader = "narrator"; b.AuthorConfirmed = true; n++; continue; }
            if (action == "npc") { if (b.VoiceType.Length == 0) continue; b.Reader = "npc"; b.AuthorConfirmed = true; n++; continue; }
            if (action == "voiceType") { b.VoiceType = S(p, "value"); b.Reader = "npc"; b.AuthorConfirmed = true; b.VoiceTypeCertainty = Certainty.Confirmed; b.VoiceTypeVia = "chosen by you"; n++; continue; }
            if (action == "restore" && !b.SpokenEdited) continue;
            if (action == "useUpdated" && b.UpdatedText == null) continue;
            BkApply(proj, b, action, p);
            n++;
        }
        BooksService.RebuildLines(proj);
        _lib.Save(proj);
        return new { changed = n, project = BkDto(proj) };
    }

    private object BkVoice(JsonElement p)
    {
        var proj = LoadBk(S(p, "id"));
        var kind = S(p, "kind");
        if (kind == "narrator")
        {
            var nid = S(p, "narratorId");
            if (B(p, "add")) proj.Narrators.Add(new BookNarrator { Name = S(p, "name", $"Narrator {proj.Narrators.Count + 1}"), Female = B(p, "female") });
            else
            {
                var n = proj.Narrators.FirstOrDefault(x => x.Id == nid) ?? throw new InvalidOperationException("Narrator not found.");
                if (B(p, "remove"))
                {
                    if (proj.Lines.Any(l => l.Voice == BooksProject.NarratorKey(n.Id) && l.Takes.Count > 0) && !B(p, "force"))
                        throw new InvalidOperationException("This narrator already has takes. Remove it anyway from the confirmation, or keep it.");
                    proj.Narrators.Remove(n);
                }
                else
                {
                    if (S(p, "name") is { Length: > 0 } name) n.Name = name.Trim();
                    if (p.TryGetProperty("engine", out var en) && en.ValueKind == JsonValueKind.String) { n.Engine = en.GetString()!; n.VoiceId = null; n.VoiceName = null; }
                    if (p.TryGetProperty("voiceId", out var vi)) { n.VoiceId = vi.ValueKind == JsonValueKind.String && vi.GetString()!.Length > 0 ? vi.GetString() : null; n.VoiceName = n.VoiceId == null ? null : S(p, "voiceName", n.VoiceId); }
                    if (NB(p, "female") is { } f) n.Female = f;
                    if (S(p, "level") is { Length: > 0 } lv) n.Level = lv;
                }
            }
        }
        else
        {
            foreach (var vt in Strings(p, "voiceTypes").DefaultIfEmpty(S(p, "voiceType")).Where(x => x.Length > 0))
            {
                if (!proj.VoiceTypes.TryGetValue(vt, out var a)) proj.VoiceTypes[vt] = a = new VoiceTypeVoice { VoiceType = vt, Female = IsFemaleVt(vt) };
                if (p.TryGetProperty("engine", out var en) && en.ValueKind == JsonValueKind.String) { a.Engine = en.GetString()!; a.VoiceId = null; a.VoiceName = null; }
                if (p.TryGetProperty("voiceId", out var vi)) { a.VoiceId = vi.ValueKind == JsonValueKind.String && vi.GetString()!.Length > 0 ? vi.GetString() : null; a.VoiceName = a.VoiceId == null ? null : S(p, "voiceName", a.VoiceId); }
                if (NB(p, "female") is { } f) a.Female = f;
                if (NB(p, "useNarrator") is { } un) a.UseNarrator = un;
                if (S(p, "level") is { Length: > 0 } lv) a.Level = lv;
            }
        }
        BooksService.RebuildLines(proj);
        _lib.Save(proj);
        return BkDto(proj);
    }

    private object BkLine(JsonElement p)
    {
        var proj = LoadBk(S(p, "id"));
        var l = proj.Lines.FirstOrDefault(x => x.LineId == S(p, "lineId")) ?? throw new InvalidOperationException("Part not found.");
        var folder = _lib.BooksFolder(proj.Id);
        switch (S(p, "action"))
        {
            case "selectTake":
                l.SelectedTakeId = S(p, "value");
                l.Status = Phase2Status.Generated;
                l.RefreshStale();
                break;
            case "deleteTake":
            {
                var t = l.Takes.FirstOrDefault(x => x.Id == S(p, "value")) ?? throw new InvalidOperationException("Take not found.");
                var dir = Path.GetDirectoryName(Path.Combine(folder, t.WavPath));
                try { if (dir != null && Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
                l.Takes.Remove(t);
                if (l.SelectedTakeId == t.Id) l.SelectedTakeId = l.Takes.LastOrDefault()?.Id;
                l.Status = l.SelectedTakeId == null ? Phase2Status.Pending : Phase2Status.Generated;
                l.RefreshStale();
                break;
            }
            case "mark": l.Marked = !l.Marked; break;
        }
        _lib.Save(proj);
        return BkBookDetail(proj, l.Stem);
    }

    private object BkVoiceTypeList(BooksProject proj)
    {
        BookSession? s;
        lock (_bkSessions) s = _bkSessions.TryGetValue(proj.Id, out var x) && x.Signature == BooksService.SessionSignature(proj) ? x : null;
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        if (s != null) foreach (var v in s.Index.VoiceTypes.Values.Where(v => BookAuthors.IsVanillaPlugin(v.Key.Plugin))) names.Add(v.EditorId);
        else foreach (var v in VoiceTypeTemplates.Shared.VoiceTypes) names.Add(v);
        foreach (var b in proj.Books) { if (b.VoiceType.Length > 0) names.Add(b.VoiceType); foreach (var c in b.VoiceTypeCandidates) names.Add(c); }
        return names.Where(n => !n.StartsWith("Cr", StringComparison.Ordinal)).ToList();
    }

    // ------------------------------------------------------------------ generate

    private List<BookLine> BkSelect(BooksProject proj, JsonElement p)
    {
        var mode = S(p, "mode", "missing");
        var stems = Strings(p, "stems").ToHashSet();
        var ids = Strings(p, "lineIds").ToHashSet();
        var voice = S(p, "voice");
        var marked = proj.Books.Where(b => b.Marked).Select(b => b.Stem).ToHashSet();
        var problems = proj.Books.Where(b => !b.Excluded && BkIssues(b).Any(i => i.Serious)).Select(b => b.Stem).ToHashSet();
        bool Usable(BookLine l) => !l.Excluded && (voice.Length == 0 || l.Voice == voice);
        bool Missing(BookLine l) => l.SelectedTake == null || l.Status is Phase2Status.Pending or Phase2Status.Failed or Phase2Status.Stale;
        return mode switch
        {
            "lines" => proj.Lines.Where(l => ids.Contains(l.LineId) && !l.Excluded).ToList(),
            "ticked" => proj.Lines.Where(l => stems.Contains(l.Stem) && Usable(l) && (B(p, "regenerate") || Missing(l))).ToList(),
            "flagged" => proj.Lines.Where(l => marked.Contains(l.Stem) && Usable(l) && (B(p, "regenerate") || Missing(l))).ToList(),
            "problems" => proj.Lines.Where(l => problems.Contains(l.Stem) && Usable(l) && Missing(l)).ToList(),
            "all" => proj.Lines.Where(Usable).ToList(),
            _ => proj.Lines.Where(l => Usable(l) && Missing(l)).ToList(),
        };
    }

    private object BkEstimate(JsonElement p)
    {
        var proj = LoadBk(S(p, "id"));
        var lines = BkSelect(proj, p);
        var groups = lines.GroupBy(l => l.Voice).Select(g =>
        {
            var v = BooksService.VoicePackFor(proj, g.Key);
            return new
            {
                voice = g.Key, name = BooksService.VoiceName(proj, g.Key), voicePack = v?.Name ?? "(no voice yet)", engine = v?.Engine ?? "", hasVoice = v != null,
                lines = g.Count(), books = g.Select(l => l.Stem).Distinct().Count(), characters = g.Sum(l => l.Spoken.Length),
                settings = v != null ? SettingsRows(v, false) : new List<string[]>(),
            };
        }).OrderByDescending(g => g.hasVoice).ToList();
        var stems = lines.Select(l => l.Stem).ToHashSet();
        var books = proj.Books.Where(b => stems.Contains(b.Stem)).ToList();
        var ok = groups.Where(g => g.hasVoice).ToList();
        return new
        {
            count = ok.Sum(g => g.lines), books = ok.Sum(g => g.books), characters = ok.Sum(g => g.characters), groups,
            unassigned = groups.Where(g => !g.hasVoice).Select(g => g.name).ToList(),
            unconfirmed = books.Count(b => b.Reader == "npc" && !b.AuthorConfirmed),
            problems = books.Count(b => BkIssues(b).Any(i => i.Serious)),
            context = proj.SendContext && !proj.Voice.Eleven.ModelId.Contains("v3", StringComparison.OrdinalIgnoreCase) && proj.Voice.Engine == "elevenlabs",
        };
    }

    private object StartBkGenerate(JsonElement p)
    {
        if (_jobCts != null) throw new InvalidOperationException("Another task is still running (see the bar at the top). Wait for it to finish, or press Cancel there.");
        var id = S(p, "id");
        var proj = LoadBk(id);
        var all = BkSelect(proj, p);
        var groups = all.GroupBy(l => l.Voice).Select(g => (Voice: BooksService.VoicePackFor(proj, g.Key), Key: g.Key, Lines: g.ToList())).Where(g => g.Voice != null).ToList();
        if (groups.Count == 0) throw new InvalidOperationException(all.Count == 0 ? "Nothing to generate." : "Pick a voice for the narrator or voice types first (Voices tab).");
        var tools = BooksTools();
        _jobCts = new CancellationTokenSource();
        var ct = _jobCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                var svc = new Phase2Service(_lib, Runtime());
                int total = groups.Sum(g => g.Lines.Count), offset = 0;
                Emit("progress", new { kind = "generate", done = 0, total, failed = 0, current = (string?)null, message = (string?)null });
                if (total > 5) _lib.Backup("before-books-" + proj.Name, _settings.BackupsToKeep, routine: true);
                var folder = _lib.BooksFolder(proj.Id);
                foreach (var g in groups)
                {
                    var shim = new DbvoPatch { Id = proj.Id, Name = proj.Name, VoicePackId = g.Voice!.Id, Kind = "tts" };
                    int baseDone = offset;
                    var progress = new DirectProgress<GenerationProgress>(x => Emit("progress", new { kind = "generate", done = baseDone + x.Done, total, x.Failed, x.Current, message = (string?)null }));
                    await svc.GenerateAsync(shim, g.Voice, g.Lines.Cast<Phase2Line>().ToList(), tools, progress,
                        l => { Emit("booksLine", new { projectId = proj.Id, stem = ((BookLine)l).Stem, line = BkLineDto((BookLine)l) }); return Task.CompletedTask; },
                        ct, _gate, null, folder, () => _lib.Save(proj),
                        makeLip: false,
                        takeFolder: l => { var bl = (BookLine)l; return Path.Combine("takes", BookJoiner.Safe(bl.Voice), bl.Stem + (bl.Variant.Length > 0 ? "@" + bl.Variant : ""), $"p{bl.Part:00}"); },
                        context: l => BooksService.ContextFor(proj, (BookLine)l));
                    offset += g.Lines.Count;
                }
                Emit("jobDone", new { kind = "generate", booksId = proj.Id, ok = true });
            }
            catch (OperationCanceledException) { Emit("jobDone", new { kind = "generate", booksId = id, ok = false, message = "Cancelled." }); }
            catch (Exception ex)
            {
                App.Log("Books generate failed: " + ex);
                Emit("jobDone", new { kind = "generate", booksId = id, ok = false, message = FriendlyError.From(ex) });
            }
            finally { _jobCts = null; }
        });
        return true;
    }

    private object StartBkScan(string id)
    {
        if (_jobCts != null) throw new InvalidOperationException("Another task is still running (see the bar at the top). Wait for it to finish, or press Cancel there.");
        _jobCts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            try
            {
                var proj = LoadBk(id);
                Emit("progress", new { kind = "revoice", done = 0, total = 1, failed = 0, message = "Reading plugins" });
                var prog = new DirectProgress<string>(msg => Emit("progress", new { kind = "revoice", done = 0, total = 1, failed = 0, message = msg }));
                if (proj.Source != "plugins" && !proj.Plugins.Any(x => File.Exists(x.Path)))
                    throw new InvalidOperationException("Set the base game folder (Global Settings → General) to see the game's own text next to the spoken text. The built-in texts work without it.");
                var s = await Task.Run(() => BkSession(proj, prog));
                BooksScanReport rep;
                await _gate.WaitAsync();
                try
                {
                    if (proj.Source == "plugins") rep = BkService().Scan(proj, s);
                    else
                    {
                        var set = BuiltInSets.Load(proj.Source) ?? throw new InvalidOperationException("That built-in set isn't in this build.");
                        BuiltInSets.Apply(proj, set);
                        BuiltInSets.AttachGameText(proj, s);
                        // Set books without a text of their own (in the .ini but never voiced) start from the game's text.
                        foreach (var b in proj.Books.Where(b => b.Spoken.Trim().Length == 0 && b.InGame.Length > 0)) b.Spoken = BookText.DefaultSpoken(b.Title, b.InGame, out _, out _);
                        // Books by NPCs without a voice type yet get suggestions from the plugins.
                        BooksService.Attribute(proj, s, proj.Books.Where(b => b.Reader == "npc" && b.VoiceType.Length == 0 && !b.AuthorConfirmed));
                        foreach (var b in proj.Books.Where(b => b.AuthorSource != "set" && b.VoiceType.Length == 0 && set.Books.Any(x => x.Stem == b.Stem && x.Reader == "npc"))) b.Reader = "npc";
                        rep = new BooksScanReport { Books = proj.Books.Count, Records = proj.Books.Sum(b => b.Records.Count), Npc = proj.Books.Count(b => b.Reader == "npc"), Excluded = proj.Books.Count(b => b.Excluded) };
                        proj.LastScan = rep;
                    }
                    BooksService.RebuildLines(proj);
                    _lib.Save(proj);
                }
                finally { _gate.Release(); }
                Emit("jobDone", new { kind = "books-scan", id, ok = true, report = rep });
            }
            catch (Exception ex)
            {
                App.Log("Books scan failed: " + ex);
                Emit("jobDone", new { kind = "books-scan", id, ok = false, message = FriendlyError.From(ex) });
            }
            finally { _jobCts = null; }
        });
        return true;
    }

    private object StartBkExport(JsonElement p)
    {
        if (_jobCts != null) throw new InvalidOperationException("Another task is still running (see the bar at the top). Wait for it to finish, or press Cancel there.");
        var id = S(p, "id");
        var proj = LoadBk(id);
        if (string.IsNullOrWhiteSpace(_settings.XwmaEncodePath) || !File.Exists(_settings.XwmaEncodePath))
            throw new InvalidOperationException("Exporting books needs xWMAEncode.exe, and it isn't set. Pick it in " + FriendlyError.ToolsPlace + " (it comes with the Creation Kit or Yakitori Audio Converter).");
        var tools = BooksTools();
        bool fomod = !p.TryGetProperty("fomod", out var fm) || fm.ValueKind != JsonValueKind.False;
        _jobCts = new CancellationTokenSource();
        var ct = _jobCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                var progress = new DirectProgress<GenerationProgress>(x => Emit("progress", new { kind = "fomod", x.Done, x.Total, x.Failed, x.Current, message = $"Joining and encoding books {x.Done}/{x.Total}" }));
                // Not under the page lock: an export takes minutes and the page stays usable meanwhile.
                var rep = await BooksExporter.ExportAsync(proj, _lib.BooksFolder(proj.Id), tools, fomod, progress, ct);
                await _gate.WaitAsync();
                try { _lib.Save(proj); } finally { _gate.Release(); }
                Emit("jobDone", new { kind = "books-export", id, ok = rep.Failed == 0, report = rep, folder = proj.OutputFolder });
            }
            catch (OperationCanceledException) { Emit("jobDone", new { kind = "books-export", id, ok = false, message = "Cancelled." }); }
            catch (Exception ex)
            {
                App.Log("Books export failed: " + ex);
                Emit("jobDone", new { kind = "books-export", id, ok = false, message = FriendlyError.From(ex) });
            }
            finally { _jobCts = null; }
        });
        return true;
    }
}
