namespace Game.Core;

/// <summary>
/// Content ceiling. Threaded through every save, every generation request and every
/// cache row (HANDOFF 1.8). Cached art produced at one ceiling must never be served
/// into a session running at another, so this participates in cache addressing.
/// </summary>
/// <remarks>
/// Numeric values are persisted in SQLite. Never renumber an existing member.
/// </remarks>
public enum Ceiling
{
    PG13 = 0,
    Suggestive = 1,
    Explicit = 2,
}
