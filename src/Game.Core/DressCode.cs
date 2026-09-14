namespace Game.Core;

/// <summary>
/// How people dress for where they are (user feedback: one outfit everywhere looked wrong at the office,
/// a bar and a campfire alike). A place type has a code; a first date dresses up whatever the place. The
/// style pack's wardrobe says what each aesthetic wears for each code, so a person keeps their style.
/// </summary>
public static class DressCode
{
    /// <summary>Everyday clothes: the aesthetic's own outfit. What a place without a code gets.</summary>
    public const string Casual = "casual";

    public const string Work = "work";

    /// <summary>Going out at night.</summary>
    public const string Evening = "evening";

    /// <summary>Around the home, relaxed.</summary>
    public const string Home = "home";

    /// <summary>Outdoors at a summer camp: shorts and t-shirts.</summary>
    public const string Camp = "camp";

    /// <summary>By the water in summer: swimwear with something over it, or shorts and a tank top.</summary>
    public const string Waterfront = "waterfront";

    /// <summary>Dressed up a little for a date, where people dress up anyway (casual and evening places).</summary>
    public const string Date = "date";

    public static readonly IReadOnlyList<string> All = [Casual, Work, Evening, Home, Camp, Waterfront, Date];

    public static bool IsKnown(string? code) => code is not null && All.Contains(code, StringComparer.OrdinalIgnoreCase);
}
