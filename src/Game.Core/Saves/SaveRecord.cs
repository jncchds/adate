namespace Game.Core.Saves;

/// <summary>
/// Mirrors the <c>save</c> table.
/// </summary>
/// <param name="StylePackId">
/// HANDOFF 1.5: locked at creation and never changed. Switching checkpoints mid-save
/// destroys character consistency, so this is immutable for the life of the save.
/// </param>
public sealed record SaveRecord(
    SaveId Id,
    string StylePackId,
    Ceiling Ceiling,
    DateTimeOffset CreatedUtc);
