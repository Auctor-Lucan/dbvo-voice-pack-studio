using DbvoStudio.Core.Naming;
using System.Text;
using System.Xml.Linq;
using DbvoStudio.Core.Fomod;
using DbvoStudio.Core.Projects;
using Xunit;
using Xunit.Abstractions;

namespace DbvoStudio.Core.Tests;

/// <summary>FOMOD import → export and build edge cases.</summary>
public class FomodRoundTripTests
{
    private readonly ITestOutputHelper _o;
    public FomodRoundTripTests(ITestOutputHelper o) => _o = o;
    static string Tmp() { var d = Path.Combine(Path.GetTempPath(), "fr" + Guid.NewGuid().ToString("N")[..8]); Directory.CreateDirectory(d); return d; }

    const string Head = "<config xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xsi:noNamespaceSchemaLocation=\"http://qconsulting.ca/fo3/ModConfig5.0.xsd\"><moduleName>M</moduleName>";

    [Fact]
    public void ModuleImageAttrs()
    {
        var x = Head + "<moduleImage path=\"a.png\" showImage=\"false\" height=\"80\"/></config>";
        var outp = FomodXml.WriteModuleConfig(FomodXml.Read(x, null));
        _o.WriteLine(outp);
        Assert.Contains("showImage", outp);
    }

    [Fact]
    public void AlwaysInstallOne()
    {
        var x = Head + "<requiredInstallFiles><file source=\"a.esp\" destination=\"a.esp\" alwaysInstall=\"1\"/></requiredInstallFiles></config>";
        var p = FomodXml.Read(x, null);
        Assert.True(p.RequiredInstallFiles[0].AlwaysInstall == true);
    }

    [Fact]
    public void Cp1252()
    {
        var bytes = Encoding.Latin1.GetBytes("<?xml version=\"1.0\" encoding=\"windows-1252\"?>" + Head.Replace(">M<", ">Café<") + "</config>");
        var p = FomodXml.Read(FomodXml.DecodeText(bytes), null);
        _o.WriteLine(p.ModuleName);
        Assert.Equal("Café", p.ModuleName);
    }

    [Fact]
    public void DefaultNamespace()
    {
        var x = "<config xmlns=\"urn:x\"><moduleName>M</moduleName><installSteps><installStep name=\"s\"><optionalFileGroups><group name=\"g\" type=\"SelectAny\"><plugins><plugin name=\"o\"><description/><conditionFlags><flag name=\"f\">On</flag></conditionFlags><typeDescriptor><type name=\"Optional\"/></typeDescriptor></plugin></plugins></group></optionalFileGroups></installStep></installSteps></config>";
        var outp = FomodXml.WriteModuleConfig(FomodXml.Read(x, null));
        _o.WriteLine(outp);
        Assert.DoesNotContain("urn:x", outp);
    }

    [Fact]
    public void ValidateInfoOnly()
    {
        var lib = new Library(Tmp());
        var p = new FomodProject { RootFolder = Tmp(), Info = new FomodInfo { Name = "n", Version = "1" } };
        p.Json.Enabled = false;
        p.Steps.Add(new FomodStep { Name = "s", Groups = { new FomodGroup { Name = "g", Options = { new FomodOption { Name = "o", Mode = "Optional" } } } } });
        var issues = new FomodService(lib, new AppSettings()).Validate(p);
        foreach (var i in issues) _o.WriteLine(i.Level + " " + i.Message);
        Assert.DoesNotContain(issues, i => i.Message.StartsWith("Schema"));
    }

    [Fact]
    public async Task ExternalCollision()
    {
        var lib = new Library(Tmp());
        var root = Tmp(); var rel = Tmp();
        var a = Path.Combine(Tmp(), "Sound"); var b = Path.Combine(Tmp(), "Sound");
        Directory.CreateDirectory(Path.Combine(a, "DBVO")); Directory.CreateDirectory(Path.Combine(b, "DBVO"));
        File.WriteAllText(Path.Combine(a, "DBVO", "a.fuz"), "A"); File.WriteAllText(Path.Combine(b, "DBVO", "b.fuz"), "B");
        var ia = Path.Combine(Tmp(), "preview.png"); var ib = Path.Combine(Tmp(), "preview.png");
        File.WriteAllText(ia, "IA"); File.WriteAllText(ib, "IB");
        var p = new FomodProject { RootFolder = root, ReleaseFolder = rel, Info = new FomodInfo { Name = "n", Version = "1" } };
        p.Json.Enabled = false;
        p.Steps.Add(new FomodStep { Name = "s", Groups = { new FomodGroup { Name = "g", Options = {
            new FomodOption { Name = "Inigo", Mode = "Optional", Image = ia, Files = { new FomodFile { Kind = "folder", Source = a, Destination = "Sound" } } },
            new FomodOption { Name = "Inigo", Mode = "Optional", Image = ib, Files = { new FomodFile { Kind = "folder", Source = b, Destination = "Sound" } } },
        } } } });
        var res = await new FomodService(lib, new AppSettings()).BuildAsync(p, NamingMode.Legacy, false);
        var mc = File.ReadAllText(Path.Combine(res.Staging, "fomod", "ModuleConfig.xml"), Encoding.Unicode);
        _o.WriteLine(mc);
        foreach (var f in Directory.EnumerateFiles(res.Staging, "*", SearchOption.AllDirectories)) _o.WriteLine(Path.GetRelativePath(res.Staging, f) + " = " + File.ReadAllText(f).Length);
        Assert.Equal(2, XDocument.Parse(mc.Substring(mc.IndexOf("<config"))).Descendants("folder").Select(e => e.Attribute("source")!.Value).Distinct().Count());
    }

    [Fact]
    public void Empty_folder_source_is_flagged()
    {
        var p = new FomodProject { RootFolder = Tmp(), Info = new FomodInfo { Name = "n", Version = "1" } };
        p.Json.Enabled = false;
        p.Steps.Add(new FomodStep { Name = "s", Groups = { new FomodGroup { Name = "g", Options = {
            new FomodOption { Name = "o", Mode = "Optional", Files = { new FomodFile { Kind = "folder", Source = "", Destination = "" } } } } } } });
        Assert.Contains(new FomodService(new Library(Tmp()), new AppSettings()).Validate(p), i => i.Message.Contains("whole FOMOD root"));
    }

    [Fact]
    public void RelinkDropsManualFix()
    {
        var o = new FomodOption { PatchId = "p", Files = { new FomodFile { Kind = "folder", Source = "old", Destination = "Sound", Linked = true }, new FomodFile { Kind = "file", Source = "x\\DBVO Fix - Other.esp", Destination = "DBVO Fix - Other.esp" } } };
        FomodService.Relink(o, new List<FomodFile> { new() { Kind = "folder", Source = "s", Destination = "Sound", Linked = true } }, new Mod { Name = "A" });
        Assert.Contains(o.Files, f => f.Destination == "DBVO Fix - Other.esp");
        Assert.Single(o.Files, f => f.Destination == "Sound");
    }

    [Fact]
    public void Simulate_follows_the_installer_order()
    {
        var lib = new Library(Tmp());
        var p = new FomodProject { StepsOrder = "Ascending" };
        foreach (var n in new[] { "Zeta", "Alpha" })
            p.Steps.Add(new FomodStep { Name = n, Groups = { new FomodGroup { Name = "g", Options = { new FomodOption { Name = "o" + n } } } } });
        var r = new FomodService(lib, new AppSettings()).Simulate(p, Array.Empty<string>());
        Assert.Equal(new[] { "Alpha", "Zeta" }, r.Steps.Select(s => s.Name));
    }

    [Fact]
    public async Task The_unpacked_installer_sits_next_to_the_archive_and_never_replaces_your_own_folder()
    {
        var lib = new Library(Tmp());
        var rel = Tmp(); var root = Tmp();
        Directory.CreateDirectory(Path.Combine(root, "Sound")); File.WriteAllText(Path.Combine(root, "Sound", "a.fuz"), "A");
        var p = new FomodProject { RootFolder = root, ReleaseFolder = rel, Info = new FomodInfo { Name = "Mod", Version = "1.0" } };
        p.Json.Enabled = false;
        p.Steps.Add(new FomodStep { Name = "s", Groups = { new FomodGroup { Name = "g", Options = {
            new FomodOption { Name = "o", Mode = "Optional", Files = { new FomodFile { Kind = "folder", Source = "Sound", Destination = "Sound" } } } } } } });
        var svc = new FomodService(lib, new AppSettings());
        var res = await svc.BuildAsync(p, NamingMode.Legacy, false);
        Assert.Equal(Path.Combine(rel, "Mod 1.0"), res.Staging);
        Assert.True(File.Exists(Path.Combine(res.Staging, "Sound", "a.fuz")));
        await svc.BuildAsync(p, NamingMode.Legacy, false); // its own earlier build is replaced
        // A folder of that name that isn't a build stays untouched.
        Directory.Delete(res.Staging, true); Directory.CreateDirectory(res.Staging); File.WriteAllText(Path.Combine(res.Staging, "mine.txt"), "x");
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.BuildAsync(p, NamingMode.Legacy, false));
        Assert.True(File.Exists(Path.Combine(res.Staging, "mine.txt")));
        // The FOMOD root itself named like the archive: refused.
        var p2 = new FomodProject { RootFolder = Path.Combine(rel, "Mod 1.0"), ReleaseFolder = rel, Info = new FomodInfo { Name = "Mod", Version = "1.0" } };
        p2.Json.Enabled = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.BuildAsync(p2, NamingMode.Legacy, false));
        Assert.True(File.Exists(Path.Combine(res.Staging, "mine.txt")));
    }

    [Fact]
    public async Task A_json_the_installer_already_installs_is_not_added_twice()
    {
        var lib = new Library(Tmp());
        var root = Tmp(); var rel = Tmp();
        var dv = Path.Combine(root, "Base", "DragonbornVoiceOver", "voice_packs"); Directory.CreateDirectory(dv);
        File.WriteAllText(Path.Combine(dv, "vampira_voice_pack.json"), "{}");
        var p = new FomodProject { RootFolder = root, ReleaseFolder = rel, Info = new FomodInfo { Name = "DVA", Version = "1" } };
        p.Json.Enabled = true; p.Json.VoicePackId = "Vampira"; p.Json.VoicePackName = "Vampira";
        p.Steps.Add(new FomodStep { Name = "s", Groups = { new FomodGroup { Name = "g", Options = {
            new FomodOption { Name = "Base", Mode = "Required", CarriesJson = true, Files = { new FomodFile { Kind = "folder", Source = @"Base\DragonbornVoiceOver", Destination = "DragonbornVoiceOver" } } } } } } });
        var svc = new FomodService(lib, new AppSettings());
        Assert.Single(svc.ExistingJson(p));
        Assert.DoesNotContain(svc.Validate(p), i => i.Level == "error");
        var res = await svc.BuildAsync(p, NamingMode.Legacy, false);
        Assert.False(Directory.Exists(Path.Combine(res.Staging, "_common")));
        Assert.Single(Directory.EnumerateFiles(res.Staging, "*.json", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Bsa_option_packs_windows_1252_names_and_leaves_only_the_rest_loose()
    {
        var lib = new Library(Tmp());
        var root = Tmp(); var rel = Tmp();
        var dir = Path.Combine(root, "Inigo", "Sound", "DBVO", "Vampira"); Directory.CreateDirectory(dir);
        foreach (var n in new[] { "you’ll_always_have_a_home_here..fuz", "i_do….fuz", "привет..fuz", "plain..fuz" }) File.WriteAllBytes(Path.Combine(dir, n), new byte[20]);
        var p = new FomodProject { RootFolder = root, ReleaseFolder = rel, PackBsa = true, Info = new FomodInfo { Name = "B", Version = "1" } };
        p.Json.Enabled = false;
        p.Steps.Add(new FomodStep { Name = "s", Groups = { new FomodGroup { Name = "g", Options = {
            new FomodOption { Name = "Inigo", Mode = "Optional", Files = { new FomodFile { Kind = "folder", Source = @"Inigo\Sound", Destination = "Sound" } } } } } } });
        var res = await new FomodService(lib, new AppSettings()).BuildAsync(p, NamingMode.Legacy, false);
        var loose = Directory.EnumerateFiles(Path.Combine(res.Staging, "_loose"), "*", SearchOption.AllDirectories).Select(Path.GetFileName).ToList();
        Assert.Equal(new[] { "привет..fuz" }, loose);
        using var bsa = DbvoStudio.Core.Archives.BsaArchive.Open(Directory.EnumerateFiles(Path.Combine(res.Staging, "_bsa"), "*.bsa").Single());
        Assert.Contains(bsa.Entries, e => e.Path.EndsWith("you’ll_always_have_a_home_here..fuz"));
        Assert.Contains(bsa.Entries, e => e.Path.EndsWith("i_do….fuz"));
        Assert.Equal(3, bsa.Entries.Count());
    }

    [Fact]
    public void A_linked_file_you_removed_is_not_added_back()
    {
        var o = new FomodOption { PatchId = "p", RemovedLinked = { "DBVO Fix - 018Auri.esp" },
            Files = { new FomodFile { Kind = "folder", Source = "x\\Sound", Destination = "Sound", Linked = true } } };
        FomodService.Relink(o, new List<FomodFile>
        {
            new() { Kind = "folder", Source = "x\\Sound", Destination = "Sound", Linked = true },
            new() { Kind = "file", Source = "x\\DBVO Fix - 018Auri.esp", Destination = "DBVO Fix - 018Auri.esp", Linked = true },
            new() { Kind = "file", Source = "x\\DBVO Fix - 018AuriVIGILANTpatch.esp", Destination = "DBVO Fix - 018AuriVIGILANTpatch.esp", Linked = true },
        }, new Mod { Name = "Auri" });
        Assert.Equal(new[] { "Sound", "DBVO Fix - 018AuriVIGILANTpatch.esp" }, o.Files.Select(f => f.Destination));
    }
}
