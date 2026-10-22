using DbvoStudio.Core.Projects;

namespace DbvoStudio.Core.Tests;

public class Fixes74Tests
{
    private static Phase2Line Line(string id, string spoken, string? takeText, Phase2Status status)
    {
        var l = new Phase2Line { LineId = id, Spoken = spoken, Status = status };
        if (takeText != null) { l.Takes.Add(new Take { Id = id + "t", SpokenText = takeText }); l.SelectedTakeId = id + "t"; }
        return l;
    }

    [Fact]
    public void A_line_with_a_take_is_never_missing_edited_lines_have_their_own_choice()
    {
        var patch = new DbvoPatch();
        patch.Lines.Add(Line("none", "Hello.", null, Phase2Status.Pending));
        patch.Lines.Add(Line("failedNoTake", "Hi.", null, Phase2Status.Failed));
        patch.Lines.Add(Line("current", "Yes.", "Yes.", Phase2Status.Generated));
        patch.Lines.Add(Line("tagged", "[sighs] No.", "No.", Phase2Status.Stale));
        patch.Lines.Add(Line("failedWithTake", "Maybe.", "Maybe.", Phase2Status.Failed));

        var missing = Phase2Service.Eligible(patch, false).Select(l => l.LineId).ToList();
        var edited = Phase2Service.Eligible(patch, false, null, edited: true).Select(l => l.LineId).ToList();

        Assert.Equal(new[] { "none", "failedNoTake" }, missing);
        Assert.Equal(new[] { "tagged", "failedWithTake" }, edited);
        Assert.Equal(5, Phase2Service.Eligible(patch, true).Count);
    }
}
