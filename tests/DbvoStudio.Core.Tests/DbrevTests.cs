using DbvoStudio.Core.Fomod;
using DbvoStudio.Core.Naming;
using DbvoStudio.Core.Projects;
using Xunit;

namespace DbvoStudio.Core.Tests;

public class DbrevTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dbvo-dbrev-" + Guid.NewGuid().ToString("N")[..8]);
    public DbrevTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Theory]
    [InlineData(0x2A000D63u, false, 2, "00000D63")]  // regular plugin's own record: load-order byte stripped
    [InlineData(0x000CA1EFu, false, 2, "000CA1EF")]  // a Skyrim.esm record a mod overrides
    [InlineData(0x02000D63u, true, 2, "00000D63")]    // ESL plugin's own record (index 2 = its own): 3 digits
    [InlineData(0x000CA1EFu, true, 2, "000CA1EF")]   // ESL plugin overriding Skyrim.esm: 6 digits
    public void FormIds_follow_dbrevs_rules(uint raw, bool light, int masters, string want) =>
        Assert.Equal(want, LineNaming.DbrevFormId(raw, light, masters));

    [Fact]
    public void Names_and_json_location()
    {
        Assert.Equal("dbrev_line_00000D63", LineNaming.DbrevName("00000D63", 0));
        Assert.Equal("dbrev_line_00000D63(2)", LineNaming.DbrevName("00000D63", 2));
        Assert.Equal(@"DBReV\voice_packs\Vampira.json", FomodService.JsonDestination(NamingMode.Dbrev, "Vampira"));
        Assert.Equal(@"Vamp - {mod}\Sound\DBReV\{packId}", Phase2Service.DbrevTemplate(@"Vamp - {mod}\Sound\DBVO\{packId}"));
    }

    [Fact]
    public void Export_plans_one_file_per_source_with_kept_takes_and_text_variants_as_variations()
    {
        var lib = new Library(Path.Combine(_dir, "lib"));
        var svc = new Phase2Service(lib, lib.LoadSettings());
        var mod = new Mod { Name = "Vigilant", Plugins = { new ModPlugin { FileName = "Vigilant.esm", Masters = { "Skyrim.esm", "Update.esm" }, Light = false } } };
        var voice = new VoicePack { Name = "V", PackId = "Vamp", Naming = NamingMode.Dbrev };
        var patch = new DbvoPatch { FinalRoot = Path.Combine(_dir, "final"), WavLipFolder = Path.Combine(_dir, "wl"), FuzTemplate = Path.Combine("Vamp - {mod}", "Sound", "DBVO", "{packId}") };
        Phase2Line Line(string text, params uint[] ids)
        {
            var l = new Phase2Line { LineId = NewId.Short(), Key = text, FinalLine = text, Spoken = text, Status = Phase2Status.Generated };
            foreach (var id in ids) l.Sources.Add(new LineSource { ModId = mod.Id, Plugin = "Vigilant.esm", FormId = id });
            patch.Lines.Add(l);
            return l;
        }
        Take T(Phase2Line l, bool kept = false) { var t = new Take { Slot = l.Takes.Count + 1, Kept = kept }; l.Takes.Add(t); return t; }

        var a = Line("Follow me.", 0x02000D63, 0x02000D64, 0x02000D64);   // one text, two FormIDs (one listed twice)
        var a1 = T(a, kept: true); var a2 = T(a, kept: true); a.SelectedTakeId = a2.Id;
        var v = Line("Follow me, friend.", 0x02000D63);         // a text variant of the first FormID
        var v1 = T(v); v.SelectedTakeId = v1.Id;
        var over = Line("Where to?", 0x000CA1EF);              // overrides a Skyrim.esm record
        T(over); over.SelectedTakeId = over.Takes[0].Id;

        var plan = Phase2Service.DbrevPlan(Phase2Service.ExportLines(patch), new[] { mod });
        Assert.Equal(new[] { "dbrev_line_00000D63", "dbrev_line_00000D63(1)", "dbrev_line_00000D64", "dbrev_line_00000D64(1)" },
            plan[a].Select(x => Path.GetFileName(x.Rel)));
        Assert.Same(a2, plan[a][0].Take); // the selected take is the line itself
        Assert.Equal(Path.Combine("Vigilant.esm", "dbrev_line_00000D63(2)"), plan[v].Single().Rel);
        Assert.Equal(Path.Combine("Vigilant.esm", "dbrev_line_000CA1EF"), plan[over].Single().Rel); // the overriding plugin's own folder

        var files = svc.PlannedExports(patch, voice, new[] { mod });
        // (Windows paths: compared with / so the test also runs elsewhere)
        Assert.Contains(files, f => f.Replace('\\', '/').EndsWith("Vamp - Vigilant/Sound/DBReV/Vamp/Vigilant.esm/dbrev_line_000CA1EF.fuz"));
        Assert.Contains(Path.GetFullPath(Path.Combine(_dir, "wl", "Vigilant.esm", "dbrev_line_00000D64(1).wav")), files);
    }
}
