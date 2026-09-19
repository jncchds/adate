using Microsoft.Extensions.Options;

namespace Game.Llm.Tests;

public class NameWriterTests
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

    private static NameWriter Writer(ILlmClient llm, bool enabled = true) =>
        new(llm, Options.Create(new LlmOptions { Enabled = enabled, MaxRetries = 1 }));

    private static Task<IReadOnlyList<string>> WriteAsync(
        ILlmClient llm, int count = 3, IEnumerable<string>? taken = null, string? language = "Русский") =>
        Writer(llm).WriteAsync("female", count, taken ?? [], "Small town", "A slow summer.", language);

    [Fact]
    public async Task Names_come_back_in_the_script_they_were_written_in()
    {
        var names = await WriteAsync(new FakeLlm(() => """{ "names": ["Катя", "Марина", "女"] }"""));

        Assert.Equal(["Катя", "Марина", "女"], names);
    }

    [Fact]
    public async Task The_language_and_the_names_already_in_the_story_are_asked_for()
    {
        var llm = new FakeLlm(() => """{ "names": ["Катя", "Марина", "Лена"] }""");
        await WriteAsync(llm, taken: ["Аня"]);

        Assert.Contains("in Русский", llm.Requests[0].User, StringComparison.Ordinal);
        Assert.Contains("none of these, which are taken: Аня", llm.Requests[0].User, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_English_story_still_asks_for_English_names()
    {
        var llm = new FakeLlm(() => """{ "names": ["Nora"] }""");
        await WriteAsync(llm, count: 1, language: null);

        Assert.Contains("in English", llm.Requests[0].User, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_name_already_taken_or_repeated_is_dropped_rather_than_given_to_two_people()
    {
        var names = await WriteAsync(new FakeLlm(() => """{ "names": ["Катя", "катя", "Аня", "Лена"] }"""), taken: ["Аня"]);

        Assert.Equal(["Катя", "Лена"], names);
    }

    [Fact]
    public async Task A_surname_a_title_or_an_empty_name_is_dropped()
    {
        var names = await WriteAsync(new FakeLlm(() => """{ "names": ["Nora", "Dr. Reed", "", "Ann-Marie O'Shea"] }"""));

        Assert.Equal(["Nora", "Ann-Marie O'Shea"], names);
    }

    [Fact]
    public async Task No_more_than_the_names_asked_for_come_back()
    {
        var names = await WriteAsync(new FakeLlm(() => """{ "names": ["A", "B", "C", "D", "E"] }"""), count: 2);

        Assert.Equal(2, names.Count);
    }

    [Fact]
    public async Task Without_a_model_the_caller_is_left_to_its_own_pool()
    {
        Assert.Empty(await Writer(new FakeLlm(), enabled: false).WriteAsync("female", 3, [], "Small town", "A slow summer.", "Русский"));
    }

    [Fact]
    public async Task An_answer_that_cannot_be_read_leaves_the_caller_to_its_own_pool()
    {
        Assert.Empty(await WriteAsync(new FakeLlm(() => "not json")));
    }
}
