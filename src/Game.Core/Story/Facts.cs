using System.Globalization;
using Game.Core.Characters;

namespace Game.Core.Story;

/// <summary>How far a fact can be relied on (plan §8).</summary>
public enum FactLevel
{
    /// <summary>From the story bible or the character's appearance.</summary>
    Core,

    /// <summary>Shown on screen.</summary>
    Established,

    /// <summary>Said by a character, and allowed to be false. Secrets work this way.</summary>
    Claimed,
}

/// <summary>A triple from the controlled predicate list, with its level, source and day.</summary>
public sealed record Fact(string Subject, string Predicate, string Object, FactLevel Level, string Source, int Day);

/// <summary>A stored fact and who knows it: <see cref="FactLedger.Player"/> or a character id.</summary>
public sealed record KnownFact(long Id, Fact Fact, IReadOnlySet<string> Knowers);

public enum FactVerdict
{
    Accepted,

    /// <summary>Already held; nothing new is stored, though new knowers can still learn it.</summary>
    Duplicate,

    /// <summary>Stored, replacing the facts in <see cref="FactCheck.Supersedes"/>.</summary>
    Superseded,

    Rejected,
}

public sealed record FactCheck(FactVerdict Verdict, IReadOnlyList<long> Supersedes, string? Reason = null, long? ExistingId = null)
{
    public bool Stored => Verdict is FactVerdict.Accepted or FactVerdict.Superseded;

    internal static FactCheck Accept() => new(FactVerdict.Accepted, []);

    internal static FactCheck Reject(string reason) => new(FactVerdict.Rejected, [], reason);
}

/// <summary>
/// Decides whether a proposed fact can join the ones held (plan §8). A single-valued immutable fact
/// that conflicts is rejected; a mutable one supersedes the old value, but only with an event that
/// explains the change. Claims may be false, so they never conflict.
/// </summary>
public static class FactLedger
{
    /// <summary>The knower id for the player.</summary>
    public const string Player = "player";

    public static FactCheck Check(IEnumerable<KnownFact> current, Fact proposed, PredicateDefinition? predicate, string? explainedBy)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(proposed);

        if (predicate is null || predicate.Id != proposed.Predicate)
        {
            return FactCheck.Reject($"'{proposed.Predicate}' is not a known predicate.");
        }

        if (string.IsNullOrWhiteSpace(proposed.Subject) || string.IsNullOrWhiteSpace(proposed.Object))
        {
            return FactCheck.Reject("A fact needs a subject and an object.");
        }

        var same = current
            .Where(f => f.Fact.Subject == proposed.Subject && f.Fact.Predicate == proposed.Predicate)
            .ToList();

        var equal = same.Where(f => f.Fact.Object == proposed.Object).ToList();
        if (equal.Count > 0)
        {
            // Only claims hold this value, and now it is shown: the claim is confirmed.
            if (proposed.Level is not FactLevel.Claimed && equal.All(f => f.Fact.Level is FactLevel.Claimed))
            {
                return new FactCheck(FactVerdict.Superseded, [.. equal.Select(f => f.Id)]);
            }

            var existing = equal.First(f => proposed.Level is FactLevel.Claimed || f.Fact.Level is not FactLevel.Claimed);
            return new FactCheck(FactVerdict.Duplicate, [], ExistingId: existing.Id);
        }

        if (proposed.Level is FactLevel.Claimed || predicate.Multi)
        {
            return FactCheck.Accept();
        }

        var conflicting = same.Where(f => f.Fact.Level is not FactLevel.Claimed).ToList();
        if (conflicting.Count == 0)
        {
            return FactCheck.Accept();
        }

        var held = string.Join(", ", conflicting.Select(f => $"'{f.Fact.Object}'"));

        if (!predicate.Mutable)
        {
            return FactCheck.Reject(
                $"'{proposed.Subject} {proposed.Predicate} {proposed.Object}' contradicts {held}, which cannot change.");
        }

        if (string.IsNullOrWhiteSpace(explainedBy))
        {
            return FactCheck.Reject(
                $"'{proposed.Subject} {proposed.Predicate} {proposed.Object}' changes {held}, and a change needs an event that explains it.");
        }

        return new FactCheck(FactVerdict.Superseded, [.. conflicting.Select(f => f.Id)]);
    }

    /// <summary>A character's appearance as immutable core facts (plan §8), known from day 0.</summary>
    public static IReadOnlyList<Fact> AppearanceFacts(string characterId, CharacterAppearance appearance)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(characterId);
        ArgumentNullException.ThrowIfNull(appearance);

        Fact Core(string predicate, string value) => new(characterId, predicate, value, FactLevel.Core, "appearance", 0);

        return
        [
            Core("age", appearance.Age.ToString(CultureInfo.InvariantCulture)),
            Core("hair-color", appearance.HairColor),
            Core("hair-style", appearance.HairStyle),
            Core("eye-color", appearance.EyeColor),
            Core("skin-tone", appearance.SkinTone),
        ];
    }
}

/// <summary>Who knows what (plan §8).</summary>
public static class Knowledge
{
    /// <summary>
    /// The facts a scene may use: what the player knows, plus what any character present knows.
    /// </summary>
    public static IReadOnlyList<KnownFact> ForScene(IEnumerable<KnownFact> facts, IReadOnlyCollection<string> present)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(present);

        return [.. facts.Where(f => f.Knowers.Contains(FactLedger.Player) || present.Any(f.Knowers.Contains))];
    }

    public static bool Knows(IEnumerable<KnownFact> facts, string who, string subject, string predicate, string value) =>
        facts.Any(f => f.Knowers.Contains(who)
            && f.Fact.Subject == subject
            && f.Fact.Predicate == predicate
            && f.Fact.Object == value);
}
