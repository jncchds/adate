using Game.Core.World;

namespace Game.Core.Story;

/// <summary>Something the player could say or do next, proposed by the writer and tagged with what it shows about them.</summary>
public sealed record ProposedChoice(string Text, IReadOnlyList<string> Tags);

/// <summary>
/// A written scene waiting for the player's reply (phase-3 plan: choices). One per save; turns wait
/// until it is answered, as an authored choice does.
/// </summary>
/// <param name="With">Encounter references of the people present.</param>
public sealed record PendingScene(
    ClockState Clock,
    string PlaceId,
    string? EncounterId,
    IReadOnlyList<string> With,
    string Text,
    IReadOnlyList<ProposedChoice> Choices);

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
