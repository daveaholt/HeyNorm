using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using HeyNorm.Config;

namespace HeyNorm.Tools;

/// <summary>
/// Current conditions and a seven-day forecast from Open-Meteo (https://open-meteo.com), which
/// is free for non-commercial use and needs no key. Web search can't stand in for this: result
/// snippets are weather-site boilerplate, not forecasts.
/// </summary>
public sealed class WeatherTool : ITool
{
    private const string GeocodingUrl = "https://geocoding-api.open-meteo.com/v1/search";
    private const string ForecastUrl = "https://api.open-meteo.com/v1/forecast";

    private readonly HttpClient _http;
    private readonly WeatherSettings _settings;
    private readonly bool _metric;
    // Places don't move; the home town in particular is looked up on every question otherwise.
    private readonly Dictionary<string, Place?> _places = new(StringComparer.OrdinalIgnoreCase);

    public WeatherTool(HttpClient http, WeatherSettings settings)
    {
        _http = http;
        _http.Timeout = TimeSpan.FromSeconds(10);
        _settings = settings;
        _metric = string.Equals(settings.Units, "metric", StringComparison.OrdinalIgnoreCase);
    }

    public string Name => "get_weather";
    public string Description =>
        "Get the current weather and the seven-day forecast: temperature, humidity, rain chance, wind. " +
        "Use it for any question about weather or humidity, including whether conditions suit gluing, " +
        "finishing, or spraying. Readings are outdoors; there is no indoor sensor. " +
        "Leave the location empty for the local weather.";
    public JsonObject ParametersSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["location"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "A town the person named, optionally with state or country, e.g. 'Austin, Texas'. Leave empty for the local weather.",
            },
        },
    };

    /// <summary>Two lookups at half a second each, plus the model reading the result.</summary>
    public string Preamble => "Let me check the forecast.";

    // What small models send when they mean "here" despite the description saying to leave it
    // empty. Measured: qwen3 sent "shop" when the description mentioned the shop, and the
    // geocoder duly found Shop, Malaysia. Compared after dropping "the", "my" and "area".
    // When, not where. Stripped from the location before it's looked up.
    private const string WhenWords =
        @"\b(today|tonight|tomorrow( night| morning)?|now|right now|later|this (week|weekend|morning|afternoon|evening)|" +
        @"next (week|weekend)|(on )?(monday|tuesday|wednesday|thursday|friday|saturday|sunday)|weekend)\b";

    private static readonly HashSet<string> MeansHere = new(StringComparer.OrdinalIgnoreCase)
        { "", "here", "home", "local", "shop", "workshop", "garage", "location", "current location", "current", "nearby" };

    public async Task<string> InvokeAsync(JsonElement arguments, CancellationToken ct)
    {
        var asked = arguments.TryGetProperty("location", out var l) && l.ValueKind == JsonValueKind.String
            ? l.GetString()!.Trim().TrimEnd('.', '?')
            : "";
        // Measured: "is it going to rain tomorrow?" came through as location "tomorrow".
        asked = Regex.Replace(asked, WhenWords, "", RegexOptions.IgnoreCase).Trim().Trim(',');
        var location = MeansHere.Contains(Regex.Replace(asked, @"\b(the|my|our|this|area)\b", "", RegexOptions.IgnoreCase).Trim())
            ? _settings.HomeLocation.Trim()
            : asked;
        if (location.Length == 0)
            return "Tell me which town you want the weather for. A home town can also be set in Norm's settings.";

        // Failures come back as sentences the model can say, as with web search.
        try
        {
            var place = await ResolveAsync(location, ct);
            if (place is null)
                return $"I couldn't find a place called {location}.";

            using var forecast = await GetJsonAsync(ForecastUrl + ForecastQuery(place), ct);
            return Describe(place, forecast.RootElement);
        }
        catch (HttpRequestException)
        {
            return "The weather service isn't available right now.";
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return "The weather service took too long to answer.";
        }
    }

    private sealed record Place(string Name, string? Region, string? Country, string? CountryCode, double Latitude, double Longitude)
    {
        // US towns go by state, everywhere else by country: "Austin, Texas", "Paris, France".
        public string Display => CountryCode == "US" ? Join(Name, Region) : Join(Name, Country);
        private static string Join(string a, string? b) => string.IsNullOrEmpty(b) || b == a ? a : $"{a}, {b}";
    }

    /// <summary>
    /// The geocoder matches town names only: "Austin, Texas" works but "Austin TX" finds
    /// nothing. So search the town and use whatever follows it to choose among the matches.
    /// </summary>
    private async Task<Place?> ResolveAsync(string location, CancellationToken ct)
    {
        if (_places.TryGetValue(location, out var cached)) return cached;

        var comma = location.IndexOf(',');
        var (town, qualifier) = comma > 0
            ? (location[..comma].Trim(), location[(comma + 1)..].Trim())
            : (location, null);

        var matches = await GeocodeAsync(town, ct);
        if (matches.Count == 0 && qualifier is null && town.LastIndexOf(' ') is var space and > 0)
        {
            // "Austin TX", "Portland Maine": the last word was the state.
            (town, qualifier) = (town[..space], town[(space + 1)..]);
            matches = await GeocodeAsync(town, ct);
        }

        // The geocoder already ranks by relevance and size, so with no qualifier, or one that
        // matches nothing, the first result is the best guess. The reply names the place it
        // chose, so a wrong guess is heard rather than hidden.
        var place = matches.Count == 0 ? null
            : qualifier is null ? matches[0]
            : matches.FirstOrDefault(m => Matches(m, qualifier)) ?? matches[0];
        _places[location] = place;
        return place;
    }

    private async Task<List<Place>> GeocodeAsync(string town, CancellationToken ct)
    {
        using var doc = await GetJsonAsync($"{GeocodingUrl}?name={Uri.EscapeDataString(town)}&count=10&language=en&format=json", ct);
        var places = new List<Place>();
        if (!doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
            return places;

        foreach (var r in results.EnumerateArray())
        {
            places.Add(new Place(
                r.GetProperty("name").GetString()!,
                Str(r, "admin1"), Str(r, "country"), Str(r, "country_code"),
                r.GetProperty("latitude").GetDouble(), r.GetProperty("longitude").GetDouble()));
        }
        return places;
    }

    private static bool Matches(Place p, string qualifier)
    {
        var q = qualifier.Replace(".", "").Trim().ToLowerInvariant();
        if (UsStates.TryGetValue(q, out var state)) q = state;
        if (q is "uk" or "england" or "scotland" or "wales") q = "gb";
        if (q is "usa" or "united states" or "america") q = "us";

        return string.Equals(p.Region, q, StringComparison.OrdinalIgnoreCase)
            || string.Equals(p.Country, q, StringComparison.OrdinalIgnoreCase)
            || string.Equals(p.CountryCode, q, StringComparison.OrdinalIgnoreCase);
    }

    private string ForecastQuery(Place p) =>
        $"?latitude={p.Latitude.ToString(CultureInfo.InvariantCulture)}&longitude={p.Longitude.ToString(CultureInfo.InvariantCulture)}" +
        "&current=temperature_2m,apparent_temperature,relative_humidity_2m,weather_code,wind_speed_10m" +
        "&daily=weather_code,temperature_2m_max,temperature_2m_min,precipitation_probability_max,precipitation_sum,wind_speed_10m_max" +
        (_metric ? "" : "&temperature_unit=fahrenheit&wind_speed_unit=mph&precipitation_unit=inch") +
        "&timezone=auto&forecast_days=7";

    private async Task<JsonDocument> GetJsonAsync(string url, CancellationToken ct)
    {
        using var response = await _http.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Open-Meteo returned {(int)response.StatusCode}.");
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
    }

    /// <summary>
    /// Units are spelled out ("miles an hour", not "mp/h") because the model tends to copy
    /// them straight into its reply, and the reply is spoken.
    /// </summary>
    private string Describe(Place place, JsonElement root)
    {
        var (speed, rain) = _metric ? ("kilometres an hour", "millimetres") : ("miles an hour", "inches");
        var text = new StringBuilder($"Weather for {place.Display}, temperatures in {(_metric ? "Celsius" : "Fahrenheit")}.\n");

        if (root.TryGetProperty("current", out var now))
        {
            text.Append(CultureInfo.InvariantCulture,
                $"Now: {Num(now, "temperature_2m"):0} degrees, feels like {Num(now, "apparent_temperature"):0}, " +
                $"{Sky(now)}, humidity {Num(now, "relative_humidity_2m"):0} percent, " +
                $"wind {Num(now, "wind_speed_10m"):0} {speed}.\n");
        }

        if (root.TryGetProperty("daily", out var daily) && daily.TryGetProperty("time", out var days))
        {
            for (var i = 0; i < days.GetArrayLength(); i++)
            {
                var date = DateOnly.ParseExact(days[i].GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture);
                var weekday = date.DayOfWeek.ToString();
                var label = i switch { 0 => $"Today, {weekday}", 1 => $"Tomorrow, {weekday}", _ => weekday };

                var amount = At(daily, "precipitation_sum", i);
                text.Append(CultureInfo.InvariantCulture,
                    $"{label}: high {At(daily, "temperature_2m_max", i):0}, low {At(daily, "temperature_2m_min", i):0}, " +
                    $"{Sky(At(daily, "weather_code", i))}, {At(daily, "precipitation_probability_max", i):0} percent chance of rain");
                if (amount >= (_metric ? 0.5 : 0.02))
                    text.Append(CultureInfo.InvariantCulture, $" with about {amount:0.##} {rain}");
                text.Append(CultureInfo.InvariantCulture, $", wind up to {At(daily, "wind_speed_10m_max", i):0} {speed}.\n");
            }
        }

        return text.ToString().TrimEnd();
    }

    private static double Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;

    private static double At(JsonElement daily, string name, int i) =>
        daily.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array && i < a.GetArrayLength() &&
        a[i].ValueKind == JsonValueKind.Number ? a[i].GetDouble() : 0;

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string Sky(JsonElement current) => Sky(Num(current, "weather_code"));

    /// <summary>WMO weather interpretation codes, as Open-Meteo documents them.</summary>
    internal static string Sky(double code) => (int)code switch
    {
        0 => "clear sky",
        1 => "mainly clear",
        2 => "partly cloudy",
        3 => "overcast",
        45 or 48 => "fog",
        51 or 53 or 55 => "drizzle",
        56 or 57 => "freezing drizzle",
        61 => "light rain",
        63 => "rain",
        65 => "heavy rain",
        66 or 67 => "freezing rain",
        71 => "light snow",
        73 => "snow",
        75 => "heavy snow",
        77 => "snow grains",
        80 or 81 => "rain showers",
        82 => "violent rain showers",
        85 or 86 => "snow showers",
        95 => "thunderstorms",
        96 or 99 => "thunderstorms with hail",
        _ => "unsettled",
    };

    private static readonly Dictionary<string, string> UsStates = new()
    {
        ["al"] = "alabama", ["ak"] = "alaska", ["az"] = "arizona", ["ar"] = "arkansas", ["ca"] = "california",
        ["co"] = "colorado", ["ct"] = "connecticut", ["de"] = "delaware", ["dc"] = "district of columbia",
        ["fl"] = "florida", ["ga"] = "georgia", ["hi"] = "hawaii", ["id"] = "idaho", ["il"] = "illinois",
        ["in"] = "indiana", ["ia"] = "iowa", ["ks"] = "kansas", ["ky"] = "kentucky", ["la"] = "louisiana",
        ["me"] = "maine", ["md"] = "maryland", ["ma"] = "massachusetts", ["mi"] = "michigan", ["mn"] = "minnesota",
        ["ms"] = "mississippi", ["mo"] = "missouri", ["mt"] = "montana", ["ne"] = "nebraska", ["nv"] = "nevada",
        ["nh"] = "new hampshire", ["nj"] = "new jersey", ["nm"] = "new mexico", ["ny"] = "new york",
        ["nc"] = "north carolina", ["nd"] = "north dakota", ["oh"] = "ohio", ["ok"] = "oklahoma", ["or"] = "oregon",
        ["pa"] = "pennsylvania", ["ri"] = "rhode island", ["sc"] = "south carolina", ["sd"] = "south dakota",
        ["tn"] = "tennessee", ["tx"] = "texas", ["ut"] = "utah", ["vt"] = "vermont", ["va"] = "virginia",
        ["wa"] = "washington", ["wv"] = "west virginia", ["wi"] = "wisconsin", ["wy"] = "wyoming",
    };
}
