using DbvoStudio.Core.Fomod;
using DbvoStudio.Core.Projects;
using Xunit;

namespace DbvoStudio.Core.Tests;

public class OptionDescriptionTests
{
    [Fact]
    public void Name_lines_fix_notice_page_and_version()
    {
        var m = new Mod { Name = "SexLab Eager NPCs SE", NexusUrl = " https://www.loverslab.com/files/file/5266-x/ ", Version = "1.7.3" };
        Assert.Equal("SexLab Eager NPCs SE\r\n\r\n1,234 lines\r\nℹ️ Contains formatting fix ESPFE patch. Let the plugin overwrite the original mod.\r\n\r\n⭐ https://www.loverslab.com/files/file/5266-x/\r\nv 1.7.3",
            FomodService.OptionDescription(m, 1234, true));
    }

    [Fact]
    public void No_fix_no_page_one_line()
    {
        Assert.Equal("A\r\n\r\n1 line", FomodService.OptionDescription(new Mod { Name = "A" }, 1, false));
        Assert.Equal("A\r\n\r\n2 lines\r\n\r\nv2.0", FomodService.OptionDescription(new Mod { Name = "A", Version = "v2.0" }, 2, false));
    }
}
