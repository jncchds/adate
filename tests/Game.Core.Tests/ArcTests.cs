using Game.Core.Cast;
using Game.Core.Content;
using Game.Core.Encounters;
using Game.Core.Scenes;
using Game.Core.Settings;
using Game.Core.Story;
using Game.Core.World;

namespace Game.Core.Tests;

/// <summary>
/// Arcs (plan §7, build step 7): reveal, obstacle, crisis at the last event and resolution, played
/// through with the shipped settings. Finishing an arc is what lets a relationship reach committed.
/// </summary>
public class ArcTests
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

    private static (ISettingCatalog Settings, IEncounterCatalog Encounters) Content()
    {
        var settings = new JsonSettingCatalog(ContentPath("settings"), new JsonLocationCatalog(ContentPath("place-types.json")));
        var routes = RouteContent.Load(ContentPath("routes.json"));
        return (settings, new JsonEncounterCatalog(ContentPath("encounters"), settings, [.. routes.Routes.Select(r => r.Id)]));
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

    private static TurnOutcome Turn(
        SettingDefinition setting,
        IEncounterCatalog encounters,
        Dictionary<string, string> flags,
        ClockState clock,
        string place,
        string? invite = null)
    {
        var context = new Dictionary<string, string>(flags, StringComparer.Ordinal);
        if (invite is not null)
        {
            context[EncounterEvaluator.InviteKey] = invite;
        }

        var outcome = TurnPlanner.Plan(setting, encounters.For(setting.Id), new TurnContext(clock, place, context, 0), place);
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

    [Theory]
    [MemberData(nameof(SettingIds))]
    public void The_main_LIs_arc_plays_from_reveal_to_resolution(string settingId)
    {
        var (settings, encounters) = Content();
        var setting = settings.Get(settingId);
        var opening = setting.Openings[0];
        var home = opening.HomePlace;
        var crisis = setting.Events.OrderBy(e => e.Day).Last();

        var flags = new Dictionary<string, string>
        {
            ["opening"] = opening.Id,
            ["main_li.home_place"] = home,
            ["main_li.met"] = "true",
            ["main_li.recognised"] = "true",
            ["main_li.contact"] = "true",
        };

        Assert.NotEqual("arc.main_li.reveal", Turn(setting, encounters, new(flags), new ClockState(JsonEncounterCatalog.ArcRevealDay - 1, TimeOfDay.Afternoon), home).EncounterId);

        var reveal = Turn(setting, encounters, flags, new ClockState(JsonEncounterCatalog.ArcRevealDay, TimeOfDay.Afternoon), home);
        Assert.Equal("arc.main_li.reveal", reveal.EncounterId);
        Assert.Contains("{want}", reveal.Text, StringComparison.Ordinal);

        // The obstacle waits for a first date.
        Assert.NotEqual("arc.main_li.obstacle", Turn(setting, encounters, new(flags), new ClockState(JsonEncounterCatalog.ArcObstacleDay, TimeOfDay.Afternoon), home).EncounterId);
        flags["main_li.first_date"] = "true";

        var obstacle = Turn(setting, encounters, flags, new ClockState(JsonEncounterCatalog.ArcObstacleDay, TimeOfDay.Afternoon), home);
        Assert.Equal("arc.main_li.obstacle", obstacle.EncounterId);
        Assert.Contains("helps:{want}", obstacle.Choices![0].Tags!);
        Answer(flags, obstacle, "think-it-through");

        // At the last event, the crisis is only for whoever the player brings.
        var crisisClock = new ClockState(crisis.Day, crisis.Time);
        Assert.Equal($"event.{crisis.Id}", Turn(setting, encounters, new(flags), crisisClock, crisis.Place).EncounterId);

        var scene = Turn(setting, encounters, flags, crisisClock, crisis.Place, invite: "main_li");
        Assert.Equal("arc.main_li.crisis", scene.EncounterId);
        Assert.Equal(["help", "stay-out", "make-it-worse"], scene.Choices!.Select(c => c.Id));
        Answer(flags, scene, "help");

        var resolution = Turn(setting, encounters, flags, new ClockState(crisis.Day, TimeOfDay.Night), home);
        Assert.Equal("arc.main_li.resolution", resolution.EncounterId);
        Answer(flags, resolution, "hear-them");

        Assert.Equal(new StageFacts(true, true, true, true, true), StageFacts.FromFlags(flags, "main_li"));
    }

    [Theory]
    [MemberData(nameof(SettingIds))]
    public void Only_the_person_brought_to_the_last_event_gets_their_crisis(string settingId)
    {
        var (settings, encounters) = Content();
        var setting = settings.Get(settingId);
        var crisis = setting.Events.OrderBy(e => e.Day).Last();
        var clock = new ClockState(crisis.Day, crisis.Time);

        var flags = new Dictionary<string, string> { ["main_li.obstacle"] = "true", ["routine.obstacle"] = "true" };

        Assert.Equal("arc.routine.crisis", Turn(setting, encounters, new(flags), clock, crisis.Place, invite: "routine").EncounterId);
        Assert.Equal("arc.main_li.crisis", Turn(setting, encounters, new(flags), clock, crisis.Place, invite: "main_li").EncounterId);
        Assert.NotEqual("arc.chance.crisis", Turn(setting, encounters, new(flags), clock, crisis.Place, invite: "chance").EncounterId);
    }

    [Fact]
    public void A_variants_arc_happens_where_they_were_met()
    {
        var (settings, encounters) = Content();
        var setting = settings.All()[0];
        var flags = new Dictionary<string, string> { ["chance.contact"] = "true", ["chance.place"] = "riverside-park" };

        Assert.NotEqual("arc.chance.reveal", Turn(setting, encounters, new(flags), new ClockState(7, TimeOfDay.Morning), setting.RoutinePlace).EncounterId);
        Assert.Equal("arc.chance.reveal", Turn(setting, encounters, flags, new ClockState(7, TimeOfDay.Morning), "riverside-park").EncounterId);
    }
}
