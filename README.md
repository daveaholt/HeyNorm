# Hey Norm

A voice assistant for the shop that lives in the Windows system tray. Say **"Hey Norm"**, talk, and it talks back. Everything runs locally on Ollama to start; switching to Claude is a config change.

```
Sleeping ──"Hey Norm"──▶ Listening ──pause──▶ Thinking ──▶ Speaking ──┐
    ▲                        ▲                (STT, model, tools)     │
    │                        └────────────────────────────────────────┘
    └── "Norm, done"  ·  model calls end_session  ·  60 s of silence
```

## Setup

You'll need Windows 10/11 x64, the .NET 8 SDK (or newer — the project targets `net8.0-windows`), and an NVIDIA GPU with 8 GB+ VRAM.

1. **Ollama.** Install from ollama.com, then `ollama pull qwen3:8b`. Any model that supports tool calling works; change `Tasks` in `appsettings.json`.
2. **Wake words.** Nothing to install. Norm transcribes whatever it hears and matches the text against the phrases in `appsettings.json` — edit `Wake:WakePhrases` and `Wake:StopPhrases` to taste. Punctuation and casing are ignored, so "Hey, Norm." matches `hey norm`.
3. **Speech-to-text.** Download `ggml-base.en.bin` from huggingface.co/ggerganov/whisper.cpp into `src/HeyNorm/models/`. Use `small.en` if base mishears you over shop noise. Whisper runs on the GPU when the **CUDA 12 runtime** is installed and falls back to the CPU build otherwise — both runtimes are referenced, so it works either way.
4. **Text-to-speech (recommended).** Download `piper_windows_amd64.zip` from github.com/rhasspy/piper/releases and a voice from huggingface.co/rhasspy/piper-voices (the default is `en/en_US/ryan/medium` — both `en_US-ryan-medium.onnx` and its `.onnx.json`). Put `piper.exe`, its DLLs and `espeak-ng-data/`, and the voice files directly in `src/HeyNorm/piper/` — the zip nests everything in a `piper/` folder of its own, so don't end up with `piper/piper/`. Without Piper, Norm falls back to the built-in Windows voice, which sounds robotic; the log's `Text-to-speech:` line at startup shows which one loaded.
5. **Web search (optional, free).** Norm searches through [SearXNG](https://docs.searxng.org), a free search engine you run yourself that asks Google, Brave, Bing and others and merges what they return — no account, no key, no subscription. With Docker Desktop running, from the repo root in PowerShell:

   ```powershell
   docker run -d --name searxng --restart unless-stopped -p 127.0.0.1:8888:8080 `
     -v "${PWD}\searxng:/etc/searxng" -e SEARXNG_SECRET=$(New-Guid) `
     searxng/searxng:2026.10.19-81371861f
   ```

   The repo's `searxng/settings.yml` switches on the JSON output Norm reads (SearXNG ships with it off, and every search then fails with a 403). Check it at http://localhost:8888. It listens on this PC only. To have it survive a reboot, turn on "Start Docker Desktop when you sign in" in Docker's settings; the container restarts with Docker. If SearXNG isn't running, Norm says search isn't available and carries on. Blank out `Search:SearXngUrl` in `appsettings.json` to switch search off entirely; the log's `Web search:` line at startup shows which.
6. **Weather.** Nothing to install — it uses [Open-Meteo](https://open-meteo.com), which is free for non-commercial use and needs no key. Set `Weather:HomeLocation` in `appsettings.json` to your town (e.g. `"Austin, Texas"`) so "what's the weather?" and "is it too humid to spray?" work without naming a place; until then Norm asks which town. `Weather:Units` is `imperial` or `metric`. Readings are outdoor ones — there's no shop sensor.
7. **Spotify (optional, needs Premium).** Norm plays and controls the Spotify desktop app on the same computer. Install Spotify there, sign in, and **play any song once** — a freshly installed app doesn't appear in Spotify's device list until it has played something, and until then Norm can't find it. Then:
   1. At [developer.spotify.com/dashboard](https://developer.spotify.com/dashboard), signed in with your Premium account, create an app: any name, redirect URI exactly `http://127.0.0.1:8889/callback` (Spotify rejects `localhost`), and tick **Web API**. Starting a song or artist by name has to go through Spotify's Web API — the desktop app has no local way to be told what to play.
   2. Copy its Client ID and run `setx SPOTIFY_CLIENT_ID "<the id>"` in PowerShell. It goes in an environment variable rather than `appsettings.json` because it identifies your account. Restart Norm afterwards so it picks the variable up.
   3. From the tray menu choose **Connect Spotify…** and approve in the browser.

   Then try "play some Allman Brothers", "put on my shop playlist", "skip", "turn it up", or "what's playing?". If Spotify isn't running, Norm opens it. Spotify's own playlists ("Today's Top Hits", "Discover Weekly") can't be found by apps like this one; ask for one and Norm says it couldn't find it exactly and what it put on instead. Your own and followed playlists work. Spotify ends the sign-in six months after you connect, so about twice a year Norm will say it needs reconnecting — choose **Connect Spotify…** again. The log's `Spotify:` line at startup shows the connection and when it runs out.

   Separately, and with no setup at all: while Norm is awake it turns Spotify down to 20% in the Windows volume mixer so it can hear you, and back up when the session ends. That works even if you skip the steps above and play Spotify by hand. `Spotify:DuckToPercent` sets the level; 100 switches it off.
8. **Your documents (optional).** Norm answers questions from PDFs in `Documents:Folder` — by default `%OneDrive%\Documents\Hey Norm` (environment variables work, so the same setting suits any machine; "Open documents folder" in the tray menu creates and opens it). Subfolders name projects: put a boat's plans in `Sailboats\Skiff 12\` and they're the "Skiff 12" project, kept apart from any other boat's. Then ask "how thick is the bottom plywood?" or "how long is the mast on the gaff rig?" and Norm answers from the plans, says which item and page, and says "the plans don't say" rather than guess.

   **More than one boat?** Name it ("how long is the mast on the Skiff?"), or tell Norm once which one you're working on ("I'm working on the Skiff now") and it uses that boat until you say otherwise, even after a restart. If you ask without naming one and both boats' plans match, Norm asks which boat you mean — it never blends two boats' figures into one answer. "I'm not working on any project" clears it.

   Norm reads the folder itself, in the background, at startup and whenever a PDF is added or changed — each file once; the index lives in `%LOCALAPPDATA%\HeyNorm`. It reads **text**: a specification or building schedule exported from Word works well.

   **Scanned drawings** have no text, so Norm reads them with a vision model — `ollama pull qwen3-vl:8b-instruct` (6 GB; the plain `8b` tag doesn't work for this). It does that by itself, only while it's idle: it waits for a quiet minute, stops the moment you say "Hey Norm", and picks up where it left off, even after a restart. An A1 sheet takes about four minutes. Every sheet is read **twice**, cut up differently each time, and only figures both reads agree on are kept — the misreads seen in testing all appeared in just one read. Even so, readings can be wrong, and the model can't reliably tell which figure measures what, so Norm reports drawing figures by where they are on the sheet — "machine-read from drawing 05, bottom right: 650, 1220… check the drawing before cutting" — always with that warning and that location. The log shows each sheet as it's read. On a OneDrive folder, choose "Always keep on this device" so Norm can read the files without the internet.
9. `dotnet run --project src/HeyNorm`, or publish and add a shortcut to `shell:startup` so it launches at login.

The log is in `%LOCALAPPDATA%\HeyNorm\log.txt` (also under "Open log" in the tray menu). After each reply it records a `Timing` line, counted from when you stopped talking, that shows where the wait went: transcription, the model's first sentence, and the first sound.

## Memory

Tell Norm to remember things and it keeps them for every future conversation: "remember I work in fractions of an inch, not decimals", "my table saw is a SawStop, keep that in mind", or a correction — "that's wrong, Titebond III's open time is about ten minutes, remember that". A correction replaces what it corrects. "What do you remember about me?" lists it all; "forget the SawStop, I sold it" removes one item (if two match, Norm asks which).

It's a plain text file, one item per line, in `%LOCALAPPDATA%\HeyNorm\memory.txt` — **Open memory** in the tray menu opens it. Edit or prune it freely; Norm picks up changes on the next question. There's no "forget everything" by voice on purpose: a misheard request shouldn't wipe the list, so clear it in the file.

## Switching to Claude

Set the `ANTHROPIC_API_KEY` environment variable, then change a task:

```json
"conversation": { "Provider": "claude", "Model": "claude-sonnet-5-5", "MaxTokens": 512 }
```

Each task can point at a different provider and model, so quick jobs can stay local while harder ones go to Claude.

## Project layout

| Folder | What's there |
|---|---|
| `src/HeyNorm/Assistant/` | `NormAssistant` (the state machine), `ConversationEngine` (the agent loop), and `Memory` |
| `src/HeyNorm/Audio/` | Mic capture (NAudio), wake/stop phrase matching on transcripts, utterance detection (WebRTC VAD) |
| `src/HeyNorm/Speech/` | Whisper.net STT, Piper and SAPI TTS, chimes |
| `src/HeyNorm/Knowledge/` | The documents folder: PDF text, passages, keyword search, background indexing |
| `src/HeyNorm/Music/` | Spotify sign-in (PKCE), Web API client, and turning Spotify down in the Windows mixer while Norm listens |
| `src/HeyNorm/Llm/` | `IChatProvider` strategy, Ollama and Claude implementations, `ModelRouter` |
| `src/HeyNorm/Tools/` | `ITool`, registry, and tools: time, timer, end_session, web_search, get_weather, control_music, search_documents, set_current_project, remember, forget |
| `searxng/` | Settings for the local SearXNG container that web search talks to |
| `tests/HeyNorm.Tests/` | xUnit tests for the providers, agent loop, tools, router, and config |

## Tests

```
dotnet test
```

About 400 tests covering the parts that don't need a microphone: both provider wire formats (request
shaping and response parsing, against a stub HTTP handler), the agent loop and its tool rounds,
the tool registry's error handling, task→provider routing, and the shipped `appsettings.json`.
The audio path is only covered where it's deterministic — VAD silence and idle-timeout
accounting. Whisper and TTS still need a person and a mic.

## Adding a tool

Implement `ITool` (name, description, JSON Schema, `InvokeAsync`) and register it in `Program.Build`. Both providers pick it up automatically. Good next ones: Spotify or Windows media controls, Home Assistant.

## Known gaps in this skeleton

- **No synthesis lookahead.** Replies stream from the model and one long-running `piper.exe` voices each sentence as it arrives, but the next sentence isn't synthesized until the current one finishes playing. That adds 0.05–0.2 s to each pause between sentences, on top of the gap Piper leaves after every sentence by design.
- **Half-duplex.** While Norm talks, only "Norm, done" is heard, so you can't interrupt with a new question. Real barge-in needs echo cancellation.
- **One mic, default device.** Pass a device number to `MicrophoneSource` to choose another.
- **Whisper hears words in noise.** Norm drops low-confidence segments and Whisper's stock phrases ("thanks for watching"), and logs each drop. If it still answers things nobody said, lower `Speech:NoSpeechThreshold`; if it ignores quiet requests, raise it.
- **GPU transcription needs the CUDA 12 runtime installed.** `Whisper.net.Runtime.Cuda` ships `ggml-cuda-whisper.dll` but not CUDA itself. Without it the CUDA backend fails to load and Whisper falls back to the CPU runtime — which is why both packages are referenced. Referencing only the CUDA one throws `Native Library not found in default paths` at startup on a machine without CUDA, even though a `ggml-cpu-whisper.dll` sits in the CUDA folder: Whisper.net looks for the CPU build under `runtimes/win-x64/`, which the CUDA package doesn't populate.
