using Game.Core;
using Game.Core.Scenes;
using Game.Core.Story;
using Game.Core.World;
using Game.Data.Repositories;

namespace Game.Data.Tests;

public class SceneLogTests
{
    [Fact]
    public async Task A_scene_is_saved_as_it_happens_and_shown_again_until_the_player_moves_on()
    {
        using var db = new TempDatabase();
        var saves = new SaveRepository(db.Database);
        var log = new SceneLogRepository(db.Database);
        var save = await saves.CreateAsync("zimage-anime", "fingerprint", Ceiling.PG13);
        var rin = Guid.NewGuid();

        var id = await log.StartAsync(save.Id, new ClockState(2, TimeOfDay.Evening), "corner-cafe", "quiet.company", "{\"placeId\":\"corner-cafe\"}", "Rin is here too.");

        var started = await log.GetOpenAsync(save.Id);
        Assert.NotNull(started);
        Assert.False(started.Written);
        Assert.Equal("Rin is here too.", started.Text);
        Assert.Null(started.BackgroundPath);
        Assert.Empty(started.Exchanges);

        await log.SetBackgroundAsync(id, "img/aa.png");
        await log.SetPersonAsync(id, rin, "Rin", "neutral", "img/bb.png");
        await log.SetWrittenAsync(id, "Rin looks up from a book.", null);
        await log.AddExchangeAsync(id, new SceneExchange("Ask about the book", "Rin smiles.", "Rin liked that."));
        await log.AddExchangeAsync(id, new SceneExchange("Ask her out", "Rin says yes.", Agreed: "You agreed to meet Rin."));

        var shown = await log.GetOpenAsync(save.Id);
        Assert.NotNull(shown);
        Assert.True(shown.Written);
        Assert.Equal("Rin looks up from a book.", shown.Text);
        Assert.Equal("neutral", shown.Expression);
        Assert.Equal(("img/aa.png", "img/bb.png"), (shown.BackgroundPath, shown.SpritePath));
        Assert.Equal((rin, "Rin"), (shown.CharacterId!.Value, shown.Speaker));
        Assert.Equal(["Ask about the book", "Ask her out"], shown.Exchanges.Select(e => e.Reply));
        Assert.Equal(("Rin smiles.", "Rin liked that."), (shown.Exchanges[0].Reaction, shown.Exchanges[0].Popup));
        Assert.Equal("You agreed to meet Rin.", shown.Exchanges[1].Agreed);
        Assert.Equal(new ClockState(2, TimeOfDay.Evening), shown.Clock);
        Assert.Null(shown.Outfit);

        await log.SetOutfitAsync(id, new Outfit(DressCode.Waterfront, "the player's denim jacket"));
        Assert.Equal(new Outfit(DressCode.Waterfront, "the player's denim jacket"), (await log.GetOpenAsync(save.Id))!.Outfit);

        await log.SetOutfitAsync(id, new Outfit(DressCode.Waterfront));
        Assert.Equal(new Outfit(DressCode.Waterfront), (await log.ListAsync(save.Id)).Single().Outfit);
    }

    [Fact]
    public async Task Words_the_writer_could_not_write_are_marked_and_the_answer_asked_for_again_takes_its_place()
    {
        using var db = new TempDatabase();
        var saves = new SaveRepository(db.Database);
        var log = new SceneLogRepository(db.Database);
        var save = await saves.CreateAsync("zimage-anime", "fingerprint", Ceiling.PG13);

        var id = await log.StartAsync(save.Id, new ClockState(1, TimeOfDay.Midday), "corner-cafe", "quiet.company", "{}", "Rin is here too.");
        Assert.False((await log.GetOpenAsync(save.Id))!.Fallback);

        await log.SetWrittenAsync(id, "Rin is here too.", null, fallback: true);
        await log.AddExchangeAsync(id, new SceneExchange("Say hello", "Rin takes that in.", Proposed: true, Fallback: true));

        var stood = await log.GetOpenAsync(save.Id);
        Assert.True(stood!.Fallback);
        Assert.True(stood.Exchanges.Single() is { Proposed: true, Fallback: true });

        // Asked for again: the scene's words and the answer take the place of the placeholders, not a second turn.
        await log.SetWrittenAsync(id, "Rin looks up from a book.", "happy");
        await log.ReplaceLastExchangeAsync(id, new SceneExchange("Say hello", "Rin smiles back.", Proposed: true));

        var written = await log.GetOpenAsync(save.Id);
        Assert.False(written!.Fallback);
        Assert.Equal("Rin looks up from a book.", written.Text);
        Assert.Equal("Rin smiles back.", written.Exchanges.Single().Reaction);
        Assert.False(written.Exchanges.Single().Fallback);
    }

    [Fact]
    public async Task The_next_turn_closes_the_last_scene_and_continue_closes_the_open_one()
    {
        using var db = new TempDatabase();
        var saves = new SaveRepository(db.Database);
        var log = new SceneLogRepository(db.Database);
        var save = await saves.CreateAsync("zimage-anime", "fingerprint", Ceiling.PG13);

        var first = await log.StartAsync(save.Id, new ClockState(1, TimeOfDay.Morning), "corner-cafe", null, "{}", "One.");
        var second = await log.StartAsync(save.Id, new ClockState(1, TimeOfDay.Midday), "office", null, "{}", "Two.");

        Assert.Equal(second, (await log.GetOpenAsync(save.Id))!.Id);
        Assert.True((await log.ListAsync(save.Id)).Single(s => s.Id == first).Closed);

        // A picture that lands late addresses its own scene, not whichever is open now.
        await log.SetBackgroundAsync(first, "img/late.png");
        Assert.Null((await log.GetOpenAsync(save.Id))!.BackgroundPath);

        await log.CloseAsync(save.Id);

        Assert.Null(await log.GetOpenAsync(save.Id));
        Assert.Equal("office", await log.GetLastPlaceIdAsync(save.Id));
        Assert.Equal(2, (await log.ListAsync(save.Id)).Count);
    }
}
