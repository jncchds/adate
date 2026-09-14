using Game.Core.Scenes;

namespace Game.Core.Content;

/// <summary>
/// One piece of a place type's vocabulary: a small visible thing that makes one cafe not another.
/// </summary>
/// <param name="Tags">The detail in booru vocabulary.</param>
/// <param name="Phrase">The same detail as a phrase for natural-language checkpoints.</param>
public sealed record PlaceDetail(string Id, IReadOnlyList<string> Tags, string Phrase);

/// <summary>
/// A place type the game can render: a cafe, a park, a boathouse. A save's actual places are rows
/// that name a type, a name and some of the type's details (phase-2 plan §10). Content, not code,
/// for the same reason style packs are.
/// </summary>
/// <param name="Tags">Base scene tags, emitted in this order. Order is load-bearing.</param>
/// <param name="TimeTags">
/// Extra tags per <see cref="TimeOfDay"/>, keyed by enum name. A cafe at night is not the
/// same prompt as a cafe at midday, and the difference is more than one lighting word.
/// </param>
/// <param name="Description">
/// The same place as a descriptive phrase, for natural-language checkpoints. Tags are booru
/// vocabulary and read badly as prose.
/// </param>
/// <param name="TimeDescriptions">The time-of-day lighting as phrases, keyed like <paramref name="TimeTags"/>.</param>
/// <param name="Details">What a place of this type may be given to tell it apart from another of the same type.</param>
/// <param name="WeatherTags">Extra tags per weather id, shared by kind (indoor or outdoor).</param>
/// <param name="WeatherDescriptions">The same per weather id as a phrase; empty for weather that adds nothing.</param>
/// <param name="NeutralTimeTags">
/// Time of day without sky or sun, used instead of <paramref name="TimeTags"/> when the weather is not
/// clear: "blue sky, bright sunlight" beside "overcast" draws a blue sky.
/// </param>
/// <param name="NeutralTimeDescriptions">The same as phrases, replacing <paramref name="TimeDescriptions"/>.</param>
/// <param name="Dress">How people dress here, a <see cref="DressCode"/>.</param>
/// <param name="OutfitLayers">What goes over an outfit per weather id, shared by kind: a rain jacket outdoors in the rain.</param>
/// <param name="Activities">What the player can do at a place of this type, besides passing time there.</param>
public sealed record LocationDefinition(
    string Id,
    string DisplayName,
    IReadOnlyList<string> Tags,
    IReadOnlyDictionary<string, IReadOnlyList<string>> TimeTags,
    string? Description = null,
    IReadOnlyDictionary<string, string>? TimeDescriptions = null,
    IReadOnlyList<PlaceDetail>? Details = null,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? WeatherTags = null,
    IReadOnlyDictionary<string, string>? WeatherDescriptions = null,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? NeutralTimeTags = null,
    IReadOnlyDictionary<string, string>? NeutralTimeDescriptions = null,
    string Dress = DressCode.Casual,
    IReadOnlyDictionary<string, string>? OutfitLayers = null,
    IReadOnlyList<PlaceActivity>? Activities = null)
{
    /// <summary>The detail <paramref name="detailId"/>, or a throw naming what this type offers.</summary>
    public PlaceDetail Detail(string detailId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(detailId);

        return (Details ?? []).FirstOrDefault(d => string.Equals(d.Id, detailId, StringComparison.Ordinal))
            ?? throw new KeyNotFoundException(
                $"Place type '{Id}' offers no detail '{detailId}'. Known: {string.Join(", ", (Details ?? []).Select(d => d.Id))}.");
    }
}
