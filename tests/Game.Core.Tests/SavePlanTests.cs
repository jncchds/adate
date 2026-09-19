using Game.Core.Content;
using Game.Core.Saves;
using Game.Core.Scenes;
using Game.Core.Settings;
using Game.Core.Story;

namespace Game.Core.Tests;

public class SavePlanTests
{
    private sealed class Types : ILocationCatalog
    {
        private static readonly LocationDefinition Cafe = new(
            "cafe", "Cafe", ["cafe"], new Dictionary<string, IReadOnlyList<string>>(),
            Details: [new PlaceDetail("window-seats", ["window seat"], "cozy window seats")]);

        private static readonly LocationDefinition Diner = new(
            "diner", "Diner", ["diner"], new Dictionary<string, IReadOnlyList<string>>(),
            Details: [new PlaceDetail("jukebox", ["jukebox"], "an old jukebox")]);

        public LocationDefinition Get(string locationId) => locationId switch
        {
            "cafe" => Cafe,
            "diner" => Diner,
            _ => throw new KeyNotFoundException(locationId),
        };

        public IReadOnlyList<LocationDefinition> All() => [Cafe, Diner];
    }

    private static readonly SettingDefinition Town = new(
        "test-town", "Test town", 20, "Quiet.", "corner",
        [
            new SettingPlace("corner", "cafe", "The Corner", ["window-seats"], Types: ["cafe", "diner"]),
            new SettingPlace("late", "diner", "The Late One", ["jukebox"], Known: false),
        ],
        [new SettingOpening("first", "First", "corner", TimeOfDay.Morning, "corner", "mornings", "A hook.", "late")],
        [new SettingEvent("authored-fair", "The fair", 10, "corner", TimeOfDay.Evening)],
        ["clerk"]);

    private static SavePlan Plan(
        IReadOnlyList<PlannedPlace>? places = null, IReadOnlyList<PlannedEvent>? events = null, IReadOnlyList<string>? threads = null) =>
        new(
            places ??
            [
                new PlannedPlace("corner", "diner", "Okonek's", ["jukebox"], "chrome stools, cracked vinyl"),
                new PlannedPlace("late", "diner", "The Night Window", []),
            ],
            events ??
            [
                new PlannedEvent("street-market", "The street market", 8, "corner", TimeOfDay.Midday),
                new PlannedEvent("last-dance", "The last dance", 16, "late", TimeOfDay.Evening),
            ],
            threads ?? ["The old mill is up for sale."],
            new Dictionary<string, string>());

    private static IReadOnlyList<string> Check(SavePlan plan) => SavePlans.Check(plan, Town, new Types());

    [Fact]
    public void A_plan_that_fills_every_role_within_what_it_offers_passes()
    {
        Assert.Empty(Check(Plan()));
    }

    [Fact]
    public void A_role_keeps_what_points_at_it_and_only_changes_what_it_turned_out_to_be()
    {
        var planned = SavePlans.Apply(Town, Plan());

        Assert.Equal(("diner", "Okonek's"), (planned.Place("corner").Type, planned.Place("corner").Name));
        Assert.Equal(["jukebox"], planned.Place("corner").Details!);

        Assert.Equal("corner", planned.RoutinePlace);
        Assert.Equal("corner", planned.Openings[0].MeetingPlace);
        Assert.Equal("late", planned.Openings[0].SecondPlace);
        Assert.False(planned.Place("late").Known);
        Assert.Equal(["cafe", "diner"], planned.Place("corner").Types!);
    }

    [Fact]
    public void The_plans_calendar_replaces_the_authored_one_in_day_order()
    {
        var planned = SavePlans.Apply(Town, Plan());

        Assert.Equal(["street-market", "last-dance"], planned.Events.Select(e => e.Id));
        Assert.DoesNotContain(planned.Events, e => e.Id == "authored-fair");
    }

    [Fact]
    public void A_places_look_is_kept_with_it_and_a_blank_one_leaves_the_place_to_its_type()
    {
        var records = SavePlans.Records(new SaveId(Guid.NewGuid()), SavePlans.Apply(Town, Plan()), Plan());

        Assert.Equal("chrome stools, cracked vinyl", records.Single(r => r.Id == "corner").Look);
        Assert.Null(records.Single(r => r.Id == "late").Look);
    }

    [Fact]
    public void A_type_the_role_does_not_offer_is_refused()
    {
        var reasons = Check(Plan([
            new PlannedPlace("corner", "diner", "Okonek's", []),
            new PlannedPlace("late", "cafe", "The Night Window", []),
        ]));

        Assert.Contains(reasons, r => r.Contains("'late' cannot be a cafe", StringComparison.Ordinal));
    }

    [Fact]
    public void A_role_that_offers_no_alternatives_keeps_the_type_it_was_authored_as()
    {
        Assert.Equal(["diner"], SavePlans.TypesFor(Town.Place("late")));
        Assert.Equal(["cafe", "diner"], SavePlans.TypesFor(Town.Place("corner")));
    }

    [Fact]
    public void A_detail_that_belongs_to_another_type_is_refused()
    {
        var reasons = Check(Plan([
            new PlannedPlace("corner", "diner", "Okonek's", ["window-seats"]),
            new PlannedPlace("late", "diner", "The Night Window", []),
        ]));

        Assert.Contains(reasons, r => r.Contains("'window-seats' is not a detail of diner", StringComparison.Ordinal));
    }

    [Fact]
    public void A_role_filled_twice_or_left_unfilled_is_refused()
    {
        var reasons = Check(Plan([
            new PlannedPlace("corner", "diner", "Okonek's", []),
            new PlannedPlace("corner", "diner", "The Night Window", []),
        ]));

        Assert.Contains(reasons, r => r.Contains("'corner' is filled twice", StringComparison.Ordinal));
        Assert.Contains(reasons, r => r.Contains("'late' is not filled", StringComparison.Ordinal));
    }

    [Fact]
    public void Two_places_a_player_could_not_tell_apart_are_refused()
    {
        var reasons = Check(Plan([
            new PlannedPlace("corner", "diner", "The Night Window", []),
            new PlannedPlace("late", "diner", "the night window", []),
        ]));

        Assert.Contains(reasons, r => r.Contains("More than one place is called", StringComparison.Ordinal));
    }

    [Fact]
    public void An_event_outside_the_story_or_sharing_a_day_is_refused()
    {
        var reasons = Check(Plan(events:
        [
            new PlannedEvent("too-early", "Too early", 1, "corner", TimeOfDay.Midday),
            new PlannedEvent("also-day-eight", "Also day eight", 8, "late", TimeOfDay.Evening),
            new PlannedEvent("day-eight", "Day eight", 8, "corner", TimeOfDay.Midday),
        ]));

        Assert.Contains(reasons, r => r.Contains("dated events fall between day 3 and day 17", StringComparison.Ordinal));
        Assert.Contains(reasons, r => r.Contains("Two events fall on day 8", StringComparison.Ordinal));
    }

    [Fact]
    public void An_event_at_no_role_is_refused()
    {
        var reasons = Check(Plan(events:
        [
            new PlannedEvent("one", "One", 8, "corner", TimeOfDay.Midday),
            new PlannedEvent("two", "Two", 12, "nowhere", TimeOfDay.Evening),
        ]));

        Assert.Contains(reasons, r => r.Contains("'two' is at 'nowhere'", StringComparison.Ordinal));
    }

    [Fact]
    public void Too_few_events_leave_a_story_with_nothing_dated_and_are_refused()
    {
        var reasons = Check(Plan(events: [new PlannedEvent("one", "One", 8, "corner", TimeOfDay.Midday)]));

        Assert.Contains(reasons, r => r.Contains("There must be 2 to 5 dated events", StringComparison.Ordinal));
    }

    [Fact]
    public void More_loose_ends_than_a_story_opens_with_are_refused()
    {
        var reasons = Check(Plan(threads: ["One.", "Two.", "Three.", "Four."]));

        Assert.Contains(reasons, r => r.Contains("at most 3 loose ends", StringComparison.Ordinal));
    }
}
