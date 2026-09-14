using Game.Core;
using Game.Core.Scenes;
using Game.Core.Story;
using Game.Core.World;
using Game.Data.Repositories;

namespace Game.Data.Tests;

public class PendingSceneTests
{
    [Fact]
    public async Task A_waiting_scene_round_trips_and_is_answered_once_with_its_changes()
    {
        using var db = new TempDatabase();
        var save = await new SaveRepository(db.Database).CreateAsync("zimage-anime", "fingerprint", Ceiling.PG13);
        var maya = await new CharacterRepository(db.Database).CreateAsync(save.Id, new Game.Core.Characters.CharacterAppearance(
            "female", 24, "brown eyes", "white hair", "hair in a long braid", "pale skin", "slim", "tall", ""), "Maya");
        var state = new GameStateRepository(db.Database);
        var story = new StoryStateRepository(db.Database);

        Assert.Null(await state.GetPendingSceneAsync(save.Id));

        var scene = new PendingScene(
            new ClockState(6, TimeOfDay.Morning),
            "office",
            "quiet.company",
            ["variant:routine"],
            "Maya looks up from her planner.",
            [new ProposedChoice("Ask about the planner", ["attentiveness"]), new ProposedChoice("Tease her about it", ["humour"])]);

        await state.SavePendingSceneAsync(save.Id, scene);

        var stored = await state.GetPendingSceneAsync(save.Id);
        Assert.NotNull(stored);
        Assert.Equal(scene.Clock, stored.Clock);
        Assert.Equal(scene.With, stored.With);
        Assert.Equal(["attentiveness"], stored.Choices[0].Tags);

        var warmer = RelationshipState.Start with { Affection = 4, Stage = RelationshipStage.Acquaintance };
        await state.ResolvePendingSceneAsync(save.Id, new Dictionary<Guid, RelationshipState> { [maya.Id] = warmer },
            new Dictionary<string, string> { ["routine.dating"] = "true" });

        Assert.Null(await state.GetPendingSceneAsync(save.Id));
        Assert.Equal(warmer, await story.GetRelationshipAsync(save.Id, maya.Id));
        Assert.Equal("true", (await state.GetFlagsAsync(save.Id))["routine.dating"]);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            state.ResolvePendingSceneAsync(save.Id, new Dictionary<Guid, RelationshipState>(), new Dictionary<string, string>()));
    }
}
