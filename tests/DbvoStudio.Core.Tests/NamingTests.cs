using System.Text;
using DbvoStudio.Core.Naming;

namespace DbvoStudio.Core.Tests;

public class NamingTests
{
    [Theory]
    [InlineData("", "EF46DB3751D8E999")]
    [InlineData("abc", "44BC2CF5AD770999")]
    [InlineData("Nobody inspects the spammish repetition", "FBCEA83C8A378BF1")]
    public void XxHash64_matches_reference_vectors(string input, string expected)
    {
        Assert.Equal(expected, XxHash64.Hash(Encoding.UTF8.GetBytes(input)).ToString("X16"));
    }

    // Values produced by running DBVO Voice Pack Maker 2.7.0's own hashing code.
    [Theory]
    [InlineData("Can you build a portal?", "69094009ED459E01")]
    [InlineData(">Can you build a portal?", "E3A577641687F91B")]
    [InlineData("Enough! I'm gonna teach you some manners. (Attack)", "5FE2652AE77DA09D")]
    [InlineData("(M) Can we talk about our life together?", "E5D95AE43C49372F")]
    [InlineData("Je n'ai pas d'été… ünïcødé", "3920E840CE5C3038")]
    [InlineData("  Can you build a portal?  ", "69094009ED459E01")]
    public void Dbvo2_names_match_2_7_0(string line, string expected)
    {
        Assert.Equal(expected, LineNaming.Dbvo2(line));
    }

    [Theory]
    [InlineData("(M) Can we talk about our life together?", "(M)_Can_we_talk_about_our_life_together_")]
    [InlineData(">Can you build a portal?", "_Can_you_build_a_portal_")]
    [InlineData("I don't know if I trust any of this. (Whisper)", "I_don't_know_if_I_trust_any_of_this.")]
    [InlineData("I have slain  Alias=DeadFriend  to gain your favor.", "I_have_slain__Alias=DeadFriend__to_gain_your_favor.")]
    [InlineData("[AFT] Follow me, I need your help...", "[AFT]_Follow_me,_I_need_your_help...")]
    [InlineData("1000 gold!  But I sold it to you for 50 gold.", "1000_gold!__But_I_sold_it_to_you_for_50_gold.")]
    [InlineData("You're the woman with the wagon! Transporting her mother…", "You're_the_woman_with_the_wagon!_Transporting_her_mother…")]
    public void Legacy_names_match_existing_packs(string line, string expected)
    {
        Assert.Equal(expected, LineNaming.Legacy(line));
    }

    [Fact]
    public void Legacy_keys_ignore_case()
    {
        Assert.Equal(LineNaming.Key("Follow me.", NamingMode.Legacy), LineNaming.Key("Follow Me.", NamingMode.Legacy));
        Assert.NotEqual(LineNaming.Key("Follow me.", NamingMode.Dbvo2Hash), LineNaming.Key("Follow Me.", NamingMode.Dbvo2Hash));
    }

    [Theory]
    [InlineData("(M) Can we talk about our life together?", "Can we talk about our life together?")]
    [InlineData("[AFT] Wait here", "Wait here")]
    [InlineData("I don't know if I trust any of this. (Whisper)", "I don't know if I trust any of this.")]
    [InlineData("[M Sex] I want to be close to you.", "I want to be close to you.")]
    public void Default_spoken_text_drops_markers(string finalLine, string expected)
    {
        Assert.Equal(expected, SpokenText.DefaultFor(finalLine));
    }

    [Fact]
    public void Tags_are_stripped_for_lip_text()
    {
        Assert.Equal("I never knew it could be this way.", SpokenText.StripTags("[whispers] I never knew it could be this way [sighs]."));
    }

    [Fact]
    public void Prefix_tags_are_added_once()
    {
        var t = SpokenText.AddPrefixTags("[sad] Hello there.", new[] { "sad", "[tired]" });
        Assert.Equal("[tired] [sad] Hello there.", t);
    }

    [Fact]
    public void Word_check_ignores_tags_and_punctuation()
    {
        Assert.Equal(SpokenText.WordsOnly("Wait. We should talk."), SpokenText.WordsOnly("[nervous] Wait... we should talk!"));
        Assert.NotEqual(SpokenText.WordsOnly("Wait. We should talk."), SpokenText.WordsOnly("Wait. We must talk."));
    }
}

public class TopicFileWriteTests
{
    [Fact]
    public void Topic_files_have_no_empty_row_at_the_end()
    {
        var path = Path.Combine(Path.GetTempPath(), "dbvo-topic-" + Guid.NewGuid().ToString("N")[..6] + ".txt");
        DbvoStudio.Core.Topics.TopicFile.Write(path, new[] { "Hello, I'm <Alias=Player>.", "The name's <Alias=Player>." });
        Assert.Equal("Hello, I'm <Alias=Player>.\r\nThe name's <Alias=Player>.", File.ReadAllText(path));
        Assert.Equal(2, DbvoStudio.Core.Topics.TopicFile.Read(path).Count);
    }
}

public class TagPlacementTests
{
    [Theory]
    [InlineData("[angry] I will kill you.", false, "[angry] [whispers] I will kill you.")]
    [InlineData("[angry] I will kill you.", true, "[whispers] I will kill you.")]
    [InlineData("I will kill you.", false, "[whispers] I will kill you.")]
    [InlineData("[whispers] I will kill you.", false, "[whispers] I will kill you.")]
    [InlineData("[angry][sad] Go.", true, "[whispers] Go.")]
    public void Tags_go_after_or_instead_of_the_leading_tags(string text, bool replace, string expected)
        => Assert.Equal(expected, DbvoStudio.Core.Naming.SpokenText.AddTagsAfterLeading(text, new[] { "[whispers]" }, replace));
}

public class PlayerNameDefaultTests
{
    [Fact]
    public void Player_name_lines_are_voiced_only_when_the_setting_is_on()
    {
        var l = new DbvoStudio.Core.Projects.Phase1Line { Original = "I'm <Alias=Player>.", Final = "I'm <Alias=Player>." };
        try
        {
            DbvoStudio.Core.Projects.Phase1Line.PlayerNamesVoicedByDefault = false;
            Assert.False(l.DefaultVoice);
            l.VoiceApproved = true;
            Assert.True(l.Voiced);
            l.VoiceApproved = null;
            DbvoStudio.Core.Projects.Phase1Line.PlayerNamesVoicedByDefault = true;
            Assert.True(l.DefaultVoice);
            var other = new DbvoStudio.Core.Projects.Phase1Line { Original = "Ask <Alias=Friend>.", Final = "Ask <Alias=Friend>." };
            Assert.False(other.DefaultVoice);
        }
        finally { DbvoStudio.Core.Projects.Phase1Line.PlayerNamesVoicedByDefault = false; }
    }
}
