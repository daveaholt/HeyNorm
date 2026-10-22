using System.Text.RegularExpressions;
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
    private readonly Func<string>? _promptAddition;
    private readonly List<ChatMessage> _history = [];

    /// <param name="promptAddition">
    /// Appended to the system prompt on every request — what the person asked Norm to
    /// remember. Read each round, so something remembered mid-turn applies at once.
    /// </param>
    public ConversationEngine(ModelRouter router, ToolRegistry tools, AssistantSettings settings, SessionSignals signals,
        Func<string>? promptAddition = null)
    {
        _promptAddition = promptAddition;
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

    /// <summary>Loads the conversation model ahead of the first question of a session.</summary>
    public Task WarmUpAsync(CancellationToken ct)
    {
        var (provider, task) = _router.Resolve(_settings.ConversationTask);
        return provider.WarmUpAsync(task.Model, ct);
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
        var resampled = false;
        var announced = false;

        for (var round = 0; round <= _settings.MaxToolRoundsPerTurn; round++)
        {
            // Models type a tool's name instead of calling it — as the whole reply, or tacked
            // onto a good one ("Have a great day. End_session."). The check is per sentence,
            // so it runs as each one streams in and the name never reaches the speaker.
            var buffer = new SentenceBuffer();
            var spokeThisRound = false;
            var onlyPromised = true;
            var removedAny = false;

            void Emit(IEnumerable<string> sentences)
            {
                // Emoji and markdown come out first: Piper reads them aloud. A sentence that was
                // nothing but an emoji is gone, not spoken as silence.
                foreach (var sentence in sentences.Select(Speakable.Clean).Where(s => s.Length > 0))
                {
                    if (IsToolName(sentence)) { removedAny = true; continue; }
                    spokeThisRound = true;
                    onlyPromised &= IsPromiseToLook(sentence);
                    spoken.Add(sentence);
                    onSentence(sentence);
                }
            }

            var response = await provider.StreamAsync(new ChatRequest
            {
                Model = task.Model,
                SystemPrompt = _settings.SystemPrompt + (_promptAddition?.Invoke() ?? ""),
                Messages = _history,
                Tools = _tools.Definitions,
                MaxTokens = task.MaxTokens,
                Think = task.Think,
                Temperature = task.Temperature,
                RepeatPenalty = task.RepeatPenalty,
                ContextLength = task.ContextLength,
            }, fragment => Emit(buffer.Append(fragment)), ct);
            Emit(buffer.Flush());

            // A reply that only promises a lookup ("I'll need to check the plans. Let me look
            // that up.") and makes none: ask again, once, silently. Measured: 2 of 5 times on
            // "how far apart are the frames?". Telling the model it had stalled was tried first
            // and was worse — it apologised, stalled again, or invented a figure — so nothing is
            // added: the stall is dropped from the history and the model gets a fresh draw. The
            // promise has already been spoken, and the real answer follows it naturally.
            if (!response.WantsTools && spokeThisRound && onlyPromised && !resampled)
            {
                resampled = true;
                announced = true; // the promise already said a lookup was coming; no second "let me check"
                continue;
            }

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
            // which covers the silence while the tools run. If there was none and a tool is
            // slow, say its preamble; it plays while the tool works. Once per turn is enough —
            // "let me look that up" twice in a row sounds broken.
            foreach (var call in response.ToolCalls)
            {
                if (!spokeThisRound && !announced && _tools.PreambleFor(call.Name) is { } preamble)
                {
                    announced = true;
                    spoken.Add(preamble);
                    onSentence(preamble);
                }
                _history.Add(await _tools.InvokeAsync(call, ct));
            }
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

    // Deliberately narrow: a sentence that only says a lookup is coming. "Let me know if you
    // need anything" and "I'll be here" are sign-offs, not stalls.
    private static readonly Regex PromiseToLook = new(
        @"^(?:okay,?\s+|sure,?\s+|alright,?\s+)?(?:let me|i'?ll|i will|i'?ll need to|i need to|i'm going to)\s+" +
        @"(?:quickly\s+|just\s+|need to\s+)?(?:check|look|find|get|see|search|pull|fetch)\b",
        RegexOptions.IgnoreCase);

    private static bool IsPromiseToLook(string sentence) =>
        sentence.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 15 &&
        PromiseToLook.IsMatch(sentence.Trim().Replace('\u2019', '\''));

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


