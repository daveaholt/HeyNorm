using System.Net;
using HeyNorm.Config;
using HeyNorm.Tools;

namespace HeyNorm.Tests;

public class WeatherToolTests
{
    // Trimmed from real Open-Meteo responses.
    private const string AustinMatches = """
        { "results": [
            { "name": "Austin", "admin1": "Texas", "country": "United States", "country_code": "US", "latitude": 30.26715, "longitude": -97.74306 },
            { "name": "Austin", "admin1": "Minnesota", "country": "United States", "country_code": "US", "latitude": 43.66663, "longitude": -92.97464 }
        ] }
        """;

    private const string NoMatches = """{ "generationtime_ms": 0.1 }""";

    private const string Forecast = """
        {
          "current": { "temperature_2m": 83.1, "apparent_temperature": 81.8, "relative_humidity_2m": 26, "weather_code": 0, "wind_speed_10m": 4.8 },
          "daily": {
            "time": ["2026-10-21", "2026-10-22", "2026-10-23"],
            "weather_code": [0, 3, 63],
            "temperature_2m_max": [84.3, 86.1, 70.2],
            "temperature_2m_min": [50.6, 56.9, 61.6],
            "precipitation_probability_max": [0, 1, 80],
            "precipitation_sum": [0.0, 0.004, 0.62],
            "wind_speed_10m_max": [6.1, 6.7, 10.6]
          }
        }
        """;

    private static (WeatherTool Tool, StubHandler Stub) Build(
        string home = "Austin, Texas", string units = "imperial", params string[] responses)
    {
        var stub = new StubHandler();
        foreach (var r in responses) stub.Enqueue(r);
        var tool = new WeatherTool(new HttpClient(stub), new WeatherSettings { HomeLocation = home, Units = units });
        return (tool, stub);
    }

    private static Task<string> Ask(WeatherTool tool, string? location = null) =>
        tool.InvokeAsync(
            location is null ? Json.Empty : Json.Element($$"""{ "location": "{{location}}" }"""),
            CancellationToken.None);

    private static string Query(StubHandler stub, int i) => Uri.UnescapeDataString(stub.RequestUris[i].Query);

    [Fact]
    public async Task With_no_location_it_uses_the_home_town()
    {
        var (tool, stub) = Build(responses: [AustinMatches, Forecast]);

        var text = await Ask(tool);

        Assert.StartsWith("Weather for Austin, Texas, temperatures in Fahrenheit.", text);
        Assert.Contains("name=Austin&", Query(stub, 0));
        Assert.Contains("latitude=30.26715&longitude=-97.74306", Query(stub, 1));
    }

    [Theory]
    [InlineData("here")]
    [InlineData("current location")]
    [InlineData("")]
    [InlineData("shop")]      // measured: qwen3 sent this, and the geocoder found Shop, Malaysia
    [InlineData("the shop")]
    [InlineData("my area")]
    [InlineData("Garage")]
    public async Task Words_that_mean_here_use_the_home_town(string location)
    {
        var (tool, _) = Build(responses: [AustinMatches, Forecast]);

        Assert.StartsWith("Weather for Austin, Texas", await Ask(tool, location));
    }

    [Fact]
    public async Task A_town_containing_a_filler_word_is_not_mistaken_for_here()
    {
        // "Weatherford" holds "the" and "Thermopolis" starts with it; neither means here.
        var (tool, stub) = Build(responses: [NoMatches]);

        await Ask(tool, "Weatherford");

        Assert.Contains("name=Weatherford&", Query(stub, 0));
    }

    [Fact]
    public async Task The_home_town_is_looked_up_once_not_on_every_question()
    {
        var (tool, stub) = Build(responses: [AustinMatches, Forecast, Forecast]);

        await Ask(tool);
        await Ask(tool);

        Assert.Equal(3, stub.RequestUris.Count); // one geocode, two forecasts
    }

    [Fact]
    public async Task With_no_location_and_no_home_town_it_asks_which_town()
    {
        var (tool, stub) = Build(home: "");

        var text = await Ask(tool);

        Assert.Contains("which town", text);
        Assert.Empty(stub.RequestUris);
    }

    [Fact]
    public async Task A_state_after_a_comma_picks_the_right_town()
    {
        var (tool, _) = Build(responses: [AustinMatches, Forecast]);

        Assert.StartsWith("Weather for Austin, Minnesota", await Ask(tool, "Austin, Minnesota"));
    }

    [Theory]
    [InlineData("Austin TX")]
    [InlineData("Austin Texas")]
    public async Task A_trailing_state_without_a_comma_is_retried_as_a_qualifier(string location)
    {
        // The geocoder finds nothing for "Austin TX", so the tool tries "Austin" and uses the
        // last word to choose.
        var (tool, stub) = Build(home: "", responses: [NoMatches, AustinMatches, Forecast]);

        var text = await Ask(tool, location);

        Assert.StartsWith("Weather for Austin, Texas", text);
        Assert.Contains("name=Austin&", Query(stub, 1));
    }

    [Fact]
    public async Task A_state_abbreviation_after_a_comma_works()
    {
        var (tool, _) = Build(home: "", responses: [AustinMatches, Forecast]);

        Assert.StartsWith("Weather for Austin, Minnesota", await Ask(tool, "Austin, MN"));
    }

    [Fact]
    public async Task A_place_that_does_not_exist_says_so()
    {
        var (tool, _) = Build(home: "", responses: [NoMatches]);

        Assert.Equal("I couldn't find a place called Qwxzv.", await Ask(tool, "Qwxzv"));
    }

    [Fact]
    public async Task Describes_now_and_each_day_with_labels_the_model_can_match_to_the_question()
    {
        var (tool, _) = Build(responses: [AustinMatches, Forecast]);

        var text = await Ask(tool);

        Assert.Contains(
            "Now: 83 degrees, feels like 82, clear sky, humidity 26 percent, wind 5 miles an hour.", text);
        Assert.Contains(
            "Today, Wednesday: high 84, low 51, clear sky, 0 percent chance of rain, wind up to 6 miles an hour.", text);
        Assert.Contains("Tomorrow, Thursday: high 86, low 57, overcast, 1 percent chance of rain, wind", text);
        // A trace of rain isn't worth mentioning; a real amount is.
        Assert.DoesNotContain("0 inches", text);
        Assert.Contains("Friday: high 70, low 62, rain, 80 percent chance of rain with about 0.62 inches,", text);
    }

    [Fact]
    public async Task Asks_for_imperial_units_by_default()
    {
        var (tool, stub) = Build(responses: [AustinMatches, Forecast]);

        await Ask(tool);

        var query = Query(stub, 1);
        Assert.Contains("temperature_unit=fahrenheit", query);
        Assert.Contains("wind_speed_unit=mph", query);
        Assert.Contains("timezone=auto", query);
    }

    [Fact]
    public async Task Metric_leaves_open_meteos_metric_defaults_and_says_so()
    {
        var (tool, stub) = Build(units: "metric", responses: [AustinMatches, Forecast]);

        var text = await Ask(tool);

        Assert.DoesNotContain("fahrenheit", Query(stub, 1));
        Assert.Contains("temperatures in Celsius", text);
        Assert.Contains("kilometres an hour", text);
    }

    [Fact]
    public async Task An_unreachable_service_comes_back_as_a_plain_sentence()
    {
        var tool = new WeatherTool(new HttpClient(new UnreachableHandler()), new WeatherSettings { HomeLocation = "Austin" });

        Assert.Equal("The weather service isn't available right now.", await Ask(tool));
    }

    [Fact]
    public async Task An_error_status_comes_back_as_a_plain_sentence()
    {
        var stub = new StubHandler().Enqueue("""{ "error": true }""", HttpStatusCode.TooManyRequests);
        var tool = new WeatherTool(new HttpClient(stub), new WeatherSettings { HomeLocation = "Austin" });

        Assert.Equal("The weather service isn't available right now.", await Ask(tool));
    }

    [Theory]
    [InlineData(0, "clear sky")]
    [InlineData(3, "overcast")]
    [InlineData(65, "heavy rain")]
    [InlineData(95, "thunderstorms")]
    [InlineData(42, "unsettled")]
    public void Weather_codes_become_words(int code, string words)
    {
        Assert.Equal(words, WeatherTool.Sky(code));
    }

    [Fact]
    public void It_has_a_preamble_because_it_makes_two_network_calls()
    {
        var (tool, _) = Build();

        Assert.False(string.IsNullOrWhiteSpace(((ITool)tool).Preamble));
    }

    [Fact]
    public void The_description_never_mentions_the_shop()
    {
        // Mentioning it is what made qwen3 send "shop" as the location.
        var (tool, _) = Build();

        Assert.DoesNotContain("shop", tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("shop", tool.ParametersSchema.ToJsonString(), StringComparison.OrdinalIgnoreCase);
    }
}
