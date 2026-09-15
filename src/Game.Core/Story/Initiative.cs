using Game.Core.Scenes;
using Game.Core.World;

namespace Game.Core.Story;

/// <summary>
/// Whether someone seeks the player out on a turn with nothing else going on (phase-3 plan: initiative
/// follows temper and play). Bolder tempers take the lead sooner, the longer they have not seen the
/// player the likelier it gets, and a player who never asks them along leaves more room for them to
/// ask instead; a player who always leads leaves less. Pure and deterministic, so it can be tested.
/// </summary>
public static class Initiative
{
    public const double Base = 0.12;
    public const double PerDayUnseen = 0.04;
    public const double MaxChance = 0.6;

    /// <summary>Days after seeking the player out before the same person does it again.</summary>
    public const int CooldownDays = 3;

    /// <summary>Invitations from the player at which they count as the one leading.</summary>
    public const int LeadingInvites = 3;

    /// <param name="temperScale">The product of their temper ends' initiative scales.</param>
    /// <param name="playerInvites">How often the player has asked them along.</param>
    public static double Chance(RouteStatus status, double temperScale, int playerInvites, int today)
    {
        ArgumentNullException.ThrowIfNull(status);

        if (!status.Open || status.LastSeenDay is not { } seen || today - seen < 1 || status.State.Dealbreaker)
        {
            return 0;
        }

        var unseen = today - seen;
        var stage = status.State.Stage is RelationshipStage.Acquaintance ? 0.5 : 1.0;
        var lead = playerInvites == 0 ? 1.5 : playerInvites >= LeadingInvites ? 0.6 : 1.0;

        return Math.Min(MaxChance, (Base + PerDayUnseen * (unseen - 1)) * temperScale * stage * lead);
    }

    public static bool CoolingDown(string? lastDay, int today) =>
        int.TryParse(lastDay, out var last) && today - last < CooldownDays;

    /// <summary>A roll that is the same for the same save, person and turn.</summary>
    public static bool Rolls(string saveKey, string key, ClockState clock, double chance)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(saveKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        return chance > 0 && Unit($"{saveKey}|{key}|{clock.Day}|{clock.Slot}|initiative") < chance;
    }

    /// <summary>FNV-1a, then a SplitMix64 finaliser, mapped onto [0, 1): a roll that is the same for the same text.</summary>
    public static double Unit(string text)
    {
        var hash = 14695981039346656037UL;
        foreach (var ch in text)
        {
            hash = unchecked((hash ^ ch) * 1099511628211UL);
        }

        hash = (hash ^ (hash >> 30)) * 0xBF58476D1CE4E5B9UL;
        hash = (hash ^ (hash >> 27)) * 0x94D049BB133111EBUL;
        hash ^= hash >> 31;

        return (hash >> 11) * (1.0 / (1UL << 53));
    }
}
