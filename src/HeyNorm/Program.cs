using HeyNorm.Assistant;
using HeyNorm.Audio;
using HeyNorm.Config;
using HeyNorm.Llm;
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
            var assistant = Build(settings, log);
            Application.Run(new TrayApplicationContext(assistant, log));
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

    private static NormAssistant Build(AppSettings settings, FileLog log)
    {
        var signals = new SessionSignals();
        NormAssistant? assistant = null;

        var tools = new ToolRegistry()
            .Add(new CurrentTimeTool())
            .Add(new EndSessionTool(signals))
            .Add(new TimerTool(text => _ = assistant?.AnnounceAsync(text)));
        if (!string.IsNullOrWhiteSpace(settings.Search.SearXngUrl))
            tools.Add(new WebSearchTool(new HttpClient(), settings.Search.SearXngUrl, settings.Search.MaxResults));
        log.Write($"Web search: {(tools.Knows("web_search") ? settings.Search.SearXngUrl : "off")}");
        // Add more here: music, Home Assistant...

        var engine = new ConversationEngine(new ModelRouter(settings), tools, settings.Assistant, signals);

        var phrases = new WakePhraseMatcher(settings.Wake);
        var mic = new MicrophoneSource();
        var recorder = new UtteranceRecorder(settings.Speech);
        var stt = new WhisperSpeechToText(settings.Speech.WhisperModelPath, settings.Speech.NoSpeechThreshold, log.Write);

        ITextToSpeech tts = PiperTextToSpeech.IsConfigured(settings.Speech.PiperExePath, settings.Speech.PiperVoicePath)
            ? new PiperTextToSpeech(settings.Speech.PiperExePath, settings.Speech.PiperVoicePath)
            : new SapiTextToSpeech();
        log.Write($"Text-to-speech: {tts.GetType().Name}");

        assistant = new NormAssistant(settings, phrases, mic, recorder, stt, tts, engine);
        return assistant;
    }
}

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
