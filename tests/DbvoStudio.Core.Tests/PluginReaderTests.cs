using System.Text;
using DbvoStudio.Core.Plugins;

namespace DbvoStudio.Core.Tests;

/// <summary>12.0.0: the reader skips top-level groups it doesn't need, but still finds records nested in other groups.</summary>
public class PluginReaderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dbvo-reader-" + Guid.NewGuid().ToString("N")[..8]);

    public PluginReaderTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static byte[] Record(string sig, uint formId, byte[] data)
    {
        var ms = new MemoryStream();
        ms.Write(Encoding.ASCII.GetBytes(sig));
        ms.Write(BitConverter.GetBytes((uint)data.Length));
        ms.Write(BitConverter.GetBytes(0u));
        ms.Write(BitConverter.GetBytes(formId));
        ms.Write(new byte[8]);
        ms.Write(data);
        return ms.ToArray();
    }

    private static byte[] Group(byte[] label, uint type, params byte[][] contents)
    {
        var body = contents.SelectMany(c => c).ToArray();
        var ms = new MemoryStream();
        ms.Write(Encoding.ASCII.GetBytes("GRUP"));
        ms.Write(BitConverter.GetBytes((uint)(24 + body.Length)));
        ms.Write(label);
        ms.Write(BitConverter.GetBytes(type));
        ms.Write(new byte[8]);
        ms.Write(body);
        return ms.ToArray();
    }

    private static byte[] Edid(string s)
    {
        var ms = new MemoryStream();
        Subrecord.Write(ms, new[] { new Subrecord("EDID", Encoding.ASCII.GetBytes(s + "\0")) });
        return ms.ToArray();
    }

    /// <summary>A dialogue plugin plus a WEAP group and a CELL group holding a placed actor (ACHR) two levels down.</summary>
    private string Write()
    {
        var b = new TestPluginBuilder();
        b.AddTopic(0x01000800, "MyTopic", "Hello there.", (0x01000801, null));
        var dialogue = b.Build();
        var weap = Group(Encoding.ASCII.GetBytes("WEAP"), 0, Record("WEAP", 0x01000900, Edid("MySword")));
        var cellRec = Record("CELL", 0x01000A00, Edid("MyCell"));
        var temp = Group(BitConverter.GetBytes(0x01000A00u), 9, Record("ACHR", 0x01000A01, Edid("MyActorRef")));
        var children = Group(BitConverter.GetBytes(0x01000A00u), 6, temp);
        var cell = Group(Encoding.ASCII.GetBytes("CELL"), 0,
            Group(BitConverter.GetBytes(0u), 2, Group(BitConverter.GetBytes(0u), 3, cellRec, children)));
        var path = Path.Combine(_dir, "Mixed.esp");
        File.WriteAllBytes(path, dialogue.Concat(weap).Concat(cell).ToArray());
        return path;
    }

    [Fact]
    public void Default_read_still_returns_the_dialogue()
    {
        var pf = PluginFile.Read(Write());
        Assert.Equal(new[] { "DIAL", "INFO" }, pf.Records.Select(r => r.Signature));
        Assert.Equal(0x01000800u, pf.Records[1].ParentTopic);
        Assert.Equal(new[] { 0, 1 }, pf.Records.Select(r => r.Index));
    }

    [Fact]
    public void Placed_actors_inside_cells_are_found()
    {
        var pf = PluginFile.Read(Write(), new HashSet<string> { "ACHR", "WEAP" });
        Assert.Equal(new[] { ("WEAP", 0x01000900u), ("ACHR", 0x01000A01u) }, pf.Records.Select(r => (r.Signature, r.FormId)));
    }

    [Fact]
    public void An_empty_set_reads_only_the_header()
    {
        var pf = PluginFile.Read(Write(), new HashSet<string>());
        Assert.Empty(pf.Records);
        Assert.Contains("Skyrim.esm", pf.Masters);
    }

    [Fact]
    public void Safe_write_replaces_the_plugin_and_leaves_no_part_file()
    {
        var path = Path.Combine(_dir, "DBVO Fix - Test.esp");
        File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
        FixPluginWriter.WriteSafely(path, new byte[] { 9, 9 });
        Assert.Equal(new byte[] { 9, 9 }, File.ReadAllBytes(path));
        Assert.Single(Directory.GetFiles(_dir));
    }

    [Fact]
    public void A_damaged_tail_in_a_group_no_task_reads_is_passed_over()
    {
        // the dialogue first, then a WEAP group that claims more bytes than the file has
        var b = new TestPluginBuilder();
        b.AddTopic(0x01000800, "MyTopic", "Hello there.", (0x01000801, null));
        var broken = Group(Encoding.ASCII.GetBytes("WEAP"), 0, Record("WEAP", 0x01000900, Edid("MySword")));
        BitConverter.GetBytes((uint)(broken.Length + 500)).CopyTo(broken, 4);
        var path = Path.Combine(_dir, "Tail.esp");
        File.WriteAllBytes(path, b.Build().Concat(broken).ToArray());
        var pf = PluginFile.Read(path);
        Assert.Equal(new[] { "DIAL", "INFO" }, pf.Records.Select(r => r.Signature));
        // in a part a task does read, it is still an error
        Assert.Throws<InvalidDataException>(() => PluginFile.Read(path, new HashSet<string> { "WEAP" }));
    }
}
