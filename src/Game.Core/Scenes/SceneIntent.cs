namespace Game.Core.Scenes;

/// <summary>
/// The structured request for a rendered scene. HANDOFF 1.3: the LLM emits this and
/// never writes a prompt itself; <see cref="IPromptCompiler"/> owns the translation
/// from intent to prompt text.
/// </summary>
/// <param name="LocationId">The place type to render.</param>
/// <param name="LocationDetails">
/// Detail ids from that place type, which make one place of a type differ from another. Ids, not
/// words: the compiler resolves them against the catalog, so a prompt is still built only from
/// authored vocabulary.
/// </param>
public sealed record SceneIntent(
    string LocationId,
    TimeOfDay Time,
    string Outfit,
    string Pose,
    string Expression,
    Framing Framing,
    IReadOnlyList<string>? LocationDetails = null);
