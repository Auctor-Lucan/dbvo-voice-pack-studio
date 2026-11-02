using DbvoStudio.Core.Archives;
using DbvoStudio.Core.Text;
using Xunit;

namespace DbvoStudio.Core.Tests;

/// <summary>BSA file and folder names are stored and hashed in Windows-1252, as Skyrim SE looks them up.</summary>
public class BsaNameTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dbvo-bsanames-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static readonly string[] Names =
    {
        "you’ll_always_have_a_home_here..fuz",
        "i_do….fuz",
        "herluin_lothaire_†_of_the_ragged_flagon.fuz",
        "plain_line..fuz",
    };

    /// <summary>The BSA name hash written out from the Windows-1252 bytes, independent of BsaWriter.</summary>
    private static ulong Expected(string name)
    {
        name = name.ToLowerInvariant();
        int dot = name.LastIndexOf('.');
        var b = Encodings.Cp1252.GetBytes(name[..dot]);
        var e = Encodings.Cp1252.GetBytes(name[dot..]);
        int n = b.Length;
        uint h1 = (uint)(b[n - 1] | (b[n - 2] << 8) | (n << 16) | (b[0] << 24));
        uint h2 = 0; for (int i = 1; i < n - 2; i++) h2 = unchecked(h2 * 0x1003F + b[i]);
        uint h3 = 0; foreach (var c in e) h3 = unchecked(h3 * 0x1003F + c);
        return ((ulong)unchecked(h2 + h3) << 32) | h1;
    }

    [Fact]
    public void Windows_1252_names_are_kept_exactly_and_hashed_from_their_1252_bytes()
    {
        var src = Path.Combine(_dir, "src");
        Directory.CreateDirectory(src);
        var data = new Dictionary<string, byte[]>();
        int k = 0;
        foreach (var n in Names)
        {
            var bytes = new byte[50 + 10 * k++]; new Random(k).NextBytes(bytes);
            File.WriteAllBytes(Path.Combine(src, n), bytes);
            data[n] = bytes;
        }
        var bsaPath = Path.Combine(_dir, "test.bsa");
        BsaWriter.Write(bsaPath, Names.Select(n => new BsaWriter.Entry(@"sound\dbvo\vampira\" + n, Path.Combine(src, n))));

        using var bsa = BsaArchive.Open(bsaPath);
        var raw = File.ReadAllBytes(bsaPath);
        foreach (var n in Names)
        {
            var path = @"sound\dbvo\vampira\" + n;
            Assert.Contains(bsa.Entries, e => e.Path == path); // exact name, no "?"
            Assert.Equal(data[n], bsa.Read(path));
            Assert.Equal(Expected(n), BsaWriter.Hash(n, false));
            // The stored record carries that hash.
            var h = BitConverter.GetBytes(Expected(n));
            Assert.True(IndexOf(raw, h) >= 0, "hash of " + n + " not in the archive");
        }
        Assert.DoesNotContain(bsa.Entries, e => e.Path.Contains('?'));
    }

    [Fact]
    public void A_name_windows_1252_cannot_hold_is_refused_with_its_name()
    {
        var src = Path.Combine(_dir, "src");
        Directory.CreateDirectory(src);
        var bad = "привет_друг..fuz";
        File.WriteAllBytes(Path.Combine(src, bad), new byte[10]);
        File.WriteAllBytes(Path.Combine(src, "ok..fuz"), new byte[10]);
        var bsaPath = Path.Combine(_dir, "bad.bsa");
        var ex = Assert.Throws<InvalidOperationException>(() => BsaWriter.Write(bsaPath, new[]
        {
            new BsaWriter.Entry(@"sound\dbvo\v\" + bad, Path.Combine(src, bad)),
            new BsaWriter.Entry(@"sound\dbvo\v\ok..fuz", Path.Combine(src, "ok..fuz")),
        }));
        Assert.Contains("привет_друг..fuz", ex.Message);
        Assert.Contains("Windows-1252", ex.Message);
        Assert.False(File.Exists(bsaPath));
    }

    private static int IndexOf(byte[] hay, byte[] needle)
    {
        for (int i = 0; i + needle.Length <= hay.Length; i++)
        {
            int j = 0;
            while (j < needle.Length && hay[i + j] == needle[j]) j++;
            if (j == needle.Length) return i;
        }
        return -1;
    }
}
