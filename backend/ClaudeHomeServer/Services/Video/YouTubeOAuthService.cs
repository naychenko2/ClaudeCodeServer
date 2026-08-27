using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using ClaudeHomeServer.Services.Mcp;

namespace ClaudeHomeServer.Services.Video;

/// <summary>
/// Вход в Google для чтения подписок YouTube.
///
/// Токены лежат в общем сторе секретов владельца (<see cref="McpSecretStore"/>) под
/// фиксированным id: стор не привязан к MCP по сути — это per-owner хранилище значений
/// с полями OAuth (access, refresh, срок), уже исключённое из облачного архива через
/// BackupPaths.SecretFileNames. Заводить второй такой же было бы дублем.
///
/// Следствие, о котором надо помнить: восстановление бэкапа на другой машине подключение
/// НЕ перенесёт — секреты в облачный архив не уезжают, аккаунт придётся подключить заново.
/// </summary>
public sealed class YouTubeOAuthService(
    IHttpClientFactory httpFactory,
    McpSecretStore secrets,
    ILogger<YouTubeOAuthService> log)
{
    public const string HttpClientName = "video-youtube";

    /// <summary>Стабильный id записи в сторе: рефреш обязан переписывать её, а не плодить новые.</summary>
    private const string SecretId = "video-youtube";

    private const string AuthEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
    private const string TokenEndpoint = "https://oauth2.googleapis.com/token";
    private const string RevokeEndpoint = "https://oauth2.googleapis.com/revoke";
    public const string Scope = "https://www.googleapis.com/auth/youtube.readonly";

    // Незавершённые входы: state → владелец. Живёт в памяти — процесс перезапустился
    // посреди входа, значит вход просто начинают заново.
    private static readonly ConcurrentDictionary<string, PendingLogin> Pending = new();
    private static readonly TimeSpan PendingTtl = TimeSpan.FromMinutes(10);

    private sealed record PendingLogin(string OwnerId, string RedirectUri, DateTime CreatedAt);

    // Обновление токена на владельца — по одному за раз: лента и список каналов грузятся
    // параллельно, и без замка оба уходили бы рефрешить, перезаписывая запись друг друга.
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> RefreshLocks = new();

    /// <summary>
    /// Адрес согласия Google. <paramref name="state"/> связывает возврат с владельцем:
    /// callback приходит переходом браузера, нашего токена в нём нет.
    /// </summary>
    public string BuildAuthUrl(string ownerId, string redirectUri, YouTubeOptions options)
    {
        CleanupPending();
        var state = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        Pending[state] = new PendingLogin(ownerId, redirectUri, DateTime.UtcNow);

        var query = new Dictionary<string, string>
        {
            ["client_id"] = options.ClientId,
            ["redirect_uri"] = redirectUri,
            ["response_type"] = "code",
            ["scope"] = Scope,
            // Без offline+consent Google не отдаёт refresh_token на повторных входах,
            // и подключение умирает вместе с часовым access-токеном.
            ["access_type"] = "offline",
            ["prompt"] = "consent",
            ["include_granted_scopes"] = "true",
            ["state"] = state,
        };
        return AuthEndpoint + "?" + string.Join("&", query.Select(kv =>
            $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
    }

    /// <summary>
    /// Обмен кода на токены. Возвращает владельца, которому принадлежит вход, либо null —
    /// когда state неизвестен (протух, чужой, повтор).
    /// </summary>
    public async Task<string?> CompleteAsync(string state, string code, YouTubeOptions options, CancellationToken ct)
    {
        CleanupPending();
        if (!Pending.TryRemove(state, out var pending)) return null;

        var form = new Dictionary<string, string>
        {
            ["client_id"] = options.ClientId,
            ["client_secret"] = options.ClientSecret,
            ["code"] = code,
            ["grant_type"] = "authorization_code",
            ["redirect_uri"] = pending.RedirectUri,
        };

        var token = await RequestTokenAsync(form, ct);
        if (token is null) return null;

        secrets.SetEntry(pending.OwnerId, token, SecretId);
        return pending.OwnerId;
    }

    /// <summary>Подключён ли аккаунт (есть ли что обновлять).</summary>
    public bool HasAccount(string ownerId) => secrets.GetEntry(ownerId, SecretId)?.RefreshToken is not null;

    /// <summary>
    /// Отключение аккаунта. Токен отзывается У GOOGLE, а не только стирается у нас: иначе
    /// выданный доступ остаётся висеть в настройках аккаунта человека, который его отключил.
    /// Отзыв — best effort: не прошёл, значит запись всё равно удаляем.
    /// </summary>
    public async Task DisconnectAsync(string ownerId, CancellationToken ct)
    {
        var entry = secrets.GetEntry(ownerId, SecretId);
        var token = entry?.RefreshToken ?? entry?.Value;

        if (!string.IsNullOrEmpty(token))
        {
            try
            {
                var http = httpFactory.CreateClient(HttpClientName);
                using var content = new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = token });
                using var _ = await http.PostAsync(RevokeEndpoint, content, ct);
            }
            catch (Exception ex)
            {
                log.LogDebug(ex, "Отзыв токена YouTube не прошёл — запись всё равно удаляется");
            }
        }

        secrets.Remove(ownerId, [SecretId]);
    }

    /// <summary>
    /// Живой access-токен: отдаёт сохранённый, пока тот не протух, иначе обновляет по refresh.
    /// null — аккаунта нет или refresh отозван (в статусе Testing у Google это происходит
    /// через неделю), и вход нужно повторить.
    /// </summary>
    public async Task<string?> GetAccessTokenAsync(string ownerId, YouTubeOptions options, CancellationToken ct)
    {
        var entry = secrets.GetEntry(ownerId, SecretId);
        if (entry is null) return null;

        // Минута запаса: токен, протухающий в полёте, дал бы 401 на ровном месте
        if (!string.IsNullOrEmpty(entry.Value)
            && entry.ExpiresAt is { } exp && exp > DateTime.UtcNow.AddMinutes(1))
            return entry.Value;

        if (string.IsNullOrEmpty(entry.RefreshToken)) return null;

        var gate = RefreshLocks.GetOrAdd(ownerId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            // Пока ждали замок, соседний ход мог уже обновить токен — перечитываем
            var fresh = secrets.GetEntry(ownerId, SecretId);
            if (fresh is not null && !string.IsNullOrEmpty(fresh.Value)
                && fresh.ExpiresAt is { } freshExp && freshExp > DateTime.UtcNow.AddMinutes(1))
                return fresh.Value;
            entry = fresh ?? entry;

            if (string.IsNullOrEmpty(entry.RefreshToken)) return null;

            var form = new Dictionary<string, string>
            {
                ["client_id"] = options.ClientId,
                ["client_secret"] = options.ClientSecret,
                ["refresh_token"] = entry.RefreshToken,
                ["grant_type"] = "refresh_token",
            };

            var refreshed = await RequestTokenAsync(form, ct);
            if (refreshed is null)
            {
                // Отозванный refresh — не ошибка приложения: раздел покажет «подключить заново»
                log.LogInformation("Обновить токен YouTube не удалось, требуется повторный вход");
                return null;
            }

            // Google на рефреше refresh_token обычно не присылает — сохраняем прежний
            refreshed.RefreshToken ??= entry.RefreshToken;
            secrets.SetEntry(ownerId, refreshed, SecretId);
            return refreshed.Value;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<McpSecretEntry?> RequestTokenAsync(Dictionary<string, string> form, CancellationToken ct)
    {
        try
        {
            var http = httpFactory.CreateClient(HttpClientName);
            using var resp = await http.PostAsync(TokenEndpoint, new FormUrlEncodedContent(form), ct);
            if (!resp.IsSuccessStatusCode) return null;

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var root = doc.RootElement;

            var access = root.TryGetProperty("access_token", out var a) ? a.GetString() : null;
            if (string.IsNullOrEmpty(access)) return null;

            var expiresIn = root.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var secs) ? secs : 3600;

            return new McpSecretEntry
            {
                Value = access,
                RefreshToken = root.TryGetProperty("refresh_token", out var r) ? r.GetString() : null,
                ExpiresAt = DateTime.UtcNow.AddSeconds(expiresIn),
                Scope = root.TryGetProperty("scope", out var s) ? s.GetString() : Scope,
                TokenType = root.TryGetProperty("token_type", out var t) ? t.GetString() : "Bearer",
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Запрос токена Google не прошёл");
            return null;
        }
    }

    private static void CleanupPending()
    {
        var deadline = DateTime.UtcNow - PendingTtl;
        foreach (var (key, value) in Pending)
            if (value.CreatedAt < deadline) Pending.TryRemove(key, out _);
    }
}
