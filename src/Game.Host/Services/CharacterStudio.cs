using Game.Core;
using Game.Core.Characters;
using Game.Core.Saves;
using Game.Core.Scenes;
using Game.Core.Style;
using Game.Data.Repositories;
using Game.Imaging;
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
    JobRunner jobs,
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

        var positive = compiler.CompilePositive(character.Appearance, intent, pack, RenderTarget.Portrait);
        var negative = compiler.CompileNegative(pack, _options.Ceiling, RenderTarget.Portrait, character.Appearance.Subject);

        var results = new List<Candidate>(_options.CandidateCount);

        for (var i = 0; i < _options.CandidateCount; i++)
        {
            // Derived from the character id, so re-running the step reproduces the same four
            // faces rather than offering the player a different set each time.
            var seed = DeriveSeed(character.Id, i);

            var image = await images.GenerateAsync(
                new ImageRequest(
                    WorkflowId: "portrait",
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
                    Ceiling: _options.Ceiling),
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
        if (character.AnchorImageHash is null)
        {
            throw new InvalidOperationException(
                "This character has no approved anchor portrait, so there is nothing for the " +
                "IP-Adapter to hold the sprites consistent against.");
        }

        var pack = await GetPackAsync(ct).ConfigureAwait(false);
        var negative = compiler.CompileNegative(pack, _options.Ceiling, RenderTarget.Sprite, character.Appearance.Subject);
        var sprites = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var expression in Expressions)
        {
            var intent = SpriteIntent(expression);
            var positive = compiler.CompilePositive(character.Appearance, intent, pack, RenderTarget.Sprite);

            // A fixed seed per expression slot, derived from the expression name. This is
            // step 2 of the HANDOFF 2 fallback ladder, taken up front: it costs nothing and
            // it means a disappointing sprite can be regenerated with a changed prompt
            // without every other sprite shifting underneath it.
            var seed = DeriveSeed(character.Id, expression.GetHashCode(StringComparison.Ordinal));

            var image = await images.GenerateAsync(
                new ImageRequest(
                    WorkflowId: "sprite",
                    Positive: positive,
                    Negative: negative,
                    Seed: seed,
                    Width: pack.Resolutions.Sprite.Width,
                    Height: pack.Resolutions.Sprite.Height,
                    PackFingerprint: PackFingerprint(),
                    AnchorImageHash: character.AnchorImageHash,
                    AnchorWeight: pack.Sampler.AnchorWeight,
                    PoseImageHash: null,
                    PoseStrength: null,
                    Ceiling: _options.Ceiling),
                ct).ConfigureAwait(false);

            await cache.RecordSpriteAsync(
                image.Hash, character.SaveId, character.Id,
                intent.Outfit, intent.Pose, expression, _options.Ceiling, image.RelativePath, ct)
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

        var image = await images.GenerateAsync(
            new ImageRequest(
                WorkflowId: "background",
                Positive: compiler.CompilePositive(null, intent, pack, RenderTarget.Background),
                Negative: compiler.CompileNegative(pack, _options.Ceiling, RenderTarget.Background, subject: null),
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
                Ceiling: _options.Ceiling),
            ct).ConfigureAwait(false);

        await cache.RecordBackgroundAsync(image.Hash, saveId, locationId, time, image.RelativePath, ct)
            .ConfigureAwait(false);

        return image.RelativePath;
    }

    // ----------------------------------------------------------------- internals

    private static SceneIntent PortraitIntent() =>
        new("studio", TimeOfDay.Midday, "casual clothes", "looking at viewer", "neutral", Framing.Portrait);

    private static SceneIntent SpriteIntent(string expression) =>
        new("studio", TimeOfDay.Midday, "casual clothes", "standing", expression, Framing.Bust);

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

    public string StylePackId { get; set; } = "counterfeit-anime";

    public string StylePackDirectory { get; set; } = "stylepacks";

    public string LocationsFile { get; set; } = Path.Combine("content", "locations.json");

    /// <summary>HANDOFF 2: four candidate portraits, same prompt, four seeds.</summary>
    public int CandidateCount { get; set; } = 4;

    /// <summary>Hardcoded for Spike 0 (HANDOFF 1.8), but threaded through everything.</summary>
    public Ceiling Ceiling { get; set; } = Ceiling.PG13;
}
