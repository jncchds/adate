using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Game.Core;
using Game.Core.Cast;
using Game.Core.Content;
using Game.Core.Gpu;
using Game.Core.Scenes;
using Game.Core.Story;
using Game.Core.World;
using Microsoft.Extensions.Options;

namespace Game.Llm.Tests;

public class SceneWriterTests
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

    private static readonly StoryContent Story =
        StoryContent.Load(ContentPath("values.json"), ContentPath("predicates.json"), ContentPath("relationship.json"));

    private static readonly CastContent Cast =
        CastContent.Load(ContentPath("temper.json"), ContentPath("wants.json"), ContentPath("contrasts.json"));

    private const string Rin = "01a09f6b-2e2e-7b53-9007-90fd1897927e";

    /// <summary>Answers from a queue, and remembers every request.</summary>
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

    private static readonly ILocationCatalog PlaceTypes = new JsonLocationCatalog(ContentPath("place-types.json"));

    private static SceneWriter Writer(ILlmClient llm, bool enabled = true, bool judge = false) =>
        new(llm, new SceneValidator(Story, Cast), Story, Cast, PlaceTypes, new SceneJudge(llm),
            Options.Create(new LlmOptions { Enabled = enabled, MaxRetries = 2, UseJudge = judge }));

    private static ScenePacket Packet() => new(
        "Big city",
        "Busy weekdays.",
        new ClockState(6, TimeOfDay.Morning),
        "corner-cafe",
        "The Corner Cup",
        "Alex",
        [new PacketPerson(Rin, "Rin", ["Speaks evenly."], RelationshipStage.Acquaintance, null)],
        [new KnownFact(1, new Fact(Rin, "hair-color", "black hair", FactLevel.Core, "appearance", 0), new HashSet<string> { FactLedger.Player, Rin })],
        [],
        "Rin admits what they really want.",
        Ceiling.PG13,
        ["neutral", "smile", "laughing"]);

    private static SceneWorld World() => new(
        Packet().PlayerKnows,
        new Dictionary<string, CharacterSchedule>(),
        [],
        new Dictionary<string, RelationshipStage> { [Rin] = RelationshipStage.Acquaintance },
        [Rin]);

    private static string Answer(string text = "Rin looks up from a book by the window.", string expression = "smile", string facts = "[]", string places = "[]", string tags = "[]") =>
        $$"""{ "text": "{{text}}", "expression": "{{expression}}", "facts": {{facts}}, "places": {{places}}, "summary": "Coffee with Rin.", "tags": {{tags}} }""";

    [Fact]
    public async Task A_scene_keeps_its_summary_and_only_known_tags()
    {
        var scene = await Writer(new FakeLlm(() => Answer(tags: """["first", "made-up"]"""))).WriteAsync(Packet(), World(), "Placeholder.");

        Assert.Equal("Coffee with Rin.", scene.Summary);
        Assert.Equal([MemoryTags.First], scene.Tags);
    }

    [Fact]
    public async Task First_person_narration_is_sent_back_but_dialogue_may_say_I()
    {
        var llm = new FakeLlm(
            () => Answer(text: "I find Rin by the window and we talk for a while about my week."),
            () => Answer(text: "Rin waits by the window. \\\"I missed you,\\\" they say, and we both know it."),
            () => Answer(text: "Rin waits by the window. \\\"I missed you,\\\" they say."));

        var scene = await Writer(llm).WriteAsync(Packet(), World(), "Placeholder.");

        Assert.False(scene.Fallback);
        Assert.Equal(2, scene.Attempts);
        Assert.Contains("first person", llm.Requests[1].User, StringComparison.Ordinal);
    }

    private static string WithChoices(string choices) =>
        Answer().TrimEnd().TrimEnd('}') + $$""", "choices": {{choices}} }""";

    [Fact]
    public async Task A_scene_with_someone_present_ends_with_two_or_three_tagged_replies()
    {
        var llm = new FakeLlm(
            () => WithChoices("""[{ "text": "Ask about the book", "tags": ["attentiveness"] }]"""),
            () => WithChoices("""[{ "text": "Ask about the book", "tags": ["attentiveness"] }, { "text": "Flirt", "tags": ["charm"] }]"""),
            () => WithChoices("""[{ "text": "Ask about the book", "tags": ["attentiveness"] }, { "text": "Tease them about the rain", "tags": ["humour", "helps:{want}"] }]"""));

        var scene = await Writer(llm).WriteAsync(Packet(), World(), "Placeholder.", wantChoices: true);

        Assert.False(scene.Fallback);
        Assert.Equal(3, scene.Attempts);
        Assert.Contains("offers 1 choices", llm.Requests[1].User, StringComparison.Ordinal);
        Assert.Contains("needs tags from the list", llm.Requests[2].User, StringComparison.Ordinal);
        Assert.Equal(["Ask about the book", "Tease them about the rain"], scene.Choices!.Select(c => c.Text));
        Assert.Equal(["humour", "helps:{want}"], scene.Choices![1].Tags);
    }

    [Fact]
    public async Task Only_one_choice_may_touch_their_want()
    {
        var llm = new FakeLlm(
            () => WithChoices("""[{ "text": "Ask about the book", "tags": ["helps:{want}"] }, { "text": "Mention the rain", "tags": ["helps:{want}"] }]"""),
            () => WithChoices("""[{ "text": "Ask about the book", "tags": ["attentiveness"] }, { "text": "Mention the rain", "tags": ["humour"] }]"""));

        var scene = await Writer(llm).WriteAsync(Packet(), World(), "Placeholder.", wantChoices: true);

        Assert.False(scene.Fallback);
        Assert.Contains("2 choices are tagged helps or hinders", llm.Requests[1].User, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Choices_are_ignored_when_the_scene_does_not_ask_for_them()
    {
        var llm = new FakeLlm(() => WithChoices("""[{ "text": "Wave", "tags": ["charm"] }]"""));

        var scene = await Writer(llm).WriteAsync(Packet(), World(), "Placeholder.");

        Assert.False(scene.Fallback);
        Assert.Empty(scene.Choices ?? []);
    }

    [Fact]
    public async Task Narration_that_decides_for_the_player_is_sent_back()
    {
        var llm = new FakeLlm(
            () => Answer(text: "You scan the room and hesitate before you sit down across from Rin."),
            () => Answer(text: "Rin looks up as the door opens. You notice the rain on the window."));

        var scene = await Writer(llm).WriteAsync(Packet(), World(), "Placeholder.");

        Assert.Equal(2, scene.Attempts);
        Assert.Contains("decides for the player", llm.Requests[1].User, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_story_in_another_language_is_not_held_to_english_word_checks()
    {
        // Spanish "me" twice would count as English first person; German closes a quote with “.
        var spanish = new FakeLlm(() => Answer(text: "Rin me mira y me sonríe desde la ventana."));
        var german = new FakeLlm(() => Answer(text: "Rin sagt: „Schön, dich zu sehen.“"));

        var inSpanish = await Writer(spanish).WriteAsync(Packet() with { Language = "Spanish" }, World(), "Placeholder.");
        var inGerman = await Writer(german).WriteAsync(Packet() with { Language = "Deutsch" }, World(), "Placeholder.");

        Assert.False(inSpanish.Fallback);
        Assert.Equal(1, inSpanish.Attempts);
        Assert.Contains("Write the prose in Spanish", spanish.Requests[0].User, StringComparison.Ordinal);
        Assert.False(inGerman.Fallback);
        Assert.Equal(1, inGerman.Attempts);
    }

    [Fact]
    public async Task A_story_in_another_language_is_still_sent_back_when_it_stops_mid_sentence()
    {
        var llm = new FakeLlm(
            () => Answer(text: "Рин поднимает взгляд и говорит: «Я"),
            () => Answer(text: "Рин поднимает взгляд и говорит: «Привет.»"));

        var scene = await Writer(llm).WriteAsync(Packet() with { Language = "Русский" }, World(), "Placeholder.");

        Assert.False(scene.Fallback);
        Assert.Equal(2, scene.Attempts);
        Assert.Contains("stops mid-sentence", llm.Requests[1].User, StringComparison.Ordinal);
    }

    [Fact]
    public void Player_actions_are_found_outside_dialogue_and_perception_is_allowed()
    {
        Assert.Equal(["You scan", "you hesitate", "you finally sit"],
            Narration.PlayerActions("You scan the room, you hesitate, and you finally sit."));

        Assert.Empty(Narration.PlayerActions("Rin waves you over. You notice the rain and hear the kettle."));
        Assert.Empty(Narration.PlayerActions("\"You take the window seat,\" Rin says, and asks whether you are staying."));
    }

    [Fact]
    public void Narration_checks_ignore_quoted_dialogue_and_count_paragraphs()
    {
        Assert.Empty(Narration.FirstPersonOutsideDialogue("You wave. “I know,” Rin says. \"We should go,\" they add."));
        Assert.Equal(["I", "my", "we"], Narration.FirstPersonOutsideDialogue("I grab my coat and we leave."));
        Assert.True(Narration.HasParagraphs("One.\n\nTwo."));
        Assert.False(Narration.HasParagraphs("One long block."));
    }

    [Fact]
    public async Task A_long_scene_in_one_block_is_sent_back()
    {
        var block = string.Join(" ", Enumerable.Repeat("You watch the rain against the window.", 20));
        var llm = new FakeLlm(() => Answer(text: block), () => Answer());

        var scene = await Writer(llm).WriteAsync(Packet(), World(), "Placeholder.");

        Assert.Equal(2, scene.Attempts);
        Assert.Contains("one block", llm.Requests[1].User, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_judge_sends_a_contradicting_scene_back_with_the_contradiction()
    {
        var llm = new FakeLlm(
            () => Answer(text: "Rin tucks a strand of blue hair behind one ear."),
            () => """{ "contradictions": ["'blue hair' contradicts: Rin hair-color black hair"] }""",
            () => Answer(),
            () => """{ "contradictions": [] }""");

        var scene = await Writer(llm, judge: true).WriteAsync(Packet(), World(), "Placeholder.");

        Assert.False(scene.Fallback);
        Assert.Equal(2, scene.Attempts);
        Assert.Equal("judge", llm.Requests[1].SchemaName);
        Assert.Contains("Rin hair-color black hair", llm.Requests[1].User, StringComparison.Ordinal);
        Assert.Contains("contradicts an established fact", llm.Requests[2].User, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_judge_that_cannot_answer_passes_the_scene()
    {
        var llm = new FakeLlm(() => Answer(), () => throw new HttpRequestException("judge timed out"));

        var scene = await Writer(llm, judge: true).WriteAsync(Packet(), World(), "Placeholder.");

        Assert.False(scene.Fallback);
        Assert.Equal(1, scene.Attempts);
    }

    [Fact]
    public async Task A_named_new_place_is_proposed_and_a_bad_one_is_sent_back()
    {
        var cafeDetail = PlaceTypes.Get("bar").Details!.First().Id;
        var llm = new FakeLlm(
            () => Answer(places: """[{ "type": "castle", "name": "The Keep", "details": [] }]"""),
            () => Answer(places: $$"""[{ "type": "bar", "name": "The Blue Note", "details": ["{{cafeDetail}}"] }]"""));

        var scene = await Writer(llm).WriteAsync(Packet(), World(), "Placeholder.", knownPlaces: ["The Corner Cup"]);

        Assert.False(scene.Fallback);
        Assert.Contains("'castle' is not one of", llm.Requests[1].User, StringComparison.Ordinal);
        var place = Assert.Single(scene.Places);
        Assert.Equal(("bar", "The Blue Note"), (place.Type, place.Name));
    }

    [Fact]
    public async Task A_place_the_player_already_knows_is_not_proposed_again()
    {
        var llm = new FakeLlm(
            () => Answer(places: """[{ "type": "cafe", "name": "the corner cup", "details": [] }]"""),
            () => Answer());

        var scene = await Writer(llm).WriteAsync(Packet(), World(), "Placeholder.", knownPlaces: ["The Corner Cup"]);

        Assert.Equal(2, scene.Attempts);
        Assert.Empty(scene.Places);
        Assert.Contains(scene.Rejections, r => r.Contains("already a place", StringComparison.Ordinal));
    }

    [Fact]
    public async Task With_no_model_configured_the_authored_text_is_used_without_a_call()
    {
        var llm = new FakeLlm();

        var scene = await Writer(llm, enabled: false).WriteAsync(Packet(), World(), "Placeholder.");

        Assert.True(scene.Fallback);
        Assert.Equal("Placeholder.", scene.Text);
        Assert.Empty(llm.Requests);
    }

    [Fact]
    public async Task A_valid_answer_is_used_with_its_facts_known_to_everyone_present()
    {
        var llm = new FakeLlm(() => Answer(facts: $$"""[{ "subject": "{{Rin}}", "predicate": "likes", "object": "rainy mornings", "level": "Established" }]"""));

        var scene = await Writer(llm).WriteAsync(Packet(), World(), "Placeholder.");

        Assert.False(scene.Fallback);
        Assert.Equal("Rin looks up from a book by the window.", scene.Text);
        Assert.Equal("smile", scene.Expression);
        var fact = Assert.Single(scene.Facts);
        Assert.Equal("rainy mornings", fact.Fact.Object);
        Assert.Equal([FactLedger.Player, Rin], fact.Knowers);
        Assert.Equal(1, scene.Attempts);
    }

    [Fact]
    public async Task A_rejected_answer_is_retried_with_its_reasons()
    {
        var llm = new FakeLlm(
            () => Answer(expression: "furious"),
            () => Answer(facts: $$"""[{ "subject": "{{Rin}}", "predicate": "hair-color", "object": "blue hair", "level": "Established" }]"""),
            () => Answer());

        var scene = await Writer(llm).WriteAsync(Packet(), World(), "Placeholder.");

        Assert.False(scene.Fallback);
        Assert.Equal(3, scene.Attempts);
        Assert.Contains("'furious' is not one of", llm.Requests[1].User, StringComparison.Ordinal);
        Assert.Contains("cannot change", llm.Requests[2].User, StringComparison.Ordinal);
        Assert.Equal(2, scene.Rejections.Count);
    }

    [Fact]
    public async Task After_the_retries_run_out_the_authored_text_is_used()
    {
        var llm = new FakeLlm(() => "not json", () => Answer(text: ""), () => Answer(expression: "angry"));

        var scene = await Writer(llm).WriteAsync(Packet(), World(), "Placeholder.");

        Assert.True(scene.Fallback);
        Assert.Equal("Placeholder.", scene.Text);
        Assert.Empty(scene.Facts);
        Assert.Equal(3, scene.Attempts);
        Assert.Equal(3, scene.Rejections.Count);
    }

    [Fact]
    public async Task An_unreachable_model_falls_back_without_blaming_the_answer()
    {
        var llm = new FakeLlm(
            () => throw new HttpRequestException("connection refused"),
            () => throw new HttpRequestException("connection refused"),
            () => throw new HttpRequestException("connection refused"));

        var scene = await Writer(llm).WriteAsync(Packet(), World(), "Placeholder.");

        Assert.True(scene.Fallback);
        Assert.All(scene.Rejections, r => Assert.Contains("could not answer", r, StringComparison.Ordinal));
        Assert.DoesNotContain("rejected", llm.Requests[1].User, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Core_facts_cannot_come_from_a_scene()
    {
        var llm = new FakeLlm(
            () => Answer(facts: $$"""[{ "subject": "{{Rin}}", "predicate": "has-secret", "object": "a debt", "level": "Core" }]"""),
            () => Answer(),
            () => Answer());

        var scene = await Writer(llm).WriteAsync(Packet(), World(), "Placeholder.");

        Assert.Equal(2, scene.Attempts);
        Assert.Contains(scene.Rejections, r => r.Contains("use Established or Claimed", StringComparison.Ordinal));
    }

    [Fact]
    public void The_schema_limits_expressions_predicates_and_levels()
    {
        var schema = Writer(new FakeLlm()).Schema(Packet());
        var properties = schema["properties"]!;

        Assert.Equal(["neutral", "smile", "laughing"], properties["expression"]!["enum"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Contains("likes", properties["facts"]!["items"]!["properties"]!["predicate"]!["enum"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal(["Established", "Claimed"], properties["facts"]!["items"]!["properties"]!["level"]!["enum"]!.AsArray().Select(n => n!.GetValue<string>()));
    }

    // -------------------------------------------------------------------- the HTTP client

    private sealed class RecordingHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public string? Sent { get; private set; }

        public Uri? Uri { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uri = request.RequestUri;
            Sent = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class CountingLease : IGpuLease
    {
        public List<GpuConsumer> Acquired { get; } = [];

        public Task<IAsyncDisposable> AcquireAsync(GpuConsumer consumer, CancellationToken ct = default)
        {
            Acquired.Add(consumer);
            return Task.FromResult<IAsyncDisposable>(new Released());
        }

        private sealed class Released : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task The_client_asks_for_the_schema_under_the_gpu_lease_and_returns_the_message()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, """{ "choices": [ { "message": { "content": "{\"text\":\"hi\"}" } } ] }""");
        var lease = new CountingLease();
        var http = new HttpClient(handler) { BaseAddress = OpenAiCompatibleClient.EnsureTrailingSlash("http://llm.local/v1") };
        var client = new OpenAiCompatibleClient(http, lease, Options.Create(new LlmOptions { Model = "qwen/qwen3.8-27b", Temperature = 0.5, ReasoningEffort = "none" }));

        var answer = await client.CompleteJsonAsync(new LlmRequest("system", "user", "scene", new JsonObject { ["type"] = "object" }, MaxTokens: 700));

        Assert.Equal("""{"text":"hi"}""", answer);
        Assert.Equal("http://llm.local/v1/chat/completions", handler.Uri!.ToString());
        Assert.Equal([GpuConsumer.Llm], lease.Acquired);

        var sent = JsonNode.Parse(handler.Sent!)!;
        Assert.Equal("qwen/qwen3.8-27b", sent["model"]!.GetValue<string>());
        Assert.Equal("none", sent["reasoning_effort"]!.GetValue<string>());
        Assert.Equal(700, sent["max_tokens"]!.GetValue<int>());
        Assert.Equal("json_schema", sent["response_format"]!["type"]!.GetValue<string>());
        Assert.Equal("scene", sent["response_format"]!["json_schema"]!["name"]!.GetValue<string>());
        Assert.Equal("user", sent["messages"]![1]!["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task An_error_from_the_endpoint_is_an_exception_with_its_status()
    {
        var handler = new RecordingHandler(HttpStatusCode.BadRequest, """{ "error": "model not loaded" }""");
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://llm.local/v1/") };
        var client = new OpenAiCompatibleClient(http, new CountingLease(), Options.Create(new LlmOptions()));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.CompleteJsonAsync(new LlmRequest("s", "u", "scene", new JsonObject())));

        Assert.Contains("400", ex.Message, StringComparison.Ordinal);
        Assert.Contains("model not loaded", ex.Message, StringComparison.Ordinal);
    }
}
