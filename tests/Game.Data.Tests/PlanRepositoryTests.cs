using Game.Core;
using Game.Core.Scenes;
using Game.Core.Settings;
using Game.Data.Repositories;

namespace Game.Data.Tests;

public class PlanRepositoryTests
{
    private static readonly SettingEvent[] Calendar =
    [
        new("last-dance", "The last dance", 16, "late", TimeOfDay.Evening),
        new("street-market", "The street market", 8, "corner", TimeOfDay.Midday),
    ];

    [Fact]
    public async Task A_plan_is_kept_per_save_with_its_calendar_in_order_and_its_rewritten_beats()
    {
        using var db = new TempDatabase();
        var saves = new SaveRepository(db.Database);
        var plans = new PlanRepository(db.Database);
        var save = await saves.CreateAsync("zimage-anime", "fingerprint", Ceiling.PG13);
        var other = await saves.CreateAsync("zimage-anime", "fingerprint", Ceiling.PG13);

        Assert.False(await plans.IsPlannedAsync(save.Id));

        Assert.True(await plans.SaveAsync(save.Id, planned: true, Calendar, new Dictionary<string, string> { ["tip"] = "The cook leans over." }));

        Assert.True(await plans.IsPlannedAsync(save.Id));
        Assert.False(await plans.IsPlannedAsync(other.Id));

        var events = await plans.EventsAsync(save.Id);
        Assert.Equal(["street-market", "last-dance"], events.Select(e => e.Id));
        Assert.Equal((8, "corner", TimeOfDay.Midday), (events[0].Day, events[0].Place, events[0].Time));

        Assert.Equal("The cook leans over.", (await plans.EncounterTextsAsync(save.Id))["tip"]);
        Assert.Empty(await plans.EncounterTextsAsync(other.Id));
    }

    [Fact]
    public async Task A_second_planning_of_one_save_changes_nothing_so_a_town_is_never_laid_out_twice()
    {
        using var db = new TempDatabase();
        var saves = new SaveRepository(db.Database);
        var plans = new PlanRepository(db.Database);
        var save = await saves.CreateAsync("zimage-anime", "fingerprint", Ceiling.PG13);

        await plans.SaveAsync(save.Id, planned: true, Calendar, new Dictionary<string, string>());

        Assert.False(await plans.SaveAsync(
            save.Id,
            planned: true,
            [new SettingEvent("something-else", "Something else", 4, "corner", TimeOfDay.Morning)],
            new Dictionary<string, string>()));

        Assert.Equal(["street-market", "last-dance"], (await plans.EventsAsync(save.Id)).Select(e => e.Id));
    }

    [Fact]
    public async Task A_save_played_without_a_model_is_still_recorded_as_laid_out()
    {
        using var db = new TempDatabase();
        var saves = new SaveRepository(db.Database);
        var plans = new PlanRepository(db.Database);
        var save = await saves.CreateAsync("zimage-anime", "fingerprint", Ceiling.PG13);

        Assert.True(await plans.SaveAsync(save.Id, planned: false, [], new Dictionary<string, string>()));

        Assert.True(await plans.IsPlannedAsync(save.Id));
        Assert.Empty(await plans.EventsAsync(save.Id));
    }
}
