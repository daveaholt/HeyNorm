namespace HeyNorm.Config;

public sealed class AppSettings
{
    public WakeSettings Wake { get; set; } = new();
    public SpeechSettings Speech { get; set; } = new();
    public AssistantSettings Assistant { get; set; } = new();
    public SearchSettings Search { get; set; } = new();
    public WeatherSettings Weather { get; set; } = new();
    public SpotifySettings Spotify { get; set; } = new();
    public DocumentsSettings Documents { get; set; } = new();
    public Dictionary<string, ProviderSettings> Providers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, TaskSettings> Tasks { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class WakeSettings
{
    /// <summary>
    /// Spoken openings that start a session. Matched against the Whisper transcript after
    /// punctuation and casing are stripped, so "Hey, Norm." matches "hey norm". Add variants
    /// here if you hear Whisper consistently mishear you.
    /// </summary>
    public List<string> WakePhrases { get; set; } = ["hey norm"];

    /// <summary>Spoken closings that end a session, matched the same way.</summary>
    public List<string> StopPhrases { get; set; } = ["norm done"];
}

public sealed class SpeechSettings
{
    public string WhisperModelPath { get; set; } = "";
    public string PiperExePath { get; set; } = "";
    public string PiperVoicePath { get; set; } = "";
    public int VadAggressiveness { get; set; } = 3;
    public int EndOfUtteranceSilenceMs { get; set; } = 900;
    public int SessionIdleTimeoutSeconds { get; set; } = 60;
    public int MaxUtteranceSeconds { get; set; } = 30;
    public float NoSpeechThreshold { get; set; } = 0.7f;
}

public sealed class AssistantSettings
{
    public string SystemPrompt { get; set; } = "";
    public string ConversationTask { get; set; } = "conversation";
    public int MaxToolRoundsPerTurn { get; set; } = 5;
}

public sealed class SearchSettings
{
    /// <summary>The local SearXNG instance. Blank leaves the web search tool out entirely.</summary>
    public string SearXngUrl { get; set; } = "";
    public int MaxResults { get; set; } = 4;
}

public sealed class WeatherSettings
{
    /// <summary>The shop's town, used when nobody names one ("what's the weather?").</summary>
    public string HomeLocation { get; set; } = "";
    /// <summary>"imperial" (Fahrenheit, mph, inches) or "metric".</summary>
    public string Units { get; set; } = "imperial";
}

public sealed class SpotifySettings
{
    /// <summary>
    /// Environment variable holding the Client ID of your Spotify developer app. Not a
    /// secret under PKCE, but it identifies your account, so it stays out of the repo.
    /// Unset leaves music control out (turning Spotify down while Norm listens still works).
    /// </summary>
    public string ClientIdEnvVar { get; set; } = "SPOTIFY_CLIENT_ID";
    /// <summary>Port for the sign-in redirect, http://127.0.0.1:{port}/callback.</summary>
    public int RedirectPort { get; set; } = 8889;
    /// <summary>
    /// Spotify's level in the Windows mixer while Norm is awake, as a percent of normal, so
    /// Norm can hear and be heard. Works without signing in; 100 turns it off.
    /// </summary>
    public int DuckToPercent { get; set; } = 20;
}

public sealed class DocumentsSettings
{
    /// <summary>
    /// Folder of PDFs Norm can answer from; subfolders name projects. Environment variables
    /// are expanded, so one setting works on any machine. Blank switches document search off.
    /// </summary>
    public string Folder { get; set; } = "";
    /// <summary>Passages handed to the model per search.</summary>
    public int MaxResults { get; set; } = 5;
    /// <summary>
    /// Task (under Tasks) whose vision model reads scanned drawings while Norm is idle. Blank,
    /// or a task that doesn't exist, leaves scans unread.
    /// </summary>
    public string DrawingTask { get; set; } = "drawings";

    public string ExpandedFolder => Environment.ExpandEnvironmentVariables(Folder.Trim());
}

public sealed class ProviderSettings
{
    /// <summary>"Ollama" or "Claude" — picks the strategy implementation.</summary>
    public string Type { get; set; } = "";
    public string BaseUrl { get; set; } = "";
    public string ApiKeyEnvVar { get; set; } = "";
}

public sealed class TaskSettings
{
    public string Provider { get; set; } = "";
    public string Model { get; set; } = "";
    public int MaxTokens { get; set; } = 1024;
    /// <summary>For Ollama thinking models (e.g. qwen3). Null leaves the model's default.</summary>
    public bool? Think { get; set; }
    public double? Temperature { get; set; }
    /// <summary>Ollama only.</summary>
    public double? RepeatPenalty { get; set; }
    /// <summary>Ollama only: the context window, in tokens.</summary>
    public int? ContextLength { get; set; }
}
