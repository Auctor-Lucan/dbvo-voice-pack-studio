using DbvoStudio.Core.Rules;
using Xunit;

namespace DbvoStudio.Core.Tests;

public class TofuTests
{
    [Theory]
    [InlineData("I’m here—now…", "I'm here--now...")]
    [InlineData("“Hello,” she said – softly.", "\"Hello,\" she said - softly.")]
    [InlineData("‘Tis the season.", "'Tis the season.")]
    public void Lines_with_tofu_characters_are_fixed_the_way_the_xEdit_script_fixes_them(string line, string expected)
    {
        var r = ScriptRules.Classify(line, null);
        Assert.Equal(LineKind.Fixed, r.Kind);
        Assert.Equal(expected, r.Final);
    }
}
