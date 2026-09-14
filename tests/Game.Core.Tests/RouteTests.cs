using Game.Core.Cast;
using Game.Core.Characters;
using Game.Core.Content;
using Game.Core.Encounters;
using Game.Core.Scenes;
using Game.Core.Settings;
using Game.Core.World;

namespace Game.Core.Tests;

/// <summary>
/// Variant routes (plan §5, build step 7): temper picks the route, and every route plays through
/// meeting, contact and a first date with the shipped settings.
/// </summary>
public class RouteTests
{
    private static string ContentPath(string file)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && dir.EnumerateFiles("*.sln").Concat(dir.EnumerateFiles("*.slnx")).Any() is false)
        {
            dir = dir.Parent;
        }

        return Path.Combine(dir?.FullName ?? throw new InvalidOperationException("No repository root."), "content", file);
    }

    private static RouteContent Routes() => RouteContent.Load(ContentPath("routes.json"));

    private static CastContent Cast() =>
        CastContent.Load(ContentPath("temper.json"), ContentPath("wants.json"), ContentPath("contrasts.json"));

    private static (ISettingCatalog Settings, IEncounterCatalog Encounters) Content()
    {
        var settings = new JsonSettingCatalog(ContentPath("settings"), new JsonLocationCatalog(ContentPath("place-types.json")));
        return (settings, new JsonEncounterCatalog(ContentPath("encounters"), settings, [.. Routes().Routes.Select(r => r.Id)]));
    }

    private static CastMember Member(params (string Axis, string End)[] temper)
    {
        var look = new CharacterAppearance("female", 24, "brown eyes", "black hair", "short hair", "fair skin", "an average build", "average height", "");
        return new CastMember("bolder", look, "", temper.ToDictionary(t => t.Axis, t => t.End), "open-a-bakery", 1, []);
    }

    private static TurnOutcome Turn(
        SettingDefinition setting,
        IEncounterCatalog encounters,
        Dictionary<string, string> flags,
        ClockState clock,
        string place,
        int alone = 0,
        string? invite = null)
    {
        var context = new Dictionary<string, string>(flags, StringComparer.Ordinal);
        if (invite is not null)
        {
            context[EncounterEvaluator.InviteKey] = invite;
        }

        var outcome = TurnPlanner.Plan(setting, encounters.For(setting.Id), new TurnContext(clock, place, context, alone), place);
        foreach (var (key, value) in outcome.FlagsToSet)
        {
            flags[key] = value;
        }

        return outcome;
    }

    private static void Answer(Dictionary<string, string> flags, TurnOutcome outcome, string choiceId)
    {
        var choice = outcome.Choices!.Single(c => c.Id == choiceId);
        foreach (var (key, value) in TurnPlanner.Assignments(choice.Sets ?? []))
        {
            flags[key] = value;
        }

        flags[EncounterEvaluator.PendingChoiceKey] = "false";
    }

    public static TheoryData<string> SettingIds()
    {
        var data = new TheoryData<string>();
        foreach (var setting in Content().Settings.All())
        {
            data.Add(setting.Id);
        }

        return data;
    }

    // -------------------------------------------------------------------- content and assignment

    [Fact]
    public void Shipped_routes_load_and_fit_the_cast()
    {
        Routes().ValidateAgainst(Cast());
    }

    [Fact]
    public void Temper_picks_each_variants_route()
    {
        var chance = Member(("temper", "fiery"), ("warmth", "guarded"));
        var routine = Member(("warmth", "open"), ("drive", "easygoing"));
        var introduced = Member(("energy", "outgoing"), ("humour", "playful"));

        Assert.Equal(["chance", "routine", "introduced"], RouteAssigner.Assign([chance, routine, introduced], Routes()));
        Assert.Equal(["introduced", "chance", "routine"], RouteAssigner.Assign([introduced, chance, routine], Routes()));
    }

    [Fact]
    public void Ties_are_broken_the_same_way_every_time()
    {
        var alike = Member(("temper", "calm"));

        var first = RouteAssigner.Assign([alike, alike, alike], Routes());

        Assert.Equal(first, RouteAssigner.Assign([alike, alike, alike], Routes()));
        Assert.Equal(3, first.Distinct().Count());
    }

    [Fact]
    public void Names_are_distinct_avoid_the_taken_ones_and_repeat_for_a_seed()
    {
        var routes = Routes();

        foreach (var seed in Enumerable.Range(0, 30).Select(i => (long)i * 131))
        {
            var names = routes.PickNames("female", 3, ["Aya", "hana"], seed);

            Assert.Equal(3, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.DoesNotContain(names, n => n is "Aya" or "Hana");
            Assert.Equal(names, routes.PickNames("female", 3, ["Aya", "hana"], seed));
        }
    }

    [Fact]
    public void An_encounter_with_an_unknown_route_is_refused()
    {
        var directory = Path.Combine(Path.GetTempPath(), "adate-route-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            var settings = new JsonSettingCatalog(ContentPath("settings"), new JsonLocationCatalog(ContentPath("place-types.json")));
            File.WriteAllText(Path.Combine(directory, "common.json"), """
                [ { "id": "dragon-sighting", "place": {}, "with": ["variant:dragon"], "text": "A dragon." } ]
                """);

            var ex = Assert.Throws<InvalidOperationException>(() => new JsonEncounterCatalog(directory, settings, ["routine", "introduced", "chance"]));

            Assert.Contains("no route 'dragon'", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // -------------------------------------------------------------------- playthroughs

    [Theory]
    [MemberData(nameof(SettingIds))]
    public void The_routine_route_meets_after_two_solo_visits_then_talks_and_dates(string settingId)
    {
        var (settings, encounters) = Content();
        var setting = settings.Get(settingId);
        var place = setting.RoutinePlace;
        var flags = new Dictionary<string, string>();

        Assert.NotEqual("route.routine.meet", Turn(setting, encounters, flags, new ClockState(3, TimeOfDay.Morning), place, alone: 1).EncounterId);

        var meet = Turn(setting, encounters, flags, new ClockState(3, TimeOfDay.Morning), place, alone: 2);
        Assert.Equal("route.routine.meet", meet.EncounterId);
        Assert.Equal(["variant:routine"], meet.With);
        Assert.Equal(place, flags["routine.place"]);

        var contact = Turn(setting, encounters, flags, new ClockState(3, TimeOfDay.Midday), place);
        Assert.Equal("route.routine.contact", contact.EncounterId);
        Answer(flags, contact, "swap-numbers");

        var date = Turn(setting, encounters, flags, new ClockState(5, TimeOfDay.Morning), place, invite: "routine");
        Assert.Equal("beat.first-date.routine", date.EncounterId);
        Assert.Equal("true", flags["routine.first_date"]);
    }

    [Theory]
    [MemberData(nameof(SettingIds))]
    public void The_chance_route_remembers_where_it_happened(string settingId)
    {
        var (settings, encounters) = Content();
        var setting = settings.Get(settingId);
        // The authored meeting: the generated contact and first date are with the chance variant too.
        var authored = encounters.For(settingId).Single(e => (e.With ?? []).Contains("variant:chance") && (e.Sets ?? []).Contains("chance.met"));
        var place = authored.Place.Id!;
        var flags = new Dictionary<string, string>();

        var meet = Turn(setting, encounters, flags, new ClockState(authored.Days![0], authored.Time![0]), place, alone: authored.Place.AloneVisitsBefore ?? 0);
        Assert.Equal(authored.Id, meet.EncounterId);
        Assert.Equal(place, flags["chance.place"]);

        var contact = Turn(setting, encounters, flags, new ClockState(authored.Days![0] + 1, authored.Time![0]), place);
        Assert.Equal("route.chance.contact", contact.EncounterId);
    }

    [Theory]
    [MemberData(nameof(SettingIds))]
    public void The_introduced_route_waits_for_dating_and_an_evening_with_the_main_LI(string settingId)
    {
        var (settings, encounters) = Content();
        var setting = settings.Get(settingId);
        var place = setting.RoutinePlace;
        var evening = new ClockState(13, TimeOfDay.Evening);

        var notYet = new Dictionary<string, string> { ["main_li.first_date"] = "true" };
        Assert.NotEqual("route.introduced.meet", Turn(setting, encounters, notYet, evening, place, invite: "main_li").EncounterId);

        var dating = new Dictionary<string, string> { ["main_li.first_date"] = "true", ["main_li.dating"] = "true" };
        Assert.NotEqual("route.introduced.meet", Turn(setting, encounters, new(dating), evening, place).EncounterId);

        var meet = Turn(setting, encounters, dating, evening, place, invite: "main_li");
        Assert.Equal("route.introduced.meet", meet.EncounterId);
        Assert.Equal(["main_li", "variant:introduced"], meet.With);
        Assert.Equal(place, dating["introduced.place"]);
    }

    [Fact]
    public void Declining_contact_closes_a_routes_dates()
    {
        var (settings, encounters) = Content();
        var setting = settings.All()[0];
        var flags = new Dictionary<string, string> { ["routine.met"] = "true", ["routine.place"] = setting.RoutinePlace };

        var contact = Turn(setting, encounters, flags, new ClockState(4, TimeOfDay.Midday), setting.RoutinePlace);
        Answer(flags, contact, "let-it-go");

        Assert.NotEqual("route.routine.contact", Turn(setting, encounters, flags, new ClockState(4, TimeOfDay.Afternoon), setting.RoutinePlace).EncounterId);
        Assert.NotEqual("beat.first-date.routine", Turn(setting, encounters, flags, new ClockState(6, TimeOfDay.Morning), setting.RoutinePlace, invite: "routine").EncounterId);
    }
}
