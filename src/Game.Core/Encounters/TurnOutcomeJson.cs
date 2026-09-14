using System.Text.Json;

namespace Game.Core.Encounters;

/// <summary>A decided turn as stored with its scene, so the scene can be finished or shown again later.</summary>
public static class TurnOutcomeJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string Serialize(TurnOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        return JsonSerializer.Serialize(outcome, Options);
    }

    public static TurnOutcome Deserialize(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        return JsonSerializer.Deserialize<TurnOutcome>(json, Options)
            ?? throw new InvalidOperationException("A stored turn is empty.");
    }
}
