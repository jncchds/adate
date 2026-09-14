using Game.Core.Encounters;
using Game.Core.Scenes;
using Game.Core.World;

namespace Game.Core.Tests;

public class TurnOutcomeJsonTests
{
    [Fact]
    public void A_stored_turn_reads_back_as_it_was_decided()
    {
        var outcome = new TurnOutcome(
            new ClockState(3, TimeOfDay.Afternoon),
            new ClockState(3, TimeOfDay.Evening),
            "corner-cafe",
            "route.routine.meet",
            "Rin is at The Corner Cup.",
            new Dictionary<string, string> { ["main_li.met"] = "true" },
            ["low-tide"],
            ["main_li"],
            false,
            [new EncounterChoice("stay", "Stay a while", ["stayed=true"], ["kindness"])]);

        var read = TurnOutcomeJson.Deserialize(TurnOutcomeJson.Serialize(outcome));

        Assert.Equal(outcome.VisitedAt, read.VisitedAt);
        Assert.Equal(outcome.Next, read.Next);
        Assert.Equal((outcome.PlaceId, outcome.EncounterId, outcome.Text), (read.PlaceId, read.EncounterId, read.Text));
        Assert.Equal("true", read.FlagsToSet["main_li.met"]);
        Assert.Equal(outcome.Reveals, read.Reveals);
        Assert.Equal(outcome.With, read.With);
        Assert.Equal("Stay a while", Assert.Single(read.Choices!).Text);
        Assert.Equal(["kindness"], read.Choices![0].Tags!);
    }
}
