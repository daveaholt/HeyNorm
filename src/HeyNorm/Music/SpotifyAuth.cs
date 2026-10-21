using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HeyNorm.Music;

/// <summary>
/// A failure with a sentence Norm can say as-is ("Spotify isn't open on the shop computer.").
/// The music tool hands these straight back to the model instead of exception detail.
/// </summary>
public sealed class SpotifyException(string spoken) : Exception(spoken)
{
    /// <summary>
    /// Spotify's "Restriction violated": the command doesn't fit the player's state right now —
    /// pausing what's already paused, or a command sent while the last one is still settling.
    /// </summary>
    public bool Restricted { get; init; }
}

/// <summary>
/// Spotify sign-in: Authorization Code with PKCE, which Spotify requires of apps that can't
/// keep a secret. The redirect is a one-shot listener on 127.0.0.1 — Spotify rejects
/// "localhost". The refresh token is kept DPAPI-encrypted under the Windows account.
///
/// Spotify ends a refresh token six months after the person signs in, however often it is
/// used, so roughly twice a year "Connect Spotify" has to be run again.
/// </summary>
public sealed class SpotifyAuth
{
    private const string AuthorizeUrl = "https://accounts.spotify.com/authorize";
    private const string TokenUrl = "https://accounts.spotify.com/api/token";

    internal const string Scopes =
        "user-read-playback-state user-modify-playback-state user-read-currently-playing " +
        "playlist-read-private playlist-read-collaborative";

    internal const string NotConnected =
        "Spotify isn't connected yet. Choose Connect Spotify in Norm's tray menu.";

    private readonly HttpClient _http;
    private readonly string _clientId;
    private readonly int _port;
    private readonly string _storePath;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _accessToken;
    private DateTimeOffset _accessExpires;

    public SpotifyAuth(HttpClient http, string clientId, int port, string storePath, TimeProvider? clock = null)
    {
        _http = http;
        _clientId = clientId;
        _port = port;
        _storePath = storePath;
        _clock = clock ?? TimeProvider.System;
    }

    public string RedirectUri => $"http://127.0.0.1:{_port}/callback";

    public bool IsConnected => File.Exists(_storePath);

    /// <summary>When the stored sign-in stops working, so the log can warn ahead of time.</summary>
    public DateTimeOffset? ExpiresAround => Load() is { } t ? t.AuthorizedAt.AddMonths(6) : null;

    /// <summary>A valid access token, refreshing it when it has expired.</summary>
    public async Task<string> GetAccessTokenAsync(CancellationToken ct, bool forceRefresh = false)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!forceRefresh && _accessToken is not null && _clock.GetUtcNow() < _accessExpires)
                return _accessToken;

            var stored = Load() ?? throw new SpotifyException(NotConnected);
            using var response = await _http.PostAsync(TokenUrl, new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = stored.RefreshToken,
                ["client_id"] = _clientId,
            }), ct);
            var body = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                // The six-month limit, or access revoked from the Spotify account page.
                // Retrying can't help; only signing in again can.
                if (body.Contains("invalid_grant", StringComparison.Ordinal))
                {
                    Forget();
                    throw new SpotifyException(
                        "Spotify's sign-in has run out. Choose Connect Spotify in Norm's tray menu to sign in again.");
                }
                throw new SpotifyException("Spotify's sign-in service isn't answering right now.");
            }

            // PKCE refresh tokens rotate: a new one replaces the old, and the old stops working.
            // The authorization time carries over — refreshing doesn't restart the six months.
            var token = ParseToken(body);
            if (token.RefreshToken is { } rotated)
                Save(stored with { RefreshToken = rotated });
            return Cache(token);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// The browser half of signing in: opens Spotify's consent page, waits for it to redirect
    /// back to the loopback listener, and trades the code for tokens.
    /// </summary>
    public async Task LoginAsync(Action<string> openBrowser, CancellationToken ct)
    {
        var verifier = RandomString(64);
        var state = RandomString(16);

        using var listener = new TcpListener(IPAddress.Loopback, _port);
        listener.Start();
        openBrowser(AuthorizeUri(Challenge(verifier), state));

        var code = await ReceiveCodeAsync(listener, state, ct);
        using var response = await _http.PostAsync(TokenUrl, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = RedirectUri,
            ["client_id"] = _clientId,
            ["code_verifier"] = verifier,
        }), ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new SpotifyException($"Spotify turned down the sign-in: {body}");

        var token = ParseToken(body);
        Save(new StoredToken(
            token.RefreshToken ?? throw new SpotifyException("Spotify's sign-in came back without a refresh token."),
            _clock.GetUtcNow()));
        Cache(token);
    }

    internal string AuthorizeUri(string challenge, string state) =>
        $"{AuthorizeUrl}?client_id={Uri.EscapeDataString(_clientId)}&response_type=code" +
        $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}&code_challenge_method=S256" +
        $"&code_challenge={challenge}&scope={Uri.EscapeDataString(Scopes)}&state={state}";

    /// <summary>RFC 7636: base64url(SHA-256(verifier)), no padding.</summary>
    internal static string Challenge(string verifier) =>
        Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    /// <summary>
    /// Serves requests until the callback arrives — browsers also ask for /favicon.ico — and
    /// answers it with a page saying the tab can be closed.
    /// </summary>
    private static async Task<string> ReceiveCodeAsync(TcpListener listener, string state, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));

        while (true)
        {
            using var client = await listener.AcceptTcpClientAsync(timeout.Token);
            await using var stream = client.GetStream();
            var requestLine = await new StreamReader(stream, Encoding.ASCII).ReadLineAsync(timeout.Token) ?? "";
            var result = ParseCallback(requestLine, state);

            var page = result switch
            {
                null => null,
                { Code: not null } => "Spotify is connected to Hey Norm. You can close this tab.",
                { Error: var error } => $"Spotify sign-in didn't finish: {WebUtility.HtmlEncode(error)}. You can close this tab.",
            };
            var html = $"<!doctype html><meta charset=utf-8><title>Hey Norm</title><p style=\"font:18px system-ui\">{page ?? "Not found."}</p>";
            var bytes = Encoding.UTF8.GetBytes(
                $"HTTP/1.1 {(page is null ? "404 Not Found" : "200 OK")}\r\nContent-Type: text/html; charset=utf-8\r\n" +
                $"Content-Length: {Encoding.UTF8.GetByteCount(html)}\r\nConnection: close\r\n\r\n{html}");
            await stream.WriteAsync(bytes, timeout.Token);

            if (result is { Code: { } code }) return code;
            if (result is { Error: { } err }) throw new SpotifyException($"Spotify sign-in didn't finish: {err}.");
        }
    }

    internal sealed record Callback(string? Code, string? Error);

    /// <summary>Reads "GET /callback?code=…&amp;state=… HTTP/1.1"; null for anything that isn't the callback.</summary>
    internal static Callback? ParseCallback(string requestLine, string expectedState)
    {
        var parts = requestLine.Split(' ');
        if (parts.Length < 2 || !parts[1].StartsWith("/callback", StringComparison.Ordinal)) return null;

        var query = parts[1].Contains('?') ? parts[1][(parts[1].IndexOf('?') + 1)..] : "";
        var values = query.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .ToDictionary(p => Uri.UnescapeDataString(p[0]), p => p.Length > 1 ? Uri.UnescapeDataString(p[1]) : "");

        // A mismatched state means the redirect didn't come from the sign-in we started.
        if (!values.TryGetValue("state", out var state) || state != expectedState)
            return new Callback(null, "the reply didn't match this sign-in");
        if (values.TryGetValue("error", out var error))
            return new Callback(null, error);
        return values.TryGetValue("code", out var code) && code.Length > 0
            ? new Callback(code, null)
            : new Callback(null, "no code in the reply");
    }

    private sealed record Token(string AccessToken, int ExpiresIn, string? RefreshToken);

    private static Token ParseToken(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        return new Token(
            root.GetProperty("access_token").GetString()!,
            root.TryGetProperty("expires_in", out var e) ? e.GetInt32() : 3600,
            root.TryGetProperty("refresh_token", out var r) ? r.GetString() : null);
    }

    private string Cache(Token token)
    {
        _accessToken = token.AccessToken;
        // A minute early, so a request never sets off with a token about to lapse.
        _accessExpires = _clock.GetUtcNow().AddSeconds(token.ExpiresIn - 60);
        return token.AccessToken;
    }

    // --- the stored sign-in --------------------------------------------------------

    internal sealed record StoredToken(string RefreshToken, DateTimeOffset AuthorizedAt);

    private StoredToken? Load()
    {
        try
        {
            if (!File.Exists(_storePath)) return null;
            var json = ProtectedData.Unprotect(File.ReadAllBytes(_storePath), null, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<StoredToken>(json);
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException)
        {
            // Written by another Windows account, or damaged: as good as not connected.
            return null;
        }
    }

    private void Save(StoredToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_storePath)!);
        var json = JsonSerializer.SerializeToUtf8Bytes(token);
        File.WriteAllBytes(_storePath, ProtectedData.Protect(json, null, DataProtectionScope.CurrentUser));
    }

    private void Forget()
    {
        _accessToken = null;
        try { File.Delete(_storePath); } catch (IOException) { /* gone already */ }
    }

    private static string RandomString(int length)
    {
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-._~";
        return string.Create(length, chars, (span, c) =>
        {
            for (var i = 0; i < span.Length; i++) span[i] = c[RandomNumberGenerator.GetInt32(c.Length)];
        });
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
