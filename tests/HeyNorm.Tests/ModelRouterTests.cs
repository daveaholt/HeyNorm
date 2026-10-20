using HeyNorm.Config;
using HeyNorm.Llm;

namespace HeyNorm.Tests;

public class ModelRouterTests
{
    private static AppSettings Settings() => new()
    {
        Providers =
        {
            ["ollama"] = new ProviderSettings { Type = "Ollama", BaseUrl = "http://localhost:11434" },
            ["claude"] = new ProviderSettings { Type = "Claude", ApiKeyEnvVar = "HEYNORM_TEST_KEY" },
        },
        Tasks =
        {
            ["default"] = new TaskSettings { Provider = "ollama", Model = "qwen3:8b" },
            ["conversation"] = new TaskSettings { Provider = "ollama", Model = "qwen3:8b", MaxTokens = 512 },
        },
    };

    [Fact]
    public void Resolves_a_known_task_to_its_provider_and_model()
    {
        var router = new ModelRouter(Settings());

        var (provider, task) = router.Resolve("conversation");

        Assert.Equal("Ollama", provider.Name);
        Assert.Equal("qwen3:8b", task.Model);
        Assert.Equal(512, task.MaxTokens);
    }

    [Fact]
    public void An_unknown_task_falls_back_to_default()
    {
        var router = new ModelRouter(Settings());

        var (_, task) = router.Resolve("daily-lesson");

        Assert.Equal("qwen3:8b", task.Model);
        Assert.Equal(1024, task.MaxTokens); // the default task's value, not conversation's 512
    }

    [Fact]
    public void An_unknown_task_with_no_default_configured_is_an_error()
    {
        var settings = Settings();
        settings.Tasks.Remove("default");
        var router = new ModelRouter(settings);

        var ex = Assert.Throws<InvalidOperationException>(() => router.Resolve("daily-lesson"));

        Assert.Contains("daily-lesson", ex.Message);
    }

    [Fact]
    public void Task_names_are_case_insensitive()
    {
        var router = new ModelRouter(Settings());

        var (_, task) = router.Resolve("CONVERSATION");

        Assert.Equal(512, task.MaxTokens);
    }

    [Fact]
    public void A_task_pointing_at_an_undefined_provider_is_an_error()
    {
        var settings = Settings();
        settings.Tasks["conversation"] = new TaskSettings { Provider = "gemini", Model = "x" };
        var router = new ModelRouter(settings);

        var ex = Assert.Throws<InvalidOperationException>(() => router.Resolve("conversation"));

        Assert.Contains("gemini", ex.Message);
    }

    [Fact]
    public void An_unrecognised_provider_type_is_an_error()
    {
        var settings = Settings();
        settings.Providers["weird"] = new ProviderSettings { Type = "telepathy" };
        settings.Tasks["conversation"] = new TaskSettings { Provider = "weird", Model = "x" };
        var router = new ModelRouter(settings);

        var ex = Assert.Throws<InvalidOperationException>(() => router.Resolve("conversation"));

        Assert.Contains("telepathy", ex.Message);
    }

    [Fact]
    public void Providers_are_built_once_and_reused_across_resolves()
    {
        var built = 0;
        var router = new ModelRouter(Settings(), _ =>
        {
            built++;
            return new FakeChatProvider();
        });

        router.Resolve("conversation");
        router.Resolve("conversation");
        router.Resolve("default");

        Assert.Equal(1, built);
    }

    [Fact]
    public void Claude_reads_its_key_from_the_configured_environment_variable()
    {
        var settings = Settings();
        settings.Tasks["conversation"] = new TaskSettings { Provider = "claude", Model = "claude-sonnet-5-5" };
        Environment.SetEnvironmentVariable("HEYNORM_TEST_KEY", "sk-test");
        try
        {
            var (provider, _) = new ModelRouter(settings).Resolve("conversation");

            Assert.Equal("Claude", provider.Name);
        }
        finally
        {
            Environment.SetEnvironmentVariable("HEYNORM_TEST_KEY", null);
        }
    }

    [Fact]
    public void A_missing_claude_key_fails_with_the_variable_name_in_the_message()
    {
        var settings = Settings();
        settings.Tasks["conversation"] = new TaskSettings { Provider = "claude", Model = "claude-sonnet-5-5" };
        Environment.SetEnvironmentVariable("HEYNORM_TEST_KEY", null);

        var ex = Assert.Throws<InvalidOperationException>(() => new ModelRouter(settings).Resolve("conversation"));

        Assert.Contains("HEYNORM_TEST_KEY", ex.Message);
    }

    [Fact]
    public void Claude_defaults_to_ANTHROPIC_API_KEY_when_no_variable_is_named()
    {
        var settings = Settings();
        settings.Providers["claude"] = new ProviderSettings { Type = "Claude", ApiKeyEnvVar = "" };
        settings.Tasks["conversation"] = new TaskSettings { Provider = "claude", Model = "claude-sonnet-5-5" };
        var saved = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null);
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(
                () => new ModelRouter(settings).Resolve("conversation"));

            Assert.Contains("ANTHROPIC_API_KEY", ex.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", saved);
        }
    }
}
