using System.Text.RegularExpressions;

namespace Game.Core.Story;

/// <summary>What someone wears in a scene: a <see cref="DressCode"/>, and something put on over it.</summary>
/// <param name="Over">A few English words for something worn over the outfit, such as "the player's denim jacket"; null for nothing.</param>
public sealed record Outfit(string Dress, string? Over = null);

/// <summary>What someone in a scene can be wearing, as the writer is told it.</summary>
/// <param name="Name">Who wears it.</param>
/// <param name="Wearing">What they wear unless the writer picks another of <paramref name="Codes"/>.</param>
/// <param name="Codes">The dress codes that suit the moment.</param>
/// <param name="Kept">Whether they were with the player just before and had no time to change.</param>
/// <param name="CameFrom">The place they came here from, when it is known and somewhere else.</param>
/// <param name="Settled">Whether the scene has already shown them in it, so only a change in the conversation is asked for.</param>
public sealed record PacketOutfit(
    string Name, Outfit Wearing, IReadOnlyList<string> Codes, bool Kept = false, string? CameFrom = null, bool Settled = false);

/// <summary>
/// How someone dresses for a scene (user feedback: Samantha wore a swimsuit on the pier every time, though she does not
/// swim; going from the pier to the lookout together, she would not have time to change). C# decides what suits the
/// moment from the place, where they came from and whether they were just with the player; the writer picks among
/// those from what they are doing, and may later say they put something on, like a jacket the player offers.
/// </summary>
public static partial class Outfits
{
    public const int MaxOverLength = 60;

    /// <param name="placeDress">The place type's dress code.</param>
    /// <param name="firstDate">Whether the scene is a first date, which dresses up where people dress up anyway.</param>
    /// <param name="kept">What they wore with the player in the slot just before, when they were.</param>
    /// <param name="cameFromDress">The dress code of the place they were at before, when that was somewhere else.</param>
    public static PacketOutfit For(string name, string placeDress, bool firstDate, Outfit? kept, string? cameFromDress, string? cameFromName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(placeDress);

        if (kept is not null)
        {
            // No time to change, except into swimwear for someone who goes swimming here.
            List<string> keptCodes = [kept.Dress];
            if (placeDress is DressCode.Waterfront && kept.Dress is not DressCode.Swim)
            {
                keptCodes.Add(DressCode.Swim);
            }

            return new PacketOutfit(name, kept, keptCodes, Kept: true, cameFromName);
        }

        // A date dresses up where people dress up anyway; at the office, at home, at camp or by the water the
        // place's own clothes still suit it (user feedback: a flowery dress on the camp's waterfront was wrong).
        var here = firstDate && placeDress is DressCode.Casual or DressCode.Evening ? DressCode.Date : placeDress;
        var codes = new List<string> { here };

        // Still in what suited where they were, unless they came from home, where they got ready for here.
        var before = cameFromDress is null or DressCode.Home or DressCode.Swim or DressCode.Date ? null : cameFromDress;
        if (before is not null && !codes.Contains(before))
        {
            codes.Add(before);
        }

        if (!codes.Contains(DressCode.Casual))
        {
            codes.Add(DressCode.Casual);
        }

        // Swimwear is only ever the writer's pick, for someone swimming: never what the water puts people in.
        if (placeDress is DressCode.Waterfront)
        {
            codes.Add(DressCode.Swim);
        }

        // Straight from work to somewhere casual, people are still in their work clothes.
        var wearing = before is DressCode.Work && here is DressCode.Casual ? DressCode.Work : here;
        return new PacketOutfit(name, new Outfit(wearing), codes, Kept: false, cameFromName);
    }

    /// <summary>
    /// The outfit an answer gives, when it is one of the codes offered: its over cleaned to a short run of words.
    /// Null when there is no answer or it names a code not offered, which keeps what they wear rather than costing the scene.
    /// </summary>
    public static Outfit? Accept(PacketOutfit? offered, string? dress, string? over)
    {
        if (offered is null || string.IsNullOrWhiteSpace(dress))
        {
            return null;
        }

        var code = offered.Codes.FirstOrDefault(c => string.Equals(c, dress.Trim(), StringComparison.OrdinalIgnoreCase));
        return code is null ? null : new Outfit(code, CleanOver(over));
    }

    /// <summary>
    /// Something worn over an outfit as it is stored and drawn: spaces collapsed, cut at a space to fit
    /// <see cref="MaxOverLength"/>, and only words; null when nothing usable is left. It reaches the sprite prompt
    /// through the content gate, which filters it like the rest of the outfit.
    /// </summary>
    public static string? CleanOver(string? over)
    {
        var text = Spaces().Replace(over?.Trim() ?? "", " ").Trim(' ', ',', '.', ';');
        if (text.Length > MaxOverLength)
        {
            var cut = text[..(MaxOverLength + 1)];
            var at = cut.LastIndexOf(' ');
            text = (at > 0 ? cut[..at] : text[..MaxOverLength]).Trim(' ', ',', '.', ';');
        }

        return text.Length > 0 && Words().IsMatch(text) ? text : null;
    }

    /// <summary>The outfit in words for the writer: "light summer clothes, not swimwear, with a denim jacket over it".</summary>
    public static string Describe(Outfit outfit)
    {
        ArgumentNullException.ThrowIfNull(outfit);
        return DressCode.Words(outfit.Dress) + (outfit.Over is { } over ? $", with {over} over it" : "");
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"^[\p{L}\p{M}\p{N}][\p{L}\p{M}\p{N} ,.'&()\-]*$")]
    private static partial Regex Words();
}
