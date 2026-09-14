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
