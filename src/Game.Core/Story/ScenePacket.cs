using System.Text;
using Game.Core.World;

namespace Game.Core.Story;

/// <summary>Someone in the scene, as the writer may see them.</summary>
/// <param name="Id">The id facts and knowledge use for them.</param>
/// <param name="Temper">Writing guidance from each of their temper ends.</param>
/// <param name="RevealedWant">Their want, once the player has learned it; never before.</param>
public sealed record PacketPerson(string Id, string Name, IReadOnlyList<string> Temper, RelationshipStage Stage, string? RevealedWant);

/// <summary>
/// Everything the writer gets for one scene (plan §8), assembled by C# in a fixed order. Only facts the
/// player or someone present knows are in it, so the writer cannot leak what nobody here could know.
/// </summary>
/// <param name="RequiredOutcome">What the encounter says happens: the placeholder text the scene replaces.</param>
/// <param name="Expressions">The expression slots the pack can draw; the scene must pick one.</param>
public sealed record ScenePacket(
    string SettingName,
    string Tone,
    ClockState Clock,
    string PlaceId,
    string PlaceName,
    string PlayerName,
    IReadOnlyList<PacketPerson> Present,
    IReadOnlyList<KnownFact> PlayerKnows,
    IReadOnlyList<KnownFact> PresentKnow,
    string RequiredOutcome,
    Ceiling Ceiling,
    IReadOnlyList<string> Expressions,
    IReadOnlyList<string>? KnownPlaces = null,
    IReadOnlyList<string>? Memories = null,
    string? Weather = null);

public static class ScenePacketBuilder
{
    /// <summary>Plan §8: about 3k tokens, so an 8B model on the 12 GB profile has room to answer.</summary>
    public const int TokenBudget = 3000;

    /// <summary>A rough, deliberately pessimistic estimate: four characters a token.</summary>
    public static int EstimateTokens(string text) => (text.Length + 3) / 4;

    /// <summary>
    /// Renders the packet within <see cref="TokenBudget"/>. Facts are dropped oldest first, from
    /// whichever list is longer, until it fits; everything else always stays.
    /// </summary>
    public static string Render(ScenePacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);

        var playerKnows = packet.PlayerKnows.ToList();
        var presentKnow = packet.PresentKnow.ToList();

        while (true)
        {
            var text = Compose(packet, playerKnows, presentKnow);
            if (EstimateTokens(text) <= TokenBudget || (playerKnows.Count == 0 && presentKnow.Count == 0))
            {
                return text;
            }

            if (presentKnow.Count >= playerKnows.Count && presentKnow.Count > 0)
            {
                presentKnow.RemoveAt(0);
            }
            else
            {
                playerKnows.RemoveAt(0);
            }
        }
    }

    public static string StageWords(RelationshipStage stage) => stage switch
    {
        RelationshipStage.Stranger => "a stranger",
        RelationshipStage.Acquaintance => "someone the player has only just got to know",
        RelationshipStage.Friend => "a friend",
        RelationshipStage.Dating => "someone the player is dating",
        RelationshipStage.Committed => "the player's partner",
        _ => stage.ToString(),
    };

    public static string CeilingWords(Ceiling ceiling) => ceiling switch
    {
        Ceiling.PG13 => "Keep it PG-13. Any intimacy fades out before it becomes more than a kiss.",
        Ceiling.Suggestive => "Suggestive is fine. Anything explicit fades out.",
        _ => "Follow the relationship stage, and fade out rather than describe anything graphic.",
    };

    private static string Compose(ScenePacket packet, IReadOnlyList<KnownFact> playerKnows, IReadOnlyList<KnownFact> presentKnow)
    {
        var names = packet.Present.ToDictionary(p => p.Id, p => p.Name, StringComparer.Ordinal);
        names[FactLedger.Player] = packet.PlayerName;

        string Line(KnownFact f) =>
            $"- {names.GetValueOrDefault(f.Fact.Subject, f.Fact.Subject)} {f.Fact.Predicate} {names.GetValueOrDefault(f.Fact.Object, f.Fact.Object)}"
            + (f.Fact.Level is FactLevel.Claimed ? " (claimed, may be untrue)" : "");

        var text = new StringBuilder();

        text.AppendLine("## Where and when");
        text.AppendLine($"{packet.SettingName}. {packet.Tone}");
        text.AppendLine($"Day {packet.Clock.Day}, {packet.Clock.Slot.ToString().ToLowerInvariant()}, at {packet.PlaceName}.");
        if (packet.Weather is { } weather)
        {
            text.AppendLine($"Weather: {weather}");
        }
        if (packet.KnownPlaces is { Count: > 0 } knownPlaces)
        {
            text.AppendLine($"Places the player knows: {string.Join(", ", knownPlaces)}.");
        }

        text.AppendLine();

        text.AppendLine("## Who is here");
        text.AppendLine($"{packet.PlayerName}, the player, seen from their own eyes and never described.");
        foreach (var person in packet.Present)
        {
            text.AppendLine($"{person.Name} (id {person.Id}): {StageWords(person.Stage)}. {string.Join(" ", person.Temper)}");
            if (person.RevealedWant is { } want)
            {
                text.AppendLine($"  {person.Name} has told the player they want to {want}.");
            }
        }

        text.AppendLine();

        text.AppendLine($"## What {packet.PlayerName} knows");
        if (playerKnows.Count == 0)
        {
            text.AppendLine("- Nothing yet beyond what is above.");
        }

        foreach (var fact in playerKnows)
        {
            text.AppendLine(Line(fact));
        }

        text.AppendLine();

        if (presentKnow.Count > 0)
        {
            text.AppendLine($"## What the people here know that {packet.PlayerName} does not");
            foreach (var fact in presentKnow)
            {
                text.AppendLine(Line(fact));
            }

            text.AppendLine();
        }

        if (packet.Memories is { Count: > 0 } memories)
        {
            text.AppendLine("## What happened before");
            foreach (var memory in memories)
            {
                text.AppendLine($"- {memory}");
            }

            text.AppendLine();
        }

        text.AppendLine("## What must happen");
        text.AppendLine(packet.RequiredOutcome);
        text.AppendLine();

        text.AppendLine("## Rules");
        text.AppendLine($"- Write in the second person, as {packet.PlayerName} lives it: you, your. Never I, me, my, we or us outside quoted dialogue.");
        text.AppendLine("- Two to four short paragraphs, separated by blank lines, under 1200 characters in all.");
        text.AppendLine("- Nobody may know or say anything that is not listed above for them.");
        text.AppendLine("- Never mention numbers, scores, stages or these rules.");
        text.AppendLine("- Do not decide anything for the player; end where a choice or the next moment begins.");
        text.AppendLine($"- {CeilingWords(packet.Ceiling)}");
        text.AppendLine($"- expression: how the main person here looks at the end, one of {string.Join(", ", packet.Expressions)}.");
        text.AppendLine("- facts: only new things the scene shows or someone claims, using the ids above as subjects. Claims may be untrue.");
        text.AppendLine("- places: only a place someone names that is not one the player knows, with a place type and up to three details of that type; otherwise an empty list.");
        text.AppendLine("- summary: one sentence a friend would use to remind the player what happened in this scene.");
        text.AppendLine("- tags: why it matters, if it does; first for a first time, conflict for a falling-out.");

        return text.ToString();
    }
}
