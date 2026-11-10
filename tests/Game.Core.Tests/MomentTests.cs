using Game.Core.Characters;
using Game.Core.Scenes;
using Game.Core.Story;
using Game.Core.Style;
using Game.Core.World;

namespace Game.Core.Tests;

public class MomentTests
{
    private static readonly Guid Rin = Guid.NewGuid();
    private static readonly Guid Kai = Guid.NewGuid();

    private static SceneFigure Figure(Guid id, string name = "Rin") => new(id, name, "sporty", "smile");

    [Fact]
    public void A_shot_is_kept_in_plain_words_and_cut_at_a_comma_to_fit()
    {
        Assert.Equal("leaning in for a kiss, eyes closed", Moments.CleanShot("  leaning in   for a kiss, eyes closed. "));
        Assert.Null(Moments.CleanShot("   "));
        Assert.Null(Moments.CleanShot("<b>kiss</b>"));

        var longShot = string.Join(", ", Enumerable.Repeat("laughing with arms flung wide", 8));
        var cut = Moments.CleanShot(longShot)!;
        Assert.True(cut.Length <= Moments.MaxShotLength);
        Assert.EndsWith("wide", cut, StringComparison.Ordinal);
    }

    [Fact]
    public void A_new_shot_draws_whoever_stands_there_now()
    {
        var moment = Moments.After(null, "slow dancing, head on the viewer's shoulder", [Figure(Rin), Figure(Kai, "Kai")], redressed: false);

        Assert.NotNull(moment);
        Assert.Equal([Rin, Kai], moment.People);
        Assert.Null(moment.PicturePath);
    }

    [Fact]
    public void The_same_shot_keeps_the_picture_while_the_same_people_stand_there_in_the_same_clothes()
    {
        var drawn = new SceneMoment("slow dancing", [Rin], "img/m.png");

        Assert.Same(drawn, Moments.After(drawn, "Slow dancing.", [Figure(Rin)], redressed: false));

        // Someone came in, or changed what they wear: the same words are a new picture.
        Assert.Null(Moments.After(drawn, "slow dancing", [Figure(Rin), Figure(Kai, "Kai")], redressed: false)!.PicturePath);
        Assert.Null(Moments.After(drawn, "slow dancing", [Figure(Rin)], redressed: true)!.PicturePath);
    }

    [Fact]
    public void No_shot_or_nobody_on_the_stage_ends_the_moment()
    {
        var drawn = new SceneMoment("slow dancing", [Rin], "img/m.png");

        Assert.Null(Moments.After(drawn, null, [Figure(Rin)], redressed: false));
        Assert.Null(Moments.After(drawn, "slow dancing", [], redressed: false));
    }

    /// <summary>The image model reads English; a shot in another alphabet would be drawn as nothing.</summary>
    [Fact]
    public void A_shot_the_image_model_cannot_read_is_no_moment()
    {
        Assert.False(Moments.IsDrawable("танцює під дощем"));
        Assert.Null(Moments.After(null, "танцює під дощем", [Figure(Rin)], redressed: false));
    }

    [Fact]
    public void When_the_words_could_not_say_the_picture_stays_only_while_it_still_shows_who_is_there()
    {
        var drawn = new SceneMoment("slow dancing", [Rin], "img/m.png");

        Assert.Same(drawn, Moments.Unsaid(drawn, [Figure(Rin)], redressed: false));
        Assert.Null(Moments.Unsaid(drawn, [], redressed: false));
        Assert.Null(Moments.Unsaid(null, [Figure(Rin)], redressed: false));
    }

    // ------------------------------------------------------------------ the packet

    private static ScenePacket Packet(string? showing = null, IReadOnlyList<string>? drawn = null) => new(
        "Big city",
        "Busy weekdays.",
        new ClockState(6, TimeOfDay.Evening),
        "corner-cafe",
        "The Corner Cup",
        "Alex",
        [new PacketPerson("rin-id", "Rin", ["Speaks evenly."], RelationshipStage.Friend, null)],
        [],
        [],
        "Rin is here.",
        Ceiling.PG13,
        ["neutral", "smile"],
        Drawn: drawn ?? ["rin-id"],
        Showing: showing);

    [Fact]
    public void The_writer_may_call_for_a_moment_only_where_someone_is_drawn()
    {
        var rule = ScenePacketBuilder.MomentRule(Packet());

        Assert.NotNull(rule);
        Assert.Contains("Rin", rule, StringComparison.Ordinal);
        Assert.Contains("the viewer", rule, StringComparison.Ordinal);
        Assert.Contains("- moment:", ScenePacketBuilder.Render(Packet()), StringComparison.Ordinal);

        Assert.Null(ScenePacketBuilder.MomentRule(Packet() with { Drawn = null }));
        Assert.DoesNotContain("- moment:", ScenePacketBuilder.Render(Packet() with { Drawn = null }), StringComparison.Ordinal);
    }

    [Fact]
    public void An_answer_is_told_which_moment_is_on_screen_so_it_can_keep_change_or_end_it()
    {
        var rule = ScenePacketBuilder.MomentRule(Packet("slow dancing, eyes closed"))!;

        Assert.Contains("\"slow dancing, eyes closed\"", rule, StringComparison.Ordinal);
        Assert.Contains("Repeat those exact words", rule, StringComparison.Ordinal);
        Assert.Contains("Null once it has passed", rule, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- the picture

    private static MomentSubject Subject(CharacterAppearance appearance, string outfit = "a red raincoat") =>
        new(appearance, TestContent.Approved(TestContent.Intent() with { Outfit = outfit, Pose = "", Expression = "a warm, gentle smile" }));

    private static string Moment(IReadOnlyList<MomentSubject> people, string shot = "laughing, spinning in the rain") =>
        new NaturalPromptCompiler(TestContent.Locations()).CompileMoment(
            people,
            TestContent.Approved(new SceneIntent("cafe", TimeOfDay.Evening, "", shot, "", Framing.FullBody)),
            TestContent.NaturalPack());

    [Fact]
    public void A_moment_draws_the_person_in_their_clothes_doing_the_shot_at_the_place_and_leaves_the_right_open()
    {
        var prompt = Moment([Subject(TestContent.Appearance())]);

        Assert.StartsWith("Anime illustration, soft cel shading. ", prompt, StringComparison.Ordinal);
        Assert.Contains("an adult woman", prompt, StringComparison.Ordinal);
        Assert.Contains("Wearing a red raincoat", prompt, StringComparison.Ordinal);
        Assert.Contains("Laughing, spinning in the rain.", prompt, StringComparison.Ordinal);
        Assert.Contains("The interior of a cozy corner cafe", prompt, StringComparison.Ordinal);
        Assert.Contains("Golden sunset light", prompt, StringComparison.Ordinal);
        Assert.Contains("on the left side of the picture, and the right side is open", prompt, StringComparison.Ordinal);

        // A place with people in it: the background's "empty scene" sentence would fight the picture.
        Assert.DoesNotContain("no people", prompt, StringComparison.Ordinal);

        // HANDOFF 1.9 still holds: the age band precedes the build.
        Assert.True(prompt.IndexOf("an adult woman", StringComparison.Ordinal) < prompt.IndexOf("slim", StringComparison.Ordinal));
    }

    [Fact]
    public void Two_people_each_keep_their_own_look()
    {
        var prompt = Moment([Subject(TestContent.Appearance()), Subject(TestContent.Appearance("male") with { HairColor = "black hair" }, "a grey suit")]);

        var left = prompt.IndexOf("On the left, ", StringComparison.Ordinal);
        var beside = prompt.IndexOf("Beside them, ", StringComparison.Ordinal);
        Assert.True(left >= 0 && beside > left);
        // Hair colour leads its sentence, so it is capitalised.
        var red = prompt.IndexOf("red hair", StringComparison.OrdinalIgnoreCase);
        var black = prompt.IndexOf("black hair", StringComparison.OrdinalIgnoreCase);
        Assert.True(red > left && red < beside);
        Assert.True(black > beside);
        Assert.Contains("Only these two people in the scene", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void A_moment_is_compiled_only_by_its_own_method()
    {
        Assert.Throws<ArgumentException>(() => new NaturalPromptCompiler(TestContent.Locations()).CompilePositive(
            TestContent.Appearance(), TestContent.Approved(), TestContent.NaturalPack(), RenderTarget.Moment));
        Assert.Throws<ArgumentException>(() => Moment([]));
    }
}
