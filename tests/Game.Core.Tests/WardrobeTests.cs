using Game.Core.Content;
using Game.Core.Style;

namespace Game.Core.Tests;

public class WardrobeTests
{
    private static string ContentPath(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && dir.EnumerateFiles("*.slnx").Any() is false)
        {
            dir = dir.Parent;
        }

        return Path.Combine([dir?.FullName ?? throw new InvalidOperationException("No repository root."), .. parts]);
    }

    private static SubjectProfile Subject() => new(
        ["a single woman"],
        [],
        ["a white blouse"],
        [],
        Aesthetics: new Dictionary<string, IReadOnlyList<string>> { ["sporty"] = ["a track jacket"] },
        Wardrobe: new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>>
        {
            ["work"] = new Dictionary<string, IReadOnlyList<string>> { ["sporty"] = ["a navy polo shirt"] },
        });

    [Fact]
    public void A_dress_code_dresses_the_aesthetic_for_the_place_and_falls_back_to_its_everyday_outfit()
    {
        Assert.Equal(["a navy polo shirt"], Subject().OutfitFor("sporty", DressCode.Work));
        Assert.Equal(["a track jacket"], Subject().OutfitFor("Sporty", DressCode.Casual));
        Assert.Equal(["a track jacket"], Subject().OutfitFor("sporty", DressCode.Evening));
        Assert.Equal(["a white blouse"], Subject().OutfitFor("", DressCode.Work));
    }

    [Fact]
    public async Task Shipped_places_and_the_anime_pack_dress_people_for_where_they_are()
    {
        var places = new JsonLocationCatalog(ContentPath("content", "place-types.json"));

        Assert.Equal(DressCode.Work, places.Get("office").Dress);
        Assert.Equal(DressCode.Evening, places.Get("bar").Dress);
        Assert.Equal(DressCode.Camp, places.Get("campfire-circle").Dress);
        Assert.Equal(DressCode.Waterfront, places.Get("lake-dock").Dress);
        Assert.Equal(DressCode.Waterfront, places.Get("lake-pier").Dress);
        Assert.Equal(DressCode.Casual, places.Get("park").Dress);
        Assert.False(string.IsNullOrWhiteSpace(places.Get("park").OutfitLayers?["rain"]));
        Assert.Null(places.Get("cafe").OutfitLayers?.GetValueOrDefault("rain"));

        var pack = await new JsonStylePackLoader(ContentPath("stylepacks")).LoadAsync("zimage-anime");
        foreach (var subject in pack.Subjects.Values)
        {
            foreach (var aesthetic in subject.Aesthetics!.Keys)
            {
                foreach (var code in DressCode.All)
                {
                    Assert.NotEmpty(subject.OutfitFor(aesthetic, code));
                }

                Assert.NotEqual(subject.OutfitFor(aesthetic, DressCode.Casual), subject.OutfitFor(aesthetic, DressCode.Date));

                // User feedback: a swimsuit on the pier every time. Swimwear is only for swimming.
                Assert.DoesNotContain(subject.OutfitFor(aesthetic, DressCode.Waterfront), p => p.Contains("swim", StringComparison.OrdinalIgnoreCase));
                Assert.Contains(subject.OutfitFor(aesthetic, DressCode.Swim), p => p.Contains("swim", StringComparison.OrdinalIgnoreCase));
            }
        }
    }
}
