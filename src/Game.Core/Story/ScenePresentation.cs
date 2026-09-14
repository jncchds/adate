namespace Game.Core.Story;

/// <summary>How a scene is shown: which expression the person in front wears.</summary>
public static class ScenePresentation
{
    /// <summary>
    /// The written scene's expression when the pack can draw it, otherwise the character's resting
    /// expression, otherwise the first slot the pack offers. Matched case-insensitively and returned
    /// in the pack's own spelling, since the slot name is part of the sprite cache key.
    /// </summary>
    public static string Expression(string? written, string resting, IReadOnlyCollection<string> slots)
    {
        ArgumentNullException.ThrowIfNull(slots);

        if (slots.Count == 0)
        {
            throw new ArgumentException("The pack offers no expressions to draw.", nameof(slots));
        }

        string? Slot(string? name) =>
            name is null ? null : slots.FirstOrDefault(s => string.Equals(s, name.Trim(), StringComparison.OrdinalIgnoreCase));

        return Slot(written) ?? Slot(resting) ?? slots.First();
    }
}
