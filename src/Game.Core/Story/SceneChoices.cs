using Game.Core.Encounters;
using Game.Core.World;

namespace Game.Core.Story;

/// <summary>Something the player could say or do next, proposed by the writer and tagged with what it shows about them.</summary>
public sealed record ProposedChoice(string Text, IReadOnlyList<string> Tags);

/// <summary>
/// A written scene waiting for the player's reply (phase-3 plan: choices). One per save; turns wait
/// until it is answered, as an authored choice does.
/// </summary>
/// <param name="With">Encounter references of the people present.</param>
/// <param name="Text">The scene so far: its words and every exchange since, as the writer reads it.</param>
/// <param name="Replies">How many times the player has already replied in this scene.</param>
public sealed record PendingScene(
    ClockState Clock,
    string PlaceId,
    string? EncounterId,
    IReadOnlyList<string> With,
    string Text,
    IReadOnlyList<ProposedChoice> Choices,
    int Replies = 0);

/// <summary>One exchange of a scene's conversation: the player's words, and how the others answered.</summary>
/// <param name="Proposed">Whether the reply was one of the offered choices, whose tags are scored once however the answer is written.</param>
/// <param name="Fallback">Whether the answer is the placeholder because the writer failed, so it can be asked for again.</param>
public sealed record SceneExchange(
    string Reply,
    string? Reaction,
    string? Popup = null,
    string? Agreed = null,
    bool Proposed = false,
    bool Fallback = false);

/// <summary>
/// A scene is a conversation (user feedback: a single reply and a reaction felt hollow). The player can
/// reply a few times; each answer may offer what to say next, and the last one closes the moment.
/// </summary>
public static class SceneConversation
{
    /// <summary>How many times the player can reply within one scene.</summary>
    public const int MaxReplies = 4;

    /// <summary>How many times the player can reply by text: a few messages between other things, not a scene.</summary>
    public const int PhoneMaxReplies = 2;

    /// <summary>The reply limit for a scene of this encounter.</summary>
    public static int MaxRepliesFor(string? encounterId) =>
        encounterId == JsonEncounterCatalog.PhoneId ? PhoneMaxReplies : MaxReplies;

    /// <summary>The scene so far with one more exchange, as the writer reads it for the next reply.</summary>
    public static string Transcript(string sceneText, string reply, string reaction)
    {
        ArgumentNullException.ThrowIfNull(sceneText);
        ArgumentNullException.ThrowIfNull(reply);
        ArgumentNullException.ThrowIfNull(reaction);

        return $"{sceneText.TrimEnd()}\n\nYou: {reply.Trim()}\n\n{reaction.Trim()}";
    }
}

/// <summary>
/// Relationship numbers stay hidden (phase-3 plan). A reply only surfaces as a short popup when the
/// reaction is considerable, or when it trips a dealbreaker.
/// </summary>
public static class ReactionPopup
{
    /// <summary>The smallest change in affection or trust from one reply worth telling the player about.</summary>
    public const int Considerable = 4;

    public static string? For(string name, RelationshipState before, RelationshipState after)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        if (after.Dealbreaker && !before.Dealbreaker)
        {
            return $"{name} won't forget that.";
        }

        var affection = after.Affection - before.Affection;
        var trust = after.Trust - before.Trust;
        var change = Math.Abs(affection) >= Math.Abs(trust) ? affection : trust;

        return change >= Considerable ? $"{name} liked that."
            : change <= -Considerable ? $"{name} didn't like that."
            : null;
    }
}
