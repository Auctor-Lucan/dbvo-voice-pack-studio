using DbvoStudio.Core.Rules;

namespace DbvoStudio.Core.Tests;

/// <summary>Cases taken from the xEdit script's real output on Inigo.esp, Lucien.esp and Skyrim.esm.</summary>
public class ScriptRulesTests
{
    [Theory]
    [InlineData("What is your homeland like? ", "What is your homeland like?")]
    [InlineData(" Why don't you just tell me now?", "Why don't you just tell me now?")]
    [InlineData("(Whisper) I don't know if I trust any of this.", "I don't know if I trust any of this. (Whisper)")]
    [InlineData("(Yawn) What?! Sorry, I drifted off.", "What?! Sorry, I drifted off. (Yawn)")]
    [InlineData("...And time is merely our method of transport!", "And time is merely our method of transport!")]
    public void Fixes_match_xedit(string input, string expected)
    {
        var r = ScriptRules.Classify(input, "SomeEditorId");
        Assert.Equal(LineKind.Fixed, r.Kind);
        Assert.Equal(expected, r.Final);
    }

    [Theory]
    [InlineData("Can you build a portal?")]
    [InlineData("Enough! I'm gonna teach you some manners. (Attack)")]
    [InlineData("Windhelm. (<Global=CarriageCost> gold)")]
    [InlineData("I presume this belongs to you? (You show the <Alias=Loot>)")]
    public void Valid_lines_pass_unchanged(string input)
    {
        var r = ScriptRules.Classify(input, null);
        Assert.Equal(LineKind.Valid, r.Kind);
        Assert.Equal(input, r.Final);
    }

    [Theory]
    [InlineData("I'm <Alias=Player>, by the way.")]
    [InlineData("<Alias=RiverwoodFriend> sent me. Riverwood is in danger.")]
    [InlineData("Hand it over *now*.")]
    public void Alias_and_star_lines_are_broken(string input)
    {
        Assert.Equal(LineKind.Broken, ScriptRules.Classify(input, null).Kind);
    }

    [Fact]
    public void Text_equal_to_editor_id_is_skipped()
    {
        var r = ScriptRules.Classify("JRLucienReading36LessonsOfVivecSermon03", "JRLucienReading36LessonsOfVivecSermon03");
        Assert.Equal(LineKind.SkippedEditorId, r.Kind);
    }

    [Fact]
    public void Over_150_characters_is_too_long()
    {
        var s = new string('a', 120) + " and then some more words to go past the limit.";
        Assert.True(s.Length > 150);
        Assert.Equal(LineKind.TooLong, ScriptRules.Classify(s, null).Kind);
    }

    [Theory]
    [InlineData("It’s fine…", "It's fine...")]
    [InlineData("Wait; listen.", "Wait, listen.")]
    [InlineData("“Honor” is a coward's tool.", "\"Honor\" is a coward's tool.")]
    public void Typography_is_normalized(string input, string expected)
    {
        var r = ScriptRules.Classify(input, null);
        Assert.Equal(LineKind.Fixed, r.Kind);
        Assert.Equal(expected, r.Final);
    }

    [Theory]
    [InlineData("")]
    [InlineData("...")]
    [InlineData("(Attack)")]
    public void Nothing_to_voice_is_empty(string input)
    {
        Assert.Equal(LineKind.Empty, ScriptRules.Classify(input, null).Kind);
    }
}
