using DbvoStudio.Core.Reference;
using DbvoStudio.Core.Revoice;
using Xunit.Abstractions;

namespace DbvoStudio.Core.Tests;

public class UespReferenceTests
{
    private readonly ITestOutputHelper _out;
    public UespReferenceTests(ITestOutputHelper o) => _out = o;

    [Fact]
    public void Embedded_data_loads_with_attribution()
    {
        var u = UespReference.LoadEmbedded();
        Assert.Contains("uesp.net", u.Source.Url);
        Assert.Equal("CC BY-SA 2.5", u.Source.License);
        Assert.True(u.Npcs.Count > 1000 && u.Quotes.Count > 20000 && u.Books.Count > 1000);
        var abelone = Assert.Single(u.Npcs, n => n.Name == "Abelone");
        Assert.Equal("FemaleCommoner", abelone.VoiceType);
        Assert.Same(abelone, u.NpcByBaseId("Skyrim.esm", 0x8774F));
        // A conversation quoted on Sorine's page: the line is Gunmar's, not Sorine's.
        Assert.Equal("Gunmar", Assert.Single(u.Speakers("Are you mad, woman? After the trouble I've gone through?")).Name);
        var note = u.BookById("Skyrim.esm", 0x90E52)!;
        Assert.Equal("Aeri", note.Author);
        Assert.Equal("Skyrim:Aeri", note.AuthorPage);
    }

    [Fact]
    public void Normalize_ignores_case_punctuation_and_spacing()
        => Assert.Equal("ill be there", UespReference.Normalize("  I'll  be there... "));

    /// <summary>The scanner's base-game speakers against UESP's quotes (skipped without the game files).</summary>
    [Fact]
    public void Base_game_speakers_agree_with_uesp()
    {
        if (RealDataTests.F("Skyrim.esm") is not { } sky) return;
        var u = UespReference.LoadEmbedded();
        var lib = new DbvoStudio.Core.Projects.Library(Path.Combine(Path.GetTempPath(), "dbvo-uesp-" + Guid.NewGuid().ToString("N")[..6]));
        var svc = new RevoiceService(lib, lib.LoadSettings());
        var p = new RevoiceProject { Name = "Vanilla", UseBaseGame = false };
        foreach (var f in new[] { "Skyrim.esm", "Update.esm", "Dawnguard.esm", "HearthFires.esm" })
            if (RealDataTests.F(f) is { } path) p.Plugins.Add(new RevoicePlugin { Path = path, FileName = f, Target = true });
        p.Sources.Add(new RevoiceSource { Kind = "bsa", Path = Path.Combine(RealDataTests.Data, "Skyrim - Interface.bsa"), Priority = -100 });
        using var s = svc.Open(p);
        svc.Scan(p, s);
        int agree = 0, disagree = 0, byUesp = p.Lines.Count(l => l.Via.Contains("UESP"));
        var seen = new HashSet<string>();
        foreach (var l in p.Lines.Where(l => l.SpeakerKey.StartsWith("npc:") && !l.Via.Contains("UESP")))
        {
            var who = u.Speakers(l.FinalLine).Where(n => n.VoiceType.Equals(l.VoiceType, StringComparison.OrdinalIgnoreCase)).ToList();
            if (who.Count != 1 || !seen.Add(l.LineId)) continue;
            var key = l.SpeakerKey[4..];
            bool same = who[0].Name.Equals(l.SpeakerName, StringComparison.OrdinalIgnoreCase)
                || (who[0].BaseId.Length > 0 && key.EndsWith("|" + who[0].BaseId, StringComparison.OrdinalIgnoreCase));
            if (same) agree++; else { disagree++; if (disagree <= 15) _out.WriteLine($"{l.FinalLine} | scanner {l.SpeakerName} ({l.Via}) | UESP {who[0].Name}"); }
        }
        _out.WriteLine($"agree {agree}, disagree {disagree}, speakers filled in from UESP {byUesp}, lines by NPC {p.Lines.Count(l => l.SpeakerKey.StartsWith("npc:"))}/{p.Lines.Count}");
        Assert.True(byUesp > 100);
        Assert.True(agree > 20 * disagree);
    }
}
