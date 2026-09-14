using Game.Core.Content;
using Game.Core.Encounters;
using Game.Core.Scenes;
using Game.Core.Settings;
using Game.Core.World;

namespace Game.Core.Tests;

/// <summary>
/// The four beats of meeting the main LI (plan §4), played through with the shipped settings and
/// encounters for every opening. The same shape for every opening, so this is the whole contract.
/// </summary>
public class OpeningBeatTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && dir.EnumerateFiles("*.sln").Concat(dir.EnumerateFiles("*.slnx")).Any() is false)
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Could not locate the repository root.");
    }

    private static (ISettingCatalog Settings, IEncounterCatalog Encounters) Content()
    {
        var content = Path.Combine(RepoRoot(), "content");
        var settings = new JsonSettingCatalog(Path.Combine(content, "settings"), new JsonLocationCatalog(Path.Combine(content, "place-types.json")));
        return (settings, new JsonEncounterCatalog(Path.Combine(content, "encounters"), settings));
    }

    public static TheoryData<string, string> Openings()
    {
        var data = new TheoryData<string, string>();
        foreach (var setting in Content().Settings.All())
        {
            foreach (var opening in setting.Openings)
            {
                data.Add(setting.Id, opening.Id);
            }
        }

        return data;
    }

    private static TurnOutcome Turn(
        SettingDefinition setting,
        IEncounterCatalog encounters,
        Dictionary<string, string> flags,
        int day,
        TimeOfDay slot,
        string place,
        bool invite = false)
    {
        var context = new Dictionary<string, string>(flags, StringComparer.Ordinal);
        if (invite)
        {
            context[EncounterEvaluator.InviteKey] = "main_li";
        }

        var outcome = TurnPlanner.Plan(setting, encounters.For(setting.Id), new TurnContext(new ClockState(day, slot), place, context, 0), place);

        foreach (var (key, value) in outcome.FlagsToSet)
        {
            flags[key] = value;
        }

        return outcome;
    }

    [Theory]
    [MemberData(nameof(Openings))]
    public void Meet_recognise_contact_and_first_date_play_through(string settingId, string openingId)
    {
        var (settings, encounters) = Content();
        var setting = settings.Get(settingId);
        var opening = setting.Openings.Single(o => o.Id == openingId);
        var flags = new Dictionary<string, string> { ["opening"] = opening.Id, ["main_li.home_place"] = opening.HomePlace };

        // 1. Meet, on day 1 at the meeting place and time.
        var meet = Turn(setting, encounters, flags, 1, opening.Time, opening.MeetingPlace);
        Assert.Equal($"opening.{opening.Id}.meet", meet.EncounterId);
        Assert.Equal(["main_li"], meet.With);
        Assert.Contains(opening.Hook, meet.Text, StringComparison.Ordinal);

        // 2. Recognise, at the home place in its window, with the contact choice.
        var recognise = Turn(setting, encounters, flags, 2, opening.Time, opening.HomePlace);
        Assert.Equal($"opening.{opening.Id}.recognise", recognise.EncounterId);
        Assert.Equal(["swap-numbers", "let-it-go"], recognise.Choices!.Select(c => c.Id));
        Assert.Equal(recognise.EncounterId, flags[EncounterEvaluator.PendingChoiceKey]);

        // 3. Contact: the player asks for their number.
        foreach (var (key, value) in TurnPlanner.Assignments(recognise.Choices![0].Sets ?? []))
        {
            flags[key] = value;
        }

        flags[EncounterEvaluator.PendingChoiceKey] = "false";

        // 4. First date, as soon as the next day, at a place the player picks, when they invite them along:
        //    no days of waiting with nothing to do.
        var date = Turn(setting, encounters, flags, 3, TimeOfDay.Evening, setting.RoutinePlace, invite: true);
        Assert.Equal("beat.first-date", date.EncounterId);
        Assert.DoesNotContain(EncounterEvaluator.InviteKey, date.FlagsToSet.Keys);

        // And it happens once.
        var again = Turn(setting, encounters, flags, 4, TimeOfDay.Evening, setting.RoutinePlace, invite: true);
        Assert.NotEqual("beat.first-date", again.EncounterId);
    }

    [Theory]
    [MemberData(nameof(Openings))]
    public void A_missed_recognise_beat_re_arms_once_at_the_second_place(string settingId, string openingId)
    {
        var (settings, encounters) = Content();
        var setting = settings.Get(settingId);
        var opening = setting.Openings.Single(o => o.Id == openingId);
        var flags = new Dictionary<string, string> { ["opening"] = opening.Id, ["main_li.met"] = "true" };

        var late = Turn(setting, encounters, flags, 5, TimeOfDay.Midday, opening.SecondPlace!);

        Assert.Equal($"opening.{opening.Id}.recognise-late", late.EncounterId);
        Assert.Equal("true", flags["main_li.recognised_late"]);
        Assert.NotEmpty(late.Choices!);

        var missed = Turn(setting, encounters, new Dictionary<string, string> { ["opening"] = opening.Id, ["main_li.met"] = "true" }, 8, TimeOfDay.Midday, opening.SecondPlace!);
        Assert.NotEqual($"opening.{opening.Id}.recognise-late", missed.EncounterId);
    }

    [Fact]
    public void Without_contact_there_is_no_first_date()
    {
        var (settings, encounters) = Content();
        var setting = settings.All()[0];
        var flags = new Dictionary<string, string>
        {
            ["opening"] = setting.Openings[0].Id,
            ["main_li.met"] = "true",
            ["main_li.recognised"] = "true",
            ["main_li.contact_declined"] = "true",
        };

        var date = Turn(setting, encounters, flags, 6, TimeOfDay.Evening, setting.RoutinePlace, invite: true);

        Assert.NotEqual("beat.first-date", date.EncounterId);
    }

    [Fact]
    public void An_unknown_text_token_is_refused_at_load()
    {
        var directory = Path.Combine(Path.GetTempPath(), "adate-token-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            var (settings, _) = Content();
            File.WriteAllText(Path.Combine(directory, "common.json"), """
                [ { "id": "typo", "place": {}, "text": "You wave at {mainli}." } ]
                """);

            var ex = Assert.Throws<InvalidOperationException>(() => new JsonEncounterCatalog(directory, settings));

            Assert.Contains("{mainli}", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
