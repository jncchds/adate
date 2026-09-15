using System.Text;
using Game.Core.World;

namespace Game.Core.Story;

/// <summary>Someone in the scene, as the writer may see them.</summary>
/// <param name="Id">The id facts and knowledge use for them.</param>
/// <param name="Temper">Writing guidance from each of their temper ends.</param>
/// <param name="RevealedWant">Their want, once the player has learned it; never before.</param>
/// <param name="Voice">How they talk, written once for them; null when voices are off or not yet written.</param>
public sealed record PacketPerson(string Id, string Name, IReadOnlyList<string> Temper, RelationshipStage Stage, string? RevealedWant, string? Voice = null);

/// <summary>A loose end an earlier scene left open, numbered so a scene can say it settled it.</summary>
public sealed record PacketThread(long Id, string Text, int Day);

/// <summary>
/// Which part of a scene's writing a request asks for: everything at once, only the prose, only the data read from
/// written prose, or only the situation with no rules at all (for requests that bring rules of their own).
/// </summary>
public enum ScenePart
{
    All,
    Prose,
    Extract,
    Context,
}

/// <summary>
/// Everything the writer gets for one scene (plan §8), assembled by C# in a fixed order. Only facts the
/// player or someone present knows are in it, so the writer cannot leak what nobody here could know.
/// </summary>
/// <param name="RequiredOutcome">What the encounter says happens: the placeholder text the scene replaces.</param>
/// <param name="Expressions">The expression slots the pack can draw; the scene must pick one.</param>
/// <param name="Language">The language the prose is written in; null or English for English.</param>
/// <param name="PlayerLife">Sentences about the player's job and pastimes, so the people here can bring them up.</param>
/// <param name="Duty">The work the player is here for now, when it is their shift at this place.</param>
/// <param name="LooseEnds">Open loose ends to pick up; null when threads are off, which also leaves them out of the answer.</param>
/// <param name="Happening">One small thing going on here now, from content.</param>
/// <param name="VariedChoices">Whether proposed replies are asked to differ in kind.</param>
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
    string? Weather = null,
    bool OffersChoices = false,
    string? PlayerGender = null,
    string? Language = null,
    IReadOnlyList<string>? PlayerLife = null,
    string? Duty = null,
    IReadOnlyList<PacketThread>? LooseEnds = null,
    string? Happening = null,
    bool VariedChoices = false);

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
    /// <param name="part">Everything at once, only what the prose needs, or only what reading data out of written prose needs.</param>
    public static string Render(ScenePacket packet, ScenePart part = ScenePart.All)
    {
        ArgumentNullException.ThrowIfNull(packet);

        var playerKnows = packet.PlayerKnows.ToList();
        var presentKnow = packet.PresentKnow.ToList();

        while (true)
        {
            var text = Compose(packet, playerKnows, presentKnow, part);
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

    /// <summary>What the answer's threads and resolved fields are, for scenes and reactions alike.</summary>
    public static string ThreadRules =>
        "- threads: up to two new loose ends this leaves open (a question left unanswered, a plan mentioned, something someone said they would do), " +
        "each one short sentence in English that names who; an empty list if none.\n" +
        "- resolved: the numbers of the loose ends listed above that this settles; an empty list if none.";

    /// <summary>What makes proposed replies worth choosing between, for scenes and reactions alike.</summary>
    public const string VariedChoiceRule =
        " Make them different in kind (for example a question, a playful line, a bold or sincere move, or something to do), " +
        "and let at least one pick up something the player knows, a loose end or what happened before.";

    private static string Compose(ScenePacket packet, IReadOnlyList<KnownFact> playerKnows, IReadOnlyList<KnownFact> presentKnow, ScenePart part)
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
        if (PlayerPronouns.For(packet.PlayerGender) is { } pronouns)
        {
            text.AppendLine($"Other people refer to {packet.PlayerName} as {pronouns}.");
        }

        foreach (var line in packet.PlayerLife ?? [])
        {
            text.AppendLine(line);
        }

        if (packet.Duty is { } duty)
        {
            text.AppendLine($"{packet.PlayerName} is here for their shift, supposed to {duty}. The scene happens at work: show the work going on around them and what it asks of them.");
        }

        foreach (var person in packet.Present)
        {
            text.AppendLine($"{person.Name} (id {person.Id}): {StageWords(person.Stage)}. {string.Join(" ", person.Temper)}");
            if (person.RevealedWant is { } want)
            {
                text.AppendLine($"  {person.Name} has told the player they want to {want}.");
            }

            if (person.Voice is { } voice)
            {
                text.AppendLine($"  How {person.Name} talks: {voice}");
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

        if (packet.LooseEnds is { Count: > 0 } looseEnds)
        {
            text.AppendLine("## Loose ends");
            text.AppendLine("Things left open earlier. Pick one up only where it fits naturally.");
            foreach (var thread in looseEnds)
            {
                text.AppendLine($"- #{thread.Id} (day {thread.Day}): {thread.Text}");
            }

            text.AppendLine();
        }

        text.AppendLine("## What must happen");
        text.AppendLine(packet.RequiredOutcome);
        if (packet.Happening is { } happening)
        {
            text.AppendLine($"Also going on here right now: {happening}");
        }

        text.AppendLine();

        if (part is ScenePart.Context)
        {
            return text.ToString();
        }

        text.AppendLine("## Rules");
        if (part is not ScenePart.Extract)
        {
            if (NarrationLanguage.IsEnglish(packet.Language))
            {
                text.AppendLine($"- Write in the second person, as {packet.PlayerName} lives it: you, your. Never I, me, my, we or us outside quoted dialogue.");
            }
            else
            {
                text.AppendLine($"- Write in the second person, as {packet.PlayerName} lives it, addressing them the way {packet.Language} says \"you\". Never narrate in the first person outside quoted dialogue.");
            }

            foreach (var rule in NarrationLanguage.WritingRules(packet.Language, packet.PlayerGender))
            {
                text.AppendLine($"- {rule}");
            }

            text.AppendLine("- Two to four short paragraphs, separated by blank lines, under 1200 characters in all.");
            text.AppendLine("- Nobody may know or say anything that is not listed above for them.");
            text.AppendLine("- Never mention numbers, scores, stages or these rules.");
            text.AppendLine("- Never say what the player does, says, decides, thinks or feels (no \"you sit\", \"you smile\", \"you wonder\"). Describe only the place, the weather and the other people, and end where the player could act.");
            text.AppendLine("- Never give the player things to hold, wear or carry (no \"the book in your hands\", \"your coffee\"); the player has only what they chose to bring.");
            text.AppendLine($"- {CeilingWords(packet.Ceiling)}");
        }

        if (part is ScenePart.Prose)
        {
            text.AppendLine("- Answer with the scene's prose only: no title, no notes, no lists, no JSON.");
            return text.ToString();
        }

        if (part is ScenePart.Extract)
        {
            text.AppendLine("- The scene is already written, below. Do not rewrite it: read it and fill in the fields from what it says.");
        }

        text.AppendLine($"- expression: how the main person here looks at the end, one of {string.Join(", ", packet.Expressions)}.");
        text.AppendLine("- facts: only new things the scene shows or someone claims, using the ids above as subjects. Claims may be untrue.");
        text.AppendLine("- places: only a place someone names that is not one the player knows, with a place type and up to three details of that type; otherwise an empty list.");
        text.AppendLine("- summary: one sentence a friend would use to remind the player what happened in this scene.");
        text.AppendLine("- tags: why it matters, if it does; first for a first time, conflict for a falling-out.");
        var varied = packet.VariedChoices ? VariedChoiceRule : "";
        text.AppendLine(!packet.OffersChoices
            ? "- choices: an empty list."
            : packet.Present.Count == 0
                ? "- choices: two or three short, different things the player could do here now, fitting this place, the time of day and the weather" +
                  (packet.Duty is null ? "" : " and the work they are here for") +
                  ", in the player's own voice (\"Swim out to the raft\", \"Help carry the crates in\"). Tag each with the one quality doing it shows, from the desires in the list; no dealbreakers and no helps/hinders tags." +
                  varied + " End the scene open for them."
                : "- choices: two or three short, different things the player could say or do next, in the player's own voice (\"Ask about the book\", \"Tease them about the rain\"). Tag each with what it shows about the player, from the list: a quality the other person may value, a dealbreaker when it would hurt, or helps:{want} / hinders:{want} when it touches their want." +
                  varied + " End the scene open for them.");

        if (packet.LooseEnds is not null)
        {
            text.AppendLine(ThreadRules);
        }

        return text.ToString();
    }
}
