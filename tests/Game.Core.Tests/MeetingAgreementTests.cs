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
    [InlineData(TimeOfDay.Morning, 5, TimeOfDay.Midday)]
    [InlineData(TimeOfDay.Evening, 5, TimeOfDay.Night)]
    [InlineData(TimeOfDay.Night, 6, TimeOfDay.Morning)]
    public void Going_together_now_is_a_meeting_in_the_next_slot(TimeOfDay madeIn, int dueDay, TimeOfDay dueSlot)
    {
        var madeAt = new ClockState(5, madeIn);
        var promise = MeetingAgreement.ToPromise(new ProposedMeeting("The Corner Cup", 2, MeetingAgreement.NowSlot), "kai-id", madeAt, 28, Known);

        Assert.NotNull(promise);
        Assert.Equal(dueDay, promise.DueDay);
        Assert.Equal(dueSlot, promise.DueSlot);
        Assert.True(MeetingAgreement.IsNow(promise));
        Assert.True(Promises.PutsThere(promise, new ClockState(dueDay, dueSlot), "corner-cafe"));
        Assert.Same(promise, MeetingAgreement.Heading([promise], new ClockState(dueDay, dueSlot)));

        var later = MeetingAgreement.ToPromise(new ProposedMeeting("The Corner Cup", 1, "Evening"), "kai-id", madeAt, 28, Known);
        Assert.NotEqual(later!.Id, promise.Id);
        Assert.False(MeetingAgreement.IsNow(later));
        Assert.Null(MeetingAgreement.Heading([later], new ClockState(madeAt.Day + 1, TimeOfDay.Evening)));
    }

    [Fact]
    public void Later_today_is_a_meeting_like_any_other_even_in_the_very_next_slot()
    {
        var madeAt = new ClockState(5, TimeOfDay.Afternoon);

        var evening = MeetingAgreement.ToPromise(new ProposedMeeting("Riverside Park", MeetingAgreement.Now, "Evening"), "kai-id", madeAt, 28, Known);

        Assert.NotNull(evening);
        Assert.Equal((5, TimeOfDay.Evening), (evening.DueDay, evening.DueSlot!.Value));
        Assert.False(MeetingAgreement.IsNow(evening));
    }

    [Fact]
    public void A_movie_night_can_be_arranged()
    {
        var tonight = MeetingAgreement.ToPromise(new ProposedMeeting("The Corner Cup", MeetingAgreement.Now, "Night"), "kai-id", Now, 28, Known);
        var tomorrowNight = MeetingAgreement.ToPromise(new ProposedMeeting("The Corner Cup", 1, "Night"), "kai-id", Now, 28, Known);

        Assert.Equal((5, TimeOfDay.Night), (tonight!.DueDay, tonight.DueSlot!.Value));
        Assert.Equal((6, TimeOfDay.Night), (tomorrowNight!.DueDay, tomorrowNight.DueSlot!.Value));
    }

    [Theory]
    [InlineData(TimeOfDay.Evening, "Evening")]
    [InlineData(TimeOfDay.Evening, "Morning")]
    public void Today_at_a_time_that_is_now_or_gone_means_straight_away(TimeOfDay madeIn, string slot)
    {
        var promise = MeetingAgreement.ToPromise(new ProposedMeeting("Riverside Park", MeetingAgreement.Now, slot), "kai-id", new ClockState(5, madeIn), 28, Known);

        Assert.True(MeetingAgreement.IsNow(promise!));
        Assert.Equal((5, TimeOfDay.Night), (promise!.DueDay, promise.DueSlot!.Value));
    }

    [Fact]
    public void Going_together_now_on_the_last_night_is_past_the_story() =>
        Assert.Null(MeetingAgreement.ToPromise(new ProposedMeeting("Riverside Park", 0, MeetingAgreement.NowSlot), "kai-id", new ClockState(28, TimeOfDay.Night), 28, Known));

    [Theory]
    [InlineData("Nowhere Bar", 1, "Evening")]
    [InlineData("Nowhere Bar", 0, "Now")]
    [InlineData("Riverside Park", -1, "Evening")]
    [InlineData("Riverside Park", 4, "Evening")]
    [InlineData("Riverside Park", 1, "teatime")]
    [InlineData("Riverside Park", 1, "7")]
    public void Anything_the_story_cannot_hold_is_dropped(string place, int inDays, string slot) =>
        Assert.Null(MeetingAgreement.ToPromise(new ProposedMeeting(place, inDays, slot), "kai-id", Now, 28, Known));

    [Fact]
    public void Nothing_is_promised_past_the_last_day_or_without_a_meeting()
    {
        Assert.Null(MeetingAgreement.ToPromise(new ProposedMeeting("Riverside Park", 2, "Morning"), "kai-id", new ClockState(27, TimeOfDay.Morning), 28, Known));
        Assert.Null(MeetingAgreement.ToPromise(null, "kai-id", Now, 28, Known));
    }
}
