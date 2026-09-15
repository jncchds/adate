using Game.Core;
using Game.Core.Characters;
using Game.Data.Repositories;

namespace Game.Data.Tests;

public class ThreadRepositoryTests
{
    [Fact]
    public async Task Loose_ends_are_kept_per_save_closed_once_and_seeded_once()
    {
        using var db = new TempDatabase();
        var saves = new SaveRepository(db.Database);
        var threads = new ThreadRepository(db.Database);
        var save = await saves.CreateAsync("zimage-anime", "fingerprint", Ceiling.PG13);
        var other = await saves.CreateAsync("zimage-anime", "fingerprint", Ceiling.PG13);

        await threads.AddAsync(save.Id, "rin", "Rin promised a record.", 3);
        await threads.AddAsync(save.Id, null, "The player meant to call home.", 4);
        await threads.AddAsync(other.Id, "kai", "Kai owes a coffee.", 2);

        var open = await threads.ListAsync(save.Id);
        Assert.Equal(["Rin promised a record.", "The player meant to call home."], open.Select(t => t.Text));
        Assert.Equal(("rin", (string?)null), (open[0].CharacterId, open[1].CharacterId));

        var kai = (await threads.ListAsync(other.Id)).Single();
        await threads.CloseAsync(save.Id, [open[0].Id, kai.Id], 5);
        await threads.CloseAsync(save.Id, [open[0].Id], 9);

        Assert.Equal([open[1].Id], (await threads.ListAsync(save.Id)).Select(t => t.Id));
        Assert.Equal(5, (await threads.ListAsync(save.Id, openOnly: false)).Single(t => t.Id == open[0].Id).ClosedDay);
        Assert.Single(await threads.ListAsync(other.Id));

        Assert.False(await threads.IsSeededAsync(save.Id));
        Assert.True(await threads.MarkSeededAsync(save.Id));
        Assert.False(await threads.MarkSeededAsync(save.Id));
        Assert.True(await threads.IsSeededAsync(save.Id));
    }

    [Fact]
    public async Task A_voice_is_kept_with_the_character()
    {
        using var db = new TempDatabase();
        var saves = new SaveRepository(db.Database);
        var characters = new CharacterRepository(db.Database);
        var story = new StoryStateRepository(db.Database);
        var save = await saves.CreateAsync("zimage-anime", "fingerprint", Ceiling.PG13);
        var rin = await characters.CreateAsync(save.Id, new CharacterAppearance(
            Subject: "female", Age: 24, EyeColor: "green eyes", HairColor: "red hair", HairStyle: "long hair",
            SkinTone: "pale skin", Build: "slim", Height: "tall", DistinguishingFeature: "freckles"));

        Assert.Null(await story.GetVoiceAsync(rin.Id));
        await story.SetVoiceAsync(rin.Id, "Short sentences.");
        Assert.Equal("Short sentences.", await story.GetVoiceAsync(rin.Id));
    }
}
