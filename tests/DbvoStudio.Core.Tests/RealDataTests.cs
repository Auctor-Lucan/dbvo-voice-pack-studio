using DbvoStudio.Core.Archives;
using Xunit.Abstractions;

namespace DbvoStudio.Core.Tests;

/// <summary>
/// Checks against real game files. Runs only where they are present (DBVO_GAMEDATA, default /mnt/user-data/uploads/Data);
/// elsewhere (CI) each test returns early.
/// </summary>
public class RealDataTests
{
    private readonly ITestOutputHelper _out;
    public RealDataTests(ITestOutputHelper o) => _out = o;
    internal static string Data => Environment.GetEnvironmentVariable("DBVO_GAMEDATA") ?? "/mnt/user-data/uploads/Data";
    internal static string? F(string name) { var p = Path.Combine(Data, name); return File.Exists(p) ? p : null; }

    [Fact]
    public void Bsa_lists_and_extracts_creation_club_archive()
    {
        if (F("ccBGSSSE001-Fish.bsa") is not { } path) return;
        using var bsa = BsaArchive.Open(path);
        Assert.Equal(105, bsa.Version);
        Assert.True(bsa.Entries.Count > 500);
        var compressed = bsa.Entries.FirstOrDefault(e => e.Compressed);
        if (compressed != null) Assert.NotEmpty(bsa.Read(compressed));
        Assert.NotEmpty(bsa.Read(bsa.Entries.First(e => e.Path.EndsWith(".pex"))));
    }

    [Fact]
    public void Bsa_reads_aela_revoiced_voice_archive()
    {
        var path = "/mnt/user-data/uploads/Aela Revoiced/Aela Revoiced.bsa";
        if (!File.Exists(path)) return;
        using var bsa = BsaArchive.Open(path);
        _out.WriteLine($"v{bsa.Version} flags {bsa.ArchiveFlags:X} files {bsa.Entries.Count}");
        foreach (var g in bsa.Entries.GroupBy(e => string.Join("\\", e.Path.Split((char)92).Take(4)))) _out.WriteLine(g.Key + " " + g.Count());
        foreach (var e in bsa.Entries.Where(e => e.Path.Contains("hearthfires.esm") && e.Path.EndsWith(".fuz"))) _out.WriteLine("HF " + e.Path);
        var fuz = bsa.Entries.First(e => e.Path.EndsWith(".fuz", StringComparison.OrdinalIgnoreCase));
        _out.WriteLine(fuz.Path + (fuz.Compressed ? " compressed" : ""));
        Assert.Equal("FUZE", System.Text.Encoding.ASCII.GetString(bsa.Read(fuz), 0, 4));
    }

    [Fact]
    public void Bsa_extracts_vanilla_strings()
    {
        if (F("Skyrim - Interface.bsa") is not { } path) return;
        using var bsa = BsaArchive.Open(path);
        var il = bsa.Find(@"strings\skyrim_english.ilstrings");
        Assert.NotNull(il);
        var data = bsa.Read(il!);
        _out.WriteLine($"ilstrings {data.Length} bytes, compressed={il!.Compressed}");
        Assert.True(data.Length > 1_000_000);
    }
}
