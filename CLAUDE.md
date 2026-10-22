# CLAUDE.md

Hey Norm — a Windows system-tray voice assistant. Wake word, local speech-to-text, an LLM
agent loop with tools, text-to-speech. Read `README.md` for end-user setup; this file is for
working on the code.

## Commands

```powershell
dotnet build HeyNorm.sln              # Debug
dotnet build HeyNorm.sln -c Release
dotnet test                           # ~400 tests, ~1 s, no network or mic needed
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
`Text-to-speech:` line at startup says which one loaded. Web search needs the SearXNG
container from README step 5; without it the tool answers "isn't available" and nothing else
breaks. The startup `Web search:` line shows the URL, or `off`.

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
- **Waking up preloads the model.** Ollama unloads a model after five idle minutes and the
  reload (about 4 s for qwen3:8b here) used to land on the first question. `BeginSessionAsync`
  fires `ConversationEngine.WarmUpAsync` so the load overlaps the chime and the person
  talking. Fire-and-forget on purpose: a failed preload is logged, and the first question
  just pays for the load as before. There is deliberately no `keep_alive`; Norm doesn't hold
  the VRAM while asleep.
- **The log's `Timing` line is counted from when the person stopped talking** — the VAD's
  end-of-speech pause is added back in — through transcription, first sentence from the
  model, and first sound out of the speaker. Check it before trusting "it feels slower".
- **Emoji and markdown are stripped before speaking** (`Speakable.Clean`, applied per sentence
  in `ConversationEngine`, so the log matches what was said). Piper reads them aloud —
  measured: an emoji added over two seconds, `**bold**` two seconds of asterisks — and the
  prompt's "no emoji" isn't always obeyed (6 of 15 sign-offs once had them). Emoji are matched
  by code-point range, not Unicode category: that category also holds °, which Piper reads as
  "degrees".
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

### Small models misbehave in three specific ways

All are handled in code or config; don't "simplify" the handling away.

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
- **They take examples in tool descriptions as facts.** The remember tool's description once
  gave "They work in fractions of an inch, not decimals." as an example of how to phrase an
  item; asked "what do you remember about me?" in a fresh session with nothing about fractions
  saved, qwen3 answered with it, 2 times in 3. A test now bans "e.g." and "for example" from
  the memory tools' descriptions. Describe the shape you want, never a sample of content.
- **They ignore the parameter schema**, sending `"minutes": "10"` where the schema says
  `number`. `TimerTool.Minutes` accepts either. Any new tool reading a numeric argument
  should do the same rather than calling `GetDouble()` on whatever arrives.
- **They announce a lookup and then don't make it.** qwen3 with thinking off, asked "what's
  the humidity today? I'm about to spray lacquer", replied "I'll check the humidity for you.
  Let me get that info." and stopped — 7 of 7 tries, any wording. Streaming has already
  spoken that by the time the reply ends. The fix is one system-prompt sentence ("If a tool
  can answer, call it straight away; never say you will check something without calling the
  tool in the same reply"): 7 of 7 after, with timers, search, the time and plain answers
  unchanged, and no tool-name echo in 30 sign-offs. It still happened 2 times in 5 on "how far
  apart are the frames?", so `ConversationEngine` also **asks again, silently, once**: a reply
  that is only a promise to look (`IsPromiseToLook`, deliberately narrow) and calls no tool is
  dropped from the history and the request re-sent unchanged; the next draw usually calls the
  tool (8 of 8 answered). **Telling the model it had stalled is worse** — "you said you'd check
  but didn't" got "I'm sorry for the delay", another stall, or an invented figure — so nothing
  is added. Thinking on also fixes it, at ~500 tokens of reasoning per question.

### Adding a tool

Implement `ITool` (name, description, JSON Schema `ParametersSchema`, `InvokeAsync`) in
`Tools/`, then register it in `Program.Build`'s `ToolRegistry` chain. Both providers pick it
up from `ToolRegistry.Definitions` automatically. Throwing is safe — `ToolRegistry.InvokeAsync`
converts exceptions into error tool-results the model can recover from. For failures you can
foresee (a service down, a bad status), return a plain sentence instead, the way
`WebSearchTool` does: the model reads tool results back almost verbatim, and "Tool failed:
No connection could be made…" is not something to say out loud. **Make a failure read as a
failure**, though: measured, a bare "Nothing is playing on Spotify right now." in answer to
"set the volume to sixty" got "Okay, I'll set the volume to sixty for you." `MusicTool`
prefixes failures with "That didn't work:" for that reason.

A tool that takes more than a moment should set `ITool.Preamble` ("Let me look that up."):
`ConversationEngine` speaks it the instant the model asks for the tool, so it plays while the
tool runs. It is skipped if the model already said something that round, said at most once
per turn, and kept out of the history (the model didn't say it). `Preamble` is a default
interface member returning null, so **a class that wraps another tool must forward it
explicitly** — forgetting compiles fine and just silently drops the preamble.

`MusicTool` / `Music/` notes — Spotify's developer rules shape most of this, and they were
tightened in 2025–26, so check the current docs before "fixing" anything here:

- **Sign-in is PKCE with a loopback redirect, `http://127.0.0.1:{port}/callback`.** Spotify
  rejects `localhost`, and the redirect must match the dashboard entry exactly. The listener
  is a raw `TcpListener` rather than `HttpListener`, which would need a URL ACL (admin).
- **Refresh tokens rotate and expire.** Each refresh may return a new refresh token that
  replaces the old one — save it or the next refresh fails. Separately, Spotify ends every
  sign-in six months after authorization no matter how often it's refreshed
  (`invalid_grant`); the store keeps the authorization time so the startup log can warn.
  The store is DPAPI-encrypted for the Windows account.
- **Development-mode apps get a restricted API** (February 2026): search `limit` max 10,
  `/playlists/{id}/tracks` replaced by `/items` and only for the user's own playlists, browse
  endpoints gone, Premium required for the app owner. Playing a context URI doesn't need its
  items, which is why playlists play by URI without being read.
- **Spotify plays on Norm's own machine, but play-by-name still goes through the Web API.**
  The desktop app has no local "play this" interface; the API reaches it as a Spotify
  Connect device. `TargetDeviceAsync` picks the computer device named after
  `Environment.MachineName` (falling back to an active or lone computer — phones and speakers
  on the account are skipped), and opens the app via the `spotify:` protocol if it isn't
  listed, polling up to 15 s for it to sign in.
- **Ducking is in the Windows mixer, not Spotify's volume** (`SpotifyDucker` +
  `SpotifyMixer`, NAudio Core Audio, every Spotify session on every active output). It needs
  no sign-in and is on even when music control is off. Because it scales on top of Spotify's
  own volume, "turn it up" mid-session changes Spotify's volume and the restore doesn't undo
  it. Ducks on `Starting`, restores on `Sleeping` (a no-op unless it ducked — that state fires
  after every rejected screening), through a strictly ordered queue off the audio thread.
- **Windows remembers per-app mixer levels across restarts.** A crash while ducked would
  leave Spotify at 20% for good, so the normal level goes to `spotify-ducked.txt` before
  ducking; startup (or, if Spotify isn't running then, the next duck) puts it back.
- **Every music command that works ends the session**, so the music comes back up as soon as
  Norm has answered instead of sitting ducked until the idle timeout. Measured on the first
  voice test: "what's playing?" and "stop the music" each left the session open, and the music
  stayed at 20% for ninety seconds after the answers. Failures ("couldn't find that") leave
  it open so the person can try again. Other questions can't end it that way (qwen3 rarely
  ends a session itself), so once Norm has answered while music is ducked *and playing*,
  the idle timeout drops to `Speech:FollowUpWithMusicSeconds` (10 s). "Playing" is the
  mixer session's Active state, which goes Inactive within a second of a pause; a paused
  Spotify keeps the full minute. The first question after the chime always gets the full time. Its preamble ("On it.") covers the second or two
  Spotify adds before the answer.
- Measured on qwen3: ten phrasings ("play some Allman Brothers", "put on my shop playlist",
  "set the volume to forty", "stop the music"…) all chose the right action and kind.

`Memory` notes (`Assistant/Memory.cs`, `RememberTool`, `ForgetTool`):

- **Everything remembered is appended to the system prompt on every request**, via the
  `ConversationEngine` `promptAddition` delegate, read each round — so an item saved mid-turn
  applies to the model's next request. The file lives in `%LOCALAPPDATA%`, never the repo.
- **The prompt says remembered items beat lookups.** Measured: without that, asked a question
  a saved correction answered, qwen3 searched the web instead — "look it up instead of
  guessing" won.
- **Saving replaces a close match** (word overlap, numbers ignored), so a correction overwrites
  the figure it corrects. "They remember that…" prefixes, which qwen3 writes, are stripped.
- **Forgetting matches on content words only**: the model passes the whole request ("Forget
  what I said about fractions."), whose own words once drowned the match. A tie removes
  nothing and asks. There is deliberately no forget-everything tool.
- qwen3 sometimes re-saves an existing item on an unrelated question. It's a no-op (same
  item), and it can't resurrect a forgotten one, which is no longer in the prompt.

`DocumentSearchTool` / `Knowledge/` notes — built against a real boat-plan specification
(Word export, numbered Key to Plans, British spelling) whose drawings are 300 dpi scans:

- **Indexing is the app's job, at runtime.** `DocumentLibrary` reads the folder in the
  background at startup and on `FileSystemWatcher` changes (debounced — copies and OneDrive
  syncs fire bursts), caching each file's passages against size and timestamp in
  `documents-index.json`. Bump `CacheVersion` whenever the splitter or tokenizer changes what
  a file would produce. A file that fails to read isn't cached, so it's retried next time.
- **Passages follow the plans' own structure** (`PassageSplitter`): each numbered item and
  lettered step is one passage, paragraphs otherwise. Every passage carries its heading —
  item numbers repeat across sections (item 53 is one mast in the Key to Plans, another in the
  rig notes). Items must count up within a section, or a drawing reference that wraps
  ("…as shown on drg.⏎03. …") becomes a bogus item 03. A paragraph ends at a stop plus trailing whitespace, and the stop
  may sit inside a bracket: missing ").␠␠" buried the paragraph that named the glue.
- **Search is BM25 keywords** (`KeywordIndex`), with British/American spelling folded
  (centreboard/centerboard), "plywood"→"ply", "long"→"length", centreplate→centerboard, and
  sizes like `18x50` kept whole. Each of those was a measured miss. `MaxResults` is 5 because
  the right item for "how long is the mast" ranked fourth.
- **Image-only pages are recorded, not dropped** (`CachedFile.ImagePages`), so the tool can
  say which drawing to look at, and `DrawingWorker` knows which pages to read.
- **Scanned drawings are read by a vision model, twice** (`DrawingSheet`, `DrawingConsensus`,
  `DrawingWorker`; the `drawings` task names the model). Measured on a 300 dpi A1 sheet:
  - Read whole, the lettering is too small; a 4×3 grid of tiles at ≤1600 px on the **longest**
    side reads well (sizing by width let a tall edge tile overflow the context: Ollama 400).
  - Pass B is the grid shifted half a tile. A figure is kept only if both passes read it in
    overlapping crops — every misread on the test sheet was in one pass only. Small sheets
    (spec sketches) get two whole reads at different sizes, since identical requests just
    agree with themselves.
  - **What a figure measures is never stored.** Both passes once agreed on a wrong meaning for
    a figure; spoken, it would be believed. Passages hold figures by sheet position, with the
    labels and notes found there, and are `MachineRead`. The tool marks them, and its
    instructions require saying "machine-read", the drawing, the spot on the sheet, and "check
    before cutting" — qwen3 complied in every live run.
  - `qwen3-vl:8b` reasons out loud and never answers; use `8b-instruct`. Without a repeat
    penalty it looped inventing a counting sequence; `Temperature` 0.1, `RepeatPenalty`,
    `ContextLength` 8192 and a JSON `ResponseSchema` (Ollama `format`) are all in the task.
  - It only runs while Norm is idle (a quiet minute; `Screening` counts as idle or a noisy
    shop would never get read) and a session start cancels the request in flight. Every tile's
    reading is saved as it lands, keyed by file size and timestamp, so it resumes rather than
    restarts. Ollama occasionally 400s a request that succeeds on retry: one retry, then the
    tile is recorded empty rather than retried forever.
- **Lessons from the first voice test of the plans**, each with a test in
  `VoiceFindingsTests`:
  - Given a passage about *part* of a question, qwen3 answers with it: "spacing between
    stations is …mm" came back for "how far apart are the frames?" as the frame spacing, 5
    times in 5. Ranking it lower wasn't enough. `KeywordIndex.Search` now returns only the
    passages matching the most distinct query terms, and "apart", "distance(s)" and "spacing"
    are one term. "Centres" is deliberately not: mapped, nail spacing at "100mm centres" was
    given as the frames' spacing.
  - Typed passages fill the answer first, readings only the slots left, and the instructions
    say typed text wins: qwen3 once passed over the spec's typed mast length for a mizzen reading.
  - A typed passage that says "see Drg. 02" (or "Drg. No. XYZ/91/04") pulls in that sheet's
    readings (`DocumentLibrary.ReadingsReferencedBy`): the frame readings hold no word for
    spacing, so only the reference connects them to the question.
  - The "meaning unknown" caveat sits beside each reading's figures, and the instructions
    never say "the person": "tell the person to check the drawing" came back aloud as "The
    person should check the drawing".
- **The tool tells the model to answer only from the passages, cite them, and say "the plans
  don't say" otherwise.** Measured: before the glue paragraph was retrievable, qwen3 filled the
  gap with "a good wood glue".
- **Two boats' plans are never blended.** `DocumentSearchTool` scopes a search to a project
  named in the question, else `CurrentProject` (set by `ProjectTool`, kept in
  `current-project.txt`), else all — and if the hits then span projects it returns no
  passages, only "ask which one", so there's nothing to blend. Names match loosely
  (`DocumentLibrary.MatchProject`: containment, then a near-miss of about one letter in four)
  because Whisper misspells unusual names; the model also sends junk as the project ("gaff
  yawl rig", "unknown", "none"), which must match nothing, not the nearest boat. Measured on
  qwen3 with two specs that both give a mast length: it asked which boat, then answered from
  the named one, and kept to the current project once set.
- `SentenceSplitter` skips abbreviations (Drg., No., approx., initials) — plans are full of
  them, and each false sentence break is a separate Piper call with a pause.

`WeatherTool` notes: **its description must never mention the shop** (a test enforces it) —
"leave it out for the weather here at the shop" made qwen3 send `"location": "shop"`, and
the geocoder found Shop, Malaysia. Words models send for "here" anyway ("local", "shop
area", "current location") map to `Weather:HomeLocation`, and time words are stripped first
— qwen3 sent `"location": "tomorrow"` for "is it going to rain tomorrow?". The geocoder matches town names
only, so "Austin TX" finds nothing; the tool retries with the last word as a state or
country qualifier. Lookups are cached for the app's lifetime.

`WebSearchTool` notes: SearXNG returns 403 when its `json` format is off, which is its
default — `searxng/settings.yml` turns it on and a config test checks that. DuckDuckGo
answering it with `CAPTCHA` in `unresponsive_engines` is normal; the other engines cover it.
Snippets are capped at 300 characters and URLs reduced to the site name, both to keep tokens
down and to give the model something it can attribute aloud.

## Tests

`tests/HeyNorm.Tests` (xUnit). Everything testable without a microphone is covered: both
provider wire formats, the agent loop, the tool registry, the router, and the shipped
`appsettings.json`. No test touches the network, the mic, or a model file — the one socket
is `Signing_in_runs_the_whole_browser_round_trip`, which plays the browser against the real
Spotify sign-in listener over loopback on a free port.

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
