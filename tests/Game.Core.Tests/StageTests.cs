using Game.Core.Story;

namespace Game.Core.Tests;

public class StageTests
{
    private static readonly string[] Slots = ["neutral", "smile", "laughing", "sad", "angry", "surprised"];

    private static readonly StageCandidate Date = new(Guid.Parse("00000000-0000-0000-0000-00000000000a"), "Rin", "casual", "smile");
    private static readonly StageCandidate Friend = new(Guid.Parse("00000000-0000-0000-0000-00000000000b"), "Kai", "sporty", "neutral");

    private static IReadOnlyList<Guid> Ids(IReadOnlyList<SceneFigure> figures) => [.. figures.Select(f => f.CharacterId)];

    [Fact]
    public void Before_the_words_only_those_there_from_the_start_stand_on_the_stage()
    {
        var opening = Stage.Opening([Date], Slots);

        Assert.Equal([Date.Id], Ids(opening));
        Assert.Equal("smile", opening[0].Expression);
    }

    [Fact]
    public void Someone_the_words_bring_in_joins_beside_whoever_was_already_there()
    {
        var opening = Stage.Opening([Date], Slots);

        var after = Stage.After(
            [Date, Friend], opening, [new Presence(Friend.Id.ToString(), "laughing"), new Presence(Date.Id.ToString(), null)], Friend.Id, "surprised", true, Slots);

        Assert.Equal([Date.Id, Friend.Id], Ids(after));
        Assert.Equal("smile", after[0].Expression);
        Assert.Equal("laughing", after[1].Expression);
    }

    [Fact]
    public void The_scene_s_own_words_bring_everyone_in_when_they_say_nothing_about_who_is_there()
    {
        var opening = Stage.Opening([Date], Slots);

        var fallback = Stage.After([Date, Friend], opening, null, Friend.Id, null, firstWords: true, Slots);

        Assert.Equal([Date.Id, Friend.Id], Ids(fallback));
    }

    [Fact]
    public void Someone_the_answer_leaves_out_has_left_and_the_other_keeps_how_they_looked()
    {
        IReadOnlyList<SceneFigure> both = [new(Date.Id, "Rin", "casual", "sad", SpritePath: "rin-sad.png"), new(Friend.Id, "Kai", "sporty", "smile")];

        var after = Stage.After([Date, Friend], both, [new Presence(Date.Id.ToString(), null)], Friend.Id, "angry", false, Slots);

        var rin = Assert.Single(after);
        Assert.Equal(Date.Id, rin.CharacterId);
        Assert.Equal("sad", rin.Expression);
        Assert.Equal("rin-sad.png", rin.SpritePath);
    }

    [Fact]
    public void An_answer_that_says_nothing_about_who_is_there_leaves_the_stage_as_it_was()
    {
        IReadOnlyList<SceneFigure> date = [new(Date.Id, "Rin", "casual", "sad")];

        var after = Stage.After([Date, Friend], date, null, Date.Id, "laughing", false, Slots);

        var rin = Assert.Single(after);
        Assert.Equal("laughing", rin.Expression);
    }

    [Fact]
    public void Ids_the_scene_is_not_with_are_ignored()
    {
        IReadOnlyList<SceneFigure> date = [new(Date.Id, "Rin", "casual", "sad")];

        var onlyUnknown = Stage.After([Date], date, [new Presence(Guid.NewGuid().ToString(), "smile")], Date.Id, null, false, Slots);
        Assert.Equal([Date.Id], Ids(onlyUnknown));

        var withUnknown = Stage.After([Date], date, [new Presence("player", "smile"), new Presence(Date.Id.ToString(), "angry")], Date.Id, null, false, Slots);
        Assert.Equal("angry", Assert.Single(withUnknown).Expression);
    }

    [Fact]
    public void When_the_only_person_leaves_nobody_is_drawn()
    {
        IReadOnlyList<SceneFigure> date = [new(Date.Id, "Rin", "casual", "sad")];

        Assert.Empty(Stage.After([Date], date, [], Date.Id, "sad", false, Slots));
    }

    [Fact]
    public void A_new_expression_needs_a_new_picture()
    {
        IReadOnlyList<SceneFigure> date = [new(Date.Id, "Rin", "casual", "sad", SpritePath: "rin-sad.png")];

        var after = Stage.After([Date], date, [new Presence(Date.Id.ToString(), "smile")], Date.Id, null, false, Slots);

        Assert.Null(Assert.Single(after).SpritePath);
    }
}
