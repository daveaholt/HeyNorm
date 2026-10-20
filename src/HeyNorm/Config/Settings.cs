namespace HeyNorm.Config;

public sealed class AppSettings
{
    public WakeSettings Wake { get; set; } = new();
    public SpeechSettings Speech { get; set; } = new();
    public AssistantSettings Assistant { get; set; } = new();
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
}
