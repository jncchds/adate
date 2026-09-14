namespace Game.Core.Story;

/// <summary>How one reply moved one person who was there.</summary>
public sealed record ChoiceEffect(string Name, int Affection, int Trust, bool Dealbreaker = false);

/// <summary>A reply or authored choice the player made, and what it did (phase-3 plan: endings).</summary>
public sealed record ChoiceRecord(int Day, string Slot, string Words, IReadOnlyList<ChoiceEffect> Effects);

/// <summary>One line of the recap: what the player did, and who it touched and how.</summary>
public sealed record RecapLine(int Day, string Slot, string Words, IReadOnlyList<string> Influence);

/// <summary>
/// The choice recap at the end (phase-3 plan: like choice-based games). Numbers stay hidden to the
/// end; each choice shows who it moved and which way, and choices that moved nobody are left out.
/// </summary>
public static class ChoiceRecap
{
    /// <summary>The smallest change that counts as influence at all.</summary>
    public const int Noticeable = 2;

    /// <summary>Most choices shown, the most influential first, then back in story order.</summary>
    public const int MaxLines = 12;

    public static IReadOnlyList<RecapLine> For(IEnumerable<ChoiceRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);

        return
        [
            .. records
                .Select((record, order) => (record, order, lines: Influence(record.Effects), weight: record.Effects.Sum(Weight)))
                .Where(r => r.lines.Count > 0)
                .OrderByDescending(r => r.weight)
                .Take(MaxLines)
                .OrderBy(r => r.order)
                .Select(r => new RecapLine(r.record.Day, r.record.Slot, r.record.Words, r.lines)),
        ];
    }

    public static string? Describe(ChoiceEffect effect)
    {
        ArgumentNullException.ThrowIfNull(effect);

        if (effect.Dealbreaker)
        {
            return $"{effect.Name} won't forget that";
        }

        var main = Math.Abs(effect.Affection) >= Math.Abs(effect.Trust) ? effect.Affection : effect.Trust;
        if (Math.Abs(main) < Noticeable)
        {
            return null;
        }

        var strongly = Math.Abs(main) >= ReactionPopup.Considerable;
        return (main == effect.Affection, main > 0) switch
        {
            (true, true) => strongly ? $"{effect.Name} warmed to you" : $"{effect.Name} liked that",
            (true, false) => strongly ? $"{effect.Name} cooled towards you" : $"{effect.Name} didn't like that",
            (false, true) => strongly ? $"{effect.Name} trusted you more" : $"{effect.Name} trusted you a little more",
            (false, false) => strongly ? $"{effect.Name} trusted you less" : $"{effect.Name} trusted you a little less",
        };
    }

    private static List<string> Influence(IReadOnlyList<ChoiceEffect> effects) =>
        [.. effects.Select(Describe).OfType<string>()];

    private static int Weight(ChoiceEffect effect) =>
        (effect.Dealbreaker ? 100 : 0) + Math.Max(Math.Abs(effect.Affection), Math.Abs(effect.Trust));
}
