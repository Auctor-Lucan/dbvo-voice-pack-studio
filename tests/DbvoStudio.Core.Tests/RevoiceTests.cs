using DbvoStudio.Core.Plugins;
using DbvoStudio.Core.Revoice;

namespace DbvoStudio.Core.Tests;

public class RevoiceTests
{
    [Theory]
    [InlineData("DLC1_BF_DunTempleQST", "DLC1_BF_DunTempleTopic", 0x00006BC3u, 1, "DLC1_BF_Du_DLC1_BF_DunTemp_00006BC3_1")]
    [InlineData("DLC1_BF_DunTempleQST", "", 0x00006BC1u, 1, "DLC1_BF_DunTempleQST__00006BC1_1")]
    [InlineData("DLC1VQ07", "DLC1VQ07AlertIdle", 0x02016329u, 1, "DLC1VQ07_DLC1VQ07AlertIdle_00016329_1")]
    [InlineData("zzzCHGeneral", "zzzCHgdHoundB01", 0x051279A0u, 1, "zzzCHGener_zzzCHgdHoundB01_001279A0_1")]
    [InlineData("Quest", "AVeryLongTopicEditorIdThatGoesOn", 0x800u, 2, "Quest_AVeryLongTopicEditor_00000800_2")]
    public void Voice_file_names_follow_the_engine_rule(string quest, string topic, uint id, int resp, string expected) =>
        Assert.Equal(expected, DialogueIndex.FileName(quest, topic, id, resp));

    [Fact]
    public void Voice_type_plugin_round_trips()
    {
        var bytes = VoiceTypePluginWriter.Build(new List<(uint, string, byte)> { (0x800, "_TestVoice", 3), (0x801, "_OtherVoice", 1) }, "test");
        var path = Path.Combine(Path.GetTempPath(), "vt-" + Guid.NewGuid().ToString("N")[..6] + ".esp");
        File.WriteAllBytes(path, bytes);
        try
        {
            var f = PluginFile.Read(path, DialogueIndex.Signatures);
            Assert.True(f.IsLight);
            Assert.Empty(f.Masters);
            var idx = DialogueIndex.Build(new[] { f });
            var vt = idx.VoiceTypes[new FormKey(Path.GetFileName(path).ToLowerInvariant(), 0x800)];
            Assert.Equal("_TestVoice", vt.EditorId);
            Assert.True(vt.Female);
            Assert.Equal(2, idx.VoiceTypes.Count);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Similarity_ignores_case_punctuation_and_stage_directions()
    {
        Assert.Equal(1.0, RevoiceService.Similarity("Stendarr's mercy upon you, friend.", "stendarr's mercy upon you friend"));
        Assert.Equal(1.0, RevoiceService.Similarity("(Laughs) You fool!", "You fool"));
        Assert.True(RevoiceService.Similarity("We cannot rest while Daedra walk among us.", "We can't rest while daedra walk among us") > 0.7);
        Assert.True(RevoiceService.Similarity("Molag Bal will not have this soul!", "This hall was a temple once.") < 0.3);
    }
}
