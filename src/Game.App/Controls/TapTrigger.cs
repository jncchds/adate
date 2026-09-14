namespace Game.App.Controls;

/// <summary>
/// Counts taps on one spot and fires once <see cref="Taps"/> of them land close together: a hidden door, such
/// as to the debug screen, that nobody opens by accident.
/// </summary>
public sealed class TapTrigger(int taps = 10, double withinSeconds = 4)
{
    private readonly Queue<DateTimeOffset> _recent = new();

    public int Taps { get; } = taps;

    /// <summary>Records a tap at <paramref name="at"/>; true when it completes the run, which starts counting again.</summary>
    public bool Tap(DateTimeOffset at)
    {
        _recent.Enqueue(at);
        while (_recent.Count > 0 && (at - _recent.Peek()).TotalSeconds > withinSeconds)
        {
            _recent.Dequeue();
        }

        if (_recent.Count < Taps)
        {
            return false;
        }

        _recent.Clear();
        return true;
    }
}
