using Game.Core.Story;

namespace Game.Data.Repositories;

/// <summary>How a save ended. <paramref name="SummaryJson"/> is the recap the host shows afterwards.</summary>
public sealed record StoredEnding(EndingKind Kind, Guid? PartnerId, int Day, string SummaryJson);
