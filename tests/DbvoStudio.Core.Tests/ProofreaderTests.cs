using DbvoStudio.Core.Text;
using Xunit;

namespace DbvoStudio.Core.Tests;

public class ProofreaderTests
{
    [Fact]
    public void Markup_is_protected_and_restored()
    {
        var (t, tokens) = Proofreader.Protect("Dont worry <Alias=Player>, I'll pay. (<bribeCost> gold)");
        Assert.Equal("Dont worry ⟦1⟧, I'll pay. ⟦2⟧", t);
        Assert.Equal(new[] { "<Alias=Player>", "(<bribeCost> gold)" }, tokens);
        Assert.Equal("Don't worry <Alias=Player>, I'll pay. (<bribeCost> gold)", Proofreader.Restore("Don't worry ⟦1⟧, I'll pay. ⟦2⟧", tokens));
    }

    [Theory]
    [InlineData("Don't worry, I'll pay. ⟦2⟧")] // lost one
    [InlineData("⟦2⟧ Don't worry ⟦1⟧")] // out of order
    [InlineData("⟦1⟧ ⟦1⟧ ⟦2⟧")] // doubled
    public void Broken_placeholders_drop_the_suggestion(string reply)
    {
        var (_, tokens) = Proofreader.Protect("Dont worry <Alias=Player>, I'll pay. (Persuade)");
        Assert.Null(Proofreader.Restore(reply, tokens));
    }

    [Fact]
    public void Reply_is_parsed_per_line()
    {
        var (_, sent) = Proofreader.BuildBatch(new List<(string, string)>
        {
            ("A", "Will killing some bandits will make you feel better?"),
            ("B", "Hello there."),
            ("C", "Dont go <Alias=Player>."),
        });
        var reply = """
            ```json
            [{"i":0,"t":"Will killing some bandits make you feel better?","kind":"Grammar","why":"doubled will"},
             {"i":2,"t":"Don't go ⟦1⟧.","kind":"spelling","why":"apostrophe"},
             {"i":1,"t":"Hello there.","kind":"style","why":"same"},
             {"i":9,"t":"x"}]
            ```
            """;
        var r = Proofreader.ParseReply(reply, sent);
        Assert.Equal(3, r.Count);
        Assert.Equal("Will killing some bandits make you feel better?", r[0].Text);
        Assert.Equal("grammar", r[0].Kind);
        Assert.Null(r[1].Text); // unchanged
        Assert.Equal("Don't go <Alias=Player>.", r[2].Text);
    }

    [Fact]
    public void Reasoning_and_prose_around_the_list_are_passed_over()
    {
        var (_, sent) = Proofreader.BuildBatch(new List<(string, string)> { ("A", "Dont go."), ("B", "Fine.") });
        var reply = "<think>Line [0] has a typo; line [1] is fine.</think>Here you go [as asked]:\n[{\"i\":0,\"t\":\"Don't go.\",\"kind\":\"spelling\"}] Hope [this] helps.";
        var r = Proofreader.ParseReply(reply, sent);
        Assert.Equal("Don't go.", r[0].Text);
        Assert.Null(r[1].Text);
    }

    [Fact]
    public void Nested_parentheses_are_one_locked_piece()
    {
        var (t, tokens) = Proofreader.Protect("(Intimidate (Level 50)) Move.");
        Assert.Equal("⟦1⟧ Move.", t);
        Assert.Single(tokens);
    }

    [Fact]
    public void Unreadable_reply_throws()
    {
        var (_, sent) = Proofreader.BuildBatch(new List<(string, string)> { ("A", "Hi.") });
        Assert.Throws<FormatException>(() => Proofreader.ParseReply("Sorry, I can't help.", sent));
    }
}
