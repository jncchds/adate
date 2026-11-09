using Game.Core;
using Game.Core.Story;
using Game.Data.Repositories;

namespace Game.Data.Tests;

public class HappeningIdeaRepositoryTests
{
    [Fact]
    public async Task An_idea_is_used_once_and_the_same_scene_gets_it_back()
    {
        using var db = new TempDatabase();
        var save = await new SaveRepository(db.Database).CreateAsync("zimage-anime", "fingerprint", Ceiling.PG13);
        var ideas = new HappeningIdeaRepository(db.Database);

        await ideas.AddAsync(save.Id, "cafe", ["A tray of cups is carried past.", "The grinder jams and is kicked."], 2);

        Assert.Equal("A tray of cups is carried past.", await ideas.TakeAsync(save.Id, "cafe", "3|Evening"));
        Assert.Equal("A tray of cups is carried past.", await ideas.TakeAsync(save.Id, "cafe", "3|Evening"));
        Assert.Equal("The grinder jams and is kicked.", await ideas.TakeAsync(save.Id, "cafe", "4|Morning"));
        Assert.Null(await ideas.TakeAsync(save.Id, "cafe", "5|Morning"));
        Assert.Null(await ideas.TakeAsync(save.Id, "park", "5|Morning"));
        Assert.Equal(2, (await ideas.TextsAsync(save.Id)).Count);
    }

    [Fact]
    public async Task A_place_type_keeps_only_so_many_unused_ideas()
    {
        using var db = new TempDatabase();
        var save = await new SaveRepository(db.Database).CreateAsync("zimage-anime", "fingerprint", Ceiling.PG13);
        var ideas = new HappeningIdeaRepository(db.Database);

        var many = Enumerable.Range(0, 20).Select(i => $"Something small number {(char)('a' + i)} happens.");
        Assert.Equal(HappeningIdeas.PoolPerPlaceType, await ideas.AddAsync(save.Id, "cafe", many, 1));
        Assert.Equal(0, await ideas.AddAsync(save.Id, "cafe", ["One more thing goes on."], 1));
    }
}

public class HappeningIdeasTests
{
    private static readonly string[] Names = ["Rin", "Sam"];

    [Fact]
    public void Plain_sentences_about_the_place_are_kept()
    {
        var kept = HappeningIdeas.Accept(["A delivery van idles outside with its door open.", "  Someone drops a stack of trays.  "], Names, []);

        Assert.Equal(["A delivery van idles outside with its door open.", "Someone drops a stack of trays."], kept);
    }

    [Theory]
    [InlineData("Rin laughs at a joke from the next table.")]
    [InlineData("Sam walks in carrying an umbrella.")]
    [InlineData("The clock strikes 7 and the lights dim.")]
    [InlineData("You notice a stain on the table by the window.")]
    [InlineData("Two strangers kiss near the counter.")]
    [InlineData("Is that a cat on the shelf above the till?")]
    [InlineData("Too short.")]
    public void Anything_that_names_decides_or_addresses_someone_is_refused(string idea) =>
        Assert.Empty(HappeningIdeas.Accept([idea], Names, []));

    [Fact]
    public void A_repeat_of_what_is_already_in_hand_is_refused_and_only_two_are_kept()
    {
        var kept = HappeningIdeas.Accept(
            ["a delivery van idles outside!", "A bell rings somewhere behind the shelves.", "A bicycle is leaned on the window.", "A radio plays softly in the back."],
            Names,
            ["A delivery van idles outside."]);

        Assert.Equal(["A bell rings somewhere behind the shelves.", "A bicycle is leaned on the window."], kept);
    }
}
