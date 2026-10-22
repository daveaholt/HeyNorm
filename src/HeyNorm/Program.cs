using HeyNorm.Assistant;
using HeyNorm.Audio;
using HeyNorm.Config;
using HeyNorm.Knowledge;
using HeyNorm.Llm;
using HeyNorm.Music;
using HeyNorm.Speech;
using HeyNorm.Tools;
using Microsoft.Extensions.Configuration;

namespace HeyNorm;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();

        // One instance only — two copies would fight over the mic.
        using var mutex = new Mutex(true, "HeyNorm.SingleInstance", out var isFirst);
        if (!isFirst) return;

        var log = new FileLog();
        try
        {
            var settings = LoadSettings();
            var assistant = Build(settings, log, out var spotify, out var documents, out var memory);
            Application.Run(new TrayApplicationContext(assistant, log, spotify, documents, memory));
        }
        catch (Exception ex)
        {
            log.Write($"Startup failed: {ex}");
            MessageBox.Show(ex.Message, "Hey Norm couldn't start", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static AppSettings LoadSettings()
    {
        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .Build();

        return config.Get<AppSettings>() ?? new AppSettings();
    }

    private static NormAssistant Build(AppSettings settings, FileLog log, out SpotifyFeature spotify,
        out DocumentLibrary? documents, out Memory memory)
    {
        var signals = new SessionSignals();
        NormAssistant? assistant = null;

        var tools = new ToolRegistry()
            .Add(new CurrentTimeTool())
            .Add(new EndSessionTool(signals))
            .Add(new TimerTool(text => _ = assistant?.AnnounceAsync(text)))
            .Add(new WeatherTool(new HttpClient(), settings.Weather));
        if (!string.IsNullOrWhiteSpace(settings.Search.SearXngUrl))
            tools.Add(new WebSearchTool(new HttpClient(), settings.Search.SearXngUrl, settings.Search.MaxResults));
        log.Write($"Web search: {(tools.Knows("web_search") ? settings.Search.SearXngUrl : "off")}");
        log.Write($"Weather home: {(settings.Weather.HomeLocation is { Length: > 0 } home ? home : "not set")}");
        spotify = BuildSpotify(settings.Spotify, tools, signals, log);
        var router = new ModelRouter(settings);
        documents = BuildDocuments(settings, router, tools, log, out var drawings);
        // Add more here: Home Assistant...

        // Kept out of the repo, under the user's profile: it's personal by nature.
        memory = new Memory(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HeyNorm", "memory.txt"));
        tools.Add(new RememberTool(memory)).Add(new ForgetTool(memory));
        log.Write($"Memory: {memory.Items.Count} things remembered");
        var engine = new ConversationEngine(router, tools, settings.Assistant, signals, memory.PromptSection);

        var phrases = new WakePhraseMatcher(settings.Wake);
        var mic = new MicrophoneSource();
        var recorder = new UtteranceRecorder(settings.Speech);
        var stt = new WhisperSpeechToText(settings.Speech.WhisperModelPath, settings.Speech.NoSpeechThreshold, log.Write);

        ITextToSpeech tts = PiperTextToSpeech.IsConfigured(settings.Speech.PiperExePath, settings.Speech.PiperVoicePath)
            ? new PiperTextToSpeech(settings.Speech.PiperExePath, settings.Speech.PiperVoicePath)
            : new SapiTextToSpeech();
        log.Write($"Text-to-speech: {tts.GetType().Name}");

        assistant = new NormAssistant(settings, phrases, mic, recorder, stt, tts, engine);
        if (drawings is not null)
        {
            // Reading drawings shares the GPU with Whisper and the conversation, so it stops the
            // moment a session starts and waits for a quiet minute after it ends. Screening (a
            // noise heard while asleep) doesn't count — in a shop that would mean never reading.
            assistant.StateChanged += s => drawings.SetBusy(s is not (NormState.Sleeping or NormState.Screening));
        }
        if (spotify.Ducker is { } ducker)
        {
            // StateChanged can fire on the audio thread, which mustn't wait on the mixer, so
            // the work is queued — strictly in order, so a quick wake-then-sleep can't restore
            // before it ducked. Both log their own failures.
            var queue = Task.CompletedTask;
            var gate = new object();
            void Enqueue(Action work)
            {
                lock (gate) queue = queue.ContinueWith(_ => work(), TaskScheduler.Default);
            }

            assistant.StateChanged += s =>
            {
                if (s == NormState.Starting) Enqueue(ducker.Duck);
                else if (s == NormState.Sleeping) Enqueue(ducker.Restore);
            };
        }
        return assistant;
    }

    /// <summary>Indexing runs in the background; Norm is usable before it finishes.</summary>
    private static DocumentLibrary? BuildDocuments(AppSettings settings, ModelRouter router, ToolRegistry tools,
        FileLog log, out DrawingWorker? drawings)
    {
        var cfg = settings.Documents;
        drawings = null;
        if (string.IsNullOrWhiteSpace(cfg.Folder))
        {
            log.Write("Documents: off");
            return null;
        }

        var appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HeyNorm");
        var library = new DocumentLibrary(cfg.ExpandedFolder, Path.Combine(appData, "documents-index.json"), log.Write);
        var current = new CurrentProject(Path.Combine(appData, "current-project.txt"));
        tools.Add(new DocumentSearchTool(library, cfg.MaxResults, current));
        tools.Add(new ProjectTool(library, current));
        if (current.Name is { } name) log.Write($"Documents: current project is {name}");

        if (!string.IsNullOrWhiteSpace(cfg.DrawingTask) && settings.Tasks.ContainsKey(cfg.DrawingTask))
        {
            drawings = new DrawingWorker(library, router, cfg.DrawingTask, Path.Combine(appData, "drawing-readings.json"), log.Write);
            library.Readings = drawings; // the library disposes it
            drawings.Start();
            log.Write($"Drawings: read while idle with {settings.Tasks[cfg.DrawingTask].Model}");
        }
        else
        {
            log.Write("Drawings: off (no drawing task configured)");
        }

        library.Start();
        return library;
    }

    private static SpotifyFeature BuildSpotify(SpotifySettings cfg, ToolRegistry tools, SessionSignals signals, FileLog log)
    {
        var appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HeyNorm");

        // Turning Spotify down is local and needs no sign-in, so it works even when music
        // control is off and Spotify is played by hand.
        SpotifyDucker? ducker = null;
        if (cfg.DuckToPercent < 100)
        {
            ducker = new SpotifyDucker(SpotifyMixer.Find, cfg.DuckToPercent, Path.Combine(appData, "spotify-ducked.txt"), log.Write);
            ducker.RecoverFromCrash();
        }

        var clientId = Environment.GetEnvironmentVariable(cfg.ClientIdEnvVar);
        if (string.IsNullOrWhiteSpace(clientId))
        {
            log.Write($"Spotify: music control off (set {cfg.ClientIdEnvVar} to turn it on)");
            return new SpotifyFeature(null, ducker);
        }

        var auth = new SpotifyAuth(new HttpClient(), clientId.Trim(), cfg.RedirectPort, Path.Combine(appData, "spotify-token.bin"));
        tools.Add(new MusicTool(new SpotifyClient(new HttpClient(), auth), signals));

        log.Write(auth.ExpiresAround is { } expires
            ? $"Spotify: connected; the sign-in lasts until about {expires:yyyy-MM-dd}" +
              (expires - DateTimeOffset.Now < TimeSpan.FromDays(14) ? " — reconnect soon from the tray menu" : "")
            : "Spotify: not connected yet (tray menu → Connect Spotify)");
        return new SpotifyFeature(auth, ducker);
    }
}

/// <summary>
/// What the tray needs for Spotify: signing in (null when music control is off), and turning
/// the music back up on exit (null when ducking is off).
/// </summary>
public sealed record SpotifyFeature(SpotifyAuth? Auth, SpotifyDucker? Ducker);

/// <summary>Plain text log in %LOCALAPPDATA%\HeyNorm\log.txt.</summary>
public sealed class FileLog
{
    private readonly object _gate = new();

    public string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HeyNorm", "log.txt");

    public FileLog() => Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);

    public void Write(string line)
    {
        lock (_gate)
            File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {line}{Environment.NewLine}");
    }
}
