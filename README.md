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
6. `dotnet run --project src/HeyNorm`, or publish and add a shortcut to `shell:startup` so it launches at login.

The log is in `%LOCALAPPDATA%\HeyNorm\log.txt` (also under "Open log" in the tray menu). After each reply it records a `Timing` line, counted from when you stopped talking, that shows where the wait went: transcription, the model's first sentence, and the first sound.

## Switching to Claude

Set the `ANTHROPIC_API_KEY` environment variable, then change a task:

```json
"conversation": { "Provider": "claude", "Model": "claude-sonnet-5-5", "MaxTokens": 512 }
```

Each task can point at a different provider and model, so quick jobs can stay local while harder ones go to Claude.

## Project layout

| Folder | What's there |
|---|---|
| `src/HeyNorm/Assistant/` | `NormAssistant` (the state machine) and `ConversationEngine` (the agent loop) |
| `src/HeyNorm/Audio/` | Mic capture (NAudio), wake/stop phrase matching on transcripts, utterance detection (WebRTC VAD) |
| `src/HeyNorm/Speech/` | Whisper.net STT, Piper and SAPI TTS, chimes |
| `src/HeyNorm/Llm/` | `IChatProvider` strategy, Ollama and Claude implementations, `ModelRouter` |
| `src/HeyNorm/Tools/` | `ITool`, registry, and tools: time, timer, end_session, web_search |
| `searxng/` | Settings for the local SearXNG container that web search talks to |
| `tests/HeyNorm.Tests/` | xUnit tests for the providers, agent loop, tools, router, and config |

## Tests

```
dotnet test
```

About 165 tests covering the parts that don't need a microphone: both provider wire formats (request
shaping and response parsing, against a stub HTTP handler), the agent loop and its tool rounds,
the tool registry's error handling, task→provider routing, and the shipped `appsettings.json`.
The audio path is only covered where it's deterministic — VAD silence and idle-timeout
accounting. Whisper and TTS still need a person and a mic.

## Adding a tool

Implement `ITool` (name, description, JSON Schema, `InvokeAsync`) and register it in `Program.Build`. Both providers pick it up automatically. Good next ones: weather (Open-Meteo is free and keyless — web search can't answer it, since result snippets don't carry forecasts), Spotify or Windows media controls, Home Assistant.

## Known gaps in this skeleton

- **No synthesis lookahead.** Replies stream from the model and one long-running `piper.exe` voices each sentence as it arrives, but the next sentence isn't synthesized until the current one finishes playing. That adds 0.05–0.2 s to each pause between sentences, on top of the gap Piper leaves after every sentence by design.
- **Half-duplex.** While Norm talks, only "Norm, done" is heard, so you can't interrupt with a new question. Real barge-in needs echo cancellation.
- **One mic, default device.** Pass a device number to `MicrophoneSource` to choose another.
- **Whisper hears words in noise.** Norm drops low-confidence segments and Whisper's stock phrases ("thanks for watching"), and logs each drop. If it still answers things nobody said, lower `Speech:NoSpeechThreshold`; if it ignores quiet requests, raise it.
- **GPU transcription needs the CUDA 12 runtime installed.** `Whisper.net.Runtime.Cuda` ships `ggml-cuda-whisper.dll` but not CUDA itself. Without it the CUDA backend fails to load and Whisper falls back to the CPU runtime — which is why both packages are referenced. Referencing only the CUDA one throws `Native Library not found in default paths` at startup on a machine without CUDA, even though a `ggml-cpu-whisper.dll` sits in the CUDA folder: Whisper.net looks for the CPU build under `runtimes/win-x64/`, which the CUDA package doesn't populate.
