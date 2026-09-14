using Game.Core;
using Game.Core.Cast;
using Game.Core.Scenes;
using Game.Core.Story;
using Game.Core.World;
using Microsoft.Extensions.Options;

namespace Game.Llm.Tests;

public class ReactionWriterTests
{
    private static string ContentPath(string file)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && dir.EnumerateFiles("*.sln").Concat(dir.EnumerateFiles("*.slnx")).Any() is false)
        {
            dir = dir.Parent;
        }

        return Path.Combine(dir!.FullName, "content", file);
    }

    private static readonly StoryContent Story =
        StoryContent.Load(ContentPath("values.json"), ContentPath("predicates.json"), ContentPath("relationship.json"));

    private static readonly CastContent Cast =
        CastContent.Load(ContentPath("temper.json"), ContentPath("wants.json"), ContentPath("contrasts.json"));

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

    private static ReactionWriter Writer(ILlmClient llm, bool enabled = true) =>
        new(llm, Story, Cast, Options.Create(new LlmOptions { Enabled = enabled, MaxRetries = 2 }));

    private static ScenePacket Packet() => new(
        "Big city", "Busy weekdays.", new ClockState(6, TimeOfDay.Morning), "office", "Meridian & Co. offices", "Alex",
        [new PacketPerson("maya-id", "Maya", ["Speaks evenly."], RelationshipStage.Acquaintance, null)],
        [], [], "The player has just replied.", Ceiling.PG13, ["neutral", "smile", "sad"]);

    private static string Answer(string text, string expression = "smile", string tags = "[]") =>
        $$"""{ "text": "{{text}}", "expression": "{{expression}}", "tags": {{tags}} }""";

    [Fact]
    public async Task In_another_language_the_judge_sends_back_a_reaction_that_adds_player_actions()
    {
        var llm = new FakeLlm(
            () => Answer("Ты берёшь её за руку, и Майя улыбается."),
            () => """{ "contradictions": [], "playerActions": ["Ты берёшь её за руку"] }""",
            () => Answer("Майя улыбается и откладывает ежедневник."),
            () => """{ "contradictions": [], "playerActions": [] }""");
        var writer = new ReactionWriter(llm, Story, Cast, Options.Create(new LlmOptions { Enabled = true, MaxRetries = 2 }), new Game.Llm.SceneJudge(llm));

        var reaction = await writer.WriteAsync(Packet() with { Language = "Русский" }, "Майя поднимает взгляд.", "Сказать, что рада её видеть", ["kindness"], "Майя кивает.");

        Assert.False(reaction.Fallback);
        Assert.Equal(2, reaction.Attempts);
        Assert.Contains("Сказать, что рада её видеть", llm.Requests[1].User, StringComparison.Ordinal);
        Assert.Contains("adds things the player did not choose (Ты берёшь её за руку)", llm.Requests[2].User, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_reaction_that_leaves_room_offers_the_next_replies()
    {
        var llm = new FakeLlm(() => """{ "text": "Maya laughs.", "expression": "smile", "tags": [], "ends": false, "choices": [{ "text": "Ask about her week", "tags": ["attentiveness"] }, { "text": "Tease her again", "tags": ["humour"] }] }""");

        var reaction = await Writer(llm).WriteAsync(Packet(), "Maya looks up.", "Tease her about the planner", ["humour"], "Maya takes that in.", replyNumber: 1, maxReplies: 4);

        Assert.False(reaction.Ends);
        Assert.Equal(["Ask about her week", "Tease her again"], reaction.Choices!.Select(c => c.Text));
        Assert.Contains("reply 1 of at most 4", llm.Requests[0].User, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_reaction_closes_the_conversation_on_the_last_reply_or_without_usable_replies()
    {
        var last = new FakeLlm(() => """{ "text": "Maya waves goodbye.", "expression": "smile", "tags": [], "ends": false, "choices": [{ "text": "Wave back", "tags": ["kindness"] }, { "text": "Call after her", "tags": ["adventure"] }] }""");
        var unusable = new FakeLlm(() => """{ "text": "Maya laughs.", "expression": "smile", "tags": [], "ends": false, "choices": [{ "text": "Say something", "tags": ["made-up"] }] }""");

        var atTheEnd = await Writer(last).WriteAsync(Packet(), "Maya looks up.", "Say goodbye", ["kindness"], "Maya takes that in.", replyNumber: 4, maxReplies: 4);
        var withoutReplies = await Writer(unusable).WriteAsync(Packet(), "Maya looks up.", "Say hi", ["kindness"], "Maya takes that in.", replyNumber: 1, maxReplies: 4);

        Assert.True(atTheEnd.Ends);
        Assert.Empty(atTheEnd.Choices ?? []);
        Assert.Contains("last reply", last.Requests[0].User, StringComparison.Ordinal);
        Assert.False(withoutReplies.Fallback);
        Assert.True(withoutReplies.Ends);
    }

    [Fact]
    public async Task Swapped_numbers_are_reported_only_when_the_reaction_says_so()
    {
        var gave = new FakeLlm(() => """{ "text": "Maya scribbles her number on a napkin and slides it across.", "expression": "smile", "tags": [], "numbers": true }""");
        var notYet = new FakeLlm(() => """{ "text": "Maya laughs and says maybe next time.", "expression": "smile", "tags": [], "numbers": false }""");

        var given = await Writer(gave).WriteAsync(Packet(), "Maya looks up.", "Ask for her number", ["adventure"], "Maya takes that in.");
        var declined = await Writer(notYet).WriteAsync(Packet(), "Maya looks up.", "Ask for her number", ["adventure"], "Maya takes that in.");

        Assert.True(given.ExchangedNumbers);
        Assert.False(declined.ExchangedNumbers);
        Assert.Contains("numbers: true only if", gave.Requests[0].User, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_proposed_choice_keeps_its_own_tags()
    {
        var llm = new FakeLlm(() => Answer("Maya laughs and pushes the planner across the desk.", tags: """["dishonesty"]"""));

        var reaction = await Writer(llm).WriteAsync(Packet(), "Maya looks up.", "Tease her about the planner", ["humour"], "Maya takes that in.");

        Assert.False(reaction.Fallback);
        Assert.Equal(["humour"], reaction.Tags);
        Assert.Contains("tags: an empty list", llm.Requests[0].User, StringComparison.Ordinal);
        Assert.Equal(ReactionWriter.MaxTokens, llm.Requests[0].MaxTokens);
    }

    [Fact]
    public async Task Free_text_is_read_into_known_tags_only()
    {
        var llm = new FakeLlm(() => Answer("Maya's smile falters for a moment.", "sad", """["lie", "honesty", "made-up"]"""));

        var reaction = await Writer(llm).WriteAsync(Packet(), "Maya looks up.", "I tell her I have never been to a concert, which is not true", null, "Maya takes that in.");

        Assert.Equal(["lie", "honesty"], reaction.Tags);
        Assert.Equal("sad", reaction.Expression);
    }

    [Fact]
    public async Task A_reaction_may_repeat_the_players_action_but_not_add_one()
    {
        var llm = new FakeLlm(
            () => Answer("You take her hand and you kiss her cheek. Maya freezes."),
            () => Answer("You take her hand. Maya squeezes back, surprised."));

        var reaction = await Writer(llm).WriteAsync(Packet(), "Maya looks up.", "Take her hand", null, "Maya takes that in.");

        Assert.False(reaction.Fallback);
        Assert.Equal(2, reaction.Attempts);
        Assert.Contains("you kiss", llm.Requests[1].User, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_agreed_meeting_comes_back_for_the_game_to_check()
    {
        var llm = new FakeLlm(() => """{ "text": "Maya nods. \"Thursday evening, then.\"", "expression": "smile", "tags": [], "meet": { "place": "Riverside Park", "inDays": 2, "slot": "Evening" } }""");

        var reaction = await Writer(llm).WriteAsync(Packet(), "Maya looks up.", "Ask her to meet at the park on Thursday evening", ["attentiveness"], "Maya takes that in.");

        Assert.Equal(new ProposedMeeting("Riverside Park", 2, "Evening"), reaction.Meet);
        Assert.Contains("meet: only if", llm.Requests[0].User, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_a_model_the_fallback_is_used_and_chosen_tags_still_count()
    {
        var reaction = await Writer(new FakeLlm(), enabled: false).WriteAsync(Packet(), "Maya looks up.", "Ask about the planner", ["attentiveness"], "Maya takes that in.");

        Assert.True(reaction.Fallback);
        Assert.Equal("Maya takes that in.", reaction.Text);
        Assert.Equal(["attentiveness"], reaction.Tags);
    }
}
