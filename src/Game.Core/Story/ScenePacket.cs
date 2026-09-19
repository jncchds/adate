using System.Text;
using Game.Core.World;

namespace Game.Core.Story;

/// <summary>Someone in the scene, as the writer may see them.</summary>
/// <param name="Id">The id facts and knowledge use for them.</param>
/// <param name="Temper">Writing guidance from each of their temper ends.</param>
/// <param name="RevealedWant">Their want, once the player has learned it; never before.</param>
/// <param name="Voice">How they talk, written once for them; null when voices are off or not yet written.</param>
/// <param name="Routine">Their usual week, so they can mention it: "weekday mornings at the dock; nights at home".</param>
/// <param name="Away">Why they are not here now although the scene is with them: they come in later, or they left.</param>
public sealed record PacketPerson(
    string Id,
    string Name,
    IReadOnlyList<string> Temper,
    RelationshipStage Stage,
    string? RevealedWant,
    string? Voice = null,
    string? Routine = null,
    PersonAway? Away = null);

/// <summary>Why someone the scene is with is not here now.</summary>
public enum PersonAway
{
    /// <summary>They come in during the scene, as what must happen says.</summary>
    Arriving,

    /// <summary>They are not here now: they left, or never came in.</summary>
    Left,
}

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
/// <param name="Outfit">What the person the scene is about can be wearing; null when nobody here is drawn.</param>
/// <param name="OtherOutfits">What anyone else drawn here is wearing, settled: only the person the scene is about picks.</param>
/// <param name="Drawn">
/// The ids of the people who can stand on the stage, the person the scene is about first; the answer says which of them
/// are here at its end. Null when nobody is drawn, such as texting.
/// </param>
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
    bool VariedChoices = false,
    PacketOutfit? Outfit = null,
    IReadOnlyList<PacketOutfit>? OtherOutfits = null,
    IReadOnlyList<string>? Drawn = null);

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

    /// <summary>What the answer's places are, for scenes and reactions alike: a new place, and whose home it is.</summary>
    public const string PlaceRule =
        "places: every place someone names that the player does not know yet, even in passing (a café they mention, where they live, " +
        "somewhere a choice suggests going), with the place type closest to it, up to three details of that type, " +
        "look: how this particular place looks, in English, a few short comma-separated visual phrases under 80 characters " +
        "(what it is built as, materials, colours, what stands out; no people, no name, no time of day or weather), " +
        "and owner: the id of the person here whose home it is, or an empty string; an empty list if none.";

    /// <summary>What the answer's routines are: what someone said about their own week.</summary>
    public const string RoutineRule =
        "routines: whenever someone here says where they usually are at some time, their id, the place's name, the time of day " +
        "(Morning, Midday, Afternoon, Evening or Night) and the days (weekdays, weekend or daily); only what they actually say, an empty list if nothing.";

    /// <summary>
    /// What the prose may say about clothes when the two came here together straight away (user request:
    /// no "I just need to change before that"). They walked here from the last scene with no slot in
    /// between, so they are in the same clothes and there was no moment in which to put on others. The
    /// picture is drawn from those clothes, so a line about having changed would describe someone the
    /// player cannot see.
    /// </summary>
    public static string KeptClothesRule(PacketOutfit outfit)
    {
        ArgumentNullException.ThrowIfNull(outfit);

        return $"{outfit.Name} came straight here with the player and is still in {Outfits.Describe(outfit.Wearing)}. " +
               "Do not write them changing, having changed, going to change, wanting to change, or apologising for what they have on, " +
               "and do not describe any other clothes on them. They may still take something off or put something over it.";
    }

    /// <summary>What the answer's outfit is for a scene: what the main person wears in it, among the codes that suit.</summary>
    public static string OutfitRule(PacketOutfit outfit)
    {
        ArgumentNullException.ThrowIfNull(outfit);

        var codes = string.Join("; ", outfit.Codes.Select(c => $"{c} ({DressCode.Words(c)})"));
        var swim = outfit.Codes.Contains(DressCode.Swim)
            ? $"; swim only if {outfit.Name} is swimming or about to, and never if anything says they do not swim"
            : "";
        var over = outfit.Wearing.Over is { } still ? $"\"{still}\" while they still have it on, or " : "";

        // The clothes named here are drawn, so what the words say and what the player sees are the same thing
        // (user feedback: the picture showed shorts and a t-shirt while the words described a dress). Someone
        // with no time to change is not asked for clothes at all: they keep exactly the ones they had on.
        var garments = outfit.Kept
            ? $" garments: leave out; {outfit.Name} is still in {Outfits.Describe(outfit.Wearing) } and had no time to change."
            : $" garments: the clothes themselves, in English, a few plain phrases naming each one with its colour " +
              $"(\"a cream linen sundress, flat sandals\"), under {Outfits.MaxGarmentsLength} characters. They are what " +
              $"{outfit.Name} is drawn wearing, so describe these clothes and no others in the prose.";

        return $"outfit: what {outfit.Name} wears here" +
               (outfit.Kept ? "" : ", fitting where they have come from, this place and what they are doing") +
               $". dress: one of {codes}. " +
               (outfit.Kept ? $"Keep {outfit.Wearing.Dress}: there was no time to change" : $"Usually {outfit.Wearing.Dress}") + swim +
               $". over: {over}anything worn over it in a few English words (a cardigan, a towel round the shoulders), or an empty string." +
               garments;
    }

    /// <summary>What the answer's outfit is for a reaction: a change only, such as putting on a jacket the player offers.</summary>
    public static string OutfitChangeRule(PacketOutfit outfit)
    {
        ArgumentNullException.ThrowIfNull(outfit);

        var codes = string.Join("; ", outfit.Codes.Select(c => $"{c} ({DressCode.Words(c)})"));
        return $"outfit: only if, in this reaction, {outfit.Name} changes what they wear, puts something on or takes it off " +
               "(puts on a jacket the player offers, takes off a sweater, goes to change, comes back in something else): " +
               $"dress, one of {codes}, over: what they now wear over it in a few English words, or an empty string for nothing, " +
               $"and garments: the clothes they are in once they have, in English, naming each with its colour, under {Outfits.MaxGarmentsLength} characters. " +
               $"{outfit.Name} is drawn again in whatever this names, so set it only when the words above say they changed, " +
               $"and name the clothes the words describe. They are wearing {Outfits.Describe(outfit.Wearing)}. Otherwise null.";
    }

    /// <summary>What the answer's expression is: how the person the scene is about looks, named, for scenes and reactions alike.</summary>
    public static string ExpressionRule(ScenePacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);

        var who = packet.Drawn is [var first, ..] && packet.Present.FirstOrDefault(p => p.Id == first) is { } owner
            ? owner.Name
            : "the main person here";
        return $"expression: how {who} looks at the end, one of {string.Join(", ", packet.Expressions)}.";
    }

    /// <summary>
    /// What the answer's present is: who of those who can be drawn is here at its end, and how each looks, so nobody
    /// stands on the stage before the words bring them in or after they leave. Null when nobody here is drawn.
    /// </summary>
    public static string? PresenceRule(ScenePacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);

        if (packet.Drawn is not { Count: > 0 } drawn)
        {
            return null;
        }

        var people = drawn.Select(id => packet.Present.FirstOrDefault(p => p.Id == id) is { } person ? $"{person.Name} ({id})" : id);
        return $"present: of {string.Join(", ", people)}, each one who is here at the end, with their id and how they look (one of {string.Join(", ", packet.Expressions)}). " +
               "Leave out anyone who has left or has not come in yet; an empty list if nobody is.";
    }

    /// <summary>What makes proposed replies worth choosing between, for scenes and reactions alike.</summary>
    public const string VariedChoiceRule =
        " Make them different in kind (for example a question, a playful line, a bold or sincere move, or something to do), " +
        "and let at least one pick up something the player knows, a loose end or what happened before. Keep each under 120 characters.";

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

            if (person.Routine is { } routine)
            {
                text.AppendLine($"  {person.Name}'s usual week: {routine}. They may mention it when it comes up naturally.");
            }

            switch (person.Away)
            {
                case PersonAway.Arriving:
                    text.AppendLine($"  {person.Name} is not here at first: they come in during the scene, as what must happen says.");
                    break;
                case PersonAway.Left:
                    text.AppendLine($"  {person.Name} is not here now: they come only if what happens next brings them.");
                    break;
            }
        }

        IEnumerable<PacketOutfit> outfits = packet.Outfit is null ? [] : [packet.Outfit];
        foreach (var outfit in outfits.Concat(packet.OtherOutfits ?? []))
        {
            text.AppendLine(outfit switch
            {
                { Settled: true } => $"  {outfit.Name} is wearing {Outfits.Describe(outfit.Wearing)}.",
                { Kept: true } => $"  {outfit.Name} is still wearing {Outfits.Describe(outfit.Wearing)}: they were just with the player and had no time to change.",
                { CameFrom: { } from } => $"  {outfit.Name} has come here from {from}.",
                _ => $"  {outfit.Name} has not been anywhere else the player knows of today.",
            });
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

            if (packet.Outfit is { Kept: true } straight)
            {
                text.AppendLine($"- {KeptClothesRule(straight)}");
            }

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
            foreach (var rule in NarrationLanguage.DataRules(packet.Language, packet.PlayerGender))
            {
                text.AppendLine($"- {rule}");
            }
        }

        text.AppendLine($"- {ExpressionRule(packet)}");
        if (PresenceRule(packet) is { } presence)
        {
            text.AppendLine($"- {presence}");
        }

        if (packet.Outfit is { Settled: false } offered)
        {
            text.AppendLine($"- {OutfitRule(offered)}");
        }

        text.AppendLine("- facts: only new things the scene shows or someone claims, using the ids above as subjects. Claims may be untrue.");
        text.AppendLine($"- {PlaceRule}");
        text.AppendLine($"- {RoutineRule}");
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
