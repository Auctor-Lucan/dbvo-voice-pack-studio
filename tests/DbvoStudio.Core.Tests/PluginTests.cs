using DbvoStudio.Core.Plugins;
using DbvoStudio.Core.Rules;

namespace DbvoStudio.Core.Tests;

public class PluginTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dbvo-tests-" + Guid.NewGuid().ToString("N")[..8]);

    public PluginTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string Save(TestPluginBuilder b, string name)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, b.Build());
        return path;
    }

    private static TestPluginBuilder Sample()
    {
        var b = new TestPluginBuilder();
        b.Masters.Add("Update.esm");
        b.AddTopic(0x01000800, "MyTopicA", "What is your homeland like? ", (0x01000801, null));
        b.AddTopic(0x01000802, "MyTopicB", "Can you build a portal?", (0x01000803, null));
        // Every response has its own prompt: topic text is hidden in game.
        var t = b.AddTopic(0x01000804, "MyTopicC", "Hidden topic text", (0x01000805, "(Whisper) Tell me more."), (0x01000806, "I'm <Alias=Player>."));
        t.Infos[0].Compressed = true;
        b.AddTopic(0x01000807, "MyTopicD", "MyTopicD", (0x01000808, null));
        var forced = b.AddTopic(0x01000809, "Greeting", "Not a player line");
        forced.Subtype = "FRCE";
        return b;
    }

    [Fact]
    public void Reads_masters_and_records()
    {
        var p = PluginFile.Read(Save(Sample(), "Sample.esp"));
        Assert.Equal(new[] { "Skyrim.esm", "Update.esm" }, p.Masters);
        Assert.Equal(5, p.Records.Count(r => r.Signature == "DIAL"));
        Assert.Equal(6, p.Records.Count(r => r.Signature == "INFO"));
        var info = p.Records.Single(r => r.FormId == 0x01000805);
        Assert.Equal(0x01000804u, info.ParentTopic);
        Assert.Equal("(Whisper) Tell me more.", p.DecodeLString(info.Get("RNAM")!.Data));
    }

    [Fact]
    public void Extracts_like_the_xedit_script()
    {
        var p = PluginFile.Read(Save(Sample(), "Sample.esp"));
        var r = PlayerLineExtractor.Extract(p);

        var a = r.Lines.Single(l => l.FormId == 0x01000800);
        Assert.Equal(LineKind.Fixed, a.Kind);
        Assert.Equal("What is your homeland like?", a.Suggested);

        Assert.Equal(LineKind.Valid, r.Lines.Single(l => l.FormId == 0x01000802).Kind);

        var hidden = r.Lines.Single(l => l.FormId == 0x01000804);
        Assert.True(hidden.HiddenByPrompts);

        var prompt = r.Lines.Single(l => l.FormId == 0x01000805);
        Assert.Equal(LineField.Prompt, prompt.Field);
        Assert.Equal("Tell me more. (Whisper)", prompt.Suggested);

        Assert.Equal(LineKind.Broken, r.Lines.Single(l => l.FormId == 0x01000806).Kind);
        Assert.Equal(LineKind.SkippedEditorId, r.Lines.Single(l => l.FormId == 0x01000807).Kind);
        Assert.DoesNotContain(r.Lines, l => l.FormId == 0x01000809);

        Assert.Equal(new[] { "What is your homeland like?", "Can you build a portal?", "Tell me more. (Whisper)" }, r.TopicLines());
        Assert.True(r.NeedsFixPlugin);
    }

    [Fact]
    public void Fix_plugin_is_esl_override_only_and_round_trips()
    {
        var src = PluginFile.Read(Save(Sample(), "Sample.esp"));
        var edits = new List<LineEdit>
        {
            new(0x01000800, LineField.TopicText, "What is your homeland like?"),
            new(0x01000805, LineField.Prompt, "Tell me more. (Whisper)"),
        };
        var outPath = Path.Combine(_dir, FixPluginWriter.FixFileName("Sample.esp"));
        FixPluginWriter.Write(src, edits, outPath);

        Assert.Equal("DBVO Fix - Sample.esp", Path.GetFileName(outPath));
        var fix = PluginFile.Read(outPath);
        Assert.True(fix.IsLight);
        Assert.False(fix.IsLocalized);
        Assert.Equal(new[] { "Skyrim.esm", "Update.esm", "Sample.esp" }, fix.Masters);

        // Edited topic, plus the parent topic of the edited INFO; only the edited INFO.
        Assert.Equal(new uint[] { 0x01000800, 0x01000804 }, fix.Records.Where(r => r.Signature == "DIAL").Select(r => r.FormId));
        var info = Assert.Single(fix.Records, r => r.Signature == "INFO");
        Assert.Equal(0x01000805u, info.FormId);
        Assert.Equal(0x01000804u, info.ParentTopic);
        Assert.Equal("Tell me more. (Whisper)", fix.DecodeLString(info.Get("RNAM")!.Data));

        var dial = fix.Records.First(r => r.FormId == 0x01000800);
        Assert.Equal("What is your homeland like?", fix.DecodeLString(dial.Get("FULL")!.Data));
        // Untouched subrecords copied byte for byte.
        var srcDial = src.Records.First(r => r.FormId == 0x01000800);
        Assert.Equal(srcDial.Get("QNAM")!.Data, dial.Get("QNAM")!.Data);
        Assert.Equal(srcDial.Get("SNAM")!.Data, dial.Get("SNAM")!.Data);

        // HEDR: 1.71, record count = 2 DIAL + 1 INFO + 2 groups, next object ID 0x800.
        var hedr = PluginFileHeader(outPath);
        Assert.Equal(1.71f, BitConverter.ToSingle(hedr, 0));
        Assert.Equal(5u, BitConverter.ToUInt32(hedr, 4));
        Assert.Equal(0x800u, BitConverter.ToUInt32(hedr, 8));
    }

    [Fact]
    public void Localized_plugins_read_strings_and_patch_inlines_them()
    {
        var b = Sample();
        b.Localized = true;
        var path = Save(b, "Loc.esm");
        // Build() assigns string IDs, so strings are written after it.
        b.WriteStrings(Path.Combine(_dir, "Strings"), "Loc");

        var p = PluginFile.Read(path);
        Assert.True(p.IsLocalized);
        Assert.True(p.AttachStrings(Path.Combine(_dir, "Strings")));
        var r = PlayerLineExtractor.Extract(p);
        Assert.Equal(0, r.UnresolvedStrings);
        Assert.Equal("Can you build a portal?", r.Lines.Single(l => l.FormId == 0x01000802).Original);

        var fix = PluginFile.Read(WriteFix(p, new LineEdit(0x01000805, LineField.Prompt, "Tell me more. (Whisper)")));
        var info = fix.Records.Single(x => x.Signature == "INFO");
        Assert.Equal("Response text.", fix.DecodeLString(info.Get("NAM1")!.Data));
        Assert.Equal("Hidden topic text", fix.DecodeLString(fix.Records.Single(x => x.Signature == "DIAL").Get("FULL")!.Data));
    }

    [Fact]
    public void Localized_plugin_without_strings_reports_unresolved()
    {
        var b = Sample();
        b.Localized = true;
        var p = PluginFile.Read(Save(b, "NoStrings.esp"));
        Assert.False(p.AttachStrings(Path.Combine(_dir, "nowhere")));
        var r = PlayerLineExtractor.Extract(p);
        Assert.True(r.UnresolvedStrings > 0);
    }

    private string WriteFix(PluginFile p, params LineEdit[] edits)
    {
        var path = Path.Combine(_dir, "fix-" + Guid.NewGuid().ToString("N")[..6] + ".esp");
        FixPluginWriter.Write(p, edits, path);
        return path;
    }

    private static byte[] PluginFileHeader(string path)
    {
        var b = File.ReadAllBytes(path);
        uint size = BitConverter.ToUInt32(b, 4);
        var subs = Subrecord.Parse(b.AsSpan(24, (int)size));
        return subs.First(s => s.Type == "HEDR").Data;
    }
}
