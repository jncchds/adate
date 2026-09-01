namespace Game.Core.Style;

/// <summary>
/// Which prompt language the pack's checkpoint understands. HANDOFF 1.3: one
/// <c>IPromptCompiler</c> implementation per dialect, which is what makes packs swappable.
/// </summary>
public enum PromptDialect
{
    /// <summary>Comma-separated danbooru-style tags. Anime checkpoints.</summary>
    Booru = 0,

    /// <summary>Flowing descriptive sentences. Photoreal / SD3-family checkpoints.</summary>
    Natural = 1,
}
