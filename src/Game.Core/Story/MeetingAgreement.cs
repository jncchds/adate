using Game.Core.Places;
using Game.Core.Scenes;
using Game.Core.World;

namespace Game.Core.Story;

/// <summary>
/// A meeting the writer says was just agreed: a place by name, how many days ahead, and when: a time of day, or
/// <see cref="MeetingAgreement.NowSlot"/> for going there together straight away.
/// </summary>
public sealed record ProposedMeeting(string Place, int InDays, string Slot);

/// <summary>
/// Turns an agreed meeting into a promise C# can hold the player to (phase-3 plan: promises made in
/// play). The writer only proposes; a place the player does not know, a day past the story, a time already
/// gone or more than a few days ahead is dropped rather than trusted.
/// </summary>
/// <remarks>
/// Going somewhere together right now is a meeting in the next slot, whatever its time of day (user feedback:
/// "Lead the way, then" was agreed, and the person was nowhere to be found when the player went). Later today and
/// nights are meetings like any other (user request: meeting later today, and a movie night).
/// </remarks>
public static class MeetingAgreement
{
    public const int MaxDaysAhead = 3;

    /// <summary><see cref="ProposedMeeting.InDays"/> for today: later today, or now.</summary>
    public const int Now = 0;

    /// <summary><see cref="ProposedMeeting.Slot"/> for going there together straight away; the days are then ignored.</summary>
    public const string NowSlot = "Now";

    private const string NowSuffix = "-now";

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
        bool together;
        var named = Enum.TryParse<TimeOfDay>(meeting.Slot, ignoreCase: true, out var slot) && Enum.IsDefined(slot);

        if (string.Equals(meeting.Slot?.Trim(), NowSlot, StringComparison.OrdinalIgnoreCase)
            // Today at a time that is now or already gone can only mean straight away.
            || (meeting.InDays == Now && named && slot <= now.Slot))
        {
            due = now.Next();
            together = true;
        }
        else if (named && meeting.InDays is >= Now and <= MaxDaysAhead)
        {
            due = new ClockState(now.Day + meeting.InDays, slot);
            together = false;
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
            $"meet-{characterId}-{now.Day}-{now.Slot}{(together ? NowSuffix : "")}".ToLowerInvariant(),
            characterId,
            PromiseKind.Meet,
            now.Day,
            due.Day,
            due.Slot,
            place.Id);
    }

    /// <summary>Whether <paramref name="promise"/> is for going there together straight away, rather than meeting later.</summary>
    public static bool IsNow(Promise promise)
    {
        ArgumentNullException.ThrowIfNull(promise);
        return promise.Kind is PromiseKind.Meet && promise.Id.EndsWith(NowSuffix, StringComparison.Ordinal);
    }

    /// <summary>
    /// The open going-together-now meeting due at <paramref name="clock"/>, if any: the player is on their way there
    /// with someone, so that is the only place to go (user request: no standing them up on the way).
    /// </summary>
    public static Promise? Heading(IEnumerable<Promise> promises, ClockState clock)
    {
        ArgumentNullException.ThrowIfNull(promises);
        return promises.FirstOrDefault(p => p.Status is PromiseStatus.Open && IsNow(p) && Promises.IsDue(p, clock));
    }
}
