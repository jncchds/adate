using Game.Core.Scenes;
using Game.Core.Style;

namespace Game.Core.Content;

/// <summary>
/// A scene intent that has passed the content gate, together with the ceiling it passed at.
/// </summary>
/// <remarks>
/// <para>
/// The image pipeline does not decide what may be depicted. The story model decides what
/// happens, that decision is checked once here, and what reaches the generator is a task that
/// has already been vetted. Putting the check in the prompt compiler instead would mean the
/// image side re-litigating a question that was settled upstream, in a class that has no idea
/// how old the character is.
/// </para>
/// <para>
/// This type cannot be constructed directly. <see cref="Approve"/> requires a
/// <see cref="ContentDecision"/>, and only <see cref="ContentPolicy.Resolve"/> produces one of
/// those, so the chain from a character's age to a rendered image has no branch that skips
/// the gate.
/// </para>
/// </remarks>
public sealed class ApprovedIntent
{
    private ApprovedIntent(SceneIntent intent, Ceiling ceiling, IReadOnlyList<string> removed)
    {
        Intent = intent;
        Ceiling = ceiling;
        Removed = removed;
    }

    /// <summary>The intent as it may be rendered, with any refused terms already gone.</summary>
    public SceneIntent Intent { get; }

    /// <summary>The ceiling this intent was approved at.</summary>
    public Ceiling Ceiling { get; }

    /// <summary>
    /// Terms the gate removed. Not an error, and not shown to the player: the story model
    /// writes these fields and will occasionally reach past the ceiling, which costs the scene
    /// that term rather than the player their turn. Surfaced so it can be logged, because a
    /// gate that removes things silently and invisibly is one nobody notices misfiring.
    /// </summary>
    public IReadOnlyList<string> Removed { get; }

    /// <summary>
    /// Vets <paramref name="intent"/> against the ceiling in <paramref name="decision"/>.
    /// </summary>
    /// <remarks>
    /// Only the fields the story model writes are filtered — outfit, pose, expression and a
    /// story place's look. Everything else in a prompt is authored content or a player-declared
    /// attribute, and filtering those would mean second-guessing the game's own data.
    /// </remarks>
    public static ApprovedIntent Approve(ContentDecision decision, SceneIntent intent, StylePack pack)
    {
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(pack);

        var removed = new List<string>();

        return new ApprovedIntent(
            intent with
            {
                Outfit = Filter(intent.Outfit),
                Pose = Filter(intent.Pose),
                Expression = Filter(intent.Expression),
                LocationLook = intent.LocationLook is { } look ? Filter(look) : null,
            },
            decision.Ceiling,
            removed);

        string Filter(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return value;

            var kept = new List<string>();

            foreach (var tag in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (pack.PermitsPositive(tag, decision.Ceiling))
                {
                    kept.Add(tag);
                }
                else
                {
                    removed.Add(tag);
                }
            }

            return string.Join(", ", kept);
        }
    }
}
