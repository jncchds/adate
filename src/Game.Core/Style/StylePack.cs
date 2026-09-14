using System.Text.Json.Serialization;

namespace Game.Core.Style;

/// <summary>
/// HANDOFF 1.5: a style pack is data, not code. Everything that decides how art looks
/// lives in a JSON manifest so packs can be added without recompiling.
/// A pack is locked per save; switching checkpoints destroys character consistency.
/// </summary>
public sealed record StylePack
{
    public required string Id { get; init; }

    public required string DisplayName { get; init; }

    /// <summary>Checkpoint filename as ComfyUI sees it, e.g. <c>meinamix_v12Final.safetensors</c>.</summary>
    public required string Checkpoint { get; init; }

    /// <summary>Base architecture. Decides latent size, IP-Adapter variant and VRAM budget.</summary>
    public required string BaseModel { get; init; }

    public required PromptDialect Dialect { get; init; }

    public required ConsistencyStrategy Consistency { get; init; }

    public IReadOnlyList<LoraSpec> Loras { get; init; } = [];

    public required SamplerSettings Sampler { get; init; }

    public required ResolutionSet Resolutions { get; init; }

    /// <summary>
    /// Which ComfyUI graph serves each job. A pack and its graphs are one unit — an SDXL pack
    /// cannot run an SD1.5 graph — so the binding belongs here rather than at the call site.
    /// </summary>
    public required WorkflowSet Workflows { get; init; }

    /// <summary>
    /// Background removal model used inside the Comfy graph (HANDOFF 6). Matting happens
    /// server-side in the graph so the client only ever receives PNGs with alpha.
    /// </summary>
    public required string MattingModel { get; init; }

    /// <summary>Ceilings this pack is willing to render. A save may not exceed these.</summary>
    public required IReadOnlyList<Ceiling> SupportedCeilings { get; init; }

    /// <summary>
    /// Tokens prepended to every positive prompt (quality boilerplate, e.g. Pony's
    /// <c>score_9</c> chain). Emitted before character tokens, in this order.
    /// </summary>
    public IReadOnlyList<string> PositivePrefix { get; init; } = [];

    /// <summary>Base negative tokens, before any ceiling-specific additions.</summary>
    public IReadOnlyList<string> NegativeBase { get; init; } = [];

    /// <summary>
    /// Negatives applied to every character render at every ceiling, with no path that omits
    /// them. Terms that must never describe a generated character, whatever the tier.
    /// </summary>
    /// <remarks>
    /// This list exists because the ceiling lists cannot do the job. A game may set its floor
    /// at 16, so PG13 is the tier that renders teenagers, and terms describing how a teenager
    /// looks cannot live there. Splitting them out means the protective terms never depend on
    /// which tier is active, and the tier lists are free to describe their own tier.
    /// </remarks>
    public IReadOnlyList<string> AlwaysNegative { get; init; } = [];

    /// <summary>
    /// Whether the checkpoint applies negatives at this pack's settings. When
    /// <see cref="NegativeSupport.Ignored"/>, no negative is compiled, and <see cref="AlwaysNegative"/>
    /// still guards the appearance vocabulary but protects nothing at render time. What holds on such
    /// a pack is structural: the age clamp in <see cref="Content.ContentPolicy"/>, restricted-positive
    /// filtering, and the schema rules of migration 002.
    /// </summary>
    public NegativeSupport NegativePrompts { get; init; } = NegativeSupport.Applied;

    /// <summary>
    /// Subject anchors, keyed by <see cref="Characters.CharacterAppearance.Subject"/>. A game
    /// declares which subjects its love interests may use; the pack supplies the vocabulary
    /// each one needs, because the right tokens are checkpoint-specific and not something
    /// the compiler can know.
    /// </summary>
    /// <remarks>
    /// Measured on Illustrious XL: <c>1boy, solo, adult</c> alone renders androgynous and
    /// young, and only reads as an adult man once <c>male focus, mature male</c> is present
    /// with <c>1girl, feminine</c> negated. That is the male half of the HANDOFF 1.9 age
    /// anchor, and it is pack data for the same reason the female half's wording is.
    /// </remarks>
    public IReadOnlyDictionary<string, SubjectProfile> Subjects { get; init; }
        = new Dictionary<string, SubjectProfile>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Tags for each expression slot, keyed by the slot name the game uses.
    /// </summary>
    /// <remarks>
    /// A bare slot name is not a usable tag. Measured, "sad" and "angry" alone move the whole
    /// body — posture, lean, shoulders — where a weighted face-scoped phrase such as
    /// <c>(crying:1.2), sad, tears</c> changes the face and leaves the body where the skeleton
    /// put it. That difference is the crossfade.
    /// </remarks>
    public IReadOnlyDictionary<string, string> Expressions { get; init; }
        = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Terms that may not enter the <em>positive</em> prompt below a stated ceiling.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Measured, and this exists because the obvious design does not work. The ceiling used to
    /// be enforced entirely by <see cref="NegativeByCeiling"/>. Asking for
    /// <c>swimsuit, beach</c> with the full PG13 negative list -- <c>nsfw, nude, cleavage,
    /// revealing clothes, suggestive, underwear, lingerie</c> -- rendered a revealing bikini
    /// on both Illustrious and NoobAI, moving 6.4% and 4.8% respectively against the same
    /// prompt with those negatives removed. The positive prompt wins.
    /// </para>
    /// <para>
    /// So a ceiling cannot be a subtraction. Scene intent is written by the LLM, and a model
    /// asked for an outfit it should not have been asked for has already lost: the term has to
    /// be refused on the way in.
    /// </para>
    /// </remarks>
    public IReadOnlyList<RestrictedTerms> RestrictedPositive { get; init; } = [];

    /// <summary>
    /// Whether <paramref name="tag"/> may appear in a positive prompt at <paramref name="ceiling"/>.
    /// Consulted by <see cref="Content.ApprovedIntent"/>, which is the only thing that should
    /// call it: this answers what a pack allows, not what a scene may do.
    /// </summary>
    public bool PermitsPositive(string tag, Ceiling ceiling)
    {
        if (string.IsNullOrWhiteSpace(tag)) return true;

        foreach (var rule in RestrictedPositive)
        {
            if (ceiling >= rule.MinCeiling) continue;

            foreach (var term in rule.Terms)
            {
                // Substring rather than equality: "school swimsuit" and "swimsuit" are the
                // same problem, and a rule listing every compound would miss the next one.
                if (tag.Contains(term, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>Tags for <paramref name="slot"/>, or a throw naming what the pack does offer.</summary>
    public string ExpressionFor(string slot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slot);

        foreach (var (key, tags) in Expressions)
        {
            if (string.Equals(key, slot, StringComparison.OrdinalIgnoreCase))
            {
                return tags;
            }
        }

        var known = Expressions.Count == 0 ? "none" : string.Join(", ", Expressions.Keys.Order(StringComparer.Ordinal));
        throw new InvalidOperationException(
            $"Style pack '{Id}' defines no expression '{slot}'. Known expressions: {known}.");
    }

    /// <summary>
    /// The anchor for <paramref name="subject"/>, or a throw naming what the pack does offer.
    /// A missing subject is a content error, not a reason to silently fall back to another
    /// one: rendering a male love interest as a woman is worse than failing.
    /// </summary>
    public SubjectProfile SubjectFor(string subject)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);

        // Deserialisation replaces the dictionary with a case-sensitive one, so the match is
        // done here rather than relying on the comparer. Subject keys come from saved
        // character data and pack JSON authored by different hands; casing is not a contract.
        foreach (var (key, profile) in Subjects)
        {
            if (string.Equals(key, subject, StringComparison.OrdinalIgnoreCase))
            {
                return profile;
            }
        }

        var known = Subjects.Count == 0 ? "none" : string.Join(", ", Subjects.Keys.Order(StringComparer.Ordinal));
        throw new InvalidOperationException(
            $"Style pack '{Id}' defines no subject '{subject}'. Known subjects: {known}.");
    }

    /// <summary>
    /// Extra negative tokens applied per ceiling. Keyed by <see cref="Ceiling"/> name.
    /// A PG13 save adds its nudity/suggestive negatives from here.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> NegativeByCeiling { get; init; }
        = new Dictionary<string, IReadOnlyList<string>>();

    public bool Supports(Ceiling ceiling) => SupportedCeilings.Contains(ceiling);

    /// <summary>
    /// The most this pack is willing to render. A cap that <see cref="Content.ContentPolicy"/>
    /// applies alongside the game setting and the age clamp.
    /// </summary>
    public Ceiling HighestCeiling => SupportedCeilings.Count == 0
        ? Ceiling.PG13
        : SupportedCeilings.Max();
}

/// <summary>
/// The tokens that establish who the subject of an image is.
/// </summary>
/// <param name="Positive">
/// Emitted immediately after the quality prefix and before the age tag, in this order.
/// Position is load-bearing: tag checkpoints weight early tokens most, and HANDOFF 1.9
/// requires the age anchor to precede every body descriptor.
/// </param>
/// <param name="Negative">
/// Added to the negative prompt for character renders only. Backgrounds negate every
/// subject regardless, so applying these there would be redundant.
/// </param>
/// <param name="Outfit">
/// Concrete garment tags for this subject, used when a scene does not name an outfit.
/// Measured: a vague placeholder like "casual clothes" is re-interpreted per render — one
/// expression came back in a different shirt — which moves the silhouette and breaks the
/// crossfade far more than any facial change does. Vague is not neutral here.
/// </param>
/// <param name="AgeBands">
/// How this subject's age is expressed, as tags rather than as a number. Ordered by
/// <see cref="AgeBand.From"/> ascending; the applicable band is the last one whose
/// <c>From</c> does not exceed the character's age.
/// </param>
/// <param name="Features">
/// The appearance choices this subject offers, keyed by <see cref="Characters.AppearanceFeatures"/>.
/// Per subject because the vocabulary differs -- the hair styles offered for a man are not the
/// ones offered for a woman -- and pack data because the words that render a look are
/// checkpoint-specific. Nullable only so a manifest written before choices existed still
/// deserialises; the loader refuses a pack that omits them.
/// </param>
public sealed record SubjectProfile(
    IReadOnlyList<string> Positive,
    IReadOnlyList<string> Negative,
    IReadOnlyList<string> Outfit,
    IReadOnlyList<AgeBand> AgeBands,
    IReadOnlyDictionary<string, IReadOnlyList<FeatureOption>>? Features = null)
{
    /// <summary>The choices for <paramref name="feature"/>, or none.</summary>
    public IReadOnlyList<FeatureOption> OptionsFor(string feature)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(feature);

        if (Features is null)
        {
            return [];
        }

        // Matched case-insensitively for the same reason as SubjectFor: deserialisation replaces
        // the dictionary with a case-sensitive one.
        foreach (var (key, options) in Features)
        {
            if (string.Equals(key, feature, StringComparison.OrdinalIgnoreCase))
            {
                return options;
            }
        }

        return [];
    }

    /// <summary>The band covering <paramref name="age"/>, or a throw if the bands do not.</summary>
    public AgeBand BandFor(int age)
    {
        AgeBand? found = null;
        foreach (var band in AgeBands)
        {
            if (band.From <= age && (found is null || band.From > found.From))
            {
                found = band;
            }
        }

        return found ?? throw new InvalidOperationException(
            $"No age band covers age {age}. Bands start at " +
            $"{(AgeBands.Count == 0 ? "nothing" : AgeBands.Min(b => b.From).ToString())}.");
    }
}

/// <summary>
/// Tags describing an age range, for ages at or above <paramref name="From"/>.
/// </summary>
/// <remarks>
/// <para>
/// Measured, and this replaced a mistake. The compiler used to emit "<c>{N} years old</c>",
/// which Danbooru has no tag for: varying it from 19 to 65 moved 1.97% of the image, meaning
/// the age anchor HANDOFF 1.9 depends on was doing nothing at all. The same contrast in
/// booru vocabulary -- <c>mature female</c> against <c>old woman, wrinkles, elderly</c> --
/// moves 9.79%, and 11.91% weighted. The model could always render age; the prompt could not
/// ask for it.
/// </para>
/// <para>
/// The narrower gap the content design actually needs, a young adult against a middle-aged
/// one, scores 9.24% -- nearly as much as the extremes, so this is usable and not merely a
/// party trick at the ends of the range.
/// </para>
/// </remarks>
public sealed record AgeBand(int From, IReadOnlyList<string> Tags);

/// <param name="MinCeiling">The lowest ceiling at which these terms are permitted.</param>
/// <param name="Terms">
/// Matched as case-insensitive substrings of a single tag, so "swimsuit" also catches
/// "school swimsuit". Deliberately blunt: the cost of over-refusing an outfit is a duller
/// image, and the cost of under-refusing is a ceiling that does not hold.
/// </param>
public sealed record RestrictedTerms(Ceiling MinCeiling, IReadOnlyList<string> Terms);

public sealed record LoraSpec(string File, double ModelWeight, double ClipWeight);

public sealed record WorkflowSet(
    string Portrait,
    string Sprite,
    string Background);

/// <param name="AnchorWeight">
/// IP-Adapter weight. Zero for packs that carry identity in the seed instead.
/// </param>
/// <param name="PoseStrength">
/// ControlNet strength for the pose skeleton. Measured on Illustrious: 0.55 holds the torso
/// but lets arms and framing wander, giving 86-89% silhouette overlap between expressions;
/// 0.95 gives 93-96%, which is the difference between a crossfade that ghosts and one that
/// nearly does not.
/// </param>
public sealed record SamplerSettings(
    string SamplerName,
    string Scheduler,
    int Steps,
    double Cfg,
    double AnchorWeight,
    double PoseStrength);

/// <summary>
/// Native generation resolutions per job. Off-native sizes cost quality on SD1.5 and
/// coherence on SDXL, so these are pack data rather than call-site choices.
/// </summary>
public sealed record ResolutionSet(
    Size Portrait,
    Size Sprite,
    Size Background);

public readonly record struct Size(int Width, int Height);
