using Game.Core.Scenes;

namespace Game.Core.Settings;

/// <summary>
/// A role a setting needs filled: the corner shop everyone passes, the water, the place the player works.
/// The id is what everything else points at; the type, name and details are what one save's planning made
/// of it, or what the setting authored when there was no planning.
/// </summary>
/// <param name="Type">A place type id from the place type catalog. What the role is without planning.</param>
/// <param name="Details">Detail ids from that place type. They are what make this one differ from another of its type.</param>
/// <param name="Known">Whether the player can go there from day one. Others are learned in play.</param>
/// <param name="Types">
/// The place types planning may fill this role with, the authored <paramref name="Type"/> among them; null
/// leaves the role fixed. A role with a dress code to keep, such as the player's home, offers only types
/// that share it.
/// </param>
public sealed record SettingPlace(
    string Id,
    string Type,
    string Name,
    IReadOnlyList<string>? Details = null,
    bool Known = true,
    IReadOnlyList<string>? Types = null);

/// <param name="HomePlace">Where the main LI can be found again after meeting (plan §4).</param>
/// <param name="HomeWindow">When they are there, as the writing describes it.</param>
/// <param name="SecondPlace">
/// Where the recognise beat re-arms if the player misses the home place on days 2-4: a second
/// place the main LI mentioned.
/// </param>
public sealed record SettingOpening(
    string Id,
    string Name,
    string MeetingPlace,
    TimeOfDay Time,
    string HomePlace,
    string HomeWindow,
    string Hook,
    string? SecondPlace = null);

/// <summary>A dated set piece where several cast members are in one place (plan §7).</summary>
public sealed record SettingEvent(string Id, string Name, int Day, string Place, TimeOfDay Time);

/// <summary>
/// A game setting (phase-2 plan §2): its places, the player's routine place, three openings, dated
/// events, occupation vocabulary and tone. The engine does not know what a summer camp is; this
/// file does.
/// </summary>
/// <param name="Tone">Guidance for the writing. Never enters an image prompt.</param>
/// <param name="Weather">Weights per weather id for this setting; null uses each kind's default weight.</param>
/// <param name="Job">The player's job here, with its shifts; null for a setting where the player has none.</param>
/// <param name="Home">Where the player lives, a place known from the start. Null uses the routine place.</param>
/// <param name="HomeType">The place type a love interest's home is drawn as here, when the story names it: a home place type.</param>
public sealed record SettingDefinition(
    string Id,
    string DisplayName,
    int Days,
    string Tone,
    string RoutinePlace,
    IReadOnlyList<SettingPlace> Places,
    IReadOnlyList<SettingOpening> Openings,
    IReadOnlyList<SettingEvent> Events,
    IReadOnlyList<string> Occupations,
    IReadOnlyDictionary<string, int>? Weather = null,
    PlayerJob? Job = null,
    string? Home = null,
    string? HomeType = null)
{
    public SettingPlace Place(string placeId) =>
        Places.FirstOrDefault(p => string.Equals(p.Id, placeId, StringComparison.Ordinal))
        ?? throw new KeyNotFoundException($"Setting '{Id}' has no place '{placeId}'.");
}

public interface ISettingCatalog
{
    SettingDefinition Get(string settingId);

    IReadOnlyList<SettingDefinition> All();
}
