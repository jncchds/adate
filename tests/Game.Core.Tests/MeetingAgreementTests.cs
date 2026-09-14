using Game.Core.Scenes;
using Game.Core.Story;
using Game.Core.World;

namespace Game.Core.Tests;

public class MeetingAgreementTests
{
    private static readonly (string Id, string Name)[] Known = [("corner-cafe", "The Corner Cup"), ("park", "Riverside Park")];
    private static readonly ClockState Now = new(5, TimeOfDay.Morning);

    [Fact]
    public void An_agreed_meeting_at_a_known_place_becomes_a_promise()
    {
        var promise = MeetingAgreement.ToPromise(new ProposedMeeting("corner cup", 2, "evening"), "kai-id", Now, 28, Known);

        Assert.NotNull(promise);
        Assert.Equal(PromiseKind.Meet, promise.Kind);
        Assert.Equal(7, promise.DueDay);
        Assert.Equal(TimeOfDay.Evening, promise.DueSlot);
        Assert.Equal("corner-cafe", promise.PlaceId);
        Assert.Equal(PromiseStatus.Open, promise.Status);

        Assert.Equal(PromiseStatus.Kept, Promises.Resolve(promise, new ClockState(7, TimeOfDay.Evening), "corner-cafe", ["kai-id"]));
        Assert.Equal(PromiseStatus.Broken, Promises.Resolve(promise, new ClockState(7, TimeOfDay.Night), "park", []));
    }

    [Theory]
    [InlineData("Nowhere Bar", 1, "Evening")]
    [InlineData("Riverside Park", 0, "Evening")]
    [InlineData("Riverside Park", 4, "Evening")]
    [InlineData("Riverside Park", 1, "Night")]
    [InlineData("Riverside Park", 1, "teatime")]
    public void Anything_the_story_cannot_hold_is_dropped(string place, int inDays, string slot) =>
        Assert.Null(MeetingAgreement.ToPromise(new ProposedMeeting(place, inDays, slot), "kai-id", Now, 28, Known));

    [Fact]
    public void Nothing_is_promised_past_the_last_day_or_without_a_meeting()
    {
        Assert.Null(MeetingAgreement.ToPromise(new ProposedMeeting("Riverside Park", 2, "Morning"), "kai-id", new ClockState(27, TimeOfDay.Morning), 28, Known));
        Assert.Null(MeetingAgreement.ToPromise(null, "kai-id", Now, 28, Known));
    }
}
