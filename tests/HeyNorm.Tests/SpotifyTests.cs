using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using HeyNorm.Assistant;
using HeyNorm.Music;
using HeyNorm.Tools;

namespace HeyNorm.Tests;

/// <summary>A clock tests can move, for access-token expiry.</summary>
internal sealed class FakeClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>Builds a Spotify stack over a StubHandler, optionally already signed in.</summary>
internal sealed class SpotifyRig : IDisposable
{
    public const string Token = """{ "access_token": "access-1", "expires_in": 3600 }""";

    public StubHandler Stub { get; } = new();
    public FakeClock Clock { get; } = new();
    public string StorePath { get; } = Path.Combine(Path.GetTempPath(), $"heynorm-test-{Guid.NewGuid():N}", "spotify.bin");
    public List<string> Logged { get; } = [];
    public SessionSignals Signals { get; } = new();

    public SpotifyAuth Auth { get; }
    public SpotifyClient Client { get; }
    public MusicTool Tool { get; }
    public int Launches { get; private set; }

    /// <param name="thisComputer">The machine name Norm runs on, as the desktop app reports it.</param>
    public SpotifyRig(bool connected = true, string thisComputer = "SHOP-PC", DateTimeOffset? authorizedAt = null,
        Action? launch = null)
    {
        if (connected) SaveToken("refresh-1", authorizedAt ?? Clock.Now);
        // One HttpClient over the stub serves both halves, so the queue sees calls in order.
        var http = new HttpClient(Stub);
        Auth = new SpotifyAuth(http, "client-123", 8889, StorePath, Clock);
        Client = new SpotifyClient(http, Auth, thisComputer, launch ?? (() => Launches++), (_, _) => Task.CompletedTask);
        Tool = new MusicTool(Client, Signals, (_, _) => Task.CompletedTask);
    }

    /// <summary>Writes the store the way SpotifyAuth does: DPAPI over its JSON record.</summary>
    public void SaveToken(string refreshToken, DateTimeOffset authorizedAt)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
        var json = JsonSerializer.SerializeToUtf8Bytes(new { RefreshToken = refreshToken, AuthorizedAt = authorizedAt });
        File.WriteAllBytes(StorePath, ProtectedData.Protect(json, null, DataProtectionScope.CurrentUser));
    }

    public SpotifyRig Then(string json, HttpStatusCode status = HttpStatusCode.OK)
    {
        Stub.Enqueue(json, status);
        return this;
    }

    /// <summary>What Spotify really sends when a player command works.</summary>
    public SpotifyRig ThenOk() { Stub.EnqueueText("RW3qkMdJfIY7CCcyKTRnO64i-Sc"); return this; }

    public const string Restricted =
        """{ "error": { "status": 403, "message": "Player command failed: Restriction violated", "reason": "UNKNOWN" } }""";

    public string Path_(int i) => Uri.UnescapeDataString(Stub.RequestUris[i].PathAndQuery);

    public Task<string> Ask(string argsJson) => Tool.InvokeAsync(Json.Element(argsJson), CancellationToken.None);

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(StorePath)!, recursive: true); } catch (IOException) { }
    }
}

public class SpotifyAuthTests
{
    [Fact]
    public void The_pkce_challenge_matches_the_rfc_7636_worked_example()
    {
        Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM",
            SpotifyAuth.Challenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"));
    }

    [Fact]
    public void The_consent_url_carries_pkce_the_loopback_redirect_and_the_playback_scopes()
    {
        using var rig = new SpotifyRig(connected: false);

        var url = Uri.UnescapeDataString(rig.Auth.AuthorizeUri("challenge-x", "state-y"));

        Assert.StartsWith("https://accounts.spotify.com/authorize?client_id=client-123&response_type=code", url);
        // Spotify rejects "localhost"; it has to be the loopback IP.
        Assert.Contains("redirect_uri=http://127.0.0.1:8889/callback", url);
        Assert.Contains("code_challenge_method=S256&code_challenge=challenge-x", url);
        Assert.Contains("user-modify-playback-state", url);
        Assert.Contains("state=state-y", url);
    }

    [Theory]
    [InlineData("GET /callback?code=abc&state=s1 HTTP/1.1", "abc", null)]
    [InlineData("GET /callback?error=access_denied&state=s1 HTTP/1.1", null, "access_denied")]
    [InlineData("GET /callback?code=abc&state=forged HTTP/1.1", null, "the reply didn't match this sign-in")]
    public void Reads_the_callback(string requestLine, string? code, string? error)
    {
        var result = SpotifyAuth.ParseCallback(requestLine, "s1");

        Assert.NotNull(result);
        Assert.Equal(code, result!.Code);
        Assert.Equal(error, result.Error);
    }

    [Fact]
    public void Ignores_requests_that_are_not_the_callback()
    {
        Assert.Null(SpotifyAuth.ParseCallback("GET /favicon.ico HTTP/1.1", "s1"));
    }

    [Fact]
    public async Task Without_a_sign_in_it_says_how_to_connect()
    {
        using var rig = new SpotifyRig(connected: false);

        var ex = await Assert.ThrowsAsync<SpotifyException>(() => rig.Auth.GetAccessTokenAsync(CancellationToken.None));

        Assert.Contains("Connect Spotify", ex.Message);
        Assert.Empty(rig.Stub.RequestUris);
    }

    [Fact]
    public async Task Refreshes_with_the_stored_token_and_caches_the_result()
    {
        using var rig = new SpotifyRig().Then(SpotifyRig.Token);

        Assert.Equal("access-1", await rig.Auth.GetAccessTokenAsync(CancellationToken.None));
        Assert.Equal("access-1", await rig.Auth.GetAccessTokenAsync(CancellationToken.None));

        Assert.Single(rig.Stub.RequestUris); // the second call came from the cache
        Assert.Equal("https://accounts.spotify.com/api/token", rig.Stub.RequestUris[0].ToString());
        Assert.Equal("grant_type=refresh_token&refresh_token=refresh-1&client_id=client-123", rig.Stub.RawBodies[0]);
    }

    [Fact]
    public async Task Refreshes_again_once_the_access_token_has_run_out()
    {
        using var rig = new SpotifyRig().Then(SpotifyRig.Token)
            .Then("""{ "access_token": "access-2", "expires_in": 3600 }""");
        await rig.Auth.GetAccessTokenAsync(CancellationToken.None);

        rig.Clock.Now = rig.Clock.Now.AddMinutes(59.5); // inside the one-minute safety margin

        Assert.Equal("access-2", await rig.Auth.GetAccessTokenAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_rotated_refresh_token_replaces_the_old_one_but_keeps_the_six_month_clock()
    {
        var signedIn = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        using var rig = new SpotifyRig(authorizedAt: signedIn)
            .Then("""{ "access_token": "access-1", "expires_in": 3600, "refresh_token": "refresh-2" }""")
            .Then(SpotifyRig.Token);

        await rig.Auth.GetAccessTokenAsync(CancellationToken.None);
        await rig.Auth.GetAccessTokenAsync(CancellationToken.None, forceRefresh: true);

        Assert.Contains("refresh_token=refresh-2", rig.Stub.RawBodies[1]);
        // Refreshing doesn't restart Spotify's six months; only signing in again does.
        Assert.Equal(signedIn.AddMonths(6), rig.Auth.ExpiresAround);
    }

    [Fact]
    public async Task An_expired_sign_in_is_forgotten_and_explained()
    {
        using var rig = new SpotifyRig()
            .Then("""{ "error": "invalid_grant", "error_description": "Refresh token revoked" }""", HttpStatusCode.BadRequest);

        var ex = await Assert.ThrowsAsync<SpotifyException>(() => rig.Auth.GetAccessTokenAsync(CancellationToken.None));

        Assert.Contains("sign in again", ex.Message);
        Assert.False(rig.Auth.IsConnected);
    }

    [Fact]
    public async Task Signing_in_runs_the_whole_browser_round_trip()
    {
        // The one test that opens a socket: loopback only, on a free port, standing in for the
        // browser that Spotify redirects back to Norm.
        var port = FreePort();
        var stub = new StubHandler().Enqueue(
            """{ "access_token": "access-1", "expires_in": 3600, "refresh_token": "refresh-1" }""");
        var store = Path.Combine(Path.GetTempPath(), $"heynorm-test-{Guid.NewGuid():N}", "spotify.bin");
        var auth = new SpotifyAuth(new HttpClient(stub), "client-123", port, store);
        using var browser = new HttpClient();
        string? page = null;

        try
        {
            await auth.LoginAsync(url =>
            {
                var state = Uri.UnescapeDataString(url.Split("state=")[1]);
                _ = Task.Run(async () =>
                {
                    await browser.GetAsync($"http://127.0.0.1:{port}/favicon.ico");
                    page = await browser.GetStringAsync($"http://127.0.0.1:{port}/callback?code=the-code&state={state}");
                });
            }, CancellationToken.None);

            Assert.True(auth.IsConnected);
            Assert.Contains("grant_type=authorization_code&code=the-code", stub.RawBodies[0]);
            Assert.Contains("code_verifier=", stub.RawBodies[0]);
            await Task.Delay(100); // let the browser side finish reading the page
            Assert.Contains("You can close this tab", page);
        }
        finally
        {
            try { Directory.Delete(Path.GetDirectoryName(store)!, recursive: true); } catch (IOException) { }
        }
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}

public class SpotifyClientTests
{
    private const string Devices = """
        { "devices": [
            { "id": "dev-laptop", "name": "Workshop Laptop", "type": "Computer", "is_active": false, "volume_percent": 50, "supports_volume": true },
            { "id": "dev-shop", "name": "SHOP-PC", "type": "Computer", "is_active": true, "volume_percent": 70, "supports_volume": true }
        ] }
        """;

    [Fact]
    public async Task Sends_the_access_token_as_a_bearer()
    {
        using var rig = new SpotifyRig().Then(SpotifyRig.Token).Then(Devices);

        await rig.Client.GetDevicesAsync(CancellationToken.None);

        Assert.Equal("Bearer access-1", rig.Stub.Headers[1]["Authorization"]);
    }

    [Fact]
    public async Task A_401_refreshes_the_token_and_retries_once()
    {
        using var rig = new SpotifyRig().Then(SpotifyRig.Token)
            .Then("""{ "error": { "status": 401, "message": "The access token expired" } }""", HttpStatusCode.Unauthorized)
            .Then("""{ "access_token": "access-2", "expires_in": 3600 }""")
            .Then(Devices);

        var devices = await rig.Client.GetDevicesAsync(CancellationToken.None);

        Assert.Equal(2, devices.Count);
        Assert.Equal("Bearer access-2", rig.Stub.Headers[3]["Authorization"]);
    }

    [Fact]
    public async Task Picks_the_spotify_app_on_this_computer_by_name_whatever_its_case()
    {
        using var rig = new SpotifyRig(thisComputer: "shop-pc").Then(SpotifyRig.Token).Then(Devices);

        Assert.Equal("dev-shop", (await rig.Client.TargetDeviceAsync(CancellationToken.None)).Id);
        Assert.Equal(0, rig.Launches);
    }

    [Fact]
    public async Task A_phone_playing_on_the_same_account_is_passed_over()
    {
        // The music is meant to come out of this machine, not whatever happens to be active.
        using var rig = new SpotifyRig().Then(SpotifyRig.Token).Then("""
            { "devices": [
                { "id": "dev-phone", "name": "Pixel", "type": "Smartphone", "is_active": true },
                { "id": "dev-shop", "name": "SHOP-PC", "type": "Computer", "is_active": false }
            ] }
            """);

        Assert.Equal("dev-shop", (await rig.Client.TargetDeviceAsync(CancellationToken.None)).Id);
    }

    [Fact]
    public async Task A_lone_computer_is_used_even_if_its_name_does_not_match()
    {
        using var rig = new SpotifyRig(thisComputer: "RENAMED").Then(SpotifyRig.Token).Then("""
            { "devices": [ { "id": "dev-x", "name": "Spotify on Windows", "type": "Computer", "is_active": false } ] }
            """);

        Assert.Equal("dev-x", (await rig.Client.TargetDeviceAsync(CancellationToken.None)).Id);
    }

    [Fact]
    public async Task Opens_spotify_when_it_is_not_running_and_waits_for_it_to_appear()
    {
        using var rig = new SpotifyRig().Then(SpotifyRig.Token)
            .Then("""{ "devices": [] }""")
            .Then("""{ "devices": [] }""")    // still starting up
            .Then(Devices);

        var device = await rig.Client.TargetDeviceAsync(CancellationToken.None);

        Assert.Equal("dev-shop", device.Id);
        Assert.Equal(1, rig.Launches);
    }

    [Fact]
    public async Task Gives_up_with_a_sentence_if_spotify_never_appears()
    {
        using var rig = new SpotifyRig().Then(SpotifyRig.Token);
        for (var i = 0; i < 16; i++) rig.Then("""{ "devices": [] }""");

        var ex = await Assert.ThrowsAsync<SpotifyException>(() => rig.Client.TargetDeviceAsync(CancellationToken.None));

        Assert.Equal("Spotify is taking a while to open. Try again in a moment.", ex.Message);
    }

    [Fact]
    public async Task Spotify_not_installed_is_a_sentence()
    {
        using var rig = new SpotifyRig(launch: () => throw new SpotifyException("Spotify isn't installed on this computer."))
            .Then(SpotifyRig.Token).Then("""{ "devices": [] }""");

        var ex = await Assert.ThrowsAsync<SpotifyException>(() => rig.Client.TargetDeviceAsync(CancellationToken.None));

        Assert.Equal("Spotify isn't installed on this computer.", ex.Message);
    }

    [Fact]
    public async Task A_track_plays_by_uri_and_anything_else_as_a_context()
    {
        using var rig = new SpotifyRig().Then(SpotifyRig.Token).Then("").Then("");

        await rig.Client.PlayAsync(new PlayableItem("spotify:track:t1", "track", "Midnight Rider", null), "dev-shop", CancellationToken.None);
        await rig.Client.PlayAsync(new PlayableItem("spotify:artist:a1", "artist", "Allman", null), "dev-shop", CancellationToken.None);

        Assert.Equal(HttpMethod.Put, rig.Stub.Methods[1]);
        Assert.Equal("/v1/me/player/play?device_id=dev-shop", rig.Path_(1));
        Assert.Equal("spotify:track:t1", (string?)rig.Stub.Request(0)["uris"]![0]);
        Assert.Equal("spotify:artist:a1", (string?)rig.Stub.Request(1)["context_uri"]);
    }

    [Fact]
    public async Task Search_asks_for_the_kinds_wanted_and_skips_the_null_entries_spotify_sends()
    {
        using var rig = new SpotifyRig().Then(SpotifyRig.Token).Then("""
            {
              "artists": { "items": [ { "uri": "spotify:artist:a1", "name": "The Allman Brothers Band" } ] },
              "tracks": { "items": [ null, { "uri": "spotify:track:t1", "name": "Midnight Rider",
                                             "artists": [ { "name": "The Allman Brothers Band" } ] } ] }
            }
            """);

        var found = await rig.Client.SearchAsync("allman brothers", "artist,track", CancellationToken.None);

        Assert.Equal("/v1/search?q=allman brothers&type=artist,track&limit=5", rig.Path_(1));
        Assert.Equal(["artist", "track"], found.Select(f => f.Kind));
        Assert.Equal("Midnight Rider by The Allman Brothers Band", found[1].Spoken);
    }

    [Fact]
    public async Task Follows_the_next_link_through_the_persons_playlists()
    {
        using var rig = new SpotifyRig().Then(SpotifyRig.Token)
            .Then("""{ "items": [ { "uri": "spotify:playlist:p1", "name": "Shop" } ], "next": "https://api.spotify.com/v1/me/playlists?offset=50&limit=50" }""")
            .Then("""{ "items": [ { "uri": "spotify:playlist:p2", "name": "Sanding" } ], "next": null }""");

        var playlists = await rig.Client.MyPlaylistsAsync(CancellationToken.None);

        Assert.Equal(["Shop", "Sanding"], playlists.Select(p => p.Name));
        Assert.Equal("/v1/me/playlists?offset=50&limit=50", rig.Path_(2));
    }

    [Fact]
    public async Task Nothing_playing_anywhere_comes_back_as_not_playing()
    {
        using var rig = new SpotifyRig().Then(SpotifyRig.Token).Then("", HttpStatusCode.NoContent);

        var playback = await rig.Client.GetPlaybackAsync(CancellationToken.None);

        Assert.False(playback.IsPlaying);
        Assert.Null(playback.Device);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "NO_ACTIVE_DEVICE", "Nothing is playing on Spotify right now.")]
    [InlineData(HttpStatusCode.Forbidden, "PREMIUM_REQUIRED", "Spotify only allows this with a Premium account.")]
    [InlineData(HttpStatusCode.TooManyRequests, null, "Spotify is limiting requests right now. Try again in a minute.")]
    [InlineData(HttpStatusCode.NotFound, null, "Spotify couldn't find that.")]
    public async Task Spotify_errors_become_sentences(HttpStatusCode status, string? reason, string spoken)
    {
        var body = reason is null
            ? $$"""{ "error": { "status": {{(int)status}} } }"""
            : $$"""{ "error": { "status": {{(int)status}}, "reason": "{{reason}}" } }""";
        using var rig = new SpotifyRig().Then(SpotifyRig.Token).Then(body, status);

        var ex = await Assert.ThrowsAsync<SpotifyException>(() => rig.Client.NextAsync(CancellationToken.None));

        Assert.Equal(spoken, ex.Message);
    }

    [Fact]
    public async Task An_unreachable_spotify_is_a_sentence_too()
    {
        var store = Path.Combine(Path.GetTempPath(), $"heynorm-test-{Guid.NewGuid():N}", "spotify.bin");
        var auth = new SpotifyAuth(new HttpClient(new UnreachableHandler()), "c", 8889, store);
        var client = new SpotifyClient(new HttpClient(new UnreachableHandler()), auth, "SHOP-PC", () => { });

        // Not signed in, so the sign-in message comes first — no network detail either way.
        var ex = await Assert.ThrowsAsync<SpotifyException>(() => client.NextAsync(CancellationToken.None));

        Assert.Contains("Connect Spotify", ex.Message);
    }
}

public class MusicToolTests
{
    private const string NoPlaylists = """{ "items": [], "next": null }""";
    private const string OneDevice = """{ "devices": [ { "id": "dev-shop", "name": "SHOP-PC", "type": "Computer", "is_active": true, "volume_percent": 70 } ] }""";

    private const string AllmanSearch = """
        {
          "artists": { "items": [ { "uri": "spotify:artist:a1", "name": "The Allman Brothers Band" } ] },
          "tracks": { "items": [ { "uri": "spotify:track:t1", "name": "Jessica", "artists": [ { "name": "The Allman Brothers Band" } ] } ] }
        }
        """;

    [Fact]
    public async Task Playing_an_artist_by_name_plays_the_artist_and_ends_the_session()
    {
        using var rig = new SpotifyRig().Then(SpotifyRig.Token).Then(NoPlaylists).Then(AllmanSearch).Then(OneDevice).Then("");

        var reply = await rig.Ask("""{ "action": "play", "query": "Allman Brothers" }""");

        Assert.StartsWith("Now playing The Allman Brothers Band.", reply);
        Assert.Equal("spotify:artist:a1", (string?)rig.Stub.Request(0)["context_uri"]);
        // So the music comes back up now, not after the idle timeout.
        Assert.True(rig.Signals.EndRequested);
    }

    [Fact]
    public async Task A_song_title_plays_the_top_song_rather_than_whoever_sang_it()
    {
        using var rig = new SpotifyRig().Then(SpotifyRig.Token).Then(NoPlaylists).Then("""
            {
              "artists": { "items": [ { "uri": "spotify:artist:e1", "name": "Eagles" } ] },
              "tracks": { "items": [ { "uri": "spotify:track:h1", "name": "Hotel California", "artists": [ { "name": "Eagles" } ] } ] }
            }
            """).Then(OneDevice).Then("");

        var reply = await rig.Ask("""{ "action": "play", "query": "Hotel California" }""");

        Assert.StartsWith("Now playing Hotel California by Eagles.", reply);
        Assert.Equal("spotify:track:h1", (string?)rig.Stub.Request(0)["uris"]![0]);
    }

    [Fact]
    public async Task My_playlist_is_found_among_the_persons_own_before_any_search()
    {
        using var rig = new SpotifyRig().Then(SpotifyRig.Token)
            .Then("""{ "items": [ { "uri": "spotify:playlist:p9", "name": "Shop Tunes" } ], "next": null }""")
            .Then(OneDevice).Then("");

        var reply = await rig.Ask("""{ "action": "play", "query": "my shop playlist" }""");

        Assert.StartsWith("Now playing the Shop Tunes playlist.", reply);
        Assert.DoesNotContain(rig.Stub.RequestUris, u => u.AbsolutePath.EndsWith("/search"));
    }

    [Fact]
    public async Task A_named_kind_searches_only_that_kind()
    {
        using var rig = new SpotifyRig().Then(SpotifyRig.Token)
            .Then("""{ "albums": { "items": [ { "uri": "spotify:album:b1", "name": "Eat a Peach", "artists": [ { "name": "The Allman Brothers Band" } ] } ] } }""")
            .Then(OneDevice).Then("");

        var reply = await rig.Ask("""{ "action": "play", "query": "Eat a Peach", "kind": "album" }""");

        Assert.StartsWith("Now playing Eat a Peach by The Allman Brothers Band.", reply);
        Assert.Contains("type=album&", rig.Path_(1));
    }

    [Fact]
    public async Task Nothing_found_says_so()
    {
        using var rig = new SpotifyRig().Then(SpotifyRig.Token).Then(NoPlaylists).Then("""{ "artists": { "items": [] }, "tracks": { "items": [] } }""");

        Assert.StartsWith("That didn't work: I couldn't find qwxzv on Spotify.", await rig.Ask("""{ "action": "play", "query": "qwxzv" }"""));
        Assert.False(rig.Signals.EndRequested);
    }

    [Theory]
    [InlineData("skip", "/v1/me/player/next")]
    [InlineData("next", "/v1/me/player/next")]
    [InlineData("back", "/v1/me/player/previous")]
    [InlineData("stop", "/v1/me/player/pause")]
    [InlineData("Pause", "/v1/me/player/pause")]
    public async Task Says_what_it_means_whatever_word_the_model_used(string action, string path)
    {
        using var rig = new SpotifyRig().Then(SpotifyRig.Token).Then("");

        await rig.Ask($$"""{ "action": "{{action}}" }""");

        Assert.Equal(path, rig.Path_(1));
    }

    [Fact]
    public async Task Every_command_that_works_ends_the_session_so_the_music_comes_back_up()
    {
        // Measured: "what's playing?" and "stop the music" left the session open, and the music
        // stayed ducked for the whole idle timeout after Norm had answered.
        using var rig = new SpotifyRig().Then(SpotifyRig.Token).ThenOk();

        Assert.StartsWith("Paused.", await rig.Ask("""{ "action": "pause" }"""));
        Assert.True(rig.Signals.EndRequested);
    }

    [Fact]
    public void Its_preamble_is_a_short_acknowledgement()
    {
        using var rig = new SpotifyRig(connected: false);

        Assert.Contains(((ITool)rig.Tool).Preamble, MusicTool.Preambles);
        Assert.All(MusicTool.Preambles, p => Assert.True(p.Split(' ').Length <= 3, p));
    }

    [Fact]
    public async Task Play_with_nothing_named_resumes()
    {
        using var rig = new SpotifyRig().Then(SpotifyRig.Token).Then(OneDevice).Then("");

        var reply = await rig.Ask("""{ "action": "play" }""");

        Assert.StartsWith("Music resumed.", reply);
        Assert.Equal("/v1/me/player/play?device_id=dev-shop", rig.Path_(2));
        Assert.Empty(rig.Stub.Requests); // no body: resume, not a new context
    }

    [Fact]
    public async Task A_volume_sent_as_a_string_still_works()
    {
        using var rig = new SpotifyRig().Then(SpotifyRig.Token).Then("");

        var reply = await rig.Ask("""{ "action": "set_volume", "volume": "40" }""");

        Assert.StartsWith("Volume set to 40 percent.", reply);
        Assert.Equal("/v1/me/player/volume?volume_percent=40", rig.Path_(1));
    }

    [Fact]
    public async Task Now_playing_names_the_song_and_artist()
    {
        using var rig = new SpotifyRig().Then(SpotifyRig.Token).Then("""
            { "is_playing": true, "device": { "id": "dev-shop", "name": "SHOP-PC", "volume_percent": 70 },
              "item": { "name": "Jessica", "artists": [ { "name": "The Allman Brothers Band" } ] } }
            """);

        Assert.StartsWith("Playing Jessica by The Allman Brothers Band.", await rig.Ask("""{ "action": "now_playing" }"""));
        Assert.True(rig.Signals.EndRequested);
    }

    [Fact]
    public async Task Without_a_sign_in_it_says_how_to_connect()
    {
        using var rig = new SpotifyRig(connected: false);

        Assert.Contains("Connect Spotify", await rig.Ask("""{ "action": "play", "query": "anything" }"""));
    }

    [Fact]
    public async Task A_command_that_failed_says_so_in_words_the_model_cannot_mistake_for_success()
    {
        // Measured: with nothing playing, the bare "Nothing is playing on Spotify right now."
        // let qwen3 reply "Okay, I'll set the volume to sixty for you."
        using var rig = new SpotifyRig().Then(SpotifyRig.Token)
            .Then("""{ "error": { "status": 404, "reason": "NO_ACTIVE_DEVICE" } }""", HttpStatusCode.NotFound);

        var reply = await rig.Ask("""{ "action": "set_volume", "volume": 60 }""");

        Assert.StartsWith("That didn't work: Nothing is playing on Spotify right now.", reply);
        Assert.Contains("didn't happen", reply);
        Assert.False(rig.Signals.EndRequested);
    }

    // --- measured against the real Spotify desktop app ----------------------------

    [Theory]
    [InlineData("skip", "Skipped to the next track.")]
    [InlineData("back", "Went back a track.")]
    [InlineData("pause", "Paused.")]
    public async Task A_command_that_worked_is_reported_even_though_spotify_answers_with_a_non_json_token(
        string action, string spoken)
    {
        // Spotify answers 200 with "RW3qkMdJfIY7..." and no content type; parsing it as JSON
        // turned every working skip into a tool failure.
        using var rig = new SpotifyRig().Then(SpotifyRig.Token).ThenOk();

        Assert.StartsWith(spoken, await rig.Ask($$"""{ "action": "{{action}}" }"""));
    }

    [Fact]
    public async Task Pausing_what_is_already_paused_counts_as_paused()
    {
        using var rig = new SpotifyRig().Then(SpotifyRig.Token)
            .Then(SpotifyRig.Restricted, HttpStatusCode.Forbidden)
            .Then("""{ "is_playing": false, "device": { "id": "dev-shop", "name": "SHOP-PC" } }""");

        Assert.StartsWith("Paused.", await rig.Ask("""{ "action": "pause" }"""));
        Assert.Equal(3, rig.Stub.RequestUris.Count); // checked the state; no second pause
    }

    [Fact]
    public async Task Resuming_what_is_already_playing_counts_as_resumed()
    {
        using var rig = new SpotifyRig().Then(SpotifyRig.Token).Then(OneDevice)
            .Then(SpotifyRig.Restricted, HttpStatusCode.Forbidden)
            .Then("""{ "is_playing": true, "device": { "id": "dev-shop", "name": "SHOP-PC" } }""");

        Assert.StartsWith("Music resumed.", await rig.Ask("""{ "action": "resume" }"""));
    }

    [Fact]
    public async Task A_command_sent_while_spotify_is_settling_is_retried_once()
    {
        using var rig = new SpotifyRig().Then(SpotifyRig.Token)
            .Then(SpotifyRig.Restricted, HttpStatusCode.Forbidden)
            .ThenOk();

        Assert.StartsWith("Skipped to the next track.", await rig.Ask("""{ "action": "skip" }"""));
        Assert.Equal(["/v1/me/player/next", "/v1/me/player/next"], rig.Stub.RequestUris.Skip(1).Select(u => u.AbsolutePath));
    }

    [Fact]
    public async Task A_command_spotify_keeps_refusing_is_a_sentence_not_a_failure()
    {
        using var rig = new SpotifyRig().Then(SpotifyRig.Token)
            .Then(SpotifyRig.Restricted, HttpStatusCode.Forbidden)
            .Then(SpotifyRig.Restricted, HttpStatusCode.Forbidden);

        Assert.StartsWith("That didn't work: Spotify won't do that right now.", await rig.Ask("""{ "action": "skip" }"""));
    }

    [Fact]
    public async Task Something_else_found_in_place_of_what_was_asked_for_is_said_plainly()
    {
        // Measured: "Today's Top Hits" is Spotify's own playlist, which search leaves out for
        // apps like this, and the top hit was someone else's similarly named one.
        using var rig = new SpotifyRig().Then(SpotifyRig.Token).Then(NoPlaylists)
            .Then("""{ "playlists": { "items": [ { "uri": "spotify:playlist:x", "name": "Todays Hits and Yesterdays Favorites " } ] } }""")
            .Then(OneDevice).ThenOk();

        var reply = await rig.Ask("""{ "action": "play", "query": "Today's Top Hits", "kind": "playlist" }""");

        Assert.StartsWith(
            "I couldn't find Today's Top Hits exactly, so now playing the Todays Hits and Yesterdays Favorites playlist.",
            reply);
    }

    [Fact]
    public async Task Turn_it_up_goes_up_from_spotifys_own_volume()
    {
        // Ducking is in the Windows mixer, so Spotify's volume is never the ducked level.
        using var rig = new SpotifyRig().Then(SpotifyRig.Token)
            .Then("""{ "is_playing": true, "device": { "id": "dev-shop", "name": "SHOP-PC", "volume_percent": 60 } }""")
            .Then("");

        var reply = await rig.Ask("""{ "action": "volume_up" }""");

        Assert.StartsWith("Volume set to 75 percent.", reply);
        Assert.Equal("/v1/me/player/volume?volume_percent=75", rig.Path_(2));
    }
}

internal sealed class FakeAppVolume(float volume) : IAppVolume
{
    public float Volume { get; set; } = volume;
}

public class SpotifyDuckerTests : IDisposable
{
    private readonly string _marker = Path.Combine(Path.GetTempPath(), $"heynorm-duck-{Guid.NewGuid():N}.txt");
    private readonly List<string> _logged = [];
    private List<IAppVolume> _spotify = [];

    private SpotifyDucker Ducker(int duckTo = 20) => new(() => _spotify, duckTo, _marker, _logged.Add);

    public void Dispose()
    {
        try { File.Delete(_marker); } catch (IOException) { }
    }

    [Fact]
    public void Turns_spotify_down_in_the_mixer_and_back_to_where_it_was()
    {
        var app = new FakeAppVolume(0.8f);
        _spotify = [app];
        var ducker = Ducker();

        ducker.Duck();
        Assert.Equal(0.16f, app.Volume, 3);  // 20% of its normal level

        ducker.Restore();
        Assert.Equal(0.8f, app.Volume, 3);
        Assert.False(File.Exists(_marker));
    }

    [Fact]
    public void Every_spotify_entry_is_turned_down_one_per_output_device()
    {
        var speakers = new FakeAppVolume(1f);
        var headphones = new FakeAppVolume(0.5f);
        _spotify = [speakers, headphones];
        var ducker = Ducker();

        ducker.Duck();
        ducker.Restore();

        Assert.Equal(1f, speakers.Volume, 3);
        Assert.Equal(0.5f, headphones.Volume, 3);
    }

    [Fact]
    public void Ducking_twice_keeps_the_original_level_not_the_ducked_one()
    {
        var app = new FakeAppVolume(1f);
        _spotify = [app];
        var ducker = Ducker();

        ducker.Duck();
        ducker.Duck();
        ducker.Restore();

        Assert.Equal(1f, app.Volume, 3);
    }

    [Fact]
    public void With_spotify_not_running_it_does_nothing()
    {
        var ducker = Ducker();

        ducker.Duck();

        Assert.False(ducker.IsDucked);
        Assert.False(File.Exists(_marker));
    }

    [Fact]
    public void While_ducked_the_normal_level_is_written_down_in_case_norm_dies()
    {
        _spotify = [new FakeAppVolume(0.7f)];

        Ducker().Duck();

        Assert.Equal("0.7", File.ReadAllText(_marker));
    }

    [Fact]
    public void After_a_crash_the_next_start_puts_spotify_back()
    {
        // Windows remembers the mixer level, so without this Spotify stays quiet for good.
        File.WriteAllText(_marker, "0.7");
        var app = new FakeAppVolume(0.14f);
        _spotify = [app];

        Ducker().RecoverFromCrash();

        Assert.Equal(0.7f, app.Volume, 3);
        Assert.False(File.Exists(_marker));
    }

    [Fact]
    public void After_a_crash_with_spotify_closed_the_next_duck_puts_it_back_first()
    {
        File.WriteAllText(_marker, "0.7");
        var ducker = Ducker();
        ducker.RecoverFromCrash();           // Spotify not running yet: marker kept
        Assert.True(File.Exists(_marker));

        var app = new FakeAppVolume(0.14f);  // Spotify opened, still at the crashed level
        _spotify = [app];
        ducker.Duck();
        ducker.Restore();

        Assert.Equal(0.7f, app.Volume, 3);
    }

    [Fact]
    public void A_mixer_failure_is_logged_and_never_thrown()
    {
        var ducker = new SpotifyDucker(() => throw new InvalidOperationException("no audio device"), 20, _marker, _logged.Add);

        ducker.Duck();

        Assert.Contains(_logged, l => l.StartsWith("Couldn't turn the music down"));
    }
}
