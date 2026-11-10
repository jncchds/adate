using System.Text.RegularExpressions;
using Game.Core.Places;

namespace Game.Core.Story;

/// <summary>
/// A moment worth seeing, drawn as one picture with the people in it (user request: on special occasions the love
/// interests are drawn into the scene itself, doing what the words say, instead of standing as cut-outs on the place).
/// </summary>
/// <param name="Shot">What the picture shows them doing, in the writer's words, already cleaned.</param>
/// <param name="People">Who is in it, left to right as they stood on the stage when it was called for.</param>
/// <param name="PicturePath">The picture, once drawn.</param>
public sealed record SceneMoment(string Shot, IReadOnlyList<Guid> People, string? PicturePath = null);

/// <summary>
/// When a moment is drawn, and for how long. The writer decides that a moment is worth seeing, in a scene's words or in
/// any answer, with no budget: the game is local-first. C# decides how long one picture can stand for what is happening.
/// </summary>
public static partial class Moments
{
    /// <summary>Long enough for an action with its pose and where they look, short enough to stay one shot.</summary>
    public const int MaxShotLength = 120;

    /// <summary>
    /// The writer's shot in plain words, at most <see cref="MaxShotLength"/> characters, cut at a comma or a space;
    /// null when nothing usable is left, which is no moment. It reaches the picture's prompt through the content gate,
    /// which filters it phrase by phrase like an outfit.
    /// </summary>
    public static string? CleanShot(string? shot)
    {
        var text = Spaces().Replace(shot?.Trim() ?? "", " ").Trim(' ', ',', '.', ';');
        if (text.Length > MaxShotLength)
        {
            var cut = text[..(MaxShotLength + 1)];
            var at = cut.LastIndexOf(',') is > 0 and var comma ? comma : cut.LastIndexOf(' ');
            text = (at > 0 ? cut[..at] : text[..MaxShotLength]).Trim(' ', ',', '.', ';');
        }

        return text.Length > 0 && Words().IsMatch(text) ? text : null;
    }

    /// <summary>Whether a shot can be drawn: the image model reads English, so it must be in the Latin alphabet.</summary>
    public static bool IsDrawable(string? shot) => PlaceProposals.IsDrawable(shot);

    /// <summary>
    /// The moment once a block of words is shown. The same shot again keeps the picture while the same people stand
    /// there in the same clothes; a new shot is a new picture of whoever stands there now; no shot, or nobody standing
    /// there, is no moment, and the people stand on the place again.
    /// </summary>
    /// <param name="current">The moment on screen before these words; null for none.</param>
    /// <param name="shot">What the words call for, uncleaned; null when they call for nothing.</param>
    /// <param name="figures">Who stands on the stage once the words are shown.</param>
    /// <param name="redressed">Whether someone on the stage changed what they wear in these words.</param>
    public static SceneMoment? After(SceneMoment? current, string? shot, IReadOnlyList<SceneFigure> figures, bool redressed)
    {
        ArgumentNullException.ThrowIfNull(figures);

        if (figures.Count == 0 || CleanShot(shot) is not { } clean || !IsDrawable(clean))
        {
            return null;
        }

        IReadOnlyList<Guid> people = [.. figures.Select(f => f.CharacterId)];

        return current is not null
               && !redressed
               && string.Equals(current.Shot, clean, StringComparison.OrdinalIgnoreCase)
               && current.People.SequenceEqual(people)
            ? current
            : new SceneMoment(clean, people);
    }

    /// <summary>
    /// The moment when the words could not say: a failed writer or no model. The picture stays only while it still
    /// shows who is there in what they wear.
    /// </summary>
    public static SceneMoment? Unsaid(SceneMoment? current, IReadOnlyList<SceneFigure> figures, bool redressed) =>
        After(current, current?.Shot, figures, redressed);

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"^[\p{L}\p{M}\p{N}][\p{L}\p{M}\p{N} ,.'&()\-]*$")]
    private static partial Regex Words();
}
