using Game.Core.Scenes;

namespace Game.Core.Story;

/// <summary>
/// What kind of day it is in town, rolled for the save and day (the same on a reload). Days had one shape: the
/// same chances every day, so the world never felt busier or emptier. A shape changes how often people are out
/// of their routine, how often strangers are met by chance, and how often someone seeks the player out, and it
/// gives the writer a line to put in the air.
/// </summary>
public enum DayShape
{
    Ordinary,
    Quiet,
    Busy,
    Lively,
}

public sealed record DayEffects(double Wander, double MeetByChance, double Initiative, string? Line);

public static class DayShapes
{
    public static DayShape For(string saveKey, int day)
    {
        var roll = Initiative.Unit($"{saveKey}|{day}|day-shape");
        return roll switch
        {
            < 0.5 => DayShape.Ordinary,
            < 0.7 => DayShape.Quiet,
            < 0.85 => DayShape.Busy,
            _ => DayShape.Lively,
        };
    }

    public static DayEffects Effects(DayShape shape) => shape switch
    {
        DayShape.Quiet => new(0.5, 0.7, 0.8, "It is a slow, quiet day in town; few people are about."),
        DayShape.Busy => new(1.3, 0.7, 0.6, "Everyone seems to be rushing about with something to do today."),
        DayShape.Lively => new(1.2, 1.6, 1.4, "The town is lively today; there are more people out than usual."),
        _ => new(1.0, 1.0, 1.0, null),
    };
}

/// <summary>How someone feels today. Rolled per person and day, and pulled toward wistful the longer the player stays away.</summary>
public enum Mood
{
    Cheerful,
    Calm,
    Restless,
    Wistful,
    Guarded,
}

/// <summary>What someone wants out of today.</summary>
public enum Goal
{
    /// <summary>Nothing in particular.</summary>
    None,
    Company,
    Alone,
    Errand,
    ShowSomething,
}

/// <summary>
/// A mood and a goal for a person's day (C# owns them; the writer only colours the scene with them). They
/// move how often the person seeks the player out and how often they are away from their routine, and they
/// reach the scene as one line about what the person is like today.
/// </summary>
public sealed record Disposition(Mood Mood, Goal Goal)
{
    /// <summary>Days away after which a person is wistful whatever the roll says.</summary>
    public const int WistfulAfterDays = 4;

    public static Disposition For(string saveKey, string personKey, int day, int? daysUnseen)
    {
        var moodRoll = Initiative.Unit($"{saveKey}|{personKey}|{day}|mood");
        var mood = daysUnseen >= WistfulAfterDays
            ? Mood.Wistful
            : moodRoll switch
            {
                < 0.3 => Mood.Calm,
                < 0.55 => Mood.Cheerful,
                < 0.75 => Mood.Restless,
                < 0.9 => Mood.Guarded,
                _ => Mood.Wistful,
            };

        var goalRoll = Initiative.Unit($"{saveKey}|{personKey}|{day}|goal");
        var goal = mood switch
        {
            Mood.Wistful => goalRoll < 0.7 ? Goal.Company : Goal.Errand,
            Mood.Guarded => goalRoll < 0.6 ? Goal.Alone : Goal.Errand,
            Mood.Restless => goalRoll < 0.5 ? Goal.Errand : goalRoll < 0.8 ? Goal.ShowSomething : Goal.Company,
            Mood.Cheerful => goalRoll < 0.4 ? Goal.Company : goalRoll < 0.7 ? Goal.ShowSomething : Goal.None,
            _ => goalRoll < 0.2 ? Goal.Company : goalRoll < 0.4 ? Goal.Alone : Goal.None,
        };

        return new Disposition(mood, goal);
    }

    /// <summary>How much likelier (or less likely) they are to seek the player out today.</summary>
    public double InitiativeScale => (Mood switch
    {
        Mood.Wistful => 1.4,
        Mood.Restless => 1.15,
        Mood.Cheerful => 1.1,
        Mood.Guarded => 0.6,
        _ => 1.0,
    }) * (Goal switch
    {
        Goal.Company => 1.6,
        Goal.ShowSomething => 1.3,
        Goal.Alone => 0.3,
        Goal.Errand => 0.7,
        _ => 1.0,
    });

    /// <summary>One line for the writer about how they are today; they show it in how they act, and do not announce it.</summary>
    public string Line(string name)
    {
        var mood = Mood switch
        {
            Mood.Cheerful => "in a good mood",
            Mood.Restless => "restless",
            Mood.Wistful => "a little wistful",
            Mood.Guarded => "guarded and keeping to themselves",
            _ => "calm",
        };

        var goal = Goal switch
        {
            Goal.Company => "hoping for company today",
            Goal.Alone => "wanting to be left alone for a while",
            Goal.Errand => "busy with something that needs doing",
            Goal.ShowSomething => "wanting to show or share something",
            _ => null,
        };

        return $"Today {name} is {mood}" + (goal is null ? "." : $", {goal}.") + " Let it show in small ways; do not state it outright.";
    }
}
