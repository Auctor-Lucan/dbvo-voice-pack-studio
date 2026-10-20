using DbvoStudio.Core.Naming;
using DbvoStudio.Core.Projects;

namespace DbvoStudio.Core.Tests;

public class VariantTests
{
    private static Phase2Line Line(params bool[] kept)
    {
        var l = new Phase2Line { FinalLine = "Hello there.", FileStem = "abc" };
        for (int i = 0; i < kept.Length; i++) l.Takes.Add(new Take { Id = "t" + i, Slot = i + 1, Kept = kept[i] });
        l.SelectedTakeId = l.Takes.LastOrDefault()?.Id;
        return l;
    }

    [Fact]
    public void No_kept_takes_export_the_selected_take()
    {
        var r = Phase2Service.ExportTakes(Line(false, false), NamingMode.Dbvo2Hash);
        Assert.Single(r);
        Assert.Equal(("t1", "abc"), (r[0].Take.Id, r[0].Stem));
    }

    [Fact]
    public void Two_kept_takes_become_numbered_variants()
    {
        var r = Phase2Service.ExportTakes(Line(true, false, true), NamingMode.Dbvo2Hash);
        Assert.Equal(new[] { ("t0", Path.Combine("abc", "0")), ("t2", Path.Combine("abc", "1")) }, r.Select(x => (x.Take.Id, x.Stem)));
    }

    [Fact]
    public void Legacy_naming_exports_one_take()
    {
        var r = Phase2Service.ExportTakes(Line(true, true, false), NamingMode.Legacy);
        Assert.Single(r);
        Assert.Equal("t2", r[0].Take.Id);
    }
}
