using Game.App.Controls;

namespace Game.App.Tests;

public class TapTriggerTests
{
    [Fact]
    public void Ten_quick_taps_open_the_door_and_slow_ones_never_do()
    {
        var start = DateTimeOffset.UnixEpoch;
        var quick = new TapTrigger();

        Assert.All(Enumerable.Range(0, 9), i => Assert.False(quick.Tap(start.AddMilliseconds(i * 200))));
        Assert.True(quick.Tap(start.AddMilliseconds(1800)));
        Assert.False(quick.Tap(start.AddMilliseconds(2000)), "The count starts again after the door opens.");

        var slow = new TapTrigger();
        Assert.All(Enumerable.Range(0, 30), i => Assert.False(slow.Tap(start.AddSeconds(i * 0.5))));
    }
}
