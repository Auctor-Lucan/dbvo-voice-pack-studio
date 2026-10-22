using DbvoStudio.Core.Audio;
using DbvoStudio.Core.Books;
using DbvoStudio.Core.Projects;
using DbvoStudio.Core.Revoice;
using DbvoStudio.Core.Tools;
using Xunit;

namespace DbvoStudio.Core.Tests;

public class BookTextTests
{
    [Fact]
    public void Clean_keeps_aliases_turns_drop_caps_into_letters_and_drops_markup()
    {
        var raw = "<p align=\"center\"><font size='40'>THE TITLE</font></p>[pagebreak]<img src='img://Textures/Interface/Books/Illuminated_Letters/T_letter.png' width=56>he <Alias=Player> was here.<br>New line<Cool graphic>.";
        var t = BookText.Clean(raw);
        Assert.Contains("The <Alias=Player> was here.", t);
        Assert.DoesNotContain("pagebreak", t);
        Assert.DoesNotContain("Cool graphic", t);
        Assert.DoesNotContain("<p", t);
        Assert.Contains("THE TITLE\n\nThe", t);
    }

    [Fact]
    public void Default_spoken_text_has_the_ivbos_structure()
    {
        var s = BookText.DefaultSpoken("A Brief Tale", "A Brief Tale\nby Mymophonus\n\nIt was a dark night.", out var author, out var sender);
        Assert.Equal("Mymophonus", author);
        Assert.Null(sender);
        Assert.StartsWith("A Brief Tale ---- By Mymophonus -----\nIt was a dark night.", s);

        var letter = BookText.DefaultSpoken("Note", "Meet me at the docks.\n\n-Vals Veran", out _, out var from);
        Assert.Equal("Vals Veran", from);
        Assert.EndsWith("----- Vals Veran", letter); // v1.6: no automatic "By" before a signature
        Assert.DoesNotContain("By Vals", letter);
    }

    [Theory]
    [InlineData("The Lusty Argonian Maid, Vol. II", "The Lusty Argonian Maid, Volume Two")]
    [InlineData("Part III of the story", "Part Three of the story")]
    [InlineData("Uriel Septim VII ruled.", "Uriel Septim the Seventh ruled.")]
    [InlineData("Titus Mede II signed it.", "Titus Mede the Second signed it.")]
    [InlineData("Then I went home.", "Then I went home.")]
    [InlineData("Treasure Map V", "Treasure Map V")]
    public void Spoken_rules_for_numbers(string input, string expected) => Assert.Equal(expected, BookText.ApplyRules(input));

    [Fact]
    public void Dates_are_spoken_and_set_apart()
    {
        var s = BookText.Dates("Written 6 Morning Star 4E 297 at dawn.");
        Assert.Contains("----- Sixth of Morning Star, Fourth Era, Year Two-hundred-Ninety-Seven -----", s);
        Assert.Contains("Twenty-Fifth of Rain's Hand", BookText.Dates("The 25th of Rain's Hand"));
        Assert.Contains("Second Era, Year Five-hundred-Eighty-Two", BookText.Dates("In 2E 582 it fell."));
    }

    [Fact]
    public void Asides_become_dashes_without_a_dash_before_the_full_stop()
    {
        Assert.Equal("He was a good man - for the most part.", BookText.Asides("He was a good man (for the most part)."));
        Assert.Equal("He was - mostly - a good man.", BookText.Asides("He was (mostly) a good man."));
        Assert.Equal("[whispers] Quiet.", BookText.Asides("[whispers] Quiet."));
    }

    [Fact]
    public void Checks_flag_aliases_markup_short_caps_and_word_changes()
    {
        var game = "Hello <Alias=Player>, the wolf is near.";
        var issues = BookText.Check(game, "Hello friend, the WOLF is NEAR... and THE end <b>.");
        Assert.Contains(issues, i => i.Kind == "alias" && i.Serious);
        Assert.Contains(issues, i => i.Kind == "markup" && i.Serious);
        Assert.Contains(issues, i => i.Kind == "caps" && i.Message.Contains("THE"));
        Assert.Contains(issues, i => i.Kind == "words");
        // The editor selects where a problem is.
        Assert.Equal("<b>", issues.First(i => i.Kind == "markup").Find);
        Assert.Equal("THE", issues.First(i => i.Kind == "caps").Find);
        // Emphasis of long words and pacing are fine.
        Assert.DoesNotContain(BookText.Check(game, "Hello <Alias=Player>... the WOLF is near."), i => i.Serious);
    }

    [Fact]
    public void Unreadable_script_is_detected()
    {
        Assert.True(BookText.UnreadableShare("<font face='$FalmerFont'>THE BETRAYED AND WHEN THE SNOW PRINCE FELL</font>") > 0.9);
        Assert.Equal(0, BookText.UnreadableShare("<font face='$HandwrittenFont'>Meet me.</font>"));
    }
}

public class BookSplitterTests
{
    private static string Para(int sentences, string word = "Lorem") => string.Join(" ", Enumerable.Range(0, sentences).Select(i => $"{word} ipsum dolor sit amet number {i} consectetur adipiscing."));

    [Fact]
    public void Short_text_is_one_part()
    {
        var p = BookSplitter.Split("Short text.", 2000, 2500);
        Assert.Single(p);
    }

    [Fact]
    public void Long_text_breaks_at_paragraphs_and_sentence_ends_only()
    {
        var text = string.Join("\n\n", Enumerable.Range(0, 12).Select(i => Para(8)));
        var parts = BookSplitter.Split(text, 1500, 2000, 5000);
        Assert.True(parts.Count >= 3);
        Assert.All(parts, x => Assert.True(x.Length <= 2000, $"part of {x.Length}"));
        Assert.Equal(text.Length, parts.Sum(x => x.Length));
        foreach (var x in parts.Take(parts.Count - 1))
        {
            var before = text[..x.End].TrimEnd();
            Assert.EndsWith(".", before); // ends a sentence
        }
        Assert.True(parts.Count(x => x.ParagraphEnd) >= parts.Count - 1);
    }

    [Fact]
    public void Abbreviations_and_initials_do_not_end_a_sentence()
    {
        var text = "See Vol. II for more. Mr. Smith met Dr. Jones in St. Olaf. J. Doe agreed. The end came.";
        var at = BookSplitter.Boundaries(text).Select(b => text[..b.At].TrimEnd()).ToList();
        Assert.DoesNotContain(at, s => s.EndsWith("Vol.") || s.EndsWith("Mr.") || s.EndsWith("Dr.") || s.EndsWith("St.") || s.EndsWith(" J."));
        Assert.Contains(at, s => s.EndsWith("for more."));
    }

    [Fact]
    public void A_sentence_longer_than_the_engine_allows_is_split_at_a_comma()
    {
        var text = string.Join(", ", Enumerable.Repeat("and the long clause goes on", 60)) + ".";
        var parts = BookSplitter.Split(text, 400, 500, 600);
        Assert.All(parts, x => Assert.True(x.Length <= 600));
        Assert.Equal(text.Length, parts.Sum(x => x.Length));
    }
}

public class AudioChainTests
{
    private static float[] Sine(int n, double amp = 0.3) => Enumerable.Range(0, n).Select(i => (float)(amp * Math.Sin(2 * Math.PI * 440 * i / 44100.0))).ToArray();

    [Fact]
    public void Reverb_rings_on_after_the_input()
    {
        var x = Sine(44100);
        var y = new SoxReverb(44100, new BookAudioSettings()).Process(x, 44100, 0);
        Assert.Equal(88200, y.Length);
        double tail = y.Skip(44100 + 4410).Take(8820).Sum(v => (double)v * v);
        Assert.True(tail > 1e-3, "reverb tail");
    }

    [Fact]
    public void Loudness_and_limiter_hit_their_targets()
    {
        var x = Sine(44100 * 3, 0.5);
        double l = Loudness.IntegratedLufs(x, 44100);
        Assert.InRange(l, -10, -5); // a 440 Hz sine at -6 dBFS is about -9 LUFS... -6
        var g = (float)Math.Pow(10, (-12 - l) / 20);
        for (int i = 0; i < x.Length; i++) x[i] *= g * 4; // push well over the threshold
        PeakLimiter.Apply(x, 44100, -3, -1, 5, 50);
        Assert.True(x.Max(Math.Abs) <= Math.Pow(10, -1 / 20.0) + 1e-3);
    }

    [Fact]
    public void Joined_book_has_the_parts_pauses_and_end_silence()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vb-join-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(dir);
        try
        {
            string W(string name, int n)
            {
                var w = new WavFile { SampleRate = 44100, Channels = 1, Samples = Sine(n).Select(v => (short)(v * 32767)).ToArray() };
                var p = Path.Combine(dir, name); w.Write(p); return p;
            }
            var s = new BookAudioSettings { ParagraphPauseMs = 300, SentencePauseMs = 100, EndSilenceMs = 1000 };
            var y = BookJoiner.Process(new[] { (W("a.wav", 44100), true), (W("b.wav", 22050), false) }, s, out var lufs, out var peak);
            Assert.Equal(44100 + 13230 + 22050 + 44100, y.Length);
            Assert.InRange(lufs, -14, -8);
            Assert.True(peak <= -0.9);
        }
        finally { Directory.Delete(dir, true); }
    }
}

public class BooksServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vb-" + Guid.NewGuid().ToString("N")[..8]);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private (BooksProject Project, BookSession Session, BooksService Service) Setup()
    {
        // A fake "Skyrim.esm" with voice types and NPCs, and a mod with books.
        var sky = new BookPluginBuilder { HeaderFlags = 1 };
        sky.VoiceTypes.Add(new(0x13AD2, "MaleNord"));
        sky.VoiceTypes.Add(new(0x13ADD, "FemaleCommoner", true));
        sky.VoiceTypes.Add(new(0x13AE0, "CrSkeletonVoice"));
        sky.Npcs.Add(new(0x1A001, "Brannoc", "Brannoc Stoneheart", 0x13AD2));
        sky.Npcs.Add(new(0x1A002, "Lyra", "Lyra Vane", 0x13ADD, Female: true));
        sky.Keywords.Add(new(0x937A5, "VendorItemSpellTome"));
        sky.Keywords.Add(new(0xF5CB0, "VendorItemRecipe"));
        sky.Write(Path.Combine(_dir, "Data", "Skyrim.esm"));

        var mod = new BookPluginBuilder();
        mod.Masters.Add("Skyrim.esm");
        mod.VoiceTypes.Add(new(0x01000900, "ExampleCustomVoice"));
        mod.Npcs.Add(new(0x01000901, "Custom", "Odd Fellow", 0x01000900));
        mod.Books.Add(new(0x01000801, "ExLetterBrannoc", "Brannoc's Letter", "Lyra,\n\nThe cart leaves at dawn. Bring the ledger.\n\n-Brannoc Stoneheart"));
        mod.Books.Add(new(0x01000802, "ExLoreBook", "On Old Roads", "On Old Roads\nby Brannoc Stoneheart\n\nThe roads of the north are older than the holds."));
        mod.Books.Add(new(0x01000803, "ExSkillCopy", "On Old Roads", "On Old Roads\nby Brannoc Stoneheart\n\nThe roads of the north are older than the holds.", Flags: 1));
        mod.Books.Add(new(0x01000804, "ExSpellTomeA", "Spell Tome: Spark", "Spark", Flags: 4, Keywords: new[] { 0x937A5u }));
        mod.Books.Add(new(0x01000805, "ExSpellTomeB", "Spell Tome: Glow", "Glow\nA soft light follows you, brightening the dark corners of every cave you enter for a while.", Flags: 4, Keywords: new[] { 0x937A5u }));
        mod.Books.Add(new(0x01000806, "ExRecipe", "Recipe - Soup", "Salt Water Leek", Keywords: new[] { 0xF5CB0u }));
        mod.Books.Add(new(0x01000807, "ExOddNote", "Odd Note", "Do not touch my things.\n\n-Odd Fellow"));
        mod.Books.Add(new(0x01000808, "ExEmpty", "Blank", ""));
        mod.Write(Path.Combine(_dir, "Mod", "Example.esp"));

        var lib = new Library(Path.Combine(_dir, "lib"));
        var svc = new BooksService(lib, new AppSettings { BaseGameFolder = Path.Combine(_dir, "Data") });
        var p = new BooksProject { Name = "t", ModName = "EXAMPLE", IniName = "Example" };
        p.Narrators.Add(new BookNarrator { Name = "Female Narrator", Female = true, VoiceId = "v1" });
        svc.ApplyBaseGame(p);
        p.Plugins.Add(new RevoicePlugin { Path = Path.Combine(_dir, "Mod", "Example.esp"), FileName = "Example.esp", Target = true });
        var s = svc.Open(p);
        svc.Scan(p, s);
        return (p, s, svc);
    }

    [Fact]
    public void Scan_reads_books_shares_identical_texts_and_leaves_out_tomes_recipes_and_empty_books()
    {
        var (p, _, _) = Setup();
        var lore = p.Books.Single(b => b.Stem == "exlorebook");
        Assert.Equal(2, lore.Records.Count); // the skill copy shares the text and the file
        Assert.Equal(0x802u, lore.Records[0].FormId);
        Assert.Equal("Example.esp", lore.Records[0].Plugin);
        Assert.True(p.Books.Single(b => b.Stem == "exspelltomea").Excluded);
        var glow = p.Books.Single(b => b.Stem == "exspelltomeb");
        Assert.True(glow.SuggestInclude);
        Assert.True(p.Books.Single(b => b.Stem == "exrecipe").Excluded);
        Assert.Equal("No text", p.Books.Single(b => b.Stem == "exempty").ExcludeReason);
    }

    [Fact]
    public void A_letter_signed_by_an_npc_with_a_vanilla_voice_type_is_read_in_that_voice()
    {
        var (p, _, _) = Setup();
        var letter = p.Books.Single(b => b.Stem == "exletterbrannoc");
        Assert.Equal("npc", letter.Reader);
        Assert.Equal("MaleNord", letter.VoiceType);
        Assert.Equal("signature", letter.AuthorSource);
        Assert.Equal(Certainty.Unverified, letter.AuthorCertainty);
        // A book (volume) credited to the same NPC stays with the narrator; a custom voice type too.
        Assert.Equal("narrator", p.Books.Single(b => b.Stem == "exlorebook").Reader);
        var odd = p.Books.Single(b => b.Stem == "exoddnote");
        Assert.Equal("ExampleCustomVoice", odd.VoiceType);
        Assert.Equal("narrator", odd.Reader);
    }

    [Fact]
    public void Lines_follow_voices_and_parts_and_keep_their_takes()
    {
        var (p, _, _) = Setup();
        BooksService.RebuildLines(p);
        // The letter is read by MaleNord only; the lore book by the narrator.
        Assert.Contains(p.Lines, l => l.Stem == "exletterbrannoc" && l.Voice == "vt:MaleNord");
        Assert.DoesNotContain(p.Lines, l => l.Stem == "exletterbrannoc" && l.Voice.StartsWith("n:"));
        Assert.Contains(p.Lines, l => l.Stem == "exlorebook" && l.Voice == "n:" + p.Narrators[0].Id);
        Assert.DoesNotContain(p.Lines, l => l.Stem == "exspelltomea");
        // A take survives a rebuild; a text change marks it stale.
        var line = p.Lines.First(l => l.Stem == "exlorebook");
        line.Takes.Add(new Take { Id = "t1", SpokenText = line.Spoken });
        line.SelectedTakeId = "t1";
        line.Status = Phase2Status.Generated;
        BooksService.RebuildLines(p);
        Assert.Equal(Phase2Status.Generated, p.Lines.First(l => l.LineId == line.LineId).Status);
        p.Books.Single(b => b.Stem == "exlorebook").Spoken += " More.";
        BooksService.RebuildLines(p);
        Assert.Equal(Phase2Status.Stale, p.Lines.First(l => l.LineId == line.LineId).Status);
        // Narrated books only: the NPC letter has no line at all.
        p.NpcVoices = false;
        BooksService.RebuildLines(p);
        Assert.DoesNotContain(p.Lines, l => l.Stem == "exletterbrannoc");
    }

    [Fact]
    public void Ini_uses_file_local_form_ids_and_lists_every_record_of_a_shared_text()
    {
        var (p, _, _) = Setup();
        var ini = BooksExporter.BuildIni(p.Books.Where(b => b.Stem == "exlorebook"));
        Assert.Equal(";Example\r\n0x802~Example.esp|sound\\fx\\voicedbooksofskyrim\\exlorebook.xwm\r\n0x803~Example.esp|sound\\fx\\voicedbooksofskyrim\\exlorebook.xwm\r\n", ini);
    }

    [Fact]
    public async Task Export_joins_encodes_packs_and_reports()
    {
        if (OperatingSystem.IsWindows()) return; // the fake encoder below is a shell script
        var (p, _, _) = Setup();
        var folder = Path.Combine(_dir, "proj");
        p.OutputFolder = Path.Combine(_dir, "out");
        p.VoiceTypes["MaleNord"] = new VoiceTypeVoice { VoiceType = "MaleNord", VoiceId = "v2" };
        BooksService.RebuildLines(p);
        // Give every line of two books a take (a short WAV).
        foreach (var l in p.Lines.Where(l => l.Stem is "exlorebook" or "exletterbrannoc"))
        {
            var rel = Path.Combine("takes", l.LineId.Replace('|', '_').Replace(':', '_'), "01", "a.wav");
            var abs = Path.Combine(folder, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
            new WavFile { SampleRate = 44100, Channels = 1, Samples = Enumerable.Range(0, 22050).Select(i => (short)(8000 * Math.Sin(i / 10.0))).ToArray() }.Write(abs);
            l.Takes.Add(new Take { Id = "t" + l.Part, WavPath = rel, SpokenText = l.Spoken });
            l.SelectedTakeId = "t" + l.Part;
            l.Status = Phase2Status.Generated;
        }
        var fake = Path.Combine(_dir, "xwma.sh");
        File.WriteAllText(fake, "#!/bin/sh\ncp \"$3\" \"$4\"\n");
        File.SetUnixFileMode(fake, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var tools = new ExternalTools { FfmpegPath = "ffmpeg", FaceFxWrapperPath = "", XwmaEncodePath = fake };
        var rep = await BooksExporter.ExportAsync(p, folder, tools, true, null, CancellationToken.None);
        Assert.Empty(rep.Errors);
        Assert.Equal(2, rep.Books);
        var narr = BooksExporter.ArchiveName(p, "Female Narrator");
        Assert.Equal("VBOS EXAMPLE - Female Narrator", narr);
        Assert.True(File.Exists(Path.Combine(p.OutputFolder, narr, narr + ".bsa")));
        Assert.Equal(49, new FileInfo(Path.Combine(p.OutputFolder, narr, narr + ".esp")).Length);
        Assert.True(File.Exists(Path.Combine(p.OutputFolder, BooksExporter.ArchiveName(p, "NPC Voices"), BooksExporter.ArchiveName(p, "NPC Voices") + ".bsa")));
        var ini = File.ReadAllText(Path.Combine(p.OutputFolder, "VBOS INI", "Example_VBOS.ini"));
        Assert.Contains("0x801~Example.esp|sound\\fx\\voicedbooksofskyrim\\exletterbrannoc.xwm", ini);
        Assert.DoesNotContain("exoddnote", ini); // no audio, no line
        Assert.True(File.Exists(Path.Combine(p.OutputFolder, "fomod", "ModuleConfig.xml")));
        var report = File.ReadAllText(Path.Combine(p.OutputFolder, rep.Report!));
        Assert.Contains("ARCHIVES AGAINST THE INI", report);
        Assert.Contains("MISSING", report); // the books without takes are listed
        // A second export reuses every join.
        var again = await BooksExporter.ExportAsync(p, folder, tools, false, null, CancellationToken.None);
        Assert.Equal(0, again.Joined);
        Assert.True(again.Reused > 0);
    }
}

public class BuiltInSetTests
{
    [Fact]
    public void The_skyrim_set_is_built_in_with_ini_identities_and_the_npc_split()
    {
        var set = BuiltInSets.Load("skyrim-dlc");
        Assert.NotNull(set);
        Assert.Equal(793, set!.Books.Count);
        Assert.Equal(333, set.Books.Count(b => b.Reader == "npc"));
        var b = set.Books.Single(x => x.Stem == "16accordsofmadnessvolix");
        Assert.Equal(0x1AFB3u, b.Records[0].FormId);
        Assert.Equal("Skyrim.esm", b.Records[0].Plugin);
        Assert.Equal(5, set.Books.Count(x => x.Variants.Count > 0));
        Assert.All(set.Books.SelectMany(x => x.Records), r => Assert.True(r.FormId <= 0xFFFFFF));
    }

    [Fact]
    public void Applying_a_newer_set_keeps_edits_and_offers_the_update()
    {
        var set = new BuiltInSet { Id = "x", Version = 1, Books = { new SetBook { Stem = "a", Title = "A", Spoken = "Old text.", Records = { new BookRecordRef { Plugin = "Skyrim.esm", FormId = 1 } } } } };
        var p = new BooksProject { Source = "x" };
        BuiltInSets.Apply(p, set);
        p.Books[0].Spoken = "My edit."; p.Books[0].SpokenEdited = true;
        set.Version = 2; set.Books[0].Version = 2; set.Books[0].Spoken = "New text.";
        BuiltInSets.Apply(p, set);
        Assert.Equal("My edit.", p.Books[0].Spoken);
        Assert.Equal("New text.", p.Books[0].UpdatedText);
    }
}

public class BookTemplatesTests
{
    [Fact]
    public void The_template_table_maps_templates_to_one_or_more_voice_types()
    {
        var t = VoiceTypeTemplates.Shared;
        Assert.True(t.Count > 500);
        Assert.Contains("MaleNord", t.For("LCharBanditBossNordM"));
        Assert.True(t.For("LvlBanditMelee1H").Count > 1);
        Assert.Empty(t.For("NoSuchTemplate"));
    }
}

public class BookDateTests
{
    [Fact]
    public void The_article_moves_inside_the_date_separators()
    {
        var s = BookText.Dates("On the 17th of Last Seed, 4E 188, I walked.");
        Assert.Equal("On ----- the Seventeenth of Last Seed, Fourth Era, Year One-hundred-Eighty-Eight, ----- I walked.", s);
    }
}
