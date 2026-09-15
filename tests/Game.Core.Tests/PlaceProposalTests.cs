using Game.Core.Content;
using Game.Core.Places;
using Game.Core.Saves;

namespace Game.Core.Tests;

public class PlaceProposalTests
{
    private static ILocationCatalog Catalog()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && dir.EnumerateFiles("*.sln").Concat(dir.EnumerateFiles("*.slnx")).Any() is false)
        {
            dir = dir.Parent;
        }

        return new JsonLocationCatalog(Path.Combine(dir!.FullName, "content", "place-types.json"));
    }

    [Fact]
    public void A_proposal_of_a_known_type_with_its_own_details_passes()
    {
        var catalog = Catalog();
        var cafe = catalog.Get("cafe");
        var details = (cafe.Details ?? []).Take(2).Select(d => d.Id).ToList();

        Assert.Empty(PlaceProposals.Check(new PlaceProposal("cafe", "Blue Door Coffee", details), catalog, ["The Corner Cup"]));
    }

    [Fact]
    public void Unknown_types_taken_names_foreign_details_and_long_names_are_each_refused()
    {
        var catalog = Catalog();

        Assert.Contains(PlaceProposals.Check(new PlaceProposal("castle", "Old Keep", []), catalog, []),
            r => r.Contains("'castle' is not one of", StringComparison.Ordinal));

        Assert.Contains(PlaceProposals.Check(new PlaceProposal("cafe", "the corner cup", []), catalog, ["The Corner Cup"]),
            r => r.Contains("already a place", StringComparison.Ordinal));

        Assert.Contains(PlaceProposals.Check(new PlaceProposal("cafe", "Somewhere", ["not-a-detail"]), catalog, []),
            r => r.Contains("'not-a-detail' is not a detail of cafe", StringComparison.Ordinal));

        Assert.Contains(PlaceProposals.Check(new PlaceProposal("cafe", new string('x', PlaceProposals.MaxNameLength + 1), []), catalog, []),
            r => r.Contains("needs a name", StringComparison.Ordinal));
    }

    [Fact]
    public void A_stored_proposal_is_a_known_story_place_with_a_unique_id_and_its_own_seed()
    {
        var save = SaveId.New();

        var first = PlaceProposals.ToRecord(save, new PlaceProposal("bar", "  The Blue Note! ", []), ["corner-cafe"], day: 9);
        var second = PlaceProposals.ToRecord(save, new PlaceProposal("bar", "The Blue Note", []), ["corner-cafe", first.Id], day: 10);

        Assert.Equal("story-the-blue-note", first.Id);
        Assert.Equal("story-the-blue-note-2", second.Id);
        Assert.Equal("The Blue Note!", first.Name);
        Assert.Equal(PlaceOrigin.Story, first.Origin);
        Assert.True(first.Known);
        Assert.Equal(9, first.FirstDay);
        Assert.Equal(PlaceRecord.SeedFor(save, first.Id), first.Seed);
        Assert.Null(first.Look);
    }

    [Fact]
    public void A_look_is_kept_tidied_and_an_overlong_one_is_sent_back()
    {
        var catalog = Catalog();
        var save = SaveId.New();

        var church = PlaceProposals.ToRecord(save, new PlaceProposal("bar", "The Chapel", [], Look: "  converted church,  stained glass windows, a stage with a piano. "), [], day: 3);
        Assert.Equal("converted church, stained glass windows, a stage with a piano", church.Look);

        var tooLong = new PlaceProposal("bar", "The Chapel", [], Look: string.Join(", ", Enumerable.Repeat("stained glass windows", 6)));
        Assert.Contains(PlaceProposals.Check(tooLong, catalog, []), r => r.Contains("look", StringComparison.Ordinal));
        Assert.Empty(PlaceProposals.Check(tooLong with { Look = church.Look }, catalog, []));
    }

    [Fact]
    public void A_look_is_cut_to_fit_at_a_phrase_and_one_with_no_words_is_dropped()
    {
        var cut = PlaceProposals.CleanLook(string.Join(", ", Enumerable.Repeat("stained glass windows", 6)));

        Assert.NotNull(cut);
        Assert.True(cut.Length <= PlaceProposals.MaxLookLength);
        Assert.EndsWith("stained glass windows", cut, StringComparison.Ordinal);
        Assert.Null(PlaceProposals.CleanLook("  "));
        Assert.Null(PlaceProposals.CleanLook("<img src=x>"));
        Assert.Null(PlaceProposals.CleanLook(null));
    }
}
