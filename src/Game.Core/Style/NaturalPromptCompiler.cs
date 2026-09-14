using Game.Core.Characters;
using Game.Core.Content;
using Game.Core.Scenes;

namespace Game.Core.Style;

/// <summary>
/// Compiles descriptive sentences for checkpoints with a language-model text encoder, such as
/// Z-Image's Qwen encoder, which read prose rather than danbooru tags.
/// </summary>
/// <remarks>
/// <para>
/// The same contract as <see cref="BooruPromptCompiler"/>: a fixed, deterministic order that is
/// part of the cache key, the age band before any body descriptor (HANDOFF 1.9), and the three
/// story-written fields taken from an already-approved intent. Only the surface differs --
/// blocks become sentences, and every word still comes from the pack or the character record.
/// </para>
/// <para>
/// Sprites say outright that there is one person on a plain background. Measured on Z-Image:
/// a half-body sprite left space beside the subject that the model filled with a second figure,
/// and matting keeps whatever is salient.
/// </para>
/// </remarks>
public sealed class NaturalPromptCompiler(ILocationCatalog locations) : IPromptCompiler
{
    public PromptDialect Dialect => PromptDialect.Natural;

    public string CompilePositive(
        CharacterAppearance? appearance,
        ApprovedIntent approved,
        StylePack pack,
        RenderTarget target)
    {
        ArgumentNullException.ThrowIfNull(approved);
        ArgumentNullException.ThrowIfNull(pack);

        var intent = approved.Intent;
        var sentences = new List<string>(8);

        // 1. Pack style sentence.
        Add(sentences, pack.PositivePrefix);

        if (target is RenderTarget.Background)
        {
            var location = locations.Get(intent.LocationId);

            if (string.IsNullOrWhiteSpace(location.Description))
            {
                throw new InvalidOperationException(
                    $"Location '{location.Id}' has no description, so it cannot be described to a " +
                    "natural-language checkpoint. Its tags are booru vocabulary and would be read as prose.");
            }

            Add(sentences, location.Description);

            var details = (intent.LocationDetails ?? []).Select(id => location.Detail(id).Phrase).ToList();
            if (details.Count > 0)
            {
                Add(sentences, $"With {string.Join(", ", details)}");
            }

            if (location.TimeDescriptions is not null &&
                location.TimeDescriptions.TryGetValue(intent.Time.ToString(), out var time))
            {
                Add(sentences, time);
            }

            // A stray figure in a cached background is permanent. Kept short on purpose. Measured:
            // spelling it out ("no pedestrians, no figures in the distance") added figures in 4 of
            // 12 renders against about 1 in 90 for this sentence, and dropping it added some
            // too. Naming the thing primes it.
            Add(sentences, "An empty scene with no people in it");

            return Join(sentences);
        }

        if (appearance is null)
        {
            throw new ArgumentNullException(
                nameof(appearance),
                $"A {target} prompt describes a character, so appearance is required.");
        }

        appearance.Validate();

        var subject = pack.SubjectFor(appearance.Subject);

        // 2. Subject, skin tone, age band, build. Measured: in the identity list declared brown skin
        //    rendered fair; placed next to the subject with pack wording it renders brown. Skin tone
        //    is not the kind of descriptor HANDOFF 1.9 orders the age band against -- that is build
        //    and height, the words tag checkpoints read as juvenile -- so build still follows the band.
        Add(sentences,
        [
            .. subject.Positive,
            Words(subject, AppearanceFeatures.SkinTone, appearance.SkinTone),
            .. subject.BandFor(appearance.Age).Tags,
            $"with {Words(subject, AppearanceFeatures.Build, appearance.Build)}",
        ]);

        // 3. Identity, hair colour first because it drifts first (HANDOFF 2).
        Add(sentences,
        [
            appearance.HairColor,
            appearance.HairStyle,
            appearance.EyeColor,
            appearance.Height,
            appearance.DistinguishingFeature,
        ]);

        // 4. Performance. Already through the content gate.
        if (!string.IsNullOrWhiteSpace(intent.Outfit))
        {
            Add(sentences, $"Wearing {intent.Outfit.Trim()}");
        }

        Add(sentences, intent.Expression);
        Add(sentences, [intent.Pose, FramingPhrase(intent.Framing)]);

        // 5. Restate what the prior drops, last before the background. Measured: a declared beard
        //    was on every portrait and gone from every sprite, where the expression and outfit
        //    sentences sit between it and the end of the prompt.
        Add(sentences, Restatement(appearance, subject));

        // 6. Background handling.
        Add(sentences, target is RenderTarget.Sprite
            ? "Only this one person, isolated on a plain flat light grey background with no scenery and no other people"
            : "A plain light grey studio background with no other people");

        return Join(sentences);
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

        var terms = new List<string>(32);
        terms.AddRange(pack.NegativeBase);

        if (target is not RenderTarget.Background)
        {
            // Unconditional, as in the booru compiler: no configuration omits these.
            terms.AddRange(pack.AlwaysNegative);
        }

        if (pack.NegativeByCeiling.TryGetValue(ceiling.ToString(), out var ceilingTerms))
        {
            terms.AddRange(ceilingTerms);
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

            terms.AddRange(pack.SubjectFor(subject).Negative);
        }

        if (target is RenderTarget.Sprite)
        {
            terms.AddRange(["detailed background", "scenery", "second person", "extra people"]);
        }

        if (target is RenderTarget.Background)
        {
            terms.AddRange(["people", "person", "human figure", "character"]);
        }

        return string.Join(", ", Distinct(terms));
    }

    /// <summary>"The same person: pale skin, an average build and freckles", skipping blanks.</summary>
    /// <remarks>A list after a colon rather than "with …", so pack wording such as
    /// "dark-skinned with dark brown skin" still reads as a sentence.</remarks>
    private static string Restatement(CharacterAppearance appearance, SubjectProfile subject)
    {
        var parts = Distinct(
        [
            Words(subject, AppearanceFeatures.SkinTone, appearance.SkinTone),
            Words(subject, AppearanceFeatures.Build, appearance.Build),
            appearance.DistinguishingFeature,
        ]);

        var listed = parts.Count switch
        {
            0 => "",
            1 => parts[0],
            _ => string.Join(", ", parts[..^1]) + " and " + parts[^1],
        };

        return listed.Length == 0 ? "" : $"The same person: {listed}";
    }

    /// <summary>The pack's prompt wording for a chosen value, or the value itself.</summary>
    private static string Words(SubjectProfile subject, string feature, string value)
    {
        foreach (var option in subject.OptionsFor(feature))
        {
            if (string.Equals(option.Tag, value.Trim(), StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(option.Prompt))
            {
                return option.Prompt.Trim();
            }
        }

        return value.Trim();
    }

    private static string FramingPhrase(Framing framing) => framing switch
    {
        Framing.Portrait => "a close-up portrait of the head and shoulders",
        Framing.Bust => "an upper body shot from the chest up",
        Framing.HalfBody => "a half body shot from the waist up",
        Framing.FullBody => "a full body shot from head to toe",
        _ => throw new ArgumentOutOfRangeException(nameof(framing), framing, "Unhandled framing."),
    };

    private static void Add(List<string> sentences, string? phrase) => Add(sentences, [phrase]);

    /// <summary>One sentence from the non-blank phrases, or nothing if every phrase is blank.</summary>
    private static void Add(List<string> sentences, IEnumerable<string?> phrases)
    {
        var kept = Distinct(phrases);
        if (kept.Count > 0)
        {
            sentences.Add(string.Join(", ", kept));
        }
    }

    private static List<string> Distinct(IEnumerable<string?> phrases)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var kept = new List<string>();

        foreach (var phrase in phrases)
        {
            var trimmed = phrase?.Trim().TrimEnd('.');
            if (!string.IsNullOrWhiteSpace(trimmed) && seen.Add(trimmed))
            {
                kept.Add(trimmed);
            }
        }

        return kept;
    }

    /// <summary>Sentences capitalised and full-stopped, joined with single spaces.</summary>
    private static string Join(List<string> sentences) =>
        string.Join(" ", sentences.Select(static s => char.ToUpperInvariant(s[0]) + s[1..] + "."));
}
