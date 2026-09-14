using Game.Core.Story;
using Microsoft.Extensions.Options;

namespace Game.Llm.Tests;

public class BibleWriterTests
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

    private static readonly BiblePerson[] People =
    [
        new("rin", "Rin", "barista", "keep the local animal shelter from closing", ["Speaks evenly."]),
        new("june", "June", "office manager", "travel for a year", ["Starts conversations."]),
    ];

    private static BibleWriter Writer(ILlmClient llm, bool enabled = true) =>
        new(llm, Options.Create(new LlmOptions { Enabled = enabled, MaxRetries = 1 }));

    private const string Valid = """
        { "people": [
          { "id": "rin", "likes": ["rainy mornings", "old paperbacks"], "secret": "Rin sold their late grandmother's ring to pay the shelter's rent." },
          { "id": "june", "likes": ["airport lounges"], "secret": "June has already bought a one-way ticket." }
        ] }
        """;

    [Fact]
    public async Task Accepted_details_become_core_facts_for_each_person()
    {
        var facts = await Writer(new FakeLlm(() => Valid)).WriteAsync("Big city", "Busy weekdays.", People);

        Assert.Equal(5, facts.Count);
        Assert.All(facts, f => Assert.Equal((FactLevel.Core, BibleWriter.Source), (f.Level, f.Source)));
        Assert.Contains(facts, f => f is { Subject: "rin", Predicate: "likes", Object: "old paperbacks" });
        Assert.Contains(facts, f => f is { Subject: "june", Predicate: "has-secret" });
    }

    [Fact]
    public async Task A_missing_or_invented_person_is_sent_back_then_accepted()
    {
        var llm = new FakeLlm(
            () => """{ "people": [ { "id": "sam", "likes": ["jazz"], "secret": "none" } ] }""",
            () => Valid);

        var facts = await Writer(llm).WriteAsync("Big city", "Busy weekdays.", People);

        Assert.NotEmpty(facts);
        Assert.Contains("'sam' is not one of the ids given", llm.Requests[1].User, StringComparison.Ordinal);
        Assert.Contains("'rin' is missing", llm.Requests[1].User, StringComparison.Ordinal);
    }

    [Fact]
    public async Task With_no_model_or_no_passing_answer_there_are_no_facts()
    {
        var disabled = new FakeLlm();
        Assert.Empty(await Writer(disabled, enabled: false).WriteAsync("Big city", "", People));
        Assert.Empty(disabled.Requests);

        var tooLong = $$"""{ "people": [ { "id": "rin", "likes": ["x"], "secret": "{{new string('s', BibleWriter.MaxSecretLength + 1)}}" }, { "id": "june", "likes": [], "secret": "y" } ] }""";
        Assert.Empty(await Writer(new FakeLlm(() => tooLong, () => "not json")).WriteAsync("Big city", "", People));
    }
}
