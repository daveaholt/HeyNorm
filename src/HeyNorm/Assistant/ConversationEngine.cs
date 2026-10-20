using System.Text.RegularExpressions;
using HeyNorm.Config;
using HeyNorm.Llm;
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

    public async Task<TurnResult> RunTurnAsync(string userText, CancellationToken ct)
    {
        _history.Add(ChatMessage.User(userText));
        var (provider, task) = _router.Resolve(_settings.ConversationTask);
        var corrected = false;

        for (var round = 0; round <= _settings.MaxToolRoundsPerTurn; round++)
        {
            var response = await provider.CompleteAsync(new ChatRequest
            {
                Model = task.Model,
                SystemPrompt = _settings.SystemPrompt,
                Messages = _history,
                Tools = _tools.Definitions,
                MaxTokens = task.MaxTokens,
                Think = task.Think,
            }, ct);

            _history.Add(new ChatMessage
            {
                Role = ChatRole.Assistant,
                Text = response.Text,
                ToolCalls = response.ToolCalls,
            });

            if (!response.WantsTools)
            {
                // Models type a tool's name instead of calling it — as the whole reply, or
                // tacked onto a good one ("Have a great day. End_session."). Either way it
                // must never reach the speaker.
                var spoken = WithoutToolNames(response.Text, out var removedAny);

                if (!string.IsNullOrWhiteSpace(spoken))
                    return new TurnResult(spoken, _signals.EndRequested);

                if (!removedAny)
                    return new TurnResult(response.Text, _signals.EndRequested);

                // Nothing left to say: the reply was only a tool name. Say so once, then
                // fall back rather than looping.
                if (corrected) return new TurnResult(StuckReply, _signals.EndRequested);

                corrected = true;
                _history.Add(ChatMessage.User(
                    "That reply was just a tool name. Either call the tool properly, or " +
                    "answer out loud in plain words."));
                continue;
            }

            foreach (var call in response.ToolCalls)
                _history.Add(await _tools.InvokeAsync(call, ct));
        }

        return new TurnResult(StuckReply, _signals.EndRequested);
    }

    private const string StuckReply = "Sorry, I got stuck on that one.";

    /// <summary>
    /// Drops any sentence that is nothing but a tool name, and reports whether it dropped
    /// one. Tolerates the wrappings models reach for: quotes, backticks, braces, "()".
    /// </summary>
    private string WithoutToolNames(string text, out bool removedAny)
    {
        removedAny = false;
        if (string.IsNullOrWhiteSpace(text)) return "";

        var kept = new List<string>();
        foreach (var sentence in Regex.Split(text.Trim(), @"(?<=[.!?])\s+"))
        {
            if (string.IsNullOrWhiteSpace(sentence)) continue;

            if (IsToolName(sentence)) removedAny = true;
            else kept.Add(sentence.Trim());
        }

        return string.Join(" ", kept).Trim();
    }

    private bool IsToolName(string sentence)
    {
        var trimmed = sentence.Trim().Trim('"', '\'', '`', '{', '}', '(', ')', '[', ']', '.', '!', '?', ' ');
        if (trimmed.EndsWith("()", StringComparison.Ordinal))
            trimmed = trimmed[..^2].Trim();

        return trimmed.Length > 0 && !trimmed.Contains(' ') && _tools.Knows(trimmed);
    }
}


