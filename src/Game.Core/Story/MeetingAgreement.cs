using Game.Core.Places;
using Game.Core.Scenes;
using Game.Core.World;

namespace Game.Core.Story;

/// <summary>A meeting the writer says was just agreed: a place by name, how many days ahead, and when.</summary>
public sealed record ProposedMeeting(string Place, int InDays, string Slot);

/// <summary>
/// Turns an agreed meeting into a promise C# can hold the player to (phase-3 plan: promises made in
/// play). The writer only proposes; a place the player does not know, a day past the story, a night
/// or more than a few days ahead is dropped rather than trusted.
/// </summary>
public static class MeetingAgreement
{
    public const int MaxDaysAhead = 3;

    public static Promise? ToPromise(
        ProposedMeeting? meeting,
        string characterId,
        ClockState now,
        int storyDays,
        IEnumerable<(string Id, string Name)> knownPlaces)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(characterId);
        ArgumentNullException.ThrowIfNull(knownPlaces);

        if (meeting is null
            || meeting.InDays is < 1 or > MaxDaysAhead
            || now.Day + meeting.InDays > storyDays
            || !Enum.TryParse<TimeOfDay>(meeting.Slot, ignoreCase: true, out var slot)
            || slot is TimeOfDay.Night
            || string.IsNullOrWhiteSpace(meeting.Place))
        {
            return null;
        }

        var place = knownPlaces.FirstOrDefault(p => PlaceProposals.SameName(p.Name, meeting.Place));
        if (place.Id is null)
        {
            return null;
        }

        return new Promise(
            $"meet-{characterId}-{now.Day}-{now.Slot}".ToLowerInvariant(),
            characterId,
            PromiseKind.Meet,
            now.Day,
            now.Day + meeting.InDays,
            slot,
            place.Id);
    }
}
