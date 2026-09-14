using Game.Core;
using Game.Core.Cast;
using Game.Core.Characters;
using Game.Core.Content;
using Game.Core.Places;
using Game.Core.Saves;
using Game.Core.Scenes;
using Game.Core.Style;
using Game.Data.Repositories;
using Game.Imaging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Game.Play;

/// <summary>
/// The Spike 0 flow: attributes to candidate portraits, an approved anchor, six expression
/// sprites, and one location background. Owns the sequencing; owns none of the protocol.
/// </summary>
public sealed class CharacterStudio(
    IImageProvider images,
    PromptCompilers compilers,
    CastContent cast,
    IStylePackLoader packs,
    JsonStylePackLoader packText,
    CharacterRepository characters,
    ImageCacheRepository cache,
    PoseCatalog poses,
    JobRunner jobs,
    ILogger<CharacterStudio> log,
    IOptions<StudioOptions> options)
{
    private readonly StudioOptions _options = options.Value;

    /// <summary>
    /// The six expressions Spike 0 judges consistency against. Fixed rather than configurable
    /// because the success criterion is stated against a set of six, and a smaller set would
    /// make the test easier without making the result better.
    /// </summary>
    public static readonly IReadOnlyList<string> Expressions =
        ["neutral", "smile", "laughing", "sad", "angry", "surprised"];

    public string StylePackId => _options.StylePackId;

    public async Task<StylePack> GetPackAsync(CancellationToken ct = default) =>
        await packs.LoadAsync(_options.StylePackId, ct).ConfigureAwait(false);

    /// <summary>
    /// The content decision for one character, after the game setting, the pack and the age
    /// clamp. Every ceiling in this class comes from here; none reads the configured value
    /// directly, because the configured value is a maximum and not a decision.
    /// </summary>
    private ContentDecision DecisionFor(CharacterRecord character, StylePack pack, Intimacy intimacy) =>
        ContentPolicy.Resolve(_options.Content, character.Appearance.Age, pack.HighestCeiling, intimacy);

    /// <summary>
    /// Vets a scene intent and hands back a task the image pipeline may execute. The gate sits
    /// here, at the boundary where a scene becomes an image job, rather than inside the prompt
    /// compiler: what may be depicted is a story-side decision, and the compiler does not know
    /// how old the character is.
    /// </summary>
    private ApprovedIntent Approve(
        CharacterRecord character,
        StylePack pack,
        SceneIntent intent,
        Intimacy intimacy = Intimacy.None)
    {
        var decision = DecisionFor(character, pack, intimacy);
        var approved = ApprovedIntent.Approve(decision, intent, pack);

        if (approved.Removed.Count > 0)
        {
            log.LogInformation(
                "Content gate removed {Count} term(s) from a {Ceiling} scene for character {Character}: {Terms}",
                approved.Removed.Count, decision.Ceiling, character.Id, string.Join(", ", approved.Removed));
        }

        return approved;
    }

    /// <summary>
    /// Hash of the pack manifest, which enters the image cache key so art never survives an
    /// edit to the checkpoint, LoRA set or sampler settings that produced it.
    /// </summary>
    public string PackFingerprint() =>
        Imaging.Caching.ContentAddress.OfManifest(packText.ReadManifestText(_options.StylePackId));

    // ------------------------------------------------------------------ portraits

    /// <summary>
    /// Generates candidate portraits: the appearance exactly as declared, then alternatives that
    /// each move one or two features to a close neighbour from the pack's vocabulary. All of them
    /// share one seed.
    /// </summary>
    /// <remarks>
    /// Measured in Spike 0, varying only the seed gave one character in several poses rather than
    /// a choice between looks, while varying tags at one seed gave different people. The seed is
    /// what holds a character together, so it stays fixed here and the tags move -- but only to
    /// declared neighbours, which is what keeps an alternative a slight variation of the person
    /// described rather than a stranger. The approved candidate's appearance replaces the
    /// declared one; see <see cref="ApproveAnchorAsync"/>.
    /// </remarks>
    public Task<IReadOnlyList<Candidate>> GenerateCandidatesAsync(CharacterRecord character) =>
        jobs.RunAsync($"candidates:{character.Id}", ct => GenerateCandidatesCoreAsync(character, ct));

    /// <summary>How many candidates are requested. Fewer come back if the vocabulary is thin.</summary>
    public int CandidateCount => _options.CandidateCount;

    private async Task<IReadOnlyList<Candidate>> GenerateCandidatesCoreAsync(
        CharacterRecord character,
        CancellationToken ct)
    {
        var pack = await GetPackAsync(ct).ConfigureAwait(false);
        var compiler = compilers.For(pack.Dialect);
        var intent = PortraitIntent();

        var approved = Approve(character, pack, intent);
        var ceiling = approved.Ceiling;
        var negative = compiler.CompileNegative(pack, ceiling, RenderTarget.Portrait, character.Appearance.Subject);

        // Derived from the character id, so re-running the step reproduces the same portraits
        // rather than offering the player a different set each time. The same seed picks which
        // neighbours are offered, for the same reason.
        var seed = DeriveSeed(character.Id, 0);
        var variants = AppearanceVariations.For(
            character.Appearance,
            pack.SubjectFor(character.Appearance.Subject),
            _options.CandidateCount,
            seed);

        var results = new List<Candidate>(variants.Count);

        foreach (var variant in variants)
        {
            // Age and subject never vary, so the content decision above holds for every variant.
            var positive = compiler.CompilePositive(variant.Appearance, approved, pack, RenderTarget.Portrait);

            var image = await images.GenerateAsync(
                new ImageRequest(
                    WorkflowId: pack.Workflows.Portrait,
                    Positive: positive,
                    Negative: negative,
                    Seed: seed,
                    Width: pack.Resolutions.Portrait.Width,
                    Height: pack.Resolutions.Portrait.Height,
                    PackFingerprint: PackFingerprint(),
                    AnchorImageHash: null,
                    AnchorWeight: null,
                    PoseImageHash: null,
                    PoseStrength: null,
                    Ceiling: ceiling),
                ct).ConfigureAwait(false);

            results.Add(new Candidate(image.Hash, image.RelativePath, seed, variant.Appearance, variant.Changes));
        }

        return results;
    }

    /// <summary>
    /// Makes the chosen portrait the character's anchor and its appearance the character's
    /// appearance. Sprites are compiled from the stored record, so they have to describe what
    /// was approved rather than what was first declared.
    /// </summary>
    public Task ApproveAnchorAsync(Guid characterId, Candidate candidate, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        return characters.SetAnchorAsync(characterId, candidate.Hash, candidate.Seed, candidate.Appearance, ct);
    }

    // -------------------------------------------------------------------- sprites

    /// <summary>
    /// Generates one sprite per expression from the approved anchor.
    /// </summary>
    public Task<IReadOnlyDictionary<string, string>> GenerateSpritesAsync(CharacterRecord character) =>
        jobs.RunAsync($"sprites:{character.Id}", ct => GenerateSpritesCoreAsync(character, ct));

    private async Task<IReadOnlyDictionary<string, string>> GenerateSpritesCoreAsync(
        CharacterRecord character,
        CancellationToken ct)
    {
        var pack = await GetPackAsync(ct).ConfigureAwait(false);
        var compiler = compilers.For(pack.Dialect);

        // Both seed strategies make the shared seed the identity. Only SeedAndTags also
        // conditions on a pose skeleton; SeedAndPrompt serves providers with no pose input.
        var seedAndTags = pack.Consistency is ConsistencyStrategy.SeedAndTags or ConsistencyStrategy.SeedAndPrompt;
        var posed = pack.Consistency is ConsistencyStrategy.SeedAndTags;

        if (seedAndTags && character.AnchorSeed is null)
        {
            throw new InvalidOperationException(
                "This character has no approved anchor seed. Under SeedAndTags the seed is " +
                "what makes the six expressions one person, so there is nothing to hold them " +
                "together without it.");
        }

        if (!seedAndTags && character.AnchorImageHash is null)
        {
            throw new InvalidOperationException(
                "This character has no approved anchor portrait, so there is nothing for the " +
                "IP-Adapter to hold the sprites consistent against.");
        }

        // Every expression is conditioned on the same skeleton. Varying it would move the
        // body between frames, which is exactly what the crossfade cannot absorb.
        var poseHash = posed
            ? await poses.HashForAsync(_options.SpritePose, ct).ConfigureAwait(false)
            : null;

        var ceiling = DecisionFor(character, pack, Intimacy.None).Ceiling;
        var negative = compiler.CompileNegative(pack, ceiling, RenderTarget.Sprite, character.Appearance.Subject);
        var sprites = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var expression in Expressions)
        {
            var approved = Approve(character, pack, SpriteIntent(pack, character.Appearance.Subject, expression));
            var intent = approved.Intent;
            var positive = compiler.CompilePositive(character.Appearance, approved, pack, RenderTarget.Sprite);

            // Under SeedAndTags all six share the character's approved seed: that shared seed
            // is the identity, so varying it per slot would hand back six related strangers.
            // Under IpAdapterPlus the anchor image carries identity instead, which frees the
            // seed to vary per slot so one disappointing sprite can be regenerated alone.
            var seed = seedAndTags
                ? character.AnchorSeed!.Value
                : DeriveSeed(character.Id, expression.GetHashCode(StringComparison.Ordinal));

            var image = await images.GenerateAsync(
                new ImageRequest(
                    WorkflowId: pack.Workflows.Sprite,
                    Positive: positive,
                    Negative: negative,
                    Seed: seed,
                    Width: pack.Resolutions.Sprite.Width,
                    Height: pack.Resolutions.Sprite.Height,
                    PackFingerprint: PackFingerprint(),
                    AnchorImageHash: seedAndTags ? null : character.AnchorImageHash,
                    AnchorWeight: seedAndTags ? null : pack.Sampler.AnchorWeight,
                    PoseImageHash: poseHash,
                    PoseStrength: poseHash is null ? null : pack.Sampler.PoseStrength,
                    Ceiling: ceiling),
                ct).ConfigureAwait(false);

            await cache.RecordSpriteAsync(
                image.Hash, character.SaveId, character.Id,
                intent.Outfit, intent.Pose, expression, ceiling, image.RelativePath, ct)
                .ConfigureAwait(false);

            sprites[expression] = image.RelativePath;
        }

        return sprites;
    }

    /// <summary>
    /// One full-body standing sprite of a cast member for a scene (phase-3 plan: characters in
    /// scenes), in their aesthetic's outfit and one expression, matted by the sprite workflow.
    /// Rendered on first use and served from the content-addressed cache after that.
    /// </summary>
    /// <remarks>
    /// Every expression of a character shares the character's seed and differs only in the
    /// expression phrase: measured on Z-Image, that holds one person together across expressions
    /// without a pose skeleton (86-98% silhouette overlap).
    /// </remarks>
    public Task<string> GenerateSceneSpriteAsync(SaveId saveId, Guid characterId, string aesthetic, string expression) =>
        jobs.RunAsync(
            $"scene-sprite:{characterId}:{aesthetic}:{expression}",
            ct => GenerateSceneSpriteCoreAsync(saveId, characterId, aesthetic, expression, ct));

    private async Task<string> GenerateSceneSpriteCoreAsync(
        SaveId saveId,
        Guid characterId,
        string aesthetic,
        string expression,
        CancellationToken ct)
    {
        var character = await characters.GetAsync(characterId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"No character with id {characterId}.");

        var pack = await GetPackAsync(ct).ConfigureAwait(false);
        var compiler = compilers.For(pack.Dialect);
        var subject = pack.SubjectFor(character.Appearance.Subject);

        var outfit = aesthetic.Length > 0 ? subject.AestheticOutfit(aesthetic) : subject.Outfit;
        var approved = Approve(character, pack, new SceneIntent(
            "studio",
            TimeOfDay.Midday,
            string.Join(", ", outfit),
            "standing",
            pack.ExpressionFor(expression),
            Framing.FullBody));

        var image = await images.GenerateAsync(
            new ImageRequest(
                WorkflowId: pack.Workflows.Sprite,
                Positive: compiler.CompilePositive(character.Appearance, approved, pack, RenderTarget.Sprite),
                Negative: compiler.CompileNegative(pack, approved.Ceiling, RenderTarget.Sprite, character.Appearance.Subject),
                Seed: character.AnchorSeed ?? DeriveSeed(character.Id, 0),
                Width: pack.Resolutions.Sprite.Width,
                Height: pack.Resolutions.Sprite.Height,
                PackFingerprint: PackFingerprint(),
                AnchorImageHash: null,
                AnchorWeight: null,
                PoseImageHash: null,
                PoseStrength: null,
                Ceiling: approved.Ceiling),
            ct).ConfigureAwait(false);

        await cache.RecordSpriteAsync(
            image.Hash, saveId, character.Id,
            aesthetic.Length > 0 ? aesthetic : "default", "standing-full", expression, approved.Ceiling, image.RelativePath, ct)
            .ConfigureAwait(false);

        return image.RelativePath;
    }

    // ---------------------------------------------------------------- backgrounds

    /// <param name="weather">A weather id; <c>clear</c> keeps the prompt, and so the cache, it always had.</param>
    public Task<string> GenerateBackgroundAsync(SaveId saveId, PlaceRecord place, TimeOfDay time, string weather = "clear") =>
        jobs.RunAsync(
            $"background:{saveId}:{place.Id}:{time}:{weather}",
            ct => GenerateBackgroundCoreAsync(saveId, place, time, weather, ct));

    private async Task<string> GenerateBackgroundCoreAsync(
        SaveId saveId,
        PlaceRecord place,
        TimeOfDay time,
        string weather,
        CancellationToken ct)
    {
        var pack = await GetPackAsync(ct).ConfigureAwait(false);
        var compiler = compilers.For(pack.Dialect);
        var intent = new SceneIntent(
            place.TypeId, time, "", "", "", Framing.FullBody, place.Details,
            weather == "clear" ? null : weather);

        // A background has no subject, so no age clamp applies -- only the game setting and
        // the pack. It still goes through the gate: a location is authored content, but the
        // route to an image is the same one, and there should not be a second one.
        var backgroundCeiling = _options.Content.MaxCeiling < pack.HighestCeiling
            ? _options.Content.MaxCeiling
            : pack.HighestCeiling;

        var approved = ApprovedIntent.Approve(
            new ContentDecision(backgroundCeiling, Depict: true, Narration.Full), intent, pack);

        var image = await images.GenerateAsync(
            new ImageRequest(
                WorkflowId: pack.Workflows.Background,
                Positive: compiler.CompilePositive(null, approved, pack, RenderTarget.Background),
                Negative: compiler.CompileNegative(pack, backgroundCeiling, RenderTarget.Background, subject: null),
                // The place's stored seed, the same at every time of day, so morning and night
                // show the same room. It used to mix in string.GetHashCode, which is randomised per
                // process, so every restart regenerated every background.
                Seed: place.Seed,
                Width: pack.Resolutions.Background.Width,
                Height: pack.Resolutions.Background.Height,
                PackFingerprint: PackFingerprint(),
                AnchorImageHash: null,
                AnchorWeight: null,
                PoseImageHash: null,
                PoseStrength: null,
                Ceiling: backgroundCeiling),
            ct).ConfigureAwait(false);

        await cache.RecordBackgroundAsync(image.Hash, saveId, place.Id, time, image.RelativePath, weather, ct)
            .ConfigureAwait(false);

        return image.RelativePath;
    }

    /// <summary>
    /// A neutral backdrop the cut-out sprites stand on wherever a person is picked rather than met,
    /// such as the map's invite cards: soft, empty and the same for everyone, so the cards compare
    /// people and not places. One image for the pack, rendered once and served from the cache after.
    /// </summary>
    /// <remarks>
    /// Not a place type: the catalog is what the writer may propose, and a backdrop is not somewhere
    /// the story can go. So the prompt is built here from the pack's own style prefix.
    /// </remarks>
    public Task<string> GenerateBackdropAsync() =>
        jobs.RunAsync("backdrop", GenerateBackdropCoreAsync);

    /// <summary>Fixed, so the backdrop is the same image on every run and machine.</summary>
    private const long BackdropSeed = 20260914;

    private async Task<string> GenerateBackdropCoreAsync(CancellationToken ct)
    {
        var pack = await GetPackAsync(ct).ConfigureAwait(false);
        var compiler = compilers.For(pack.Dialect);

        var positive = pack.Dialect is PromptDialect.Booru
            ? string.Join(", ", [.. pack.PositivePrefix, "simple background", "gradient background", "pastel colors", "soft lighting", "no humans", "empty"])
            : string.Join(" ", [.. pack.PositivePrefix, "An empty, softly lit studio backdrop: a smooth pastel gradient fading to a faint floor shadow, with no objects, no furniture and no people."]);

        var ceiling = _options.Content.MaxCeiling < pack.HighestCeiling ? _options.Content.MaxCeiling : pack.HighestCeiling;

        var image = await images.GenerateAsync(
            new ImageRequest(
                WorkflowId: pack.Workflows.Background,
                Positive: positive,
                Negative: compiler.CompileNegative(pack, ceiling, RenderTarget.Background, subject: null),
                Seed: BackdropSeed,
                Width: pack.Resolutions.Background.Width,
                Height: pack.Resolutions.Background.Height,
                PackFingerprint: PackFingerprint(),
                AnchorImageHash: null,
                AnchorWeight: null,
                PoseImageHash: null,
                PoseStrength: null,
                Ceiling: ceiling),
            ct).ConfigureAwait(false);

        return image.RelativePath;
    }

    // ----------------------------------------------------------------------- cast

    /// <summary>
    /// The main LI and the alternatives built from them (phase-2 plan §5), each as a portrait in
    /// their own aesthetic. Nothing is stored: the cast is deterministic from the character, so it
    /// is rebuilt on request and the images are served from the content-addressed cache.
    /// </summary>
    /// <summary>
    /// The save's cast as records, built and stored the first time it is asked for (plan §3: the cast
    /// is generated at new game, with no art). The main LI's temper is the one the player chose; a
    /// save created before the new-game flow gets a placeholder temper instead.
    /// </summary>
    public async Task<IReadOnlyList<CastMember>> EnsureCastAsync(CharacterRecord main, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(main);

        var stored = await characters.GetCastAsync(main.Id, ct).ConfigureAwait(false);
        if (stored is not null)
        {
            return stored;
        }

        var pack = await GetPackAsync(ct).ConfigureAwait(false);
        cast.ValidateAgainst(pack);

        var subject = pack.SubjectFor(main.Appearance.Subject);
        var anchorSeed = main.AnchorSeed ?? DeriveSeed(main.Id, 0);
        var temper = await characters.GetTemperAsync(main.Id, ct).ConfigureAwait(false);

        var lead = temper is null
            ? CastGenerator.PlaceholderMain(main.Appearance, subject, cast, anchorSeed)
            : CastGenerator.Main(main.Appearance, temper, subject, cast, anchorSeed);
        var variants = CastGenerator.For(lead, subject, cast, DeriveSeed(main.Id, 1000));

        await characters.SaveCastAsync(main, lead, variants, ct).ConfigureAwait(false);
        return [lead, .. variants];
    }

    public Task<IReadOnlyList<CastPortrait>> GenerateCastAsync(CharacterRecord main) =>
        jobs.RunAsync($"cast:{main.Id}", ct => GenerateCastCoreAsync(main, ct));

    private async Task<IReadOnlyList<CastPortrait>> GenerateCastCoreAsync(CharacterRecord main, CancellationToken ct)
    {
        var pack = await GetPackAsync(ct).ConfigureAwait(false);
        var compiler = compilers.For(pack.Dialect);
        var subject = pack.SubjectFor(main.Appearance.Subject);

        cast.ValidateAgainst(pack);

        var members = await EnsureCastAsync(main, ct).ConfigureAwait(false);

        var results = new List<CastPortrait>(members.Count);

        foreach (var member in members)
        {
            // The content decision is made per member, from that member's own age.
            var person = main with { Appearance = member.Appearance };

            var outfit = member.Aesthetic.Length > 0
                ? subject.AestheticOutfit(member.Aesthetic)
                : subject.Outfit;

            var approved = Approve(person, pack, new SceneIntent(
                "studio",
                TimeOfDay.Midday,
                string.Join(", ", outfit),
                "looking at viewer",
                pack.ExpressionFor(member.RestingExpression(cast)),
                Framing.Portrait));

            var image = await images.GenerateAsync(
                new ImageRequest(
                    WorkflowId: pack.Workflows.Portrait,
                    Positive: compiler.CompilePositive(member.Appearance, approved, pack, RenderTarget.Portrait),
                    Negative: compiler.CompileNegative(pack, approved.Ceiling, RenderTarget.Portrait, member.Appearance.Subject),
                    Seed: member.Seed,
                    Width: pack.Resolutions.Portrait.Width,
                    Height: pack.Resolutions.Portrait.Height,
                    PackFingerprint: PackFingerprint(),
                    AnchorImageHash: null,
                    AnchorWeight: null,
                    PoseImageHash: null,
                    PoseStrength: null,
                    Ceiling: approved.Ceiling),
                ct).ConfigureAwait(false);

            results.Add(new CastPortrait(member, image.RelativePath));
        }

        return results;
    }

    // ----------------------------------------------------------------- internals

    private static SceneIntent PortraitIntent() =>
        new("studio", TimeOfDay.Midday, "casual clothes", "looking at viewer", "neutral", Framing.Portrait);

    /// <summary>
    /// The framing tag has to agree with the pose skeleton. Measured: a full-body skeleton
    /// against an "upper body" prompt drops silhouette overlap between expressions from
    /// 93-96% to 73-77%, because the model reconciles the conflict differently every time and
    /// the crop wanders. Changing the skeleton means changing this to match.
    /// </summary>
    private static SceneIntent SpriteIntent(StylePack pack, string subject, string expression) =>
        new("studio",
            TimeOfDay.Midday,
            string.Join(", ", pack.SubjectFor(subject).Outfit),
            "standing",
            pack.ExpressionFor(expression),
            Framing.HalfBody);

    /// <summary>
    /// A stable seed from an id and a slot. Generation must be reproducible: the same
    /// character re-running the same step has to produce the same images, or the
    /// content-addressed cache never hits and every visit costs GPU time.
    /// </summary>
    private static long DeriveSeed(Guid id, int slot)
    {
        Span<byte> bytes = stackalloc byte[16];
        id.TryWriteBytes(bytes);

        // Golden-ratio odd constant, so adjacent slots land far apart in the seed space
        // rather than producing four near-identical portraits.
        var seed = BitConverter.ToInt64(bytes) ^ unchecked((long)((ulong)slot * 0x9E3779B97F4A7C15UL));

        // ComfyUI seeds are non-negative; a negative value is rejected by the sampler node.
        return Math.Abs(seed % int.MaxValue);
    }
}

/// <param name="Seed">Shared by every candidate for a character; the look is what differs.</param>
/// <param name="Appearance">What this portrait was rendered from, and what approving it stores.</param>
/// <param name="Changes">How it differs from what the player declared. Empty for the declared look.</param>
public sealed record Candidate(
    string Hash,
    string RelativePath,
    long Seed,
    CharacterAppearance Appearance,
    IReadOnlyList<FeatureChange> Changes);

public sealed record CastPortrait(CastMember Member, string RelativePath);

public sealed class StudioOptions
{
    public const string SectionName = "Studio";

    public string TemperFile { get; set; } = Path.Combine("content", "temper.json");

    public string WantsFile { get; set; } = Path.Combine("content", "wants.json");

    public string ContrastsFile { get; set; } = Path.Combine("content", "contrasts.json");

    public string ValuesFile { get; set; } = Path.Combine("content", "values.json");

    public string PredicatesFile { get; set; } = Path.Combine("content", "predicates.json");

    public string RelationshipFile { get; set; } = Path.Combine("content", "relationship.json");

    public string RoutesFile { get; set; } = Path.Combine("content", "routes.json");

    public string EndingsFile { get; set; } = Path.Combine("content", "endings.json");

    public string WeatherFile { get; set; } = Path.Combine("content", "weather.json");

    public string StylePackId { get; set; } = "illustrious-anime";

    public string StylePackDirectory { get; set; } = "stylepacks";

    /// <summary>Place types: tags, descriptions and detail vocabulary for every kind of place.</summary>
    public string PlaceTypesFile { get; set; } = Path.Combine("content", "place-types.json");

    /// <summary>One JSON file per setting (phase-2 plan §2).</summary>
    public string SettingsDirectory { get; set; } = Path.Combine("content", "settings");

    /// <summary><c>common.json</c> plus one file per setting (phase-2 plan §7).</summary>
    public string EncountersDirectory { get; set; } = Path.Combine("content", "encounters");

    /// <summary>
    /// The setting a save gets when it was created without one. Stand-in until the new-game flow
    /// asks the player (build step 5).
    /// </summary>
    public string DefaultSettingId { get; set; } = "big-city";

    /// <summary>Authored OpenPose skeletons, one PNG per pose slot.</summary>
    public string PoseDirectory { get; set; } = Path.Combine("content", "poses");

    /// <summary>
    /// The pose slot every expression sprite is conditioned on. One slot in Spike 0: the six
    /// expressions have to share a skeleton to be crossfadable, so a second slot would be a
    /// second set of six rather than a variation within this one.
    /// </summary>
    public string SpritePose { get; set; } = "standing";

    /// <summary>
    /// How many portraits the player chooses between: the declared look plus nearby
    /// alternatives, all at one seed. Replaces HANDOFF 2's four seeds against one prompt, which
    /// Spike 0 measured as four poses of one look.
    /// </summary>
    public int CandidateCount { get; set; } = 4;

    /// <summary>
    /// Per-game content configuration. Defaults to adults-only and PG13: a game that says
    /// nothing gets the most restrictive setting rather than the most permissive one.
    /// </summary>
    public GameContentSettings Content { get; set; } = GameContentSettings.SafeDefault;
}
