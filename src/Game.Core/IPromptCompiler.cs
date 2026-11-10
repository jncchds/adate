using Game.Core.Characters;
using Game.Core.Scenes;
using Game.Core.Style;

namespace Game.Core;

/// <summary>
/// HANDOFF 1.3 and 4: turns a scene intent plus a character record into prompt text.
/// The LLM never writes prompts; it emits <see cref="SceneIntent"/> and this does the rest.
/// One implementation per <see cref="PromptDialect"/>, which is what makes packs swappable.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="CompilePositive"/> MUST emit tokens in a fixed, deterministic order. The prompt
/// string feeds the content-addressed cache key, so an unstable order silently invalidates
/// every cached image and destroys reproducibility (HANDOFF 4, 1.7).
/// </para>
/// <para>
/// The <see cref="RenderTarget"/> parameter is an addition to the HANDOFF 4 sketch. Without
/// it one intent cannot serve both a sprite, which must contain no background, and a
/// background, which must contain no character.
/// </para>
/// </remarks>
public interface IPromptCompiler
{
    PromptDialect Dialect { get; }

    /// <param name="approved">
    /// An intent that has already passed the content gate. Taking
    /// <see cref="Content.ApprovedIntent"/> rather than a raw <see cref="SceneIntent"/> is the
    /// point: what may be depicted is decided upstream, where the character's age is known,
    /// and a compiler cannot be handed something unvetted.
    /// </param>
    string CompilePositive(
        CharacterAppearance? appearance,
        Content.ApprovedIntent approved,
        StylePack pack,
        RenderTarget target);

    /// <param name="subject">
    /// The character's subject key, required for every target except
    /// <see cref="RenderTarget.Background"/>. The pack's per-subject negatives are not
    /// cosmetic: measured, a PG13 ceiling built only from female-coded terms let a bare
    /// male chest through, so each subject carries the terms its own ceiling needs.
    /// </param>
    string CompileNegative(StylePack pack, Ceiling ceiling, RenderTarget target, string? subject);

    /// <summary>
    /// A moment worth seeing (<see cref="RenderTarget.Moment"/>): the people in it, each in their own clothes and look,
    /// doing what the shot says, at the place. The same fixed order as <see cref="CompilePositive"/>, and the same rule:
    /// every word comes from the pack, the catalog, a character record or an already-approved intent.
    /// </summary>
    /// <param name="people">Who is in it, left to right, each with an intent approved for them: their outfit and expression.</param>
    /// <param name="scene">
    /// The place as a background intent, with the shot in <see cref="SceneIntent.Pose"/>, approved at the strictest
    /// ceiling of anyone in the picture.
    /// </param>
    string CompileMoment(IReadOnlyList<MomentSubject> people, Content.ApprovedIntent scene, StylePack pack);
}

/// <summary>Someone in a moment's picture: who they are, and what they wear and look like there, already through the gate.</summary>
public sealed record MomentSubject(CharacterAppearance Appearance, Content.ApprovedIntent Approved);
