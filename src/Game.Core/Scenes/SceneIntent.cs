namespace Game.Core.Scenes;

/// <summary>
/// The structured request for a rendered scene. HANDOFF 1.3: the LLM emits this and
/// never writes a prompt itself; <see cref="IPromptCompiler"/> owns the translation
/// from intent to prompt text.
/// </summary>
public sealed record SceneIntent(
    string LocationId,
    TimeOfDay Time,
    string Outfit,
    string Pose,
    string Expression,
    Framing Framing);
