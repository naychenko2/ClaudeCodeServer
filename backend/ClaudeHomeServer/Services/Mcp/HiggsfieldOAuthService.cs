using System.Collections.Concurrent;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Mcp;

namespace ClaudeHomeServer.Services.Mcp;

/// <summary>
/// Инстансное подключение Higgsfield: единый OAuth-вход админа (Clerk DCR + authorize +
/// refresh), шарится всеми владельцами через бэкенд.
///
/// Токены лежат в McpSecretStore под фиксированным id. Состояние
/// (подключено/срок/AuthVersion) — в data/higgsfield.json: файл без секретов,
/// едет в облачный архив штатно.
///
/// Проба протокола (2026-09-15, токен прод-админа):
/// - Session id: заголовок Mcp-Session-Id в ответе НЕ приходит — сервер безсессионный.
/// - Транспорт: ВСЕ ответы — Content-Type: text/event-stream (SSE), даже single-request.
///   Формат: event: message + data: {json}. Стандартный McpHttpTransport достаточен.
/// - initialize: capabilities.tools.listChanged + capabilities.resources.listChanged;
///   prompts ОТСУТСТВУЕТ (prompts/list → Method not found).
/// - media_upload: работает обычным tools/call (POST), SSE-потоком НЕ едет.
///   98 инструментов в tools/list, media_upload и media_upload_widget в списке.
/// </summary>
public sealed class HiggsfieldOAuthService(
    HiggsfieldIntegration higgsfield,
    McpOAuthService oauth,
    McpSecretStore secrets,
    IConfiguration config,
    ILogger<HiggsfieldOAuthService> log)
{
    /// <summary>Имя HTTP-клиента для тихих запросов.</summary>
    public const string HttpClientName = "higgsfield-oauth";

    private static readonly TimeSpan PendingTtl = TimeSpan.FromMinutes(10);

    // Состояние без секретов: едет в облачный архив штатно.
    private readonly string _statePath = Path.Combine(
        Path.GetDirectoryName(config["DataPath"]
            ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json"))!,
        "higgsfield.json");

    private readonly ConcurrentDictionary<string, Pending> _pending = new(StringComparer.Ordinal);

    private sealed record Pending(string AdminOwnerId, string RedirectUri, DateTime CreatedAt);

    // Состояние подключения: без секретов, едет в облачный архив.
    public sealed class State
    {
        public bool Connected { get; set; }
        public string? AdminOwnerId { get; set; }
        public DateTime? ExpiresAt { get; set; }
        public int AuthVersion { get; set; }
    }

    public State LoadState()
    {
        var loaded = JsonFileStore.Load<State>(_statePath,
            new JsonSerializerOptions { WriteIndented = true });
        return loaded ?? new State();
    }

    private void SaveState(State state) =>
        JsonFileStore.Save(_statePath, state, new JsonSerializerOptions { WriteIndented = true });

    /// Старт OAuth-входа админа: создаёт запись Higgsfield, запускает discovery + DCR.
    public async Task<(string AuthorizeUrl, string State, string RedirectUri)> ConnectAsync(
        string adminOwnerId, string redirectUri, CancellationToken ct = default)
    {
        CleanupPending();
        var record = higgsfield.EnsureRecord(adminOwnerId);
        var start = await oauth.StartAsync(adminOwnerId, record, redirectUri, input: null, ct);
        _pending[start.State] = new Pending(adminOwnerId, start.RedirectUri, DateTime.UtcNow);
        var state = LoadState();
        state.AuthVersion++;
        state.AdminOwnerId = adminOwnerId;
        SaveState(state);
        return (start.AuthorizeUrl, start.State, start.RedirectUri);
    }

    /// Обмен кода на токены.
    public async Task<bool> CompleteAsync(string? state, string? code,
        string adminOwnerId, CancellationToken ct = default)
    {
        CleanupPending();
        if (string.IsNullOrEmpty(state) || !_pending.TryRemove(state, out var pending))
            throw new McpOAuthException("Вход не найден или истёк — начни заново");
        if (pending.CreatedAt + PendingTtl <= DateTime.UtcNow)
            throw new McpOAuthException("Вход истёк — начни заново");
        if (!string.Equals(pending.AdminOwnerId, adminOwnerId, StringComparison.Ordinal))
            throw new McpOAuthException("Вход не найден или истёк — начни заново");

        await oauth.CompleteAsync(state, code, adminOwnerId, ct: ct);

        var rec = higgsfield.TryGetRecord(adminOwnerId);
        var st = LoadState();
        st.Connected = true;
        st.AdminOwnerId = adminOwnerId;
        st.ExpiresAt = rec?.Auth.OAuth?.ExpiresAt;
        st.AuthVersion++;
        SaveState(st);
        log.LogInformation("Higgsfield: подключение (admin={Admin}, ver={Ver})",
            adminOwnerId, st.AuthVersion);
        return true;
    }

    /// Отключение: чистит токены админа, сбрасывает состояние.
    public void Disconnect()
    {
        var st = LoadState();
        if (st.AdminOwnerId is { } owner)
            higgsfield.Logout(owner);
        st.Connected = false;
        st.ExpiresAt = null;
        SaveState(st);
        log.LogInformation("Higgsfield: подключение сброшено");
    }

    /// Действующий Bearer-токен: отдаёт сохранённый, пока тот жив, иначе обновляет.
    /// null — не подключено, refresh протух или токенов нет.
    public string? EnsureFresh()
    {
        var st = LoadState();
        if (st.AdminOwnerId is not { } owner) return null;
        var record = higgsfield.TryGetRecord(owner);
        if (record is null || record.Auth.Kind != McpAuthKind.OAuth2) return null;

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var fresh = oauth.EnsureFreshAsync(owner, record, cts.Token).GetAwaiter().GetResult();
            if (fresh?.Auth.OAuth is not { } oauthConfig) return null;
            if (string.IsNullOrEmpty(oauthConfig.AccessTokenRef)) return null;

            var token = secrets.Resolve(owner, oauthConfig.AccessTokenRef);
            if (oauthConfig.ExpiresAt is { } exp && exp != st.ExpiresAt)
            {
                st.ExpiresAt = exp;
                SaveState(st);
            }
            return token;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Higgsfield EnsureFresh: не удалось обновить токен");
            return null;
        }
    }

    /// Текущее состояние: подключено, срок, версия авторизации.
    public (bool Connected, string? ExpiresAt, int AuthVersion) Status()
    {
        var st = LoadState();
        return (st.Connected, st.ExpiresAt?.ToString("O"), st.AuthVersion);
    }

    private void CleanupPending()
    {
        var deadline = DateTime.UtcNow - PendingTtl;
        foreach (var (key, value) in _pending)
            if (value.CreatedAt < deadline) _pending.TryRemove(key, out _);
    }
}
