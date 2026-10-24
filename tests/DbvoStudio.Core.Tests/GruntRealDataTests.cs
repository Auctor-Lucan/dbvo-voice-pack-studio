using DbvoStudio.Core.Grunts;
using Xunit;
using Xunit.Abstractions;

namespace DbvoStudio.Core.Tests;

/// <summary>
/// The catalog readers against the real PCHT and SPVS files, when DBVO_GRUNT_REFS points at a folder holding
/// "PC Head Tracking and Voice Type SE" and "Smart Player Voice System" (the mods aren't shipped in the repo).
/// With DBVO_GRUNT_SNAPSHOT set, the readers' output is written as the built-in catalogs.
/// </summary>
public class GruntRealDataTests
{
    private readonly ITestOutputHelper _out;
    public GruntRealDataTests(ITestOutputHelper o) => _out = o;

    private static string? Refs(string sub)
    {
        var dir = Environment.GetEnvironmentVariable("DBVO_GRUNT_REFS");
        if (string.IsNullOrEmpty(dir)) return null;
        var p = Path.Combine(dir, sub);
        return Directory.Exists(p) ? p : null;
    }

    [Fact]
    public void Pcht_catalog_from_the_installed_plugins()
    {
        var dir = Refs("PC Head Tracking and Voice Type SE");
        if (dir == null) return;
        var c = GruntCatalogs.ReadPcht(dir);
        var pools = c.Events.Where(e => e.Kind == "pool").ToList();
        var shouts = c.Events.Where(e => e.Kind == "slots").ToList();
        _out.WriteLine($"{pools.Count} pools, {shouts.Count} shouts");
        foreach (var s in shouts) _out.WriteLine($"{s.Id} {s.Label}: {string.Join(" | ", s.Slots.Select(x => x.Id + "=" + x.Text + " " + string.Join(",", x.Files)))}");
        Assert.Equal(72, pools.Count);
        Assert.All(pools, p => Assert.False(string.IsNullOrEmpty(p.Sndr)));
        Assert.Contains(pools, p => p.Folder == @"Combat\Attack" && p.Sndr == "_ComAttack" && p.BaseSlots == 5);
        Assert.Contains(pools, p => p.Folder == @"Greeting\Random" && p.BaseSlots == 10);
        Assert.Equal(28, shouts.Count);
        var fus = shouts.Single(s => s.Label == "Unrelenting Force");
        Assert.Equal(4, fus.Slots.Count);
        Assert.Equal(@"Sound\Voice\PC Head Tracking - MCM.esp\{vt}\_VP_01a_0000A181_1", fus.Slots[0].Files.Single());
        var dg = shouts.Single(s => s.Slots.Any(x => x.Files.Any(f => f.Contains("_VPDG_DG_01a_00014946_1"))));
        Assert.Contains("Patch.esp", dg.Slots[0].Files[0]);
        if (Environment.GetEnvironmentVariable("DBVO_GRUNT_SNAPSHOT") is { Length: > 0 } outDir) Snapshot(c, outDir);
    }

    [Fact]
    public void Spvs_catalog_from_the_installed_files()
    {
        var dir = Refs("Smart Player Voice System");
        if (dir == null) return;
        var c = GruntCatalogs.ReadSpvs(dir);
        var pools = c.Events.Where(e => e.Kind == "pool").ToList();
        var shouts = c.Events.Where(e => e.Kind == "slots").ToList();
        _out.WriteLine($"{c.SourceInfo}: {pools.Count} pools, {shouts.Count} shouts");
        foreach (var p in pools) _out.WriteLine($"{p.Category} / {p.Label} [{p.Key}] {p.Folder} vanilla:{p.VanillaNames.Count} pcht:{string.Join(",", p.PchtFolders)}");
        foreach (var s in shouts) _out.WriteLine($"{s.Id} {s.Label}: {string.Join(" | ", s.Slots.Select(x => x.Id + "=" + x.Text + " " + string.Join(",", x.Files)))}");
        Assert.Contains(pools, p => p.Key == "MagicHeal" && p.Folder == @"Magic\Heal" && p.PchtFolders.Contains(@"Magic\Heal"));
        var attack = pools.Single(p => p.Key == "VanillaAttack");
        Assert.Equal(new[] { "Combat_____Attack__00000811_1", "Combat_____Attack__00000812_1", "Combat_____Attack__00000813_1", "Combat_____Attack__00000814_1", "Combat_____Attack__00000815_1", "Combat_____Attack__00000816_1" }, attack.VanillaNames);
        Assert.Equal(9, pools.Single(p => p.Key == "VanillaSprint").VanillaNames.Count);
        Assert.Equal("Breath_____EnterBowZoomBre_00000AC1_1", pools.Single(p => p.Key == "VanillaEnterEagleEye").VanillaNames[0]);
        Assert.Contains(pools, p => p.Folder == @"Discovered\Specific\Voice");
        var fus = shouts.Single(s => s.Label == "Unrelenting Force");
        Assert.Contains(fus.Slots.SelectMany(x => x.Files), f => f.EndsWith(@"SPVS_VanillaShout.esp\SPVS_Replace{slot}\Shout______01aStartShort___000009C0_1", StringComparison.Ordinal) || f.Contains("Shout______01aStartShort___"));
        Assert.Contains(fus.Slots.SelectMany(x => x.Files), f => f.Contains(@"SmartPlayerVoiceSystem.esp\SPVS_Replace{slot}\Shout______01aStartShort__0"));
        if (Environment.GetEnvironmentVariable("DBVO_GRUNT_SNAPSHOT") is { Length: > 0 } outDir) Snapshot(c, outDir);
    }

    [Fact]
    public void Pcht_plugins_build_and_validate()
    {
        var dir = Refs("PC Head Tracking and Voice Type SE");
        var multi = Refs("Multi Voice Packs support for PC Head Tracking");
        if (dir == null || multi == null) return;
        var src = PchtPlugins.ReadSource(Path.Combine(dir, GruntCatalogs.PchtPlugin));
        Assert.False(PchtPlugins.IsMulti(src));
        var files = new Dictionary<string, List<string>>
        {
            ["_ComAttack"] = Enumerable.Range(1, 12).Select(i => $@"data\Sound\FX\PC Head Tracking\Combat\Attack\{i:00}.wav").ToList(),
            ["_GreetHello"] = new() { @"data\Sound\FX\PC Head Tracking\Greeting\Hello\01.wav" },
        };
        var ov = PchtPlugins.BuildOverride(src, "PCHT - Test Lines.esp", files, "test");
        Assert.Empty(PchtPlugins.Validate(ov.Bytes, ov.FileName, src));
        var tmp = Path.Combine(Path.GetTempPath(), "pcht-ov-test.esp");
        File.WriteAllBytes(tmp, ov.Bytes);
        var back = Plugins.PluginFile.Read(tmp, new HashSet<string> { "SNDR" });
        var atk = back.Records.Single(r => r.EditorId == "_ComAttack");
        Assert.Equal(12, atk.Subrecords.Count(s => s.Type == "ANAM"));
        Assert.Equal(src.Records.Single(r => r.EditorId == "_ComAttack").FormId, atk.FormId);
        Assert.Equal(new[] { "EDID", "CNAM", "GNAM" }, atk.Subrecords.Take(3).Select(s => s.Type));
        Assert.Equal("ONAM", atk.Subrecords[3 + 12].Type);

        var msrc = PchtPlugins.ReadSource(Path.Combine(multi, GruntCatalogs.PchtPlugin));
        Assert.True(PchtPlugins.IsMulti(msrc));
        var vs = PchtPlugins.BuildVoiceSet(msrc, "Vampira", "Vampira", "female", files, "test");
        var problems = PchtPlugins.Validate(vs.Bytes, vs.FileName, msrc);
        Assert.Empty(problems);
        File.WriteAllBytes(Path.Combine(Path.GetTempPath(), vs.FileName), vs.Bytes);
        var vb = Plugins.PluginFile.Read(Path.Combine(Path.GetTempPath(), vs.FileName), PchtPlugins.SourceSignatures);
        Assert.Equal(1 + 1 + 72 + 72 + 2, vb.Records.Count);
        Assert.Equal("Vampira_MAGA_VoiceEvents", vb.Records.Single(r => r.Signature == "SPEL").EditorId);
        Assert.Contains(@"PC Head Tracking\Vampira\Combat\Hit\01.wav", vb.Records.Single(r => r.EditorId == "Vampira_ComHit").Subrecords.Where(s => s.Type == "ANAM").Select(s => Plugins.PluginRecord.ZString(s.Data)).First());
        Assert.Equal("FormList=BYOVoiceTypesF|Vampira_FemaleBYO", vs.FlmLines[2]);
        _out.WriteLine(string.Join("\n", vs.FlmLines));
    }

    private static void Snapshot(GruntCatalog c, string outDir)
    {
        var copy = GruntCatalogs.Clone(new GruntEventDef()); // warm the serializer
        c.Source = "built-in";
        foreach (var e in c.Events) e.Origin = "built-in";
        c.SourceInfo = c.Target == GruntTargets.Spvs ? c.SourceInfo.Split(" · ")[0] : "PC Head Tracking and Voice Type SE 2.6";
        Directory.CreateDirectory(outDir);
        File.WriteAllBytes(Path.Combine(outDir, c.Target + ".json.gz"), GruntCatalogs.Pack(c));
    }
}
