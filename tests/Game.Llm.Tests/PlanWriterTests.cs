using Game.Core.Content;
using Game.Core.Scenes;
using Game.Core.Settings;
using Game.Core.Story;
using Microsoft.Extensions.Options;

namespace Game.Llm.Tests;

public class PlanWriterTests
{
    private sealed class FakeLlm(params Func<string>[] answers) : ILlmClient
    {
        private readonly Queue<Func<string>> _answers = new(answers);

        public List<LlmRequest> Requests { get; } = [];

        public Task<string> CompleteJsonAsync(LlmRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);
            return Task.FromResult(_answers.Dequeue()());
        }
    }

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
        [],
        ["clerk"]);

    private static readonly PlannableEncounter[] Beats =
    [
        new("tip", "corner", "The barista tells you about {main_li}."),
    ];

    private static PlanWriter Writer(ILlmClient llm, bool enabled = true) =>
        new(llm, Options.Create(new LlmOptions { Enabled = enabled, MaxRetries = 1 }));

    private const string Valid = """
        {
          "places": [
            { "role": "corner", "type": "diner", "name": "Okonek's", "details": ["jukebox"], "look": "chrome stools, cracked vinyl" },
            { "role": "late", "type": "diner", "name": "The Night Window", "details": [], "look": "" }
          ],
          "events": [
            { "id": "street-market", "name": "The street market", "day": 8, "role": "corner", "time": "Midday" },
            { "id": "last-dance", "name": "The last dance", "day": 16, "role": "late", "time": "Evening" }
          ],
          "threads": ["The old mill is up for sale and nobody will say who is buying."],
          "encounters": [{ "id": "tip", "text": "The cook leans over the counter to tell you about {main_li}." }]
        }
        """;

    private static async Task<SavePlan?> PlanAsync(params Func<string>[] answers) =>
        await Writer(new FakeLlm(answers)).WriteAsync(Town, new Types(), Beats, null);

    [Fact]
    public async Task A_valid_answer_fills_every_role_dates_the_calendar_and_rewrites_the_beats()
    {
        var plan = await PlanAsync(() => Valid);

        Assert.NotNull(plan);
        Assert.Equal(["corner", "late"], plan.Places.Select(p => p.Role));
        Assert.Equal("Okonek's", plan.Places[0].Name);
        Assert.Equal(["street-market", "last-dance"], plan.Events.Select(e => e.Id));
        Assert.Single(plan.Threads);
        Assert.Contains("{main_li}", plan.EncounterTexts["tip"]);
    }

    [Fact]
    public async Task The_plan_is_folded_into_the_setting_the_save_plays()
    {
        var planned = SavePlans.Apply(Town, (await PlanAsync(() => Valid))!);

        Assert.Equal("diner", planned.Place("corner").Type);
        Assert.Equal("Okonek's", planned.Place("corner").Name);
        Assert.Equal(["jukebox"], planned.Place("corner").Details);

        // What points at a role keeps pointing at it: only what the role turned out to be changed.
        Assert.Equal("corner", planned.RoutinePlace);
        Assert.Equal("corner", planned.Openings[0].MeetingPlace);
        Assert.False(planned.Place("late").Known);
        Assert.Equal([8, 16], planned.Events.Select(e => e.Day));
    }

    [Fact]
    public async Task A_role_filled_with_a_type_it_does_not_offer_is_refused()
    {
        var wrong = Valid.Replace(@"{ ""role"": ""late"", ""type"": ""diner""", @"{ ""role"": ""late"", ""type"": ""cafe""", StringComparison.Ordinal);
        var llm = new FakeLlm(() => wrong, () => Valid);

        Assert.NotNull(await Writer(llm).WriteAsync(Town, new Types(), Beats, null));
        Assert.Contains("cannot be a cafe", llm.Requests[1].User, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_rewrite_that_drops_a_token_is_refused()
    {
        var dropped = Valid.Replace("to tell you about {main_li}.", "to tell you about someone.", StringComparison.Ordinal);
        var llm = new FakeLlm(() => dropped, () => Valid);

        Assert.NotNull(await Writer(llm).WriteAsync(Town, new Types(), Beats, null));
        Assert.Contains("{main_li}", llm.Requests[1].User, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Two_events_on_one_day_are_refused()
    {
        var sameDay = Valid.Replace(@"""day"": 16", @"""day"": 8", StringComparison.Ordinal);
        var llm = new FakeLlm(() => sameDay, () => Valid);

        Assert.NotNull(await Writer(llm).WriteAsync(Town, new Types(), Beats, null));
        Assert.Contains("fall on day 8", llm.Requests[1].User, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_event_outside_the_story_is_refused()
    {
        var late = Valid.Replace(@"""day"": 16", @"""day"": 19", StringComparison.Ordinal);
        var llm = new FakeLlm(() => late, () => Valid);

        Assert.NotNull(await Writer(llm).WriteAsync(Town, new Types(), Beats, null));
        Assert.Contains("dated events fall between", llm.Requests[1].User, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_role_left_unfilled_is_refused()
    {
        const string missing = """
            {
              "places": [
                { "role": "corner", "type": "diner", "name": "Okonek's", "details": ["jukebox"], "look": "chrome stools" }
              ],
              "events": [
                { "id": "street-market", "name": "The street market", "day": 8, "role": "corner", "time": "Midday" },
                { "id": "last-dance", "name": "The last dance", "day": 16, "role": "corner", "time": "Evening" }
              ],
              "threads": [],
              "encounters": [{ "id": "tip", "text": "The cook tells you about {main_li}." }]
            }
            """;

        var llm = new FakeLlm(() => missing, () => Valid);

        Assert.NotNull(await Writer(llm).WriteAsync(Town, new Types(), Beats, null));
        Assert.Contains("'late' is not filled", llm.Requests[1].User, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Two_places_with_the_same_name_are_refused()
    {
        var same = Valid.Replace("The Night Window", "Okonek's", StringComparison.Ordinal);
        var llm = new FakeLlm(() => same, () => Valid);

        Assert.NotNull(await Writer(llm).WriteAsync(Town, new Types(), Beats, null));
        Assert.Contains("More than one place is called", llm.Requests[1].User, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_a_model_there_is_no_plan_and_the_setting_is_played_as_authored()
    {
        Assert.Null(await Writer(new FakeLlm(), enabled: false).WriteAsync(Town, new Types(), Beats, null));
    }

    [Fact]
    public async Task Nothing_that_passes_leaves_no_plan_rather_than_a_half_laid_out_town()
    {
        Assert.Null(await PlanAsync(() => "{ \"places\": [] }", () => "not json at all"));
    }

    [Fact]
    public async Task The_roles_their_types_and_what_each_is_for_are_all_in_the_request()
    {
        var llm = new FakeLlm(() => Valid);
        await Writer(llm).WriteAsync(Town, new Types(), Beats, null);

        var request = llm.Requests[0].User;
        Assert.Contains("**corner**", request, StringComparison.Ordinal);
        Assert.Contains("where the player passes through most days", request, StringComparison.Ordinal);
        Assert.Contains("somewhere the player could first meet someone", request, StringComparison.Ordinal);
        Assert.Contains("found later in the story", request, StringComparison.Ordinal);
        Assert.Contains("window-seats (cozy window seats)", request, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_story_in_another_language_asks_for_names_in_it_and_ids_in_English()
    {
        var llm = new FakeLlm(() => Valid);
        await Writer(llm).WriteAsync(Town, new Types(), Beats, "Русский");

        Assert.Contains("in Русский", llm.Requests[0].User, StringComparison.Ordinal);
        Assert.Contains("Keep everything else exactly as listed, in English", llm.Requests[0].User, StringComparison.Ordinal);
    }
}
