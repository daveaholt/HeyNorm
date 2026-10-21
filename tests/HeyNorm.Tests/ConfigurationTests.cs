using HeyNorm.Config;
using Microsoft.Extensions.Configuration;

namespace HeyNorm.Tests;

/// <summary>
/// Guards the shipped appsettings.json. It is JSON *with comments*, which the configuration
/// provider tolerates and a plain JsonSerializer would not — and a parse failure here means
/// the app dies in Program.Main before the tray icon ever appears.
/// </summary>
public class ConfigurationTests
{
    private static string AppSettingsPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "HeyNorm.sln")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        var path = Path.Combine(dir!.FullName, "src", "HeyNorm", "appsettings.json");
        Assert.True(File.Exists(path), $"appsettings.json not found at {path}");
        return path;
    }

    private static AppSettings Load() =>
        new ConfigurationBuilder().AddJsonFile(AppSettingsPath(), optional: false).Build().Get<AppSettings>()
        ?? throw new InvalidOperationException("binding returned null");

    [Fact]
    public void The_shipped_config_parses_despite_its_comments()
    {
        var settings = Load();

        Assert.NotNull(settings);
        Assert.NotEmpty(settings.Tasks);
        Assert.NotEmpty(settings.Providers);
    }

    [Fact]
    public void Wake_and_stop_phrases_bind()
    {
        var wake = Load().Wake;

        Assert.NotEmpty(wake.WakePhrases);
        Assert.NotEmpty(wake.StopPhrases);
        Assert.All(wake.WakePhrases, p => Assert.False(string.IsNullOrWhiteSpace(p)));
        Assert.All(wake.StopPhrases, p => Assert.False(string.IsNullOrWhiteSpace(p)));
    }

    [Fact]
    public void The_configured_phrases_actually_match_what_they_are_meant_to()
    {
        var wake = Load().Wake;
        var matcher = new HeyNorm.Audio.WakePhraseMatcher(wake);

        Assert.All(wake.WakePhrases, p =>
            Assert.Equal(HeyNorm.Audio.Keyword.Wake, matcher.Match(p).Keyword));
        Assert.All(wake.StopPhrases, p => Assert.True(matcher.IsStop(p)));
    }

    [Fact]
    public void No_api_key_is_committed_in_the_config_file()
    {
        var settings = Load();

        Assert.All(settings.Providers.Values, p =>
            Assert.True(string.IsNullOrEmpty(p.ApiKeyEnvVar) || !p.ApiKeyEnvVar.StartsWith("sk-")));
    }

    [Fact]
    public void Speech_settings_bind_to_sane_values()
    {
        var speech = Load().Speech;

        Assert.EndsWith(".bin", speech.WhisperModelPath);
        Assert.InRange(speech.VadAggressiveness, 0, 3);
        Assert.True(speech.EndOfUtteranceSilenceMs > 0);
        Assert.True(speech.SessionIdleTimeoutSeconds > 0);
        Assert.True(speech.MaxUtteranceSeconds > 0);
        Assert.InRange(speech.NoSpeechThreshold, 0.5f, 0.95f); // below 0.5 drops short real words like "Thanks."
    }

    [Fact]
    public void The_conversation_task_named_by_the_assistant_actually_exists()
    {
        var settings = Load();

        Assert.True(settings.Tasks.ContainsKey(settings.Assistant.ConversationTask));
    }

    [Fact]
    public void A_default_task_exists_for_the_routers_fallback()
    {
        Assert.True(Load().Tasks.ContainsKey("default"));
    }

    [Fact]
    public void Every_task_points_at_a_defined_provider()
    {
        var settings = Load();

        Assert.All(settings.Tasks, entry =>
            Assert.True(settings.Providers.ContainsKey(entry.Value.Provider),
                $"Task '{entry.Key}' references undefined provider '{entry.Value.Provider}'."));
    }

    [Fact]
    public void Every_provider_declares_a_type_the_router_understands()
    {
        var settings = Load();

        Assert.All(settings.Providers.Values, p =>
            Assert.Contains(p.Type.ToLowerInvariant(), new[] { "ollama", "claude" }));
    }

    [Fact]
    public void The_system_prompt_tells_the_model_it_is_being_spoken_aloud()
    {
        var prompt = Load().Assistant.SystemPrompt;

        Assert.False(string.IsNullOrWhiteSpace(prompt));
        Assert.Contains("spoken aloud", prompt);
    }

    [Fact]
    public void Search_points_at_a_local_searxng()
    {
        var search = Load().Search;

        // Local on purpose: the whole point is search without a hosted service or a key.
        Assert.Equal("localhost", new Uri(search.SearXngUrl).Host);
        Assert.InRange(search.MaxResults, 1, 10);
    }

    [Fact]
    public void The_searxng_settings_turn_on_the_json_output_the_tool_reads()
    {
        // Off by default in SearXNG, and without it every search comes back 403.
        var settings = File.ReadAllText(Path.Combine(
            Path.GetDirectoryName(AppSettingsPath())!, "..", "..", "searxng", "settings.yml"));

        Assert.Matches(@"formats:\s*\n(\s*-\s*\w+\s*\n)*\s*-\s*json", settings.Replace("\r\n", "\n"));
    }

    [Fact]
    public void Weather_units_are_ones_the_tool_understands()
    {
        Assert.Contains(Load().Weather.Units, new[] { "imperial", "metric" });
    }

    [Fact]
    public void Spotify_settings_bind_and_keep_the_client_id_out_of_the_file()
    {
        var spotify = Load().Spotify;

        Assert.Equal("SPOTIFY_CLIENT_ID", spotify.ClientIdEnvVar);
        Assert.InRange(spotify.DuckToPercent, 0, 60);
        Assert.InRange(spotify.RedirectPort, 1024, 65535);
        // A Client ID is 32 hex characters; none belongs in a committed file.
        Assert.DoesNotMatch("[0-9a-f]{32}", File.ReadAllText(AppSettingsPath()));
    }

    [Fact]
    public void The_system_prompt_never_names_a_tool()
    {
        // Naming a tool in the prompt makes models echo the name into their reply, which
        // Norm then reads out loud. Measured: 2 of 3 sign-offs leaked it, 0 of 3 without.
        var settings = Load();
        var tools = new[] { "get_current_time", "set_timer", "end_session", "web_search", "get_weather", "control_music" };

        Assert.All(tools, t => Assert.DoesNotContain(t, settings.Assistant.SystemPrompt));
    }

    [Fact]
    public void Section_names_bind_case_insensitively_like_the_configuration_provider_does()
    {
        var settings = Load();

        Assert.True(settings.Tasks.ContainsKey("CONVERSATION"));
        Assert.True(settings.Providers.ContainsKey("OLLAMA"));
    }
}
