using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HeyNorm.Music;

public sealed record SpotifyDevice(string Id, string Name, string Type, bool IsActive, int? Volume, bool SupportsVolume);

public sealed record Playback(bool IsPlaying, SpotifyDevice? Device, string? Title, string? By);

/// <summary>Something playable that search or the playlist list turned up. A track carries
/// its album, which is what it actually plays from.</summary>
public sealed record PlayableItem(string Uri, string Kind, string Name, string? By, string? Album = null)
{
    public string Spoken => Kind switch
    {
        "track" or "album" when By is not null => $"{Name} by {By}",
        "playlist" => $"the {Name} playlist",
        _ => Name,
    };
}

/// <summary>
/// The slice of the Spotify Web API Norm uses: devices, playback, search, and the person's
/// own playlists. Spotify plays on the same machine as Norm, but the desktop app has no local
/// interface for "play this artist" — the Web API is the only way to start something by name,
/// and it reaches the local app as a Spotify Connect device like any other.
/// </summary>
public sealed class SpotifyClient
{
    private const string Api = "https://api.spotify.com/v1";

    private readonly HttpClient _http;
    private readonly SpotifyAuth _auth;
    private readonly string _thisComputer;
    private readonly Action _launchSpotify;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    /// <param name="thisComputer">This machine's name, which the Spotify desktop app reports
    /// as its device name.</param>
    /// <param name="launchSpotify">Opens the desktop app when it isn't running.</param>
    /// <param name="delay">Waits between checks for the app to appear; tests skip the wait.</param>
    public SpotifyClient(HttpClient http, SpotifyAuth auth, string? thisComputer = null,
        Action? launchSpotify = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _http = http;
        _http.Timeout = TimeSpan.FromSeconds(10);
        _auth = auth;
        _thisComputer = thisComputer ?? Environment.MachineName;
        _launchSpotify = launchSpotify ?? LaunchDesktopApp;
        _delay = delay ?? Task.Delay;
    }

    public async Task<Playback> GetPlaybackAsync(CancellationToken ct)
    {
        using var doc = await SendAsync(HttpMethod.Get, "/me/player", null, ct);
        // 204 No Content: nothing is playing anywhere and no device is active.
        if (doc is null) return new Playback(false, null, null, null);

        var root = doc.RootElement;
        var item = root.TryGetProperty("item", out var i) && i.ValueKind == JsonValueKind.Object ? i : (JsonElement?)null;
        return new Playback(
            root.TryGetProperty("is_playing", out var p) && p.GetBoolean(),
            root.TryGetProperty("device", out var d) && d.ValueKind == JsonValueKind.Object ? Device(d) : null,
            item is { } t ? Str(t, "name") : null,
            item is { } a ? Artists(a) : null);
    }

    public async Task<IReadOnlyList<SpotifyDevice>> GetDevicesAsync(CancellationToken ct)
    {
        using var doc = await SendAsync(HttpMethod.Get, "/me/player/devices", null, ct);
        return doc is not null && doc.RootElement.TryGetProperty("devices", out var list)
            ? list.EnumerateArray().Select(Device).ToList()
            : [];
    }

    /// <summary>
    /// The Spotify app on this computer, opening it if it isn't running. Spotify only lists a
    /// device while its app is open and signed in, which takes a few seconds after launch.
    /// </summary>
    public async Task<SpotifyDevice> TargetDeviceAsync(CancellationToken ct)
    {
        if (ThisComputer(await GetDevicesAsync(ct)) is { } device) return device;

        _launchSpotify();
        for (var attempt = 0; attempt < 15; attempt++)
        {
            await _delay(TimeSpan.FromSeconds(1), ct);
            if (ThisComputer(await GetDevicesAsync(ct)) is { } opened) return opened;
        }
        throw new SpotifyException("Spotify is taking a while to open. Try again in a moment.");
    }

    /// <summary>
    /// Phones and speakers on the same account are skipped: the music is meant to come out of
    /// this machine. The desktop app names itself after the computer; failing that, an active
    /// or lone computer is the best guess.
    /// </summary>
    private SpotifyDevice? ThisComputer(IReadOnlyList<SpotifyDevice> devices)
    {
        var computers = devices.Where(d => d.Type == "Computer").ToList();
        return computers.FirstOrDefault(d => string.Equals(d.Name, _thisComputer, StringComparison.OrdinalIgnoreCase))
            ?? computers.FirstOrDefault(d => d.IsActive)
            ?? (computers.Count == 1 ? computers[0] : null);
    }

    /// <summary>
    /// The spotify: protocol is the tidy way in, but Spotify only registers it on its first run
    /// — measured: absent straight after a winget install — so the usual exe locations follow.
    /// </summary>
    private static void LaunchDesktopApp()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var candidates = new[]
        {
            "spotify:",
            Path.Combine(appData, "Spotify", "Spotify.exe"),                          // installer, winget
            Path.Combine(localAppData, "Microsoft", "WindowsApps", "Spotify.exe"),    // Microsoft Store
        };

        foreach (var target in candidates)
        {
            if (target != "spotify:" && !File.Exists(target)) continue;
            try
            {
                Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
                return;
            }
            catch (System.ComponentModel.Win32Exception) { /* try the next one */ }
        }
        throw new SpotifyException("Spotify isn't installed on this computer.");
    }

    /// <summary>Spotify's search caps limit at 10 for development-mode apps.</summary>
    public async Task<IReadOnlyList<PlayableItem>> SearchAsync(string query, string kinds, CancellationToken ct)
    {
        using var doc = await SendAsync(HttpMethod.Get,
            $"/search?q={Uri.EscapeDataString(query)}&type={kinds}&limit=5", null, ct);
        var found = new List<PlayableItem>();
        if (doc is null) return found;

        // Grouped by kind in the order asked for, best match first within each.
        foreach (var kind in kinds.Split(','))
        {
            if (!doc.RootElement.TryGetProperty(kind + "s", out var group) ||
                !group.TryGetProperty("items", out var items)) continue;

            foreach (var item in items.EnumerateArray())
            {
                // Search can return null entries in place of items it won't show.
                if (item.ValueKind != JsonValueKind.Object || Str(item, "uri") is not { } uri) continue;
                found.Add(new PlayableItem(uri, kind, (Str(item, "name") ?? "").Trim(),
                    kind == "playlist" ? null : Artists(item),
                    kind == "track" && item.TryGetProperty("album", out var album) && album.ValueKind == JsonValueKind.Object
                        ? Str(album, "uri")
                        : null));
            }
        }
        return found;
    }

    /// <summary>The person's own and followed playlists, which search doesn't reliably find.</summary>
    public async Task<IReadOnlyList<PlayableItem>> MyPlaylistsAsync(CancellationToken ct)
    {
        var found = new List<PlayableItem>();
        string? next = "/me/playlists?limit=50";
        // Four pages is 200 playlists — past that, search has to do.
        for (var page = 0; page < 4 && next is not null; page++)
        {
            using var doc = await SendAsync(HttpMethod.Get, next, null, ct);
            if (doc is null) break;
            foreach (var p in doc.RootElement.GetProperty("items").EnumerateArray())
            {
                if (p.ValueKind == JsonValueKind.Object && Str(p, "uri") is { } uri)
                    found.Add(new PlayableItem(uri, "playlist", (Str(p, "name") ?? "").Trim(), null));
            }
            next = Str(doc.RootElement, "next") is { } url ? url.Replace(Api, "") : null;
        }
        return found;
    }

    public async Task PlayAsync(PlayableItem item, string deviceId, CancellationToken ct)
    {
        // A track plays as its place in its album, everything else as a context. Measured
        // (October 2026): a track sent alone as "uris" got 204 and silence — nothing loaded,
        // every song tried — while the same track as an album offset played at once.
        var body = item switch
        {
            { Kind: "track", Album: { } album } => new JsonObject
            {
                ["context_uri"] = album,
                ["offset"] = new JsonObject { ["uri"] = item.Uri },
            },
            { Kind: "track" } => new JsonObject { ["uris"] = new JsonArray(item.Uri) },
            _ => new JsonObject { ["context_uri"] = item.Uri },
        };
        (await SendAsync(HttpMethod.Put, $"/me/player/play?device_id={deviceId}", body, ct))?.Dispose();
    }

    public async Task ResumeAsync(string deviceId, CancellationToken ct) =>
        (await SendAsync(HttpMethod.Put, $"/me/player/play?device_id={deviceId}", null, ct))?.Dispose();

    public async Task PauseAsync(CancellationToken ct) =>
        (await SendAsync(HttpMethod.Put, "/me/player/pause", null, ct))?.Dispose();

    public async Task NextAsync(CancellationToken ct) =>
        (await SendAsync(HttpMethod.Post, "/me/player/next", null, ct))?.Dispose();

    public async Task PreviousAsync(CancellationToken ct) =>
        (await SendAsync(HttpMethod.Post, "/me/player/previous", null, ct))?.Dispose();

    public async Task SetVolumeAsync(int percent, string? deviceId, CancellationToken ct) =>
        (await SendAsync(HttpMethod.Put,
            $"/me/player/volume?volume_percent={Math.Clamp(percent, 0, 100)}" + (deviceId is null ? "" : $"&device_id={deviceId}"),
            null, ct))?.Dispose();

    /// <summary>
    /// Sends one request, refreshing the access token and retrying once on a 401. Returns the
    /// parsed body, or null for an empty one. Spotify's error reasons become sentences.
    /// </summary>
    private async Task<JsonDocument?> SendAsync(HttpMethod method, string path, JsonObject? body, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(method, Api + path);
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Bearer", await _auth.GetAccessTokenAsync(ct, forceRefresh: attempt > 0));
            // Spotify wants a body on PUT play even when there's nothing to say.
            if (body is not null) request.Content = JsonContent.Create(body);
            else if (method != HttpMethod.Get) request.Content = new StringContent("");

            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, ct);
            }
            catch (HttpRequestException)
            {
                throw new SpotifyException("Spotify isn't reachable right now.");
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new SpotifyException("Spotify took too long to answer.");
            }

            using (response)
            {
                var raw = await response.Content.ReadAsStringAsync(ct);
                if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0) continue;
                if (!response.IsSuccessStatusCode) throw Failure(response.StatusCode, raw);
                // A player command that worked answers 200 with an opaque token like
                // "RW3qkMdJfIY7CCcyKTRnO64i-Sc" and no content type — not JSON, so don't parse it.
                return response.Content.Headers.ContentType?.MediaType == "application/json" &&
                       !string.IsNullOrWhiteSpace(raw)
                    ? JsonDocument.Parse(raw)
                    : null;
            }
        }
    }

    private static SpotifyException Failure(HttpStatusCode status, string raw)
    {
        string? reason = null, message = null;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.Object)
                (reason, message) = (Str(e, "reason"), Str(e, "message"));
        }
        catch (JsonException) { /* not JSON; status alone decides */ }

        if (status == HttpStatusCode.Forbidden && message?.Contains("Restriction violated", StringComparison.OrdinalIgnoreCase) == true)
            return new SpotifyException("Spotify won't do that right now.") { Restricted = true };

        return new SpotifyException((status, reason) switch
        {
            (_, "NO_ACTIVE_DEVICE") => "Nothing is playing on Spotify right now.",
            (HttpStatusCode.NotFound, _) => "Spotify couldn't find that.",
            (_, "PREMIUM_REQUIRED") => "Spotify only allows this with a Premium account.",
            (HttpStatusCode.TooManyRequests, _) => "Spotify is limiting requests right now. Try again in a minute.",
            (HttpStatusCode.Forbidden, _) => "Spotify won't allow that right now.",
            _ => $"Spotify answered with status {(int)status}.",
        });
    }

    private static SpotifyDevice Device(JsonElement d) => new(
        Str(d, "id") ?? "",
        Str(d, "name") ?? "",
        Str(d, "type") ?? "",
        d.TryGetProperty("is_active", out var a) && a.ValueKind == JsonValueKind.True,
        d.TryGetProperty("volume_percent", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null,
        !d.TryGetProperty("supports_volume", out var s) || s.ValueKind != JsonValueKind.False);

    private static string? Artists(JsonElement item) =>
        item.TryGetProperty("artists", out var artists) && artists.ValueKind == JsonValueKind.Array
            ? string.Join(" and ", artists.EnumerateArray().Take(2).Select(x => Str(x, "name")).OfType<string>()) is { Length: > 0 } names
                ? names
                : null
            : null;

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
