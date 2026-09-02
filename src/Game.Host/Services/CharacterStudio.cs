using Game.Core;
using Game.Core.Characters;
using Game.Core.Content;
using Game.Core.Saves;
using Game.Core.Scenes;
using Game.Core.Style;
using Game.Data.Repositories;
using Game.Imaging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Game.Host.Services;

/// <summary>
/// The Spike 0 flow: attributes to candidate portraits, an approved anchor, six expression
/// sprites, and one location background. Owns the sequencing; owns none of the protocol.
/// </summary>
public sealed class CharacterStudio(
    IImageProvider images,
    IPromptCompiler compiler,
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
    /// Generates candidate portraits: one prompt, several seeds (HANDOFF 2). Varying only the
    /// seed is the point — the player is choosing between interpretations of the attributes
    /// they declared, not between different characters.
    /// </summary>
    /// <remarks>
    /// Measured, this is a weaker choice than it reads: three seeds against identical tags
    /// gave one character in three poses, differing far more in framing and hair flow than in
    /// who they were. A picker worth the name varies appearance tags as well, and pins the
    /// framing with the same skeleton the sprites use. Left as-is for now because changing it
    /// changes what the player is being asked to decide, which is a design call.
    /// </remarks>
    public Task<IReadOnlyList<Candidate>> GenerateCandidatesAsync(CharacterRecord character) =>
        jobs.RunAsync($"candidates:{character.Id}", ct => GenerateCandidatesCoreAsync(character, ct));

    private async Task<IReadOnlyList<Candidate>> GenerateCandidatesCoreAsync(
        CharacterRecord character,
        CancellationToken ct)
    {
        var pack = await GetPackAsync(ct).ConfigureAwait(false);
        var intent = PortraitIntent();

        var approved = Approve(character, pack, intent);
        var ceiling = approved.Ceiling;
        var positive = compiler.CompilePositive(character.Appearance, approved, pack, RenderTarget.Portrait);
        var negative = compiler.CompileNegative(pack, ceiling, RenderTarget.Portrait, character.Appearance.Subject);

        var results = new List<Candidate>(_options.CandidateCount);

        for (var i = 0; i < _options.CandidateCount; i++)
        {
            // Derived from the character id, so re-running the step reproduces the same four
            // faces rather than offering the player a different set each time.
            var seed = DeriveSeed(character.Id, i);

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

            results.Add(new Candidate(image.Hash, image.RelativePath, seed));
        }

        return results;
    }

    public Task ApproveAnchorAsync(Guid characterId, Candidate candidate, CancellationToken ct = default) =>
        characters.SetAnchorAsync(characterId, candidate.Hash, candidate.Seed, ct);

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
        var seedAndTags = pack.Consistency is ConsistencyStrategy.SeedAndTags;

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
        var poseHash = seedAndTags
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

    // ---------------------------------------------------------------- backgrounds

    public Task<string> GenerateBackgroundAsync(SaveId saveId, string locationId, TimeOfDay time) =>
        jobs.RunAsync(
            $"background:{saveId}:{locationId}:{time}",
            ct => GenerateBackgroundCoreAsync(saveId, locationId, time, ct));

    private async Task<string> GenerateBackgroundCoreAsync(
        SaveId saveId,
        string locationId,
        TimeOfDay time,
        CancellationToken ct)
    {
        var pack = await GetPackAsync(ct).ConfigureAwait(false);
        var intent = new SceneIntent(locationId, time, "", "", "", Framing.FullBody);

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
                // Backgrounds are generated once per location and time and then reused for
                // the life of the save, so the seed only needs to be stable, not varied.
                Seed: DeriveSeed(saveId.Value, locationId.GetHashCode(StringComparison.Ordinal) ^ (int)time),
                Width: pack.Resolutions.Background.Width,
                Height: pack.Resolutions.Background.Height,
                PackFingerprint: PackFingerprint(),
                AnchorImageHash: null,
                AnchorWeight: null,
                PoseImageHash: null,
                PoseStrength: null,
                Ceiling: backgroundCeiling),
            ct).ConfigureAwait(false);

        await cache.RecordBackgroundAsync(image.Hash, saveId, locationId, time, image.RelativePath, ct)
            .ConfigureAwait(false);

        return image.RelativePath;
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

public sealed record Candidate(string Hash, string RelativePath, long Seed);

public sealed class StudioOptions
{
    public const string SectionName = "Studio";

    public string StylePackId { get; set; } = "illustrious-anime";

    public string StylePackDirectory { get; set; } = "stylepacks";

    public string LocationsFile { get; set; } = Path.Combine("content", "locations.json");

    /// <summary>Authored OpenPose skeletons, one PNG per pose slot.</summary>
    public string PoseDirectory { get; set; } = Path.Combine("content", "poses");

    /// <summary>
    /// The pose slot every expression sprite is conditioned on. One slot in Spike 0: the six
    /// expressions have to share a skeleton to be crossfadable, so a second slot would be a
    /// second set of six rather than a variation within this one.
    /// </summary>
    public string SpritePose { get; set; } = "standing";

    /// <summary>HANDOFF 2: four candidate portraits, same prompt, four seeds.</summary>
    public int CandidateCount { get; set; } = 4;

    /// <summary>
    /// Per-game content configuration. Defaults to adults-only and PG13: a game that says
    /// nothing gets the most restrictive setting rather than the most permissive one.
    /// </summary>
    public GameContentSettings Content { get; set; } = GameContentSettings.SafeDefault;
}
