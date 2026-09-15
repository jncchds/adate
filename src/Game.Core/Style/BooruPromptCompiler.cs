using System.Globalization;
using Game.Core.Characters;
using Game.Core.Content;
using Game.Core.Scenes;

namespace Game.Core.Style;

/// <summary>
/// Compiles comma-separated danbooru-style tags for anime checkpoints.
/// </summary>
/// <remarks>
/// <para>
/// Token order is fixed and is part of the on-disk cache contract. It is also load-bearing
/// for output quality: tag-based checkpoints weight earlier tokens more heavily, so the
/// ordering below runs from most identity-defining to most incidental. Reordering these
/// sections changes generated images and invalidates every cached one.
/// </para>
/// <para>
/// The age anchor sits immediately after the subject count, before any body descriptor.
/// HANDOFF 1.9: these checkpoints associate "petite", "slim" and "youthful" with juvenile
/// features, and an anchor placed after those tags is too late to counteract them.
/// </para>
/// </remarks>
public sealed class BooruPromptCompiler(ILocationCatalog locations) : IPromptCompiler
{
    public PromptDialect Dialect => PromptDialect.Booru;

    public string CompilePositive(
        CharacterAppearance? appearance,
        ApprovedIntent approved,
        StylePack pack,
        RenderTarget target)
    {
        ArgumentNullException.ThrowIfNull(approved);
        ArgumentNullException.ThrowIfNull(pack);

        var intent = approved.Intent;

        var tags = new List<string>(32);

        // 1. Pack quality prefix.
        tags.AddRange(pack.PositivePrefix);

        if (target is RenderTarget.Background)
        {
            AppendLocation(tags, intent);

            // Explicitly empty. Without this a scene checkpoint will populate a cafe with
            // people, and the composite would then show a character standing in a crowd
            // that the game does not know exists.
            tags.Add("no humans");
            tags.Add("scenery");

            return Join(tags);
        }

        if (appearance is null)
        {
            throw new ArgumentNullException(
                nameof(appearance),
                $"A {target} prompt describes a character, so appearance is required.");
        }

        appearance.Validate();

        // 2. Subject, and 3. the age anchor -- before any body descriptor, deliberately.
        //    Both come from the pack: the tokens that read as an adult man differ from the
        //    ones that read as an adult woman, and both differ per checkpoint.
        var subject = pack.SubjectFor(appearance.Subject);
        tags.AddRange(subject.Positive);

        // Tags, not a number. "{N} years old" is not booru vocabulary and measured as inert:
        // 19 and 65 rendered the same young woman. See AgeBand.
        tags.AddRange(subject.BandFor(appearance.Age).Tags);

        // 4. Identity. Most stable attributes first; hair colour leads because HANDOFF 2
        //    names it as the first thing to drift across sprites.
        Add(tags, appearance.HairColor);
        Add(tags, appearance.HairStyle);
        Add(tags, appearance.EyeColor);
        Add(tags, appearance.SkinTone);
        Add(tags, appearance.Build);
        Add(tags, appearance.Height);
        Add(tags, appearance.DistinguishingFeature);

        // 5. Performance: what this particular image shows. Added as given -- ApprovedIntent
        //    has already removed anything the ceiling refuses, and re-checking here would put
        //    a content decision in a class that does not know the character's age.
        Add(tags, intent.Expression);
        Add(tags, intent.Outfit);
        Add(tags, intent.Pose);
        tags.Add(FramingTag(intent.Framing));

        // 6. Background handling.
        if (target is RenderTarget.Sprite)
        {
            // The sprite is matted to alpha and composited over a separately generated
            // background. Any scenery generated here would either survive matting as a
            // fringe or be cut away as wasted pixels.
            tags.Add("simple background");
            tags.Add("white background");
            tags.Add("transparent background");
        }
        else
        {
            // Portraits are reference art for the anchor, never composited, so a plain
            // studio background keeps attention on the face without fighting the matting.
            tags.Add("simple background");
            tags.Add("upper body");
        }

        return Join(tags);
    }

    public string CompileNegative(
        StylePack pack,
        Ceiling ceiling,
        RenderTarget target,
        string? subject)
    {
        ArgumentNullException.ThrowIfNull(pack);

        if (!pack.Supports(ceiling))
        {
            throw new InvalidOperationException(
                $"Style pack '{pack.Id}' does not support ceiling {ceiling}. " +
                $"Supported: {string.Join(", ", pack.SupportedCeilings)}.");
        }

        // A negative the model never sees still changes the cache key, and reads as a protection.
        if (pack.NegativePrompts is NegativeSupport.Ignored)
        {
            return "";
        }

        var tags = new List<string>(32);
        tags.AddRange(pack.NegativeBase);

        if (target is not RenderTarget.Background)
        {
            // Unconditional, and first among the negatives so nothing later can be read as
            // qualifying them. Not gated on ceiling, subject, age or anything else: the whole
            // point is that there is no configuration under which these are absent.
            tags.AddRange(pack.AlwaysNegative);
        }

        if (pack.NegativeByCeiling.TryGetValue(ceiling.ToString(), out var ceilingTags))
        {
            tags.AddRange(ceilingTags);
        }

        if (target is not RenderTarget.Background)
        {
            if (string.IsNullOrWhiteSpace(subject))
            {
                throw new ArgumentException(
                    $"A {target} negative prompt needs the subject it is rendering, so the " +
                    "pack's subject negatives can be applied.",
                    nameof(subject));
            }

            tags.AddRange(pack.SubjectFor(subject).Negative);
        }

        if (target is RenderTarget.Sprite)
        {
            // Anything that reads as scenery survives matting as a halo around the sprite,
            // which HANDOFF 2 identifies as the fastest way to destroy the composite.
            tags.Add("detailed background");
            tags.Add("scenery");
            tags.Add("shadow");
        }

        if (target is RenderTarget.Background)
        {
            // A stray figure in a cached background is permanent: backgrounds are generated
            // once per location and reused for the life of the save.
            tags.Add("1girl");
            tags.Add("1boy");
            tags.Add("person");
            tags.Add("character");
        }

        return Join(tags);
    }

    private void AppendLocation(List<string> tags, SceneIntent intent)
    {
        var location = locations.Get(intent.LocationId);
        tags.AddRange(location.Tags);

        // A story place's look is prose for a natural-language pack; read as booru tags it would be noise, so it is left out.

        foreach (var detail in intent.LocationDetails ?? [])
        {
            tags.AddRange(location.Detail(detail).Tags);
        }

        // Weather other than clear takes the sky, so the time tags must not also name one.
        var weathered = intent.Weather is { } w && w != "clear" && location.NeutralTimeTags is not null;
        var lighting = weathered ? location.NeutralTimeTags! : location.TimeTags;

        if (lighting.TryGetValue(intent.Time.ToString(), out var timeTags))
        {
            tags.AddRange(timeTags);
        }

        if (intent.Weather is { } weather && location.WeatherTags?.TryGetValue(weather, out var weatherTags) is true)
        {
            tags.AddRange(weatherTags);
        }
    }

    private static string FramingTag(Framing framing) => framing switch
    {
        Framing.Portrait => "portrait",
        Framing.Bust => "upper body",
        Framing.HalfBody => "cowboy shot",
        Framing.FullBody => "full body",
        _ => throw new ArgumentOutOfRangeException(nameof(framing), framing, "Unhandled framing."),
    };

    private static void Add(List<string> tags, string? value)
    {
        // Blank attributes are dropped rather than emitted as empty tags. A stray ", ,"
        // in a booru prompt is not harmless: it shifts the weighting of everything after it.
        if (!string.IsNullOrWhiteSpace(value))
        {
            tags.Add(value.Trim());
        }
    }

    /// <summary>
    /// Deduplicates while preserving first-occurrence order. A tag repeated by coincidence
    /// (a pack prefix that also appears as an outfit) would otherwise gain weight silently
    /// and differently depending on which fields the player filled in.
    /// </summary>
    private static string Join(List<string> tags)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<string>(tags.Count);

        foreach (var tag in tags)
        {
            if (!string.IsNullOrWhiteSpace(tag) && seen.Add(tag.Trim()))
            {
                ordered.Add(tag.Trim());
            }
        }

        return string.Join(", ", ordered);
    }
}
