using System.Text;
using System.Xml.Linq;
using DbvoStudio.Core.Fomod;
using DbvoStudio.Core.Naming;
using DbvoStudio.Core.Projects;
using Xunit;
using Xunit.Abstractions;
namespace DbvoStudio.Core.Tests;
/// <summary>Linked options, conditional installs, missing sources and the simulator: what the bug sweep before 11.0.8 found.</summary>
public class FomodSweepTests
{
    readonly ITestOutputHelper _o; public FomodSweepTests(ITestOutputHelper o) => _o = o;
    static string Tmp() { var d = Path.Combine(Path.GetTempPath(), "sw" + Guid.NewGuid().ToString("N")[..8]); Directory.CreateDirectory(d); return d; }

    [Fact] public void ManualSoundReplacingRemovedLinkedSound_IsDropped()
    {
        var o = new FomodOption { PatchId = "p", ModId = "m", RemovedLinked = { "Sound" },
            Files = { new FomodFile { Kind = "file", Source = "x\\DBVO Fix - A.esp", Destination = "DBVO Fix - A.esp", Linked = true },
                      new FomodFile { Kind = "folder", Source = "MyOwn\\Sound", Destination = "Sound" } } };
        FomodService.Relink(o, new() { new() { Kind = "folder", Source = "x\\Sound", Destination = "Sound", Linked = true },
            new() { Kind = "file", Source = "x\\DBVO Fix - A.esp", Destination = "DBVO Fix - A.esp", Linked = true } }, new Mod { Name = "A" });
        _o.WriteLine(string.Join(", ", o.Files.Select(f => f.Source)));
        Assert.Contains(o.Files, f => f.Source == "MyOwn\\Sound");
    }
    [Fact] public void ExtraManualSoundFolder_IsDropped()
    {
        var o = new FomodOption { PatchId = "p", ModId = "m",
            Files = { new FomodFile { Kind = "folder", Source = "x\\Sound", Destination = "Sound", Linked = true },
                      new FomodFile { Kind = "folder", Source = "Extra\\Sound", Destination = "Sound" } } };
        FomodService.Relink(o, new() { new() { Kind = "folder", Source = "x\\Sound", Destination = "Sound", Linked = true } }, new Mod { Name = "A" });
        _o.WriteLine(string.Join(", ", o.Files.Select(f => f.Source)));
        Assert.Contains(o.Files, f => f.Source == "Extra\\Sound");
        Assert.Contains(o.Files, f => f.Source == "x\\Sound" && f.Linked); // the patch's own audio stays too
    }
    [Fact] public void AllLinkedRemoved_ManualOtherFixDropped()
    {
        var o = new FomodOption { PatchId = "p", ModId = "m", RemovedLinked = { "Sound" },
            Files = { new FomodFile { Kind = "file", Source = "y\\DBVO Fix - Other.esp", Destination = "DBVO Fix - Other.esp" } } };
        FomodService.Relink(o, new() { new() { Kind = "folder", Source = "x\\Sound", Destination = "Sound", Linked = true } }, new Mod { Name = "A" });
        Assert.Contains(o.Files, f => f.Destination == "DBVO Fix - Other.esp");
    }
    [Fact] public void LinkedPriorityReset()
    {
        var o = new FomodOption { PatchId = "p", ModId = "m",
            Files = { new FomodFile { Kind = "folder", Source = "x\\Sound", Destination = "Sound", Linked = true, Priority = 5 } } };
        FomodService.Relink(o, new() { new() { Kind = "folder", Source = "x\\Sound", Destination = "Sound", Linked = true } }, new Mod { Name = "A" });
        Assert.Equal(5, o.Files[0].Priority);
    }
    [Fact] public async Task ConditionalInstallSourcesNotStaged()
    {
        var root = Tmp(); var rel = Tmp();
        File.WriteAllText(Path.Combine(root, "extra.txt"), "x");
        Directory.CreateDirectory(Path.Combine(root, "S")); File.WriteAllText(Path.Combine(root, "S", "a.fuz"), "a");
        var p = new FomodProject { RootFolder = root, ReleaseFolder = rel, Info = new FomodInfo { Name = "C", Version = "1" },
            ConditionalFileInstallsXml = "<conditionalFileInstalls><patterns><pattern><dependencies operator=\"And\"><flagDependency flag=\"f\" value=\"\"/></dependencies><files><file source=\"extra.txt\" destination=\"extra.txt\"/></files></pattern></patterns></conditionalFileInstalls>" };
        p.Json.Enabled = false;
        p.Steps.Add(new FomodStep { Name = "s", Groups = { new FomodGroup { Name = "g", Options = { new FomodOption { Name = "o", Mode = "Optional", Files = { new FomodFile { Kind = "folder", Source = "S", Destination = "Sound" } } } } } } });
        var svc = new FomodService(new Library(Tmp()), new AppSettings());
        Assert.DoesNotContain(svc.Validate(p), i => i.Level == "error");
        Assert.True(svc.Simulate(p, new string[0]).Files.Single(f => f.Source == "extra.txt").Exists);
        var res = await svc.BuildAsync(p, NamingMode.Legacy, false);
        foreach (var f in Directory.EnumerateFiles(res.Staging, "*", SearchOption.AllDirectories)) _o.WriteLine(Path.GetRelativePath(res.Staging, f));
        Assert.True(File.Exists(Path.Combine(res.Staging, "extra.txt")), "extra.txt referenced by XML but not staged");
    }
    [Fact] public async Task MissingLinkedSoundStillInXml()
    {
        var root = Tmp(); var rel = Tmp();
        var p = new FomodProject { RootFolder = root, ReleaseFolder = rel, Info = new FomodInfo { Name = "M", Version = "1" } };
        p.Json.Enabled = false;
        p.Steps.Add(new FomodStep { Name = "s", Groups = { new FomodGroup { Name = "g", Options = { new FomodOption { Name = "o", Mode = "Optional", PatchId = "x", ModId = "y", Files = { new FomodFile { Kind = "folder", Source = "Vamp - A\\Sound", Destination = "Sound", Linked = true } } } } } } });
        var svc = new FomodService(new Library(Tmp()), new AppSettings());
        Assert.DoesNotContain(svc.Validate(p), i => i.Level == "error");
        var res = await svc.BuildAsync(p, NamingMode.Legacy, false);
        var mc = File.ReadAllText(Path.Combine(res.Staging, "fomod", "ModuleConfig.xml"), Encoding.Unicode);
        _o.WriteLine(string.Join("|", res.Warnings));
        Assert.DoesNotContain("Vamp - A", mc);
    }
    [Fact] public void SimulateRadioCannotPickSecond()
    {
        var p = new FomodProject();
        var a = new FomodOption { Name = "A", Mode = "Recommended" }; var b = new FomodOption { Name = "B", Mode = "Optional" };
        p.Steps.Add(new FomodStep { Name = "s", Groups = { new FomodGroup { Name = "g", Type = "SelectExactlyOne", Options = { a, b } } } });
        var r = new FomodService(new Library(Tmp()), new AppSettings()).Simulate(p, new string[0], new Dictionary<string, bool> { [b.Id] = true });
        Assert.True(r.Steps[0].Groups[0].Options.Single(x => x.Name == "B").Selected);
    }
}
