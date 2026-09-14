using Game.Core.Scenes;

namespace Game.Core.Encounters;

/// <summary>Where an encounter can happen. Every set field must hold; none set means anywhere.</summary>
/// <param name="Id">A place id from the setting.</param>
/// <param name="PlaceFlag">
/// A flag whose value is a place id, for places decided in play, such as the main LI's home place,
/// which the opening sets.
/// </param>
/// <param name="AloneVisitsBefore">How many earlier visits the player made to this place with no one else there.</param>
public sealed record EncounterPlace(string? Id = null, string? PlaceFlag = null, int? AloneVisitsBefore = null);

/// <summary>
/// Something that can happen when the player spends a slot at a place (phase-2 plan §7). Content:
/// the engine decides whether one fires, and nothing that writes prose ever does.
/// </summary>
/// <param name="Time">Slots it can happen in. Empty or absent means any.</param>
/// <param name="Days">An inclusive [first, last] day range. Absent means any day.</param>
/// <param name="Requires">Flag expressions that must all hold: <c>key</c>, <c>!key</c> or <c>key=value</c>.</param>
/// <param name="Once">Whether it can fire only once per save. The default, because most beats are beats.</param>
/// <param name="Sets">Flags it sets: <c>key</c> (to <c>true</c>) or <c>key=value</c>.</param>
/// <param name="Reveals">Place ids that become known.</param>
/// <param name="With">Who is there: <c>main_li</c> or <c>variant:{route}</c>. Recorded on the visit.</param>
/// <param name="Priority">Higher wins when several match. Setting events use <c>100</c>.</param>
/// <param name="Text">
/// Placeholder prose until the LLM writes scenes (build step 9). May use <c>{main_li}</c>,
/// <c>{player}</c>, <c>{place}</c> and <c>{slot}</c>, filled in when shown.
/// </param>
/// <param name="Choices">
/// A choice the player makes before the next turn. While one is open, no turn can be taken.
/// </param>
public sealed record EncounterDefinition(
    string Id,
    EncounterPlace Place,
    IReadOnlyList<TimeOfDay>? Time = null,
    IReadOnlyList<int>? Days = null,
    IReadOnlyList<string>? Requires = null,
    bool Once = true,
    IReadOnlyList<string>? Sets = null,
    IReadOnlyList<string>? Reveals = null,
    IReadOnlyList<string>? With = null,
    int Priority = 50,
    string Text = "",
    IReadOnlyList<EncounterChoice>? Choices = null);

/// <summary>One answer to an encounter's choice. Its flags are set when the player picks it.</summary>
public sealed record EncounterChoice(string Id, string Text, IReadOnlyList<string>? Sets = null);

public interface IEncounterCatalog
{
    /// <summary>A setting's encounters: shared ones, its own, and one per dated event.</summary>
    IReadOnlyList<EncounterDefinition> For(string settingId);
}
