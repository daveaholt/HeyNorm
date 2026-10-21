namespace HeyNorm.Llm;

/// <summary>
/// The strategy. One implementation per model provider; swap them in config, not code.
/// </summary>
public interface IChatProvider
{
    string Name { get; }

    Task<ChatResponse> CompleteAsync(ChatRequest request, CancellationToken ct = default);

    /// <summary>
    /// Same contract as <see cref="CompleteAsync"/>, but hands each fragment of reply text to
    /// <paramref name="onText"/> as it arrives, so speech can start before the model finishes.
    /// Tool calls only appear in the returned response, once they are complete.
    /// </summary>
    Task<ChatResponse> StreamAsync(ChatRequest request, Action<string> onText, CancellationToken ct = default);
}
