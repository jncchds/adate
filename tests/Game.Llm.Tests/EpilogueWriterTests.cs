using Game.Core;
using Game.Core.Story;
using Microsoft.Extensions.Options;

namespace Game.Llm.Tests;

public class EpilogueWriterTests
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

    private static EpilogueWriter Writer(ILlmClient llm, bool enabled = true) =>
        new(llm, Options.Create(new LlmOptions { Enabled = enabled, MaxRetries = 2 }));

    private static EpilogueRequest Together() => new(
        "Big city", "Warm and a little wistful.", "Alex", EndingKind.Together, "Kai",
        ["Speaks evenly."], ["Rin"], [], ["Kai recognised Alex at the cafe."],
        [new RecapLine(4, "Morning", "Admit the lie", ["Kai trusted you less"])], Ceiling.PG13);

    private static string Answer(string text) => $$"""{ "text": "{{text}}" }""";

    [Fact]
    public void The_request_carries_the_ending_memories_and_choices()
    {
        var prompt = EpilogueWriter.Render(Together());

        Assert.Contains("chose to stay with Kai", prompt, StringComparison.Ordinal);
        Assert.Contains("Kai recognised Alex at the cafe.", prompt, StringComparison.Ordinal);
        Assert.Contains("\"Admit the lie\" (Kai trusted you less)", prompt, StringComparison.Ordinal);
        Assert.Contains("not chosen: Rin", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_ending_together_must_name_the_partner_and_be_finished()
    {
        var llm = new FakeLlm(
            () => Answer("A month later, the cafe still smells of roasted beans and"),
            () => Answer("A month later, the cafe still smells of roasted beans."),
            () => Answer("A month later, Kai saves you the table by the window."));

        var epilogue = await Writer(llm).WriteAsync(Together(), "Authored.");

        Assert.False(epilogue.Fallback);
        Assert.Equal(3, epilogue.Attempts);
        Assert.Contains("mid-sentence", llm.Requests[1].User, StringComparison.Ordinal);
        Assert.Contains("name them", llm.Requests[2].User, StringComparison.Ordinal);
        Assert.Equal(EpilogueWriter.MaxTokens, llm.Requests[0].MaxTokens);
    }

    [Fact]
    public async Task Without_a_model_the_authored_ending_is_used()
    {
        var epilogue = await Writer(new FakeLlm(), enabled: false).WriteAsync(Together(), "Authored.");

        Assert.True(epilogue.Fallback);
        Assert.Equal("Authored.", epilogue.Text);
    }
}
