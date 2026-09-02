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

    /// <param name="ceiling">
    /// The effective ceiling from <see cref="Content.ContentPolicy"/>. Terms the pack
    /// restricts above it are dropped from the LLM-written parts of the intent. Measured: a
    /// ceiling enforced only by negative prompts does not hold, because a positive asking for
    /// the opposite wins.
    /// </param>
    string CompilePositive(
        CharacterAppearance? appearance,
        SceneIntent intent,
        StylePack pack,
        RenderTarget target,
        Ceiling ceiling);

    /// <param name="subject">
    /// The character's subject key, required for every target except
    /// <see cref="RenderTarget.Background"/>. The pack's per-subject negatives are not
    /// cosmetic: measured, a PG13 ceiling built only from female-coded terms let a bare
    /// male chest through, so each subject carries the terms its own ceiling needs.
    /// </param>
    string CompileNegative(StylePack pack, Ceiling ceiling, RenderTarget target, string? subject);
}
