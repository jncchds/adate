using Game.Core.Scenes;
using Game.Core.Settings;

namespace Game.Core.Tests;

/// <summary>
/// A setting is mostly references to places, place types and details. A broken one would surface
/// days into a playthrough, so each is refused at load with the reference named.
/// </summary>
public sealed class SettingCatalogTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "adate-setting-tests", Guid.NewGuid().ToString("N"));

    public SettingCatalogTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A leaked temp directory is not worth failing a test run over.
        }
    }

    private static string Setting(int openings = 3)
    {
        string[] times = ["Morning", "Afternoon", "Evening", "Night"];
        var list = Enumerable.Range(0, openings).Select(i =>
            $$"""{ "id": "o{{i}}", "name": "Opening {{i}}", "meetingPlace": "cafe-1", "time": "{{times[i % times.Length]}}", "homePlace": "cafe-1", "secondPlace": "alley-1", "homeWindow": "mornings", "hook": "Hook." }""");

        return $$"""
            {
              "id": "test-town",
              "displayName": "Test town",
              "days": 28,
              "tone": "Quiet.",
              "routinePlace": "cafe-1",
              "home": "home-1",
              "places": [
                { "id": "home-1", "type": "flat", "name": "Your place" },
                { "id": "cafe-1", "type": "cafe", "name": "Cafe", "details": ["window-seat"] },
                { "id": "alley-1", "type": "bare", "name": "Alley", "known": false }
              ],
              "openings": [ {{string.Join(", ", list)}} ],
              "events": [ { "id": "e", "name": "E", "day": 10, "place": "alley-1", "time": "Evening" } ],
              "occupations": ["clerk"]
            }
            """;
    }

    private JsonSettingCatalog Load(string json, string fileName = "test-town")
    {
        File.WriteAllText(Path.Combine(_directory, fileName + ".json"), json);
        return new JsonSettingCatalog(_directory, TestContent.Locations());
    }

    [Fact]
    public void A_valid_setting_loads_with_its_defaults()
    {
        var setting = Load(Setting()).Get("test-town");

        Assert.Equal(3, setting.Openings.Count);
        Assert.Equal(TimeOfDay.Morning, setting.Openings[0].Time);
        Assert.True(setting.Place("cafe-1").Known);
        Assert.False(setting.Place("alley-1").Known);
    }

    [Theory]
    [InlineData("\"type\": \"cafe\"", "\"type\": \"castle\"", "unknown place type 'castle'")]
    [InlineData("\"details\": [\"window-seat\"]", "\"details\": [\"hot-tub\"]", "detail 'hot-tub'")]
    [InlineData("\"id\": \"o0\", \"name\": \"Opening 0\", \"meetingPlace\": \"cafe-1\"", "\"id\": \"o0\", \"name\": \"Opening 0\", \"meetingPlace\": \"nowhere\"", "meets at 'nowhere'")]
    [InlineData("\"id\": \"o0\", \"name\": \"Opening 0\", \"meetingPlace\": \"cafe-1\"", "\"id\": \"o0\", \"name\": \"Opening 0\", \"meetingPlace\": \"alley-1\"", "meets at 'alley-1'")]
    [InlineData("\"day\": 10", "\"day\": 40", "day 40")]
    [InlineData("\"routinePlace\": \"cafe-1\"", "\"routinePlace\": \"alley-1\"", "routine place 'alley-1'")]
    [InlineData("\"home\": \"home-1\"", "\"home\": \"nowhere\"", "home 'nowhere' must be one of its places")]
    [InlineData("\"home\": \"home-1\"", "\"home\": \"cafe-1\"", "is a cafe, which is not somewhere anyone lives")]
    [InlineData("\"home\": \"home-1\",", "", "has no home for the player")]
    public void A_broken_reference_is_refused_by_name(string from, string to, string expected)
    {
        var json = Setting();
        Assert.Contains(from, json, StringComparison.Ordinal);

        var ex = Assert.Throws<InvalidOperationException>(() => Load(json.Replace(from, to, StringComparison.Ordinal)));

        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public void A_setting_offers_exactly_three_openings(int openings)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Load(Setting(openings)));

        Assert.Contains($"declares {openings} openings", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_setting_whose_id_does_not_match_its_file_is_refused()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Load(Setting(), fileName: "other-town"));

        Assert.Contains("declares id 'test-town'", ex.Message, StringComparison.Ordinal);
    }
}
