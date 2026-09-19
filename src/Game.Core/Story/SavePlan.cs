using Game.Core.Content;
using Game.Core.Places;
using Game.Core.Scenes;
using Game.Core.Settings;

namespace Game.Core.Story;

/// <summary>What the plan fills one of a setting's role slots with.</summary>
/// <param name="Role">The setting place id the slot is known by. Everything that points at a place points at this.</param>
/// <param name="Type">A place type id, from the types the role allows.</param>
/// <param name="Name">Display text, in the story's language. Never enters a prompt.</param>
/// <param name="Details">Detail ids from the chosen type.</param>
/// <param name="Look">A few English visual phrases, as a story place gets; null for the type's own look.</param>
public sealed record PlannedPlace(string Role, string Type, string Name, IReadOnlyList<string> Details, string? Look = null);

/// <summary>A dated set piece the plan puts on this save's calendar, in place of the setting's authored ones.</summary>
/// <param name="Role">The role slot it happens at.</param>
public sealed record PlannedEvent(string Id, string Name, int Day, string Role, TimeOfDay Time);

/// <summary>
/// One save's own version of its setting: what each role slot turned out to be, what is dated on the
/// calendar, and the loose ends the story means to pull on.
/// </summary>
/// <param name="EncounterTexts">Rewritten prose by encounter id, so an authored beat reads for the places this save got.</param>
public sealed record SavePlan(
    IReadOnlyList<PlannedPlace> Places,
    IReadOnlyList<PlannedEvent> Events,
    IReadOnlyList<string> Threads,
    IReadOnlyDictionary<string, string> EncounterTexts);

/// <summary>
/// Checks a plan against the setting it fills and the place-type catalog, and folds an accepted one back
/// into a setting the rest of the game reads (user request: the same setting should not lay out the same
/// town twice).
/// </summary>
/// <remarks>
/// The setting file stops being a list of places and becomes a list of roles: which types may fill each,
/// which are known from the start, and what points at them. Every id — the routine place, the player's
/// home, an opening's meeting place, an encounter's place — keeps naming a role, so nothing downstream
/// learns that the places are now written per save. A setting's authored places stay as the fallback for
/// a save planned without a model.
/// </remarks>
public static class SavePlans
{
    /// <summary>The fewest and most dated events a plan puts on the calendar.</summary>
    public const int MinEvents = 2;

    public const int MaxEvents = 5;

    /// <summary>The most loose ends a plan opens a story with.</summary>
    public const int MaxThreads = 3;

    public const int MaxThreadLength = 200;

    public const int MaxEventNameLength = 60;

    /// <summary>The days a dated event is kept away from the first and last, so nothing lands before the story starts or after it ends.</summary>
    public const int EventMargin = 3;

    /// <summary>The place types a role may be filled with: the ones it names, or the one it is authored as.</summary>
    public static IReadOnlyList<string> TypesFor(SettingPlace role)
    {
        ArgumentNullException.ThrowIfNull(role);
        return role.Types is { Count: > 0 } offered ? offered : [role.Type];
    }

    /// <summary>Why a plan cannot be used, written for a retry prompt. Empty when it can.</summary>
    public static IReadOnlyList<string> Check(SavePlan plan, SettingDefinition setting, ILocationCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(setting);
        ArgumentNullException.ThrowIfNull(catalog);

        var reasons = new List<string>();
        CheckPlaces(plan, setting, catalog, reasons);
        CheckEvents(plan, setting, reasons);

        foreach (var thread in plan.Threads ?? [])
        {
            if (string.IsNullOrWhiteSpace(thread) || thread.Length > MaxThreadLength)
            {
                reasons.Add($"A loose end must be one short sentence under {MaxThreadLength} characters; '{thread}' is not.");
            }
        }

        if ((plan.Threads?.Count ?? 0) > MaxThreads)
        {
            reasons.Add($"There are at most {MaxThreads} loose ends to open with; the plan has {plan.Threads!.Count}.");
        }

        return reasons;
    }

    private static void CheckPlaces(SavePlan plan, SettingDefinition setting, ILocationCatalog catalog, List<string> reasons)
    {
        var places = plan.Places ?? [];
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var place in places)
        {
            var role = setting.Places.FirstOrDefault(p => string.Equals(p.Id, place.Role, StringComparison.Ordinal));
            if (role is null)
            {
                reasons.Add($"'{place.Role}' is not one of the roles to fill: {string.Join(", ", setting.Places.Select(p => p.Id))}.");
                continue;
            }

            if (!seen.Add(place.Role))
            {
                reasons.Add($"Role '{place.Role}' is filled twice.");
                continue;
            }

            var offered = TypesFor(role);
            if (!offered.Contains(place.Type, StringComparer.Ordinal))
            {
                reasons.Add($"Role '{place.Role}' cannot be a {place.Type}. It may be: {string.Join(", ", offered)}.");
            }
            else
            {
                var details = place.Details ?? [];
                var known = (catalog.Get(place.Type).Details ?? []).Select(d => d.Id).ToList();

                foreach (var detail in details.Where(d => !known.Contains(d, StringComparer.Ordinal)))
                {
                    reasons.Add($"'{detail}' is not a detail of {place.Type}. It offers: {(known.Count == 0 ? "none" : string.Join(", ", known))}.");
                }

                if (details.Count > PlaceProposals.MaxDetails)
                {
                    reasons.Add($"'{place.Role}' has {details.Count} details; a place takes at most {PlaceProposals.MaxDetails}.");
                }

                if (details.Distinct(StringComparer.Ordinal).Count() != details.Count)
                {
                    reasons.Add($"'{place.Role}' lists a detail twice.");
                }
            }

            var name = place.Name?.Trim() ?? "";
            if (name.Length is 0 or > PlaceProposals.MaxNameLength)
            {
                reasons.Add($"Role '{place.Role}' needs a name of 1 to {PlaceProposals.MaxNameLength} characters; '{place.Name}' is not.");
            }
            else if (places.Any(other => !ReferenceEquals(other, place) && PlaceProposals.SameName(other.Name ?? "", name)))
            {
                reasons.Add($"More than one place is called '{name}'. Every place needs its own name.");
            }

            if (place.Look is { } look && PlaceProposals.CleanLook(look) is null && look.Trim().Length > 0)
            {
                reasons.Add($"The look of '{place.Role}' must be a few short visual phrases in English, under {PlaceProposals.MaxLookLength} characters.");
            }
            else if (!PlaceProposals.IsDrawable(place.Look))
            {
                reasons.Add(
                    $"The look of '{place.Role}' is not in English. Names are in the story's language, but every look is English, " +
                    "because it is read by an image model and never shown to the player.");
            }
        }

        foreach (var missing in setting.Places.Select(p => p.Id).Where(id => !seen.Contains(id)))
        {
            reasons.Add($"Role '{missing}' is not filled. Every role needs a place.");
        }
    }

    private static void CheckEvents(SavePlan plan, SettingDefinition setting, List<string> reasons)
    {
        var events = plan.Events ?? [];
        if (events.Count is < MinEvents or > MaxEvents)
        {
            reasons.Add($"There must be {MinEvents} to {MaxEvents} dated events; the plan has {events.Count}.");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var days = new HashSet<int>();

        foreach (var ev in events)
        {
            if (string.IsNullOrWhiteSpace(ev.Id) || !ids.Add(ev.Id))
            {
                reasons.Add($"Event id '{ev.Id}' is blank or used twice.");
            }

            if (string.IsNullOrWhiteSpace(ev.Name) || ev.Name.Length > MaxEventNameLength)
            {
                reasons.Add($"Event '{ev.Id}' needs a name under {MaxEventNameLength} characters.");
            }

            var first = EventMargin;
            var last = setting.Days - EventMargin;
            if (ev.Day < first || ev.Day > last)
            {
                reasons.Add($"Event '{ev.Id}' is on day {ev.Day}; dated events fall between day {first} and day {last}.");
            }
            else if (!days.Add(ev.Day))
            {
                reasons.Add($"Two events fall on day {ev.Day}. Spread them across the {setting.Days} days.");
            }

            if (!setting.Places.Any(p => string.Equals(p.Id, ev.Role, StringComparison.Ordinal)))
            {
                reasons.Add($"Event '{ev.Id}' is at '{ev.Role}', which is not one of the roles: {string.Join(", ", setting.Places.Select(p => p.Id))}.");
            }
        }
    }

    /// <summary>
    /// The setting as this save plays it: every role filled by the plan, and the plan's events on the
    /// calendar. The plan is expected to have passed <see cref="Check"/>; roles it does not fill keep
    /// what the setting authored.
    /// </summary>
    public static SettingDefinition Apply(SettingDefinition setting, SavePlan plan)
    {
        ArgumentNullException.ThrowIfNull(setting);
        ArgumentNullException.ThrowIfNull(plan);

        var filled = (plan.Places ?? []).ToDictionary(p => p.Role, p => p, StringComparer.Ordinal);

        return setting with
        {
            Places =
            [
                .. setting.Places.Select(role => filled.TryGetValue(role.Id, out var planned)
                    ? role with { Type = planned.Type, Name = planned.Name.Trim(), Details = planned.Details ?? [] }
                    : role),
            ],
            Events =
            [
                .. (plan.Events ?? []).OrderBy(e => e.Day).Select(e => new SettingEvent(e.Id, e.Name.Trim(), e.Day, e.Role, e.Time)),
            ],
        };
    }

    /// <summary>The places a planned save starts with: the setting's roles as the plan filled them, each with the look it was given.</summary>
    public static IReadOnlyList<PlaceRecord> Records(Saves.SaveId saveId, SettingDefinition planned, SavePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var looks = (plan.Places ?? []).ToDictionary(p => p.Role, p => PlaceProposals.CleanLook(p.Look), StringComparer.Ordinal);

        return
        [
            .. PlaceRecord.Authored(saveId, planned)
                .Select(record => looks.GetValueOrDefault(record.Id) is { } look ? record with { Look = look } : record),
        ];
    }
}
