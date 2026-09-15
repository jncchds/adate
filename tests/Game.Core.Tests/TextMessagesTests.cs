using Game.Core.Encounters;
using Game.Core.Story;

namespace Game.Core.Tests;

public class TextMessagesTests
{
    [Fact]
    public void Each_quoted_message_is_a_bubble_and_narration_is_left_out()
    {
        const string text = """
            The gray light of the evening filters into the stairwell. Your phone buzzes.

            "Just got home. My brain is still spinning."

            Another message follows quickly.

            "Anyway, I'm exhausted." "What are you up to?"
            """;

        Assert.Equal(["Just got home. My brain is still spinning.", "Anyway, I'm exhausted.", "What are you up to?"], TextMessages.Split(text));
    }

    [Fact]
    public void Text_without_quotes_is_a_bubble_a_line()
    {
        Assert.Equal(["hey", "you up?"], TextMessages.Split("hey\n\nyou up?\n"));
        Assert.Empty(TextMessages.Split("  "));
        Assert.Empty(TextMessages.Split(null));
    }

    [Fact]
    public void Typographic_quotes_count()
    {
        Assert.Equal(["Привет!", "Ты где?"], TextMessages.Split("«Привет!»\n„Ты где?“"));
        Assert.Equal(["See you at eight."], TextMessages.Split("“See you at eight.”"));
    }

    [Fact]
    public void A_quoted_reply_loses_its_quotes_and_anything_else_is_kept()
    {
        Assert.Equal("I think it's hard, but maybe worth trying.", TextMessages.Unquote("\"I think it's hard, but maybe worth trying.\""));
        Assert.Equal("ok \"sure\"", TextMessages.Unquote(" ok \"sure\" "));
    }

    [Fact]
    public void A_conversation_by_text_is_shorter_than_a_scene()
    {
        Assert.Equal(SceneConversation.PhoneMaxReplies, SceneConversation.MaxRepliesFor(JsonEncounterCatalog.PhoneId));
        Assert.Equal(SceneConversation.MaxReplies, SceneConversation.MaxRepliesFor(JsonEncounterCatalog.QuietCompanyId));
        Assert.True(SceneConversation.PhoneMaxReplies < SceneConversation.MaxReplies);
    }
}
