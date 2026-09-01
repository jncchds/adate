using Game.Core.Characters;
using Game.Core.Scenes;
using Game.Core.Style;

namespace Game.Core;

/// <summary>
/// HANDOFF 1.3 and 4: turns a scene intent plus a character record into prompt text.
/// The LLM never writes prompts; it emits <see cref="SceneIntent"/> and this does the rest.
/// One implementation per <see cref="PromptDialect"/>.
/// </summary>
/// <remarks>
/// <see cref="CompilePositive"/> MUST emit tokens in a fixed, deterministic order.
/// The prompt string feeds the content-addressed cache key, so an unstable token order
/// silently invalidates every cached image and breaks reproducibility (HANDOFF 4, 1.7).
/// </remarks>
public interface IPromptCompiler
{
    PromptDialect Dialect { get; }

    string CompilePositive(CharacterAppearance appearance, SceneIntent intent, StylePack pack);

    string CompileNegative(StylePack pack, Ceiling ceiling);
}
