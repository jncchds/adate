namespace Game.Core.Style;

/// <summary>
/// One choice a style pack offers for an appearance feature, such as <c>red hair</c> for hair
/// colour.
/// </summary>
/// <param name="Tag">
/// Emitted into the prompt exactly as written, and stored on the character when chosen. Pack
/// vocabulary for the same reason subject anchors are: the words that render a given look are
/// checkpoint-specific.
/// </param>
/// <param name="Near">
/// Choices close enough that swapping to one still reads as the character the player described.
/// Candidate portraits offer these as alternatives. Every entry must itself be an option of the
/// same feature, so an approved alternative is always something the form could have chosen.
/// </param>
/// <param name="Prompt">
/// The words emitted into a prompt in place of <paramref name="Tag"/>, when they need to differ.
/// The tag stays what the player picks and what the character stores, so rewording a choice never
/// invalidates a saved character. Measured on Z-Image: the choice "brown skin" rendered light
/// peach on three seeds out of three, while "dark-skinned with dark brown skin" rendered brown on
/// all three. The player chose brown skin, and these are the words that draw it.
/// </param>
/// <param name="PlayerOnly">
/// A choice a player may declare but a generator must never pick for someone the player did not
/// describe. Meant for build and height words near juvenile-coded vocabulary (HANDOFF 1.9).
/// </param>
public sealed record FeatureOption(string Tag, IReadOnlyList<string>? Near = null, string? Prompt = null, bool PlayerOnly = false);
