using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using HeyNorm.Assistant;
using HeyNorm.Music;

namespace HeyNorm.Tools;

/// <summary>
/// Spotify playback on this computer: play something by name, pause, resume, skip, change the
/// volume, say what's playing. Every command that works ends the session, so the music comes
/// back up the moment Norm has answered. Measured: "what's playing?" used to leave the session
/// open, and the music sat ducked for the full idle timeout after the answer.
/// </summary>
public sealed class MusicTool : ITool
{
    private const int VolumeStep = 15;

    private readonly SpotifyClient _spotify;
    private readonly SessionSignals _signals;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    /// <param name="delay">The pause before retrying a command Spotify wasn't ready for; tests skip it.</param>
    public MusicTool(SpotifyClient spotify, SessionSignals signals, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _spotify = spotify;
        _signals = signals;
        _delay = delay ?? Task.Delay;
    }

    // Spotify takes a second or two on top of the model's own time — long enough to wonder
    // whether Norm heard. A few phrasings so it doesn't sound canned.
    internal static readonly string[] Preambles = ["On it.", "You got it.", "Sure thing."];

    public string Preamble => Preambles[Random.Shared.Next(Preambles.Length)];

    public string Name => "control_music";
    public string Description =>
        "Control the Spotify music: play a song, artist, album, or playlist by name; pause, resume, " +
        "skip to the next track, go back, change the volume, or tell what's playing.";
    public JsonObject ParametersSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["action"] = new JsonObject
            {
                ["type"] = "string",
                ["enum"] = new JsonArray("play", "pause", "resume", "next", "previous", "set_volume",
                    "volume_up", "volume_down", "now_playing"),
            },
            ["query"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "For play: what to play, as the person said it, e.g. 'Allman Brothers' or 'shop playlist'.",
            },
            ["kind"] = new JsonObject
            {
                ["type"] = "string",
                ["enum"] = new JsonArray("track", "artist", "album", "playlist"),
                ["description"] = "For play, if the person said which: a song, an artist, an album, or a playlist.",
            },
            ["volume"] = new JsonObject
            {
                ["type"] = "number",
                ["description"] = "For set_volume: 0 to 100.",
            },
        },
        ["required"] = new JsonArray("action"),
    };

    public async Task<string> InvokeAsync(JsonElement arguments, CancellationToken ct)
    {
        var query = Text(arguments, "query");
        var action = Action(Text(arguments, "action"), query, Number(arguments, "volume"));

        try
        {
            return action switch
            {
                "play" => await PlayAsync(query!, Text(arguments, "kind"), ct),
                "resume" => await ResumeAsync(ct),
                "pause" => await Do(() => _spotify.PauseAsync(ct), p => !p.IsPlaying, "Paused.", ct),
                "next" => await Do(() => _spotify.NextAsync(ct), null, "Skipped to the next track.", ct),
                "previous" => await Do(() => _spotify.PreviousAsync(ct), null, "Went back a track.", ct),
                "set_volume" when Number(arguments, "volume") is { } v => await SetVolumeAsync((int)Math.Round(v), ct),
                "set_volume" => "Tell me what volume, from 0 to 100.",
                "volume_up" => await NudgeVolumeAsync(+VolumeStep, ct),
                "volume_down" => await NudgeVolumeAsync(-VolumeStep, ct),
                "now_playing" => await NowPlayingAsync(ct),
                _ => "I can play, pause, resume, skip, go back, change the volume, or say what's playing.",
            };
        }
        catch (SpotifyException ex)
        {
            // Measured: "Nothing is playing on Spotify right now" on its own let qwen3 answer
            // "Okay, I'll set the volume to sixty" — it read a fact, not a refusal.
            return $"That didn't work: {ex.Message} Tell the person it didn't happen.";
        }
    }

    // Models ignore the enum and say what they mean. "play" with nothing to play is a resume.
    private static string Action(string? raw, string? query, double? volume)
    {
        var a = Regex.Replace((raw ?? "").ToLowerInvariant(), @"[\s-]+", "_");
        return a switch
        {
            "play" or "start" when string.IsNullOrWhiteSpace(query) => "resume",
            "play" or "start" or "play_music" or "search" => "play",
            "stop" or "pause_music" => "pause",
            "unpause" or "continue" or "play_again" => "resume",
            "skip" or "next_track" or "skip_track" => "next",
            "back" or "prev" or "previous_track" or "go_back" => "previous",
            "volume" or "volume_set" when volume is not null => "set_volume",
            "louder" or "turn_up" or "increase_volume" => "volume_up",
            "quieter" or "softer" or "turn_down" or "decrease_volume" => "volume_down",
            "whats_playing" or "current" or "current_track" or "playing" => "now_playing",
            _ => a,
        };
    }

    private async Task<string> PlayAsync(string query, string? kind, CancellationToken ct)
    {
        var item = await FindAsync(query, kind, ct)
            ?? throw new SpotifyException($"I couldn't find {query} on Spotify.");
        var device = await _spotify.TargetDeviceAsync(ct);
        await _spotify.PlayAsync(item, device.Id, ct);
        // Search always returns something, not always the thing: Spotify keeps its own
        // playlists ("Today's Top Hits") out of search for apps like this one, and the top hit
        // was someone's "Todays Hits and Yesterdays Favorites". Say so rather than pretend.
        return Ending(Resembles(item.Name, query)
            ? $"Now playing {item.Spoken}."
            : $"I couldn't find {query} exactly, so now playing {item.Spoken}.");
    }

    /// <summary>
    /// The person's own playlists first — "my shop playlist" is theirs, and search ranks other
    /// people's ahead of it. Then search, preferring an artist whose name is what was asked
    /// for ("Allman Brothers"), else the top song ("Midnight Rider").
    /// </summary>
    private async Task<PlayableItem?> FindAsync(string query, string? kind, CancellationToken ct)
    {
        var wantsPlaylist = kind == "playlist" || Regex.IsMatch(query, @"\bplaylist\b", RegexOptions.IgnoreCase);
        var name = Normalize(Regex.Replace(query, @"\b(my|the|playlist)\b", "", RegexOptions.IgnoreCase));

        if (wantsPlaylist || kind is null)
        {
            var mine = await _spotify.MyPlaylistsAsync(ct);
            var match = mine.FirstOrDefault(p => Normalize(p.Name) == name)
                ?? (wantsPlaylist ? mine.FirstOrDefault(p => Normalize(p.Name).Contains(name) && name.Length > 0) : null);
            if (match is not null) return match;
        }

        if (kind is "track" or "artist" or "album" or "playlist")
            return (await _spotify.SearchAsync(query, kind, ct)).FirstOrDefault();

        var found = await _spotify.SearchAsync(query, wantsPlaylist ? "playlist" : "artist,track", ct);
        return found.FirstOrDefault(f => f.Kind == "artist" && SameName(f.Name, query))
            ?? found.FirstOrDefault(f => f.Kind == "track")
            ?? found.FirstOrDefault();
    }

    private async Task<string> ResumeAsync(CancellationToken ct)
    {
        var device = await _spotify.TargetDeviceAsync(ct);
        return await Do(() => _spotify.ResumeAsync(device.Id, ct), p => p.IsPlaying, "Music resumed.", ct);
    }

    /// <summary>
    /// Spotify's own volume. Ducking works in the Windows mixer on top of it, so this is never
    /// the ducked level, and the restore at session end doesn't undo it.
    /// </summary>
    private async Task<string> SetVolumeAsync(int percent, CancellationToken ct)
    {
        percent = Math.Clamp(percent, 0, 100);
        await _spotify.SetVolumeAsync(percent, null, ct);
        return Ending($"Volume set to {percent} percent.");
    }

    private async Task<string> NudgeVolumeAsync(int step, CancellationToken ct)
    {
        var current = (await _spotify.GetPlaybackAsync(ct)).Device?.Volume
            ?? throw new SpotifyException("Nothing is playing on Spotify right now.");
        return await SetVolumeAsync(current + step, ct);
    }

    private async Task<string> NowPlayingAsync(CancellationToken ct)
    {
        var playback = await _spotify.GetPlaybackAsync(ct);
        if (playback.Title is null) return "Nothing is playing on Spotify right now.";
        var what = playback.By is null ? playback.Title : $"{playback.Title} by {playback.By}";
        return Ending(playback.IsPlaying ? $"Playing {what}." : $"Paused on {what}.");
    }

    /// <summary>
    /// Runs a player command. When Spotify calls it a restriction, either it's already done
    /// (pause while paused — measured: a second pause gets 403) or the player is still settling
    /// from the last command, so check, then retry once after a moment.
    /// </summary>
    private async Task<string> Do(Func<Task> action, Func<Playback, bool>? alreadyDone, string done,
        CancellationToken ct, bool endSession = true)
    {
        try
        {
            await action();
        }
        catch (SpotifyException ex) when (ex.Restricted)
        {
            if (alreadyDone is null || !alreadyDone(await _spotify.GetPlaybackAsync(ct)))
            {
                await _delay(TimeSpan.FromSeconds(1), ct);
                await action();
            }
        }
        return endSession ? Ending(done) : done;
    }

    private string Ending(string done)
    {
        _signals.EndRequested = true;
        // Measured: "the session will end after your reply" made qwen3 sign off ("Have a great
        // day!") after every skip and song title. Asking for one sentence and no goodbye didn't.
        return $"{done} Tell the person in one short sentence, with no goodbye.";
    }

    /// <summary>Whether what was found is plausibly what was asked for, by name.</summary>
    private static bool Resembles(string found, string query)
    {
        static string Core(string s) => Normalize(Regex.Replace(s, @"\b(the|my|band|playlist|album|song)\b", "", RegexOptions.IgnoreCase));
        var (f, q) = (Core(found), Core(query));
        return q.Length == 0 || f.Contains(q) || q.Contains(f) && f.Length >= 4;
    }

    private static bool SameName(string a, string b)
    {
        var (x, y) = (Normalize(Regex.Replace(a, @"\b(the|band)\b", "", RegexOptions.IgnoreCase)),
                      Normalize(Regex.Replace(b, @"\b(the|band)\b", "", RegexOptions.IgnoreCase)));
        return x.Length > 0 && (x == y || x.Contains(y) && y.Length >= 4);
    }

    private static string Normalize(string s) =>
        Regex.Replace(Regex.Replace(s.ToLowerInvariant(), @"[^\p{L}\p{N} ]", ""), @"\s+", " ").Trim();

    private static string? Text(JsonElement args, string name) =>
        args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString()!.Trim() is { Length: > 0 } s
            ? s
            : null;

    /// <summary>Accepts 40 or "40" — small models send numbers as strings.</summary>
    private static double? Number(JsonElement args, string name) =>
        !args.TryGetProperty(name, out var v) ? null : v.ValueKind switch
        {
            JsonValueKind.Number => v.GetDouble(),
            JsonValueKind.String => double.TryParse(v.GetString()?.TrimEnd('%'), NumberStyles.Float,
                CultureInfo.InvariantCulture, out var d) ? d : null,
            _ => null,
        };
}
