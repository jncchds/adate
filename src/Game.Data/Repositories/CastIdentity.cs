namespace Game.Data.Repositories;

/// <summary>Who a stored cast member is: their row id, contrast profile, name and route.</summary>
/// <param name="ProfileId">Null for the main LI.</param>
/// <param name="Route">How a variant is met; null for the main LI, and for a variant not yet given one.</param>
public sealed record CastIdentity(Guid Id, string? ProfileId, string? Name, string? Route);
