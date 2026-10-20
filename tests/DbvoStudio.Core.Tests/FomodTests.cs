using System.Xml.Linq;
using DbvoStudio.Core.Fomod;
using DbvoStudio.Core.Naming;
using DbvoStudio.Core.Projects;

namespace DbvoStudio.Core.Tests;

public class FomodTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dbvo-fomod-" + Guid.NewGuid().ToString("N")[..8]);

    public FomodTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private const string Sample = """
<?xml version="1.0" encoding="utf-16"?>
<config xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xsi:noNamespaceSchemaLocation="http://qconsulting.ca/fo3/ModConfig5.0.xsd">
	<moduleName position="Right" colour="FFFFFF">DVA</moduleName>
	<moduleImage path="Images\Header.png" />
	<installSteps order="Explicit">
		<installStep name="Followers">
			<visible><flagDependency flag="showFollowers" value="On"/></visible>
			<optionalFileGroups order="Explicit">
				<group name="Followers (A-Z)" type="SelectAny">
					<plugins order="Explicit">
						<plugin name="Inigo">
							<description>Inigo patch
second line</description>
							<image path="Images\Inigo.png" />
							<files>
								<folder source="Vamp - Inigo\Sound" destination="Sound" priority="0" />
								<file source="Vamp - Inigo\DBVO Fix - Inigo.esp" destination="DBVO Fix - Inigo.esp" priority="0" />
							</files>
							<typeDescriptor>
								<dependencyType>
									<defaultType name="Optional"/>
									<patterns>
										<pattern><dependencies operator="And"><fileDependency file="Inigo.esp" state="Active"/></dependencies><type name="Recommended"/></pattern>
										<pattern><dependencies operator="And"><fileDependency file="Inigo.esp" state="Inactive"/></dependencies><type name="NotUsable"/></pattern>
									</patterns>
								</dependencyType>
							</typeDescriptor>
						</plugin>
						<plugin name="Two plugins">
							<description/>
							<files><folder source="Vamp - Two\Sound" destination="Sound" priority="0" /></files>
							<typeDescriptor>
								<dependencyType>
									<defaultType name="Optional"/>
									<patterns>
										<pattern><dependencies operator="Or"><fileDependency file="A.esp" state="Active"/><fileDependency file="B.esp" state="Active"/></dependencies><type name="Recommended"/></pattern>
										<pattern><dependencies operator="And"><fileDependency file="A.esp" state="Inactive"/><fileDependency file="B.esp" state="Inactive"/></dependencies><type name="NotUsable"/></pattern>
									</patterns>
								</dependencyType>
							</typeDescriptor>
						</plugin>
						<plugin name="Custom">
							<description>x</description>
							<conditionFlags><flag name="custom">yes</flag></conditionFlags>
							<typeDescriptor>
								<dependencyType>
									<defaultType name="Optional"/>
									<patterns><pattern><dependencies operator="And"><flagDependency flag="showFollowers" value="On"/></dependencies><type name="Recommended"/></pattern></patterns>
								</dependencyType>
							</typeDescriptor>
						</plugin>
						<plugin name="Base"><description/><typeDescriptor><type name="Required"/></typeDescriptor></plugin>
					</plugins>
				</group>
			</optionalFileGroups>
		</installStep>
	</installSteps>
	<conditionalFileInstalls><patterns><pattern><dependencies operator="And"><flagDependency flag="custom" value="yes"/></dependencies><files><file source="extra.txt" destination="extra.txt"/></files></pattern></patterns></conditionalFileInstalls>
</config>
""";

    [Fact]
    public void Import_recognises_simple_detection_and_keeps_the_rest()
    {
        var p = FomodXml.Read(Sample, "<fomod><Name>DVA</Name><Author>Auctor Lucan</Author><Version>1.0</Version><Id>123</Id></fomod>");
        var opts = p.AllOptions().ToList();
        Assert.Equal(4, opts.Count);
        Assert.Equal(DetectMode.Detect, opts[0].Mode);
        Assert.Equal(new[] { "Inigo.esp" }, opts[0].Plugins);
        Assert.True(opts[0].NotUsableWhenMissing);
        Assert.Equal("Inigo patch\nsecond line", opts[0].Description.Replace("\r", ""));
        Assert.Equal(DetectMode.Detect, opts[1].Mode);
        Assert.False(opts[1].RequireAll);
        Assert.Equal(DetectMode.Advanced, opts[2].Mode);
        Assert.NotNull(opts[2].ConditionFlagsXml);
        Assert.Equal(DetectMode.Required, opts[3].Mode);
        Assert.NotNull(p.Steps[0].VisibleXml);
        Assert.NotNull(p.ConditionalFileInstallsXml);
        Assert.Equal("Right", p.ModuleNameAttributes["position"]);
        Assert.Single(p.Info.ExtraXml);
    }

    [Fact]
    public void Round_trip_is_stable_and_schema_valid()
    {
        var p = FomodXml.Read(Sample, null);
        var xml1 = FomodXml.WriteModuleConfig(p);
        var p2 = FomodXml.Read(xml1, null);
        var xml2 = FomodXml.WriteModuleConfig(p2);
        Assert.Equal(xml1, xml2);
        Assert.Contains("<flagDependency flag=\"showFollowers\" value=\"On\"", xml1);
        Assert.Contains("conditionalFileInstalls", xml1);
        // The detection written for "any of A, B" is Or(Active) + And(Inactive).
        var two = XDocument.Parse(xml1.Substring(xml1.IndexOf("<config", StringComparison.Ordinal))).Descendants("plugin").First(e => (string?)e.Attribute("name") == "Two plugins");
        var ops = two.Descendants("dependencies").Select(d => (string?)d.Attribute("operator")).ToList();
        Assert.Equal(new[] { "Or", "And" }, ops);

        var svc = new FomodService(new Library(Path.Combine(_dir, "lib")), new AppSettings());
        p.RootFolder = _dir;
        var schemaErrors = svc.Validate(p).Where(i => i.Message.StartsWith("Schema")).ToList();
        Assert.Empty(schemaErrors);
    }

    [Fact]
    public void Simulation_follows_detection_and_flags()
    {
        var p = FomodXml.Read(Sample, null);
        p.RootFolder = _dir;
        var svc = new FomodService(new Library(Path.Combine(_dir, "lib")), new AppSettings());

        // The step is only visible with the flag, which nothing sets: hidden.
        var hidden = svc.Simulate(p, new[] { "Inigo.esp" });
        Assert.False(hidden.Steps[0].Visible);

        p.Steps[0].VisibleXml = null;
        var r = svc.Simulate(p, new[] { "Inigo.esp" });
        var g = r.Steps[0].Groups[0].Options;
        Assert.Equal("Recommended", g[0].Type);
        Assert.True(g[0].Selected);
        Assert.Equal("NotUsable", g[1].Type);
        Assert.False(g[1].Selected);
        Assert.Equal("Required", g[3].Type);
    }

    [Fact]
    public void Real_vampira_fomod_round_trips()
    {
        var mc = FindTestData("ModuleConfig.xml");
        if (mc == null) return; // private test data, not in the repository
        var info = FindTestData("info.xml");
        var p = FomodXml.Read(FomodXml.ReadText(mc), info == null ? null : FomodXml.ReadText(info));
        var opts = p.AllOptions().ToList();
        Assert.True(opts.Count > 300);
        Assert.True(opts.Count(o => o.Mode == DetectMode.Detect) > 300);
        var xml = FomodXml.WriteModuleConfig(p);
        var again = FomodXml.Read(xml, null);
        Assert.Equal(opts.Count, again.AllOptions().Count());
        Assert.Equal(opts.Select(o => o.Mode + o.Name + string.Join(",", o.Plugins) + o.Files.Count + o.Description.Replace("\r", "")),
                     again.AllOptions().Select(o => o.Mode + o.Name + string.Join(",", o.Plugins) + o.Files.Count + o.Description.Replace("\r", "")));
        var svc = new FomodService(new Library(Path.Combine(_dir, "lib")), new AppSettings());
        p.RootFolder = _dir;
        Assert.DoesNotContain(svc.Validate(p), i => i.Message.StartsWith("Schema"));
    }

    [Fact]
    public async Task Build_stages_files_json_and_zip()
    {
        var root = Path.Combine(_dir, "root");
        Directory.CreateDirectory(Path.Combine(root, "Vamp - Inigo", "Sound", "DBVO", "vampira"));
        File.WriteAllText(Path.Combine(root, "Vamp - Inigo", "Sound", "DBVO", "vampira", "Hello.fuz"), "x");
        File.WriteAllText(Path.Combine(root, "Vamp - Inigo", "DBVO Fix - Inigo.esp"), "esp");
        var voice = new VoicePack { Name = "Vampira", PackId = "Vampira", Author = "Auctor Lucan" };
        var p = FomodService.NewProject(voice, "DVA");
        p.RootFolder = root;
        p.ReleaseFolder = Path.Combine(_dir, "release");
        p.Info.Version = "1.0";
        var g = FomodService.GroupFor(p, "Followers");
        g.Options.Add(new FomodOption
        {
            Name = "Inigo", Mode = DetectMode.Detect, Plugins = { "Inigo.esp" },
            Files = { new FomodFile { Kind = "folder", Source = @"Vamp - Inigo\Sound", Destination = "Sound" },
                      new FomodFile { Kind = "file", Source = @"Vamp - Inigo\DBVO Fix - Inigo.esp", Destination = "DBVO Fix - Inigo.esp" } },
        });
        var svc = new FomodService(new Library(Path.Combine(_dir, "lib")), new AppSettings());
        Assert.DoesNotContain(svc.Validate(p), i => i.Level == "error");
        var res = await svc.BuildAsync(p, NamingMode.Legacy, true);
        Assert.True(File.Exists(res.Archive));
        var json = Path.Combine(res.Staging, "_common", "DragonbornVoiceOver", "voice_packs", "vampira_voice_pack.json");
        Assert.True(File.Exists(json));
        Assert.Contains("\"voice_pack_id\": \"Vampira\"", File.ReadAllText(json));
        Assert.True(File.Exists(Path.Combine(res.Staging, "Vamp - Inigo", "Sound", "DBVO", "vampira", "Hello.fuz")));
        var mc = FomodXml.ReadText(Path.Combine(res.Staging, "fomod", "ModuleConfig.xml"));
        Assert.Contains(@"source=""_common\DragonbornVoiceOver"" destination=""DragonbornVoiceOver""", mc);
        var sim = svc.Simulate(FomodXml.Read(mc, null) is var built && (built.RootFolder = res.Staging) != null ? built : built, new[] { "Inigo.esp" });
        Assert.Contains(sim.Files, f => f.Destination == "DragonbornVoiceOver" && f.Exists);
    }

    [Fact]
    public async Task Build_works_when_the_release_folder_is_the_fomod_root()
    {
        var root = Path.Combine(_dir, "root2");
        Directory.CreateDirectory(Path.Combine(root, "Vamp - Inigo", "Sound", "DBVO", "vampira"));
        File.WriteAllText(Path.Combine(root, "Vamp - Inigo", "Sound", "DBVO", "vampira", "Hello.fuz"), "x");
        var voice = new VoicePack { Name = "Vampira", PackId = "vampira" };
        var p = FomodService.NewProject(voice, "DVA");
        p.RootFolder = root;
        p.ReleaseFolder = root; // archive and _staging inside the root, as users often set it up
        p.Info.Version = "1.0";
        p.Steps[0].Groups[0].Options[0].Files.Add(new FomodFile { Kind = "folder", Source = "", Destination = "" }); // the whole root: must not swallow _staging
        FomodService.GroupFor(p, "Followers").Options.Add(new FomodOption { Name = "Inigo", Mode = DetectMode.Detect, Plugins = { "Inigo.esp" },
            Files = { new FomodFile { Kind = "folder", Source = @"Vamp - Inigo\Sound", Destination = "Sound" } } });
        var svc = new FomodService(new Library(Path.Combine(_dir, "lib2")), new AppSettings());
        var res = await svc.BuildAsync(p, NamingMode.Legacy, true);
        Assert.True(File.Exists(res.Archive));
        Assert.True(File.Exists(Path.Combine(res.Staging, "Vamp - Inigo", "Sound", "DBVO", "vampira", "Hello.fuz")));
        using var zip = System.IO.Compression.ZipFile.OpenRead(res.Archive!);
        Assert.Contains(zip.Entries, e => e.FullName == "fomod/ModuleConfig.xml");
        Assert.DoesNotContain(zip.Entries, e => e.FullName.Contains("_staging"));
        // building again over an existing archive and staging works
        var again = await svc.BuildAsync(p, NamingMode.Legacy, true);
        Assert.True(File.Exists(again.Archive));
    }

    private static string? FindTestData(string name)
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null)
        {
            var f = Path.Combine(d.FullName, "testdata", "private", name);
            if (File.Exists(f)) return f;
            d = d.Parent;
        }
        return null;
    }
}
