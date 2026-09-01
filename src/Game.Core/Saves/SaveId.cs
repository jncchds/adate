namespace Game.Core.Saves;

/// <summary>
/// HANDOFF 1.4: everything is scoped to a save from day one, even while exactly one
/// save exists. A dedicated type stops a save id from being passed where a character id
/// or a location id belongs.
/// </summary>
public readonly record struct SaveId(Guid Value)
{
    public static SaveId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString("D");

    public static SaveId Parse(string s) => new(Guid.Parse(s));
}
