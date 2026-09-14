namespace Game.Llm.Tests;

public class NarrationTests
{
    [Theory]
    [InlineData("Rin watches you for a moment.\n\n\"It does,")]
    [InlineData("Rin takes a slow sip and")]
    [InlineData("“Maybe,” Rin says. “Or")]
    [InlineData("   ")]
    public void Text_cut_off_mid_sentence_is_unfinished(string text) =>
        Assert.True(Narration.Unfinished(text));

    [Theory]
    [InlineData("Rin looks up from a book by the window.")]
    [InlineData("\"It does,\" Rin says.")]
    [InlineData("Rin shrugs. “Maybe.”")]
    [InlineData("Is that so?")]
    [InlineData("The rain keeps on…")]
    public void Finished_text_is_not(string text) =>
        Assert.False(Narration.Unfinished(text));
}
