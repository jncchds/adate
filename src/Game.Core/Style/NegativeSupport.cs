namespace Game.Core.Style;

/// <summary>Whether a pack's checkpoint, at the pack's sampler settings, applies a negative prompt at all.</summary>
public enum NegativeSupport
{
    /// <summary>Classifier-free guidance above 1: the negative is part of the sampling.</summary>
    Applied = 0,

    /// <summary>
    /// Guidance at 1.0, as distilled models such as Z-Image Turbo run: the unconditional branch is
    /// skipped and the negative never reaches the model. Measured on Z-Image Turbo at cfg 1.0: the
    /// full pack negative and a deliberately contradicting one both rendered byte-identical to no
    /// negative, on three seeds. A pack like this compiles no negative, so nothing downstream can
    /// mistake one for a protection.
    /// </summary>
    Ignored = 1,
}
