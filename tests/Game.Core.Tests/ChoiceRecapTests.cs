using Game.Core.Story;

namespace Game.Core.Tests;

public class ChoiceRecapTests
{
    [Fact]
    public void Each_effect_reads_as_which_way_and_how_much()
    {
        Assert.Equal("Kai warmed to you", ChoiceRecap.Describe(new ChoiceEffect("Kai", 5, 1)));
        Assert.Equal("Kai liked that", ChoiceRecap.Describe(new ChoiceEffect("Kai", 2, 0)));
        Assert.Equal("Kai cooled towards you", ChoiceRecap.Describe(new ChoiceEffect("Kai", -6, -2)));
        Assert.Equal("Kai trusted you a little less", ChoiceRecap.Describe(new ChoiceEffect("Kai", 0, -3)));
        Assert.Equal("Kai won't forget that", ChoiceRecap.Describe(new ChoiceEffect("Kai", 0, 0, Dealbreaker: true)));
        Assert.Null(ChoiceRecap.Describe(new ChoiceEffect("Kai", 1, -1)));
    }

    [Fact]
    public void Choices_that_moved_nobody_are_left_out_and_the_rest_keep_story_order()
    {
        var recap = ChoiceRecap.For(
        [
            new ChoiceRecord(2, "Morning", "Ask for their number", [new ChoiceEffect("Kai", 3, 0)]),
            new ChoiceRecord(3, "Evening", "Wave", [new ChoiceEffect("Kai", 1, 0)]),
            new ChoiceRecord(5, "Morning", "Admit the lie", [new ChoiceEffect("Kai", -2, -5), new ChoiceEffect("Rin", 0, 2)]),
        ]);

        Assert.Equal(["Ask for their number", "Admit the lie"], recap.Select(r => r.Words));
        Assert.Equal(["Kai trusted you less", "Rin trusted you a little more"], recap[1].Influence);
    }

    [Fact]
    public void Only_the_most_influential_choices_are_kept()
    {
        var records = Enumerable.Range(1, ChoiceRecap.MaxLines + 5)
            .Select(i => new ChoiceRecord(i, "Morning", $"choice {i}", [new ChoiceEffect("Kai", i, 0)]))
            .ToList();

        var recap = ChoiceRecap.For(records);

        Assert.Equal(ChoiceRecap.MaxLines, recap.Count);
        Assert.Equal("choice 6", recap[0].Words);
        Assert.Equal(recap.OrderBy(r => r.Day).Select(r => r.Day), recap.Select(r => r.Day));
    }
}
