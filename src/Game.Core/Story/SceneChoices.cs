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
/// A scene is a conversation (user feedback: a single reply and a reaction felt hollow). It runs for as
/// long as it is going somewhere: the writer says when the moment has run its course, and until it does
/// each answer offers what to say next.
/// </summary>
/// <remarks>
/// <para>
/// Nothing here is a budget the player is spending. A conversation used to stop dead at four replies
/// whatever was being said (user request: do not limit the length, and do not make it endless either), so
/// the count is no longer shown to the writer as a quota. What ends a scene is the writer judging it over.
/// </para>
/// <para>
/// A ceiling remains, because a writer that never says so would hold the player in one slot forever and
/// grow the prompt by an exchange every turn: the whole conversation is sent back with each reply. It sits
/// far above any moment worth writing, and the wind-down below is what a long scene should actually end by,
/// so reaching the ceiling means something has gone wrong rather than that a scene was long.
/// </para>
/// </remarks>
public static class SceneConversation
{
    /// <summary>
    /// The most replies a scene will take before it is closed whatever the writer says. A runaway guard,
    /// not a length: a moment that has taken this many turns stopped going anywhere long before.
    /// </summary>
    public const int ReplyCeiling = 20;

    /// <summary>The same for texting, which is a few messages between other things rather than a scene.</summary>
    public const int PhoneReplyCeiling = 8;

    /// <summary>
    /// The reply after which the writer is asked to start bringing the moment to a close. Nothing stops at
    /// it; it is where a scene that has said what it had to say is nudged towards an ending, so conversations
    /// finish by running their course rather than by hitting the ceiling.
    /// </summary>
    public const int WindDownAfter = 6;

    public const int PhoneWindDownAfter = 3;

    /// <summary>The ceiling for a scene of this encounter.</summary>
    public static int CeilingFor(string? encounterId) =>
        encounterId == JsonEncounterCatalog.PhoneId ? PhoneReplyCeiling : ReplyCeiling;

    /// <summary>Where a scene of this encounter starts being nudged towards its ending.</summary>
    public static int WindDownFor(string? encounterId) =>
        encounterId == JsonEncounterCatalog.PhoneId ? PhoneWindDownAfter : WindDownAfter;

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
