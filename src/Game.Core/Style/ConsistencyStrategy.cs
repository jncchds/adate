namespace Game.Core.Style;

/// <summary>
/// How a pack keeps a character looking like themselves across sprites. Ordered to match
/// the HANDOFF 2 fallback ladder: later members are escalations from earlier ones.
/// </summary>
public enum ConsistencyStrategy
{
    /// <summary>
    /// Prompt tokens and a fixed seed only. Written off in the HANDOFF 2 ladder as the
    /// baseline that would not survive, and measured on Illustrious XL as the strategy that
    /// actually works: identity holds across expressions, and leaving the model unpatched
    /// keeps outfit, pose and framing controllable, which every adapter tested took away.
    /// </summary>
    SeedAndTags = 0,

    /// <summary>
    /// IP-Adapter Plus, full-image reference against the approved anchor. The Spike 0
    /// strategy for the anime pack. NOT FaceID/InstantID: those rely on InsightFace
    /// embeddings trained on real faces and perform poorly on anime.
    /// </summary>
    IpAdapterPlus = 1,

    /// <summary>IP-Adapter FaceID / InstantID. Photoreal packs only.</summary>
    IpAdapterFaceId = 2,

    /// <summary>Per-character LoRA trained from approved sprites. Ladder step 4.</summary>
    CharacterLora = 3,
}
