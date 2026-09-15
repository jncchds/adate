using Game.Core.Places;
using Game.Core.Scenes;
using Game.Core.World;

namespace Game.Core.Story;

/// <summary>A meeting the writer says was just agreed: a place by name, how many days ahead (<see cref="MeetingAgreement.Now"/> for straight away), and when.</summary>
public sealed record ProposedMeeting(string Place, int InDays, string Slot);

/// <summary>
/// Turns an agreed meeting into a promise C# can hold the player to (phase-3 plan: promises made in
/// play). The writer only proposes; a place the player does not know, a day past the story, a night
/// or more than a few days ahead is dropped rather than trusted.
/// </summary>
/// <remarks>
/// Going somewhere together right now is a meeting in the next slot, whatever its time of day (user feedback:
/// "Lead the way, then" was agreed, and the person was nowhere to be found when the player went).
/// </remarks>
public static class MeetingAgreement
{
    public const int MaxDaysAhead = 3;

    /// <summary><see cref="ProposedMeeting.InDays"/> for going there together straight away; the slot is then ignored.</summary>
    public const int Now = 0;

    public static Promise? ToPromise(
        ProposedMeeting? meeting,
        string characterId,
        ClockState now,
        int storyDays,
        IEnumerable<(string Id, string Name)> knownPlaces)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(characterId);
        ArgumentNullException.ThrowIfNull(knownPlaces);

        if (meeting is null || string.IsNullOrWhiteSpace(meeting.Place))
        {
            return null;
        }

        ClockState due;
        if (meeting.InDays == Now)
        {
            due = now.Next();
        }
        else if (meeting.InDays is >= 1 and <= MaxDaysAhead
                 && Enum.TryParse<TimeOfDay>(meeting.Slot, ignoreCase: true, out var slot)
                 && slot is not TimeOfDay.Night)
        {
            due = new ClockState(now.Day + meeting.InDays, slot);
        }
        else
        {
            return null;
        }

        if (due.Day > storyDays)
        {
            return null;
        }

        var place = knownPlaces.FirstOrDefault(p => PlaceProposals.SameName(p.Name, meeting.Place));
        if (place.Id is null)
        {
            return null;
        }

        return new Promise(
            $"meet-{characterId}-{now.Day}-{now.Slot}{(meeting.InDays == Now ? "-now" : "")}".ToLowerInvariant(),
            characterId,
            PromiseKind.Meet,
            now.Day,
            due.Day,
            due.Slot,
            place.Id);
    }

    /// <summary>Whether <paramref name="promise"/>, made at <paramref name="madeAt"/>, is for going there together straight away.</summary>
    public static bool IsNow(Promise promise, ClockState madeAt)
    {
        ArgumentNullException.ThrowIfNull(promise);
        var next = madeAt.Next();
        return promise.DueDay == next.Day && promise.DueSlot == next.Slot;
    }
}
