namespace HeyNorm.Llm;

/// <summary>
/// The strategy. One implementation per model provider; swap them in config, not code.
/// </summary>
public interface IChatProvider
{
    string Name { get; }

    Task<ChatResponse> CompleteAsync(ChatRequest request, CancellationToken ct = default);
}
