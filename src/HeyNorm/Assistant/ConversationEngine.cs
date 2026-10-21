using HeyNorm.Config;
using HeyNorm.Llm;
using HeyNorm.Speech;
using HeyNorm.Tools;

namespace HeyNorm.Assistant;

/// <summary>Flags tools can raise for the session (e.g. end_session).</summary>
public sealed class SessionSignals
{
    public volatile bool EndRequested;
}

public sealed record TurnResult(string SpokenReply, bool EndSession);

/// <summary>
/// The agent loop: send the conversation, run any tools the model asks for, feed results back,
/// repeat until the model answers in plain text. Provider-agnostic.
/// </summary>
public sealed class ConversationEngine
{
    private readonly ModelRouter _router;
    private readonly ToolRegistry _tools;
    private readonly AssistantSettings _settings;
    private readonly SessionSignals _signals;
    private readonly List<ChatMessage> _history = [];

    public ConversationEngine(ModelRouter router, ToolRegistry tools, AssistantSettings settings, SessionSignals signals)
    {
        _router = router;
        _tools = tools;
        _settings = settings;
        _signals = signals;
    }

    /// <summary>Called when a session ends so the next "Hey Norm" starts fresh.</summary>
    public void Reset()
    {
        _history.Clear();
        _signals.EndRequested = false;
    }

    public Task<TurnResult> RunTurnAsync(string userText, CancellationToken ct) =>
        RunTurnAsync(userText, _ => { }, ct);

    /// <summary>
    /// Runs one turn, handing each sentence of the reply to <paramref name="onSentence"/> as soon
    /// as the model has finished writing it. <see cref="TurnResult.SpokenReply"/> is everything
    /// that was handed over, for the log.
    /// </summary>
    public async Task<TurnResult> RunTurnAsync(string userText, Action<string> onSentence, CancellationToken ct)
    {
        _history.Add(ChatMessage.User(userText));
        var (provider, task) = _router.Resolve(_settings.ConversationTask);
        var spoken = new List<string>();
        var corrected = false;

        for (var round = 0; round <= _settings.MaxToolRoundsPerTurn; round++)
        {
            // Models type a tool's name instead of calling it — as the whole reply, or tacked
            // onto a good one ("Have a great day. End_session."). The check is per sentence,
            // so it runs as each one streams in and the name never reaches the speaker.
            var buffer = new SentenceBuffer();
            var spokeThisRound = false;
            var removedAny = false;

            void Emit(IEnumerable<string> sentences)
            {
                foreach (var sentence in sentences)
                {
                    if (IsToolName(sentence)) { removedAny = true; continue; }
                    spokeThisRound = true;
                    spoken.Add(sentence);
                    onSentence(sentence);
                }
            }

            var response = await provider.StreamAsync(new ChatRequest
            {
                Model = task.Model,
                SystemPrompt = _settings.SystemPrompt,
                Messages = _history,
                Tools = _tools.Definitions,
                MaxTokens = task.MaxTokens,
                Think = task.Think,
            }, fragment => Emit(buffer.Append(fragment)), ct);
            Emit(buffer.Flush());

            _history.Add(new ChatMessage
            {
                Role = ChatRole.Assistant,
                Text = response.Text,
                ToolCalls = response.ToolCalls,
            });

            if (!response.WantsTools)
            {
                if (spokeThisRound || !removedAny)
                    return Result();

                // Nothing left to say: the reply was only a tool name. Say so once, then
                // fall back rather than looping.
                if (corrected) return Stuck();

                corrected = true;
                _history.Add(ChatMessage.User(
                    "That reply was just a tool name. Either call the tool properly, or " +
                    "answer out loud in plain words."));
                continue;
            }

            // Any text that came with the tool calls ("Let me check.") has already been spoken,
            // which covers the silence while the tools run.
            foreach (var call in response.ToolCalls)
                _history.Add(await _tools.InvokeAsync(call, ct));
        }

        return Stuck();

        TurnResult Result() => new(string.Join(" ", spoken), _signals.EndRequested);

        TurnResult Stuck()
        {
            spoken.Add(StuckReply);
            onSentence(StuckReply);
            return Result();
        }
    }

    private const string StuckReply = "Sorry, I got stuck on that one.";

    /// <summary>
    /// True for a sentence that is nothing but a tool name. Tolerates the wrappings models
    /// reach for: quotes, backticks, braces, "()".
    /// </summary>
    private bool IsToolName(string sentence)
    {
        var trimmed = sentence.Trim().Trim('"', '\'', '`', '{', '}', '(', ')', '[', ']', '.', '!', '?', ' ');
        if (trimmed.EndsWith("()", StringComparison.Ordinal))
            trimmed = trimmed[..^2].Trim();

        return trimmed.Length > 0 && !trimmed.Contains(' ') && _tools.Knows(trimmed);
    }
}


