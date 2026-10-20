using HeyNorm.Config;

namespace HeyNorm.Llm;

/// <summary>
/// Resolves a task name ("conversation", "daily-lesson", ...) to a provider strategy + model,
/// using the Tasks and Providers sections of appsettings.json. Unknown tasks fall back to "default".
/// </summary>
public sealed class ModelRouter
{
    private readonly AppSettings _settings;
    private readonly Func<ProviderSettings, IChatProvider> _create;
    private readonly Dictionary<string, IChatProvider> _providers = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="create">
    /// How to build a provider from its config. Defaults to the real Ollama/Claude clients;
    /// tests substitute fakes so the agent loop can run without a server.
    /// </param>
    public ModelRouter(AppSettings settings, Func<ProviderSettings, IChatProvider>? create = null)
    {
        _settings = settings;
        _create = create ?? CreateDefault;
    }

    public (IChatProvider Provider, TaskSettings Task) Resolve(string taskName)
    {
        if (!_settings.Tasks.TryGetValue(taskName, out var task) &&
            !_settings.Tasks.TryGetValue("default", out task))
            throw new InvalidOperationException($"No task '{taskName}' and no 'default' task in config.");

        return (GetProvider(task.Provider), task);
    }

    private IChatProvider GetProvider(string key)
    {
        if (_providers.TryGetValue(key, out var existing))
            return existing;

        if (!_settings.Providers.TryGetValue(key, out var cfg))
            throw new InvalidOperationException($"Provider '{key}' isn't defined in config.");

        var provider = _create(cfg);
        _providers[key] = provider;
        return provider;
    }

    private static IChatProvider CreateDefault(ProviderSettings cfg) => cfg.Type.ToLowerInvariant() switch
    {
        "ollama" => new OllamaChatProvider(new HttpClient(), cfg.BaseUrl),
        "claude" => new ClaudeChatProvider(new HttpClient(), cfg.BaseUrl, ReadKey(cfg)),
        _ => throw new InvalidOperationException($"Unknown provider type '{cfg.Type}'."),
    };

    private static string ReadKey(ProviderSettings cfg)
    {
        var name = string.IsNullOrWhiteSpace(cfg.ApiKeyEnvVar) ? "ANTHROPIC_API_KEY" : cfg.ApiKeyEnvVar;
        return Environment.GetEnvironmentVariable(name)
            ?? throw new InvalidOperationException($"Set the {name} environment variable to use Claude.");
    }
}
