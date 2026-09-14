using Game.Core.Content;
using Game.Core.Scenes;
using Game.Core.Settings;
using Game.Core.Style;
using Game.Core.World;

namespace Game.Core.Tests;

/// <summary>Weather in the writing and the backgrounds (phase-3 plan, step 1).</summary>
public class WeatherTests
{
    private static string ContentPath(string file)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && dir.EnumerateFiles("*.sln").Concat(dir.EnumerateFiles("*.slnx")).Any() is false)
        {
            dir = dir.Parent;
        }

        return Path.Combine(dir?.FullName ?? throw new InvalidOperationException("No repository root."), "content", file);
    }

    private static WeatherContent Weather() => WeatherContent.Load(ContentPath("weather.json"));

    private static JsonLocationCatalog Catalog() => new(ContentPath("place-types.json"));

    private static JsonSettingCatalog Settings() => new(ContentPath("settings"), Catalog());

    [Fact]
    public void Shipped_weather_fits_every_place_type_and_setting()
    {
        Weather().ValidateAgainst(Catalog(), Settings());
    }

    [Fact]
    public void The_same_save_and_day_always_have_the_same_weather()
    {
        var weather = Weather();
        var setting = Settings().Get("big-city");

        var first = Enumerable.Range(1, 28).Select(d => WeatherRoll.For("save-a", d, weather, setting)).ToList();

        Assert.Equal(first, Enumerable.Range(1, 28).Select(d => WeatherRoll.For("save-a", d, weather, setting)));
        Assert.NotEqual(first, Enumerable.Range(1, 28).Select(d => WeatherRoll.For("save-b", d, weather, setting)));
    }

    [Fact]
    public void Weather_tends_to_hold_from_one_day_to_the_next()
    {
        var weather = Weather();
        var setting = Settings().Get("big-city");
        int same = 0, total = 0;

        foreach (var save in Enumerable.Range(0, 200).Select(i => $"save-{i}"))
        {
            var days = Enumerable.Range(1, 28).Select(d => WeatherRoll.For(save, d, weather, setting)).ToList();
            for (var d = 1; d < days.Count; d++)
            {
                same += days[d] == days[d - 1] ? 1 : 0;
                total++;
            }
        }

        // Kept with KeepChance, plus redrawing the same kind: well above chance, below always.
        var ratio = (double)same / total;
        Assert.InRange(ratio, WeatherRoll.KeepChance + 0.05, 0.9);
    }

    [Fact]
    public void A_setting_only_rolls_the_weather_it_weights()
    {
        var weather = Weather();
        var setting = Settings().Get("summer-camp") with { Weather = new Dictionary<string, int> { ["clear"] = 3, ["rain"] = 1 } };

        var seen = Enumerable.Range(0, 60)
            .SelectMany(i => Enumerable.Range(1, setting.Days).Select(d => WeatherRoll.For($"camp-{i}", d, weather, setting)))
            .ToHashSet();

        Assert.Equal(["clear", "rain"], seen.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Rain_reaches_a_background_prompt_and_clear_adds_nothing()
    {
        var compiler = new NaturalPromptCompiler(Catalog());
        var pack = TestContent.NaturalPack();

        string Prompt(string type, string? weather) => compiler.CompilePositive(
            null,
            TestContent.Approved(new SceneIntent(type, TimeOfDay.Midday, "", "", "", Framing.FullBody, Weather: weather)),
            pack,
            RenderTarget.Background);

        Assert.Equal(Prompt("park", null), Prompt("park", WeatherContent.Clear));
        Assert.Contains("puddles", Prompt("park", "rain"), StringComparison.Ordinal);
        Assert.Contains("Rain streaks down the windows", Prompt("cafe", "rain"), StringComparison.Ordinal);
        Assert.EndsWith("no people in it.", Prompt("park", "storm"), StringComparison.Ordinal);
    }

    /// <summary>
    /// Measured: an overcast midday park came back under a blue sky, because the midday lighting line
    /// asked for one. Weather other than clear uses lighting that names no sky or sun.
    /// </summary>
    [Fact]
    public void Weather_replaces_the_sky_in_the_lighting_line()
    {
        var compiler = new NaturalPromptCompiler(Catalog());
        var pack = TestContent.NaturalPack();

        string Prompt(string type, TimeOfDay time, string? weather) => compiler.CompilePositive(
            null,
            TestContent.Approved(new SceneIntent(type, time, "", "", "", Framing.FullBody, Weather: weather)),
            pack,
            RenderTarget.Background);

        Assert.Contains("blue sky", Prompt("park", TimeOfDay.Midday, null), StringComparison.Ordinal);

        foreach (var weather in new[] { "cloudy", "rain", "storm", "fog" })
        {
            foreach (var time in Enum.GetValues<TimeOfDay>())
            {
                var prompt = Prompt("park", time, weather);
                Assert.DoesNotContain("blue sky", prompt, StringComparison.Ordinal);
                Assert.DoesNotContain("sunlight", prompt, StringComparison.Ordinal);
                Assert.DoesNotContain("starry", prompt, StringComparison.Ordinal);
                Assert.DoesNotContain("sunset", Prompt("cafe", time, weather), StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void A_kind_with_weather_tags_but_different_descriptions_is_refused()
    {
        var path = Path.Combine(Path.GetTempPath(), $"adate-weather-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """
            {
              "defaults": {
                "indoor": {
                  "timeTags": { "Morning": ["a"], "Midday": ["a"], "Afternoon": ["a"], "Evening": ["a"], "Night": ["a"] },
                  "timeDescriptions": { "Morning": "a", "Midday": "a", "Afternoon": "a", "Evening": "a", "Night": "a" },
                  "weatherTags": { "clear": [], "rain": ["rain on window"] },
                  "weatherDescriptions": { "clear": "" }
                }
              },
              "types": [ { "id": "cafe", "displayName": "Cafe", "kind": "indoor", "tags": ["cafe"], "description": "A cafe" } ]
            }
            """);

        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() => new JsonLocationCatalog(path));
            Assert.Contains("different weather", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
