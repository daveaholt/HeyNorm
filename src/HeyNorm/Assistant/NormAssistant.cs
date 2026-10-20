using HeyNorm.Audio;
using HeyNorm.Config;
using HeyNorm.Speech;

namespace HeyNorm.Assistant;

public enum NormState { Sleeping, Screening, Starting, Listening, Thinking, Speaking }

/// <summary>
/// The state machine.
///
///   Sleeping  --speech heard--> Screening (STT) --matches "Hey Norm"--> Starting (chime) --> Listening
///   Screening --no match--> Sleeping
///   Listening --pause after speech--> Thinking (STT + model + tools) --> Speaking --> Listening
///   any active state --"Norm, done" / end_session tool / idle timeout--> Sleeping
///
/// There is no dedicated wake-word engine: every utterance is transcribed and the text is
/// matched against the wake phrase. That costs a Whisper pass per utterance heard while
/// asleep and buys Whisper's tolerance for shop noise.
///
/// The mic is half-duplex. While Norm screens, thinks, or talks, frames are dropped, so Norm
/// never transcribes its own voice — which also means the stop phrase is only heard while
/// Norm is listening.
/// </summary>
public sealed class NormAssistant : IAsyncDisposable
{
    private readonly AppSettings _settings;
    private readonly WakePhraseMatcher _phrases;
    private readonly MicrophoneSource _mic;
    private readonly UtteranceRecorder _recorder;
    private readonly ISpeechToText _stt;
    private readonly ITextToSpeech _tts;
    private readonly ConversationEngine _engine;
    private readonly CancellationTokenSource _shutdown = new();

    private int _state = (int)NormState.Sleeping;
    private CancellationTokenSource? _turnCts;
    private Task? _loop;

    public NormAssistant(
        AppSettings settings, WakePhraseMatcher phrases, MicrophoneSource mic, UtteranceRecorder recorder,
        ISpeechToText stt, ITextToSpeech tts, ConversationEngine engine)
    {
        _settings = settings;
        _phrases = phrases;
        _mic = mic;
        _recorder = recorder;
        _stt = stt;
        _tts = tts;
        _engine = engine;
    }

    public event Action<NormState>? StateChanged;
    public event Action<string>? Log;

    public NormState State => (NormState)Volatile.Read(ref _state);

    public void Start()
    {
        _mic.Start();
        _loop = Task.Run(() => RunAsync(_shutdown.Token));
        Log?.Invoke("Listening for \"Hey Norm\".");
    }

    /// <summary>Same as saying the wake word — used by the tray menu.</summary>
    public void BeginSessionManually()
    {
        if (State == NormState.Sleeping) _ = BeginSessionAsync();
    }

    /// <summary>Speak something unprompted (e.g. a timer going off).</summary>
    public async Task AnnounceAsync(string text)
    {
        await AudioPlayer.ChimeAsync(rising: true);
        await _tts.SpeakAsync(text, _shutdown.Token);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var frame in _mic.Frames.ReadAllAsync(ct))
            {
                switch (State)
                {
                    case NormState.Sleeping:
                        // No pre-roll here: lead-in audio garbles the wake phrase, and the
                        // wake phrase has been heard fine without it.
                        switch (_recorder.Feed(frame, withPreRoll: false))
                        {
                            case UtteranceEvent.Complete:
                                // Somebody said something. Transcribe it and see if it was for us.
                                SetState(NormState.Screening);
                                _ = ScreenAsync(_recorder.TakeUtterance(), ct);
                                break;
                            case UtteranceEvent.IdleTimeout:
                                // Nothing to end while asleep; just clear the counter.
                                _recorder.Reset();
                                break;
                        }
                        break;

                    case NormState.Listening:
                        switch (_recorder.Feed(frame))
                        {
                            case UtteranceEvent.Complete:
                                SetState(NormState.Thinking);
                                var samples = _recorder.TakeUtterance();
                                _turnCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                                _ = RunTurnAsync(samples, _turnCts.Token);
                                break;
                            case UtteranceEvent.IdleTimeout:
                                await EndSessionAsync("idle timeout");
                                break;
                        }
                        break;

                    // Screening, Starting, Thinking, Speaking: drop the frame.
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    /// <summary>
    /// Transcribes an utterance heard while asleep. Starts a session only if it opened with
    /// the wake phrase; anything else is shop chatter and we go back to sleep.
    /// </summary>
    private async Task ScreenAsync(float[] samples, CancellationToken ct)
    {
        try
        {
            var heard = await _stt.TranscribeAsync(samples, ct);
            var match = _phrases.Match(heard);
            if (match.Keyword != Keyword.Wake)
            {
                BackToSleep();
                return;
            }

            Log?.Invoke($"Woken by: {heard}");
            await BeginSessionAsync();

            // "Hey Norm, set a ten minute timer" — the request came in the same breath.
            if (!string.IsNullOrWhiteSpace(match.Remainder))
            {
                SetState(NormState.Thinking);
                _turnCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                await RunTurnFromTextAsync(match.Remainder, _turnCts.Token);
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
        catch (Exception ex)
        {
            Log?.Invoke($"Error while screening: {ex.Message}");
            BackToSleep();
        }
    }

    private void BackToSleep()
    {
        if (State != NormState.Screening) return;
        _recorder.Reset();
        SetState(NormState.Sleeping);
    }

    private async Task BeginSessionAsync()
    {
        var previous = Interlocked.Exchange(ref _state, (int)NormState.Starting);
        if (previous is (int)NormState.Listening or (int)NormState.Thinking or (int)NormState.Speaking)
        {
            // Already awake — leave the running session alone.
            Volatile.Write(ref _state, previous);
            return;
        }

        StateChanged?.Invoke(NormState.Starting);
        _engine.Reset();
        await AudioPlayer.ChimeAsync(rising: true);
        _recorder.Reset(); // discard anything captured during the chime
        SetState(NormState.Listening);
        Log?.Invoke("Session started.");
    }

    private async Task RunTurnAsync(float[] samples, CancellationToken ct)
    {
        try
        {
            var heard = await _stt.TranscribeAsync(samples, ct);
            if (string.IsNullOrWhiteSpace(heard))
            {
                BackToListening();
                return;
            }
            await RunTurnFromTextAsync(heard, ct);
        }
        catch (OperationCanceledException) { /* stop phrase or shutdown */ }
        catch (Exception ex)
        {
            await ReportTurnFailureAsync(ex);
        }
    }

    private async Task RunTurnFromTextAsync(string heard, CancellationToken ct)
    {
        try
        {
            Log?.Invoke($"You: {heard}");

            if (_phrases.IsStop(heard))
            {
                await EndSessionAsync("stop phrase");
                return;
            }

            var result = await _engine.RunTurnAsync(heard, ct);
            Log?.Invoke($"Norm: {result.SpokenReply}");

            SetState(NormState.Speaking);
            if (!string.IsNullOrWhiteSpace(result.SpokenReply))
                await _tts.SpeakAsync(result.SpokenReply, ct);

            if (result.EndSession) await EndSessionAsync("job complete");
            else BackToListening();
        }
        catch (OperationCanceledException) { /* stop phrase or shutdown */ }
        catch (Exception ex)
        {
            await ReportTurnFailureAsync(ex);
        }
    }

    private async Task ReportTurnFailureAsync(Exception ex)
    {
        Log?.Invoke($"Error: {ex.Message}");
        try { await _tts.SpeakAsync("Sorry, something went wrong. Check the log.", CancellationToken.None); }
        catch { /* nothing more to do */ }
        BackToListening();
    }

    private void BackToListening()
    {
        if (State is NormState.Thinking or NormState.Speaking)
        {
            _recorder.Reset();
            SetState(NormState.Listening);
        }
    }

    private async Task EndSessionAsync(string reason)
    {
        if (State == NormState.Sleeping) return;
        SetState(NormState.Sleeping);
        _recorder.Reset();
        _engine.Reset();
        Log?.Invoke($"Session ended ({reason}).");
        await AudioPlayer.ChimeAsync(rising: false);
    }

    private void SetState(NormState s)
    {
        Volatile.Write(ref _state, (int)s);
        StateChanged?.Invoke(s);
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        _mic.Dispose();
        if (_loop is not null)
        {
            try { await _loop; } catch { /* shutting down */ }
        }
        _recorder.Dispose();
        (_stt as IDisposable)?.Dispose();
        (_tts as IDisposable)?.Dispose();
    }
}
