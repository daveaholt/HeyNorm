# CLAUDE.md

Hey Norm — a Windows system-tray voice assistant. Wake word, local speech-to-text, an LLM
agent loop with tools, text-to-speech. Read `README.md` for end-user setup; this file is for
working on the code.

## Commands

```powershell
dotnet build HeyNorm.sln              # Debug
dotnet build HeyNorm.sln -c Release
dotnet test                           # ~165 tests, ~1 s, no network or mic needed
dotnet run --project src/HeyNorm      # see "Running it" below before expecting output
```

## Toolchain

Targets `net8.0-windows`. Only the 9.0 and 10.0 SDKs are installed on this machine; that is
fine — the 8.0 targeting pack resolves from NuGet and the 8.0.27 runtime is present. Don't
"fix" the TFM or add a `global.json` to chase an SDK that isn't there.

App `PackageReference` versions float (`3.*`, `1.*`, …), so restores can pull newer
transitive packages over time (Whisper.net 1.9.1 currently drags in
`Microsoft.Extensions.AI.Abstractions` 10.2.0 and `System.IO.Pipelines` 10.0.2 — harmless,
but it means builds aren't reproducible). Pin a version if a float ever breaks the build.
Test-project packages are pinned.

**Both Whisper runtime packages are referenced on purpose.** `Whisper.net.Runtime.Cuda` puts
its natives in `runtimes/cuda/win-x64/` (including a ~147 MB `ggml-cuda-whisper.dll`, hence a
~300 MB output directory) but does *not* bundle the CUDA runtime, so the GPU backend only
loads when CUDA 12 is installed on the machine. Whisper.net then falls back to the CPU build —
but it looks for that under `runtimes/win-x64/`, which the CUDA package does not populate.
Verified: with the CUDA package alone and no CUDA installed, startup dies with
`Native Library not found in default paths`, even though a `ggml-cpu-whisper.dll` is sitting
right there in the CUDA folder. Hence `Whisper.net.Runtime` stays referenced alongside it.
Don't remove it unless CUDA is a hard requirement.

## Running it

`OutputType` is `WinExe`, so there is **no console**. Nothing prints to the terminal — ever.
Diagnostics go to `%LOCALAPPDATA%\HeyNorm\log.txt` (also "Open log" in the tray menu).
Startup failures additionally raise a `MessageBox`, which will hang a non-interactive run
until the process is killed.

A fresh clone **cannot** reach a working state: `models/*.bin` and `piper/` are gitignored
and must be downloaded per README steps 3–4. Without the Whisper model, `Program.Build` throws
in `WhisperSpeechToText`'s constructor before the tray icon appears — expected, not a
regression. Without Piper, Norm silently falls back to the SAPI voice; the log's
`Text-to-speech:` line at startup says which one loaded.

The app holds `bin/…/HeyNorm.exe` open, and its long-running `piper.exe` child holds
`bin/…/piper/`, so **stop Norm before rebuilding** or the build fails with
`MSB3027 … locked by: "HeyNorm (pid)"`. Piper exits on its own when Norm does — it quits when
its stdin closes.

## Architecture

```
MicrophoneSource ──short[] frames──▶ NormAssistant (state machine)
  16 kHz mono            │
                         └─▶ UtteranceRecorder (WebRTC VAD: utterance end, idle timeout)
                                   │
                         float[] ──▶ WhisperSpeechToText
                                   │
                              text ──┬─▶ WakePhraseMatcher  (asleep: is this for us?)
                                     │
                                     └─▶ ConversationEngine ──▶ ModelRouter ──▶ IChatProvider
                                              │ agent loop        (task → provider+model)
                                              └──▶ ToolRegistry ──▶ ITool
                                   │
                              reply ──▶ ITextToSpeech (Piper, else SAPI)
```

**There is no wake-word engine.** Porcupine was removed because Picovoice's console requires
a business email. Instead every utterance — asleep or awake — goes through Whisper, and
`WakePhraseMatcher` matches the transcript text. That costs one Whisper pass per utterance
heard while sleeping and buys Whisper's noise tolerance, which matters more in a shop than
the milliseconds a purpose-built detector would save.

Composition root is `Program.Build` — everything is hand-wired, no DI container. The tray
(`TrayApplicationContext`) owns the assistant and is the only UI.

### Things that will bite you

- **One thread owns the mic.** `NormAssistant.RunAsync` is a single loop reading a
  `Channel<short[]>`. In `Sleeping` and `Listening` every frame goes through the VAD; in
  every other state it is dropped. Don't add blocking work to that loop; dispatch to a `Task`
  the way `RunTurnAsync` does.
- **Half-duplex by design.** While `Screening`, `Thinking`, or `Speaking`, frames are dropped
  entirely, so Norm never transcribes its own voice. The cost is that the stop phrase is only
  heard while `Listening` — you can't interrupt a reply in progress. Real barge-in needs echo
  cancellation, not a change to the state machine.
- **`Screening` exists to stop double-transcription.** When a sleeping Norm hears an
  utterance it moves to `Screening` and transcribes asynchronously; without that state the
  frames arriving meanwhile would kick off a second overlapping transcription.
- **`MicrophoneSource` frames are 512 samples**, chosen here rather than dictated by a wake
  engine. `UtteranceRecorder` re-chunks internally to the 20 ms (320-sample) frames WebRTC
  VAD requires — the two sizes are unrelated, so keep the re-chunking.
- **State transitions go through `SetState`/`CompareExchange`,** and `StateChanged` fires on
  the audio thread. The tray marshals to the UI thread via `WindowsFormsSynchronizationContext`.
  Anything else subscribing must do the same.
- **Whisper invents speech from noise** — "Thank you for having me." with nobody talking.
  `WhisperSpeechToText` drops segments whose `NoSpeechProbability` exceeds
  `Speech:NoSpeechThreshold` (0.7: measured real speech peaks at 0.64, noise starts at 0.73),
  then drops whole transcripts matching a short list of Whisper's stock phrases. Both log an
  `Ignored as …` line. Keep plain "thank you" off that list — people say it to Norm.
- **The utterance gets a 300 ms pre-roll, but only in a session.** The VAD triggers late
  enough at aggressiveness 3 to clip a soft opening ("How many inches…" → "inches…"), so
  `UtteranceRecorder` keeps the audio before the trigger. While asleep `NormAssistant` passes
  `withPreRoll: false`: measured on Piper's voice, any lead-in at all — even 40 ms — turned
  "Hey Norm" into "Pay Norm".
- **Replies are streamed sentence by sentence.** `ConversationEngine` feeds stream fragments
  through `SentenceBuffer` and hands each finished sentence to `NormAssistant`, which speaks
  them from a channel while the model keeps writing. State goes `Thinking` → `Speaking` on the
  first sentence, not when the reply is done. Text a model sends alongside a tool call ("Let
  me check.") is spoken too — it covers the silence while the tool runs.
- **One `piper.exe` lives as long as Norm.** `PiperTextToSpeech` sends it a JSON line per
  sentence and waits for the WAV path to come back on stdout — one reply per request, strictly
  in order, behind a lock. Anything that breaks that lockstep (timeout, wrong path, crash)
  stops the process, and the next sentence starts a new one. A synthesis already in flight is
  not abandoned on cancel for the same reason. Piper's stderr must keep being drained: it logs
  a line per sentence, and an undrained pipe fills and freezes it.
- **`appsettings.json` contains `//` comments.** That's deliberate and legal — the JSON
  configuration provider skips them. Don't strip them, and don't switch to raw
  `JsonSerializer`, which would reject the file.

## Conventions

- Provider-neutral LLM types live in `Llm/ChatModels.cs`. Nothing outside `Llm/` should know
  about Ollama or Claude wire formats; each provider translates in both directions.
- Config, never code, selects a model: a feature asks `ModelRouter` for a *task* name,
  `Tasks` maps it to a provider + model, unknown tasks fall back to `default`.
- API keys come from environment variables named in config (`ApiKeyEnvVar`). Never put a key
  in `appsettings.json`.
- Tool results are text the model reads back — keep them short and speakable; everything here
  ends up spoken aloud.
- The system prompt is tuned for speech: no markdown, no lists, spoken-form numbers. Keep any
  new user-facing strings in that register.
- Comments explain *why*, not *what*; the existing files set the density. Match it.

### Small models misbehave in two specific ways

Both are handled in code; don't "simplify" the handling away.

- **They type a tool's name as the reply text** instead of calling it — as the whole reply
  (llama3.2, on any question no tool fits: "what's the weather?" → the reply is literally
  `end_session`) or tacked onto a good one (qwen3, signing off: "Have a great day.
  End_session."). `ConversationEngine.IsToolName` checks each sentence as it streams in and
  drops any that is only a tool name. If something usable is left it is spoken; if not, the
  model is told once and retried. Without this Norm reads "end session" aloud and the tool
  never runs.

  **The system prompt must never name a tool** — that is what provokes the echo. Measured on
  qwen3 over three sign-offs: naming `end_session` leaked it into 2 replies, describing it as
  "end the session with the tool provided" leaked 0. A test enforces this. Tool names belong
  in `ITool.Description`, which the model receives as structured data, not in prose.
- **They ignore the parameter schema**, sending `"minutes": "10"` where the schema says
  `number`. `TimerTool.Minutes` accepts either. Any new tool reading a numeric argument
  should do the same rather than calling `GetDouble()` on whatever arrives.

### Adding a tool

Implement `ITool` (name, description, JSON Schema `ParametersSchema`, `InvokeAsync`) in
`Tools/`, then register it in `Program.Build`'s `ToolRegistry` chain. Both providers pick it
up from `ToolRegistry.Definitions` automatically. Throwing is safe — `ToolRegistry.InvokeAsync`
converts exceptions into error tool-results the model can recover from.

## Tests

`tests/HeyNorm.Tests` (xUnit). Everything testable without a microphone is covered: both
provider wire formats, the agent loop, the tool registry, the router, and the shipped
`appsettings.json`. No test touches the network, the mic, or a model file.

- **Provider tests drive a `StubHandler`** (`Fakes.cs`) — a fake `HttpMessageHandler` that
  records request bodies and replays canned responses, so assertions cover both what we send
  and how we parse what comes back. Add provider tests there, not against a live endpoint.
- **`ModelRouter` takes an optional `Func<ProviderSettings, IChatProvider>`** purely so tests
  can inject a fake. Production passes nothing and gets the real Ollama/Claude clients.
  `ConversationEngine` depends on the concrete router, so this delegate is the only seam into
  the agent loop — keep it.
- **`WhisperSpeechToText.CleanUp` is `internal`,** exposed via `InternalsVisibleTo` in the app
  csproj. Prefer that over making implementation details public.
- **The test project references `WebRtcVadSharp` directly.** It must: the native
  `WebRtcVad.dll` is copied by a build target that only runs for direct package references,
  and `UtteranceRecorderTests` exercises the real VAD. A transitive reference silently gives
  you the managed wrapper and a `DllNotFoundException` at run time.
- **VAD tests use digital silence only.** Synthesising audio the WebRTC VAD reliably calls
  *speech* isn't practical, so the speech-detected branches of `UtteranceRecorder` — and all
  of Whisper and TTS — are not covered. Those need a person and a mic.

### Adding a provider

Implement `IChatProvider` — both `CompleteAsync` and `StreamAsync`; the conversation only
uses the streaming one — add a case to `ModelRouter.GetProvider`'s `switch` on
`ProviderSettings.Type`, and add the entry under `Providers` in `appsettings.json`.
