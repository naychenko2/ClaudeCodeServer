using System.Collections.Concurrent;
using System.Text.Json;
using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Mcp;

/// <summary>
/// Инстансное подключение Higgsfield: единый OAuth-вход админа (Clerk DCR + authorize +
/// refresh), шарится всеми владельцами через бэкенд.
///
/// Токены лежат в McpSecretStore под фиксированным id (<see cref="ServiceOwnerId"/>).
/// Состояние (подключено/срок/AuthVersion) — в data/higgsfield.json: файл без секретов,
/// едет в облачный архив штатно.
///
/// ⚠ ОСОЗНАННОЕ РЕШЕНИЕ (ф.2.1, 2026-09-15): Higgsfield — ПЕРВЫЙ http-тулсет, где
/// владелец JWT-claims (admin) авторизует ДОСТУП, но данные за ним ОБЩИЕ (один внешний
/// аккаунт Higgsfield, один OAuth-токен). Единственная защита — белый список
/// инструментов (<see cref="Http.HiggsfieldToolset.Whitelist"/>): ни листинга, ни
/// биллинга, ни публикации. Это НЕ дефект, а осознанный выбор — см. ADR/коммент.
///
/// С 2026-09-17 вход идёт через общий callback <see cref="McpOAuthService.CallbackPath"/>:
/// провайдер (Clerk DCR) прибит к нему, и собственный путь ронял authorize с
/// «redirect_uri does not match any pre-registered url». Обмен кода в живом потоке
/// делает <see cref="McpOAuthController"/>, а <see cref="NotifyCompletedAsync"/>
/// ставит Connected/AdminOwnerId/ExpiresAt/AuthVersion в higgsield.json. Старый
/// <see cref="CompleteAsync"/> оставлен только для legacy-эндпоинта
/// <c>/api/higgsfield/callback</c>.
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
    McpRegistry registry,
    McpSecretStore secrets,
    McpStatusStore statuses,
    McpOAuthService oauth,
    IConfiguration config,
    ILogger<HiggsfieldOAuthService> log)
{
    /// <summary>Имя HTTP-клиента для тихих запросов.</summary>
    public const string HttpClientName = "higgsfield-oauth";

    /// <summary>Фиксированный pseudo-owner: запись + секреты лежат под этим id, не под ownerId человека.</summary>
    public const string ServiceOwnerId = "higgsfield-instance";

    /// <summary>Ключ сервера (в ReservedKeys, в IntegrationKeys).</summary>
    public const string Key = "higgsfield";

    /// <summary>Базовый URL Higgsfield MCP.</summary>
    public const string Url = "https://mcp.higgsfield.ai/mcp";

    /// <summary>Этикетка для записи реестра.</summary>
    public const string Label = "Higgsfield";

    /// <summary>Описание.</summary>
    public const string Description = "Видео и картинки по вашей подписке";

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

    // ── Операции над записью (под pseudo-owner) ─────────────────────────────────────────

    private McpServerRecord? TryGetServiceRecord()
    {
        foreach (var r in registry.GetByOwner(ServiceOwnerId))
            if (string.Equals(r.Key, Key, StringComparison.OrdinalIgnoreCase)) return r;
        return null;
    }

    private McpServerRecord EnsureServiceRecord()
    {
        var existing = TryGetServiceRecord();
        if (existing is not null) return existing;

        var draft = new McpServerRecord
        {
            Key = Key,
            Label = Label,
            Description = Description,
            Transport = McpTransport.Http,
            Url = Url,
            Auth = new McpAuthConfig { Kind = McpAuthKind.OAuth2 },
            Enabled = true,
            Source = McpServerSource.Manual,
        };
        return registry.CreateBuiltIn(ServiceOwnerId, draft);
    }

    // ── Публичные операции ─────────────────────────────────────────────────────────────

    /// Старт OAuth-входа админа: создаёт запись, запускает discovery + DCR.
    public async Task<(string AuthorizeUrl, string State, string RedirectUri)> ConnectAsync(
        string adminOwnerId, string redirectUri, CancellationToken ct = default)
    {
        CleanupPending();
        var record = EnsureServiceRecord();
        // Запись и секреты — под фиксированным pseudo-owner, а не под человеком.
        var start = await oauth.StartAsync(ServiceOwnerId, record, redirectUri, input: null, ct);
        _pending[start.State] = new Pending(adminOwnerId, start.RedirectUri, DateTime.UtcNow);
        var state = LoadState();
        state.AuthVersion++;
        state.AdminOwnerId = adminOwnerId;
        SaveState(state);
        return (start.AuthorizeUrl, start.State, start.RedirectUri);
    }

    /// Обмен кода на токены. Авторизация — сам state (одноразовый, 10 мин);
    /// adminOwnerId берётся из pending-записи, сохранённого при ConnectAsync.
    public async Task<bool> CompleteAsync(string? state, string? code,
        CancellationToken ct = default)
    {
        CleanupPending();
        if (string.IsNullOrEmpty(state) || !_pending.TryRemove(state, out var pending))
            throw new McpOAuthException("Вход не найден или истёк — начни заново");
        if (pending.CreatedAt + PendingTtl <= DateTime.UtcNow)
            throw new McpOAuthException("Вход истёк — начни заново");

        await oauth.CompleteAsync(state, code, ServiceOwnerId, ct: ct);

        ApplyConnected(pending.AdminOwnerId);
        return true;
    }

    /// <summary>
    /// Хук для общего потока входа: <see cref="McpOAuthController.Callback"/> уже
    /// обменял код на токены и сохранил их в записи, остаётся поставить состояние в
    /// higgsfield.json. State обязан быть в нашем pending (создан <see cref="ConnectAsync"/>);
    /// если его нет — это либо чужой state, либо повторный callback того же входа.
    /// Молчаливый no-op, чтобы общий контроллер не зависел от внутренней логики Higgsfield.
    /// </summary>
    public bool NotifyCompletedAsync(string? state)
    {
        CleanupPending();
        if (string.IsNullOrEmpty(state) || !_pending.TryRemove(state, out var pending))
        {
            log.LogDebug("Higgsfield NotifyCompleted: state не в pending — чужой или повторный callback");
            return false;
        }
        if (pending.CreatedAt + PendingTtl <= DateTime.UtcNow)
        {
            log.LogWarning("Higgsfield NotifyCompleted: state истёк до завершения входа");
            return false;
        }
        ApplyConnected(pending.AdminOwnerId);
        return true;
    }

    // Единая точка выставления Connected/AdminOwnerId/ExpiresAt/AuthVersion —
    // оба пути (legacy CompleteAsync и NotifyCompletedAsync из общего callback)
    // делают ровно то же.
    private void ApplyConnected(string adminOwnerId)
    {
        var rec = TryGetServiceRecord();
        var st = LoadState();
        st.Connected = true;
        st.AdminOwnerId = adminOwnerId;
        st.ExpiresAt = rec?.Auth.OAuth?.ExpiresAt;
        st.AuthVersion++;
        SaveState(st);
        log.LogInformation("Higgsfield: подключение (admin={Admin}, ver={Ver})",
            adminOwnerId, st.AuthVersion);
    }

    /// Отключение: чистит токены, сбрасывает состояние.
    public void Disconnect()
    {
        // Чистим секреты и сбрасываем OAuth в записи.
        var record = TryGetServiceRecord();
        if (record is not null)
        {
            if (record.Auth.OAuth is { } oauthCfg)
            {
                var toRemove = new List<string>();
                if (!string.IsNullOrEmpty(oauthCfg.AccessTokenRef)) toRemove.Add(oauthCfg.AccessTokenRef);
                if (!string.IsNullOrEmpty(oauthCfg.RefreshTokenRef)) toRemove.Add(oauthCfg.RefreshTokenRef);
                if (toRemove.Count > 0) secrets.Remove(ServiceOwnerId, toRemove);
            }
            // Сбрасываем OAuth-конфиг в записи
            var draft = new McpServerRecord
            {
                Key = record.Key,
                Label = record.Label,
                Description = record.Description,
                Transport = record.Transport,
                Command = record.Command,
                Args = record.Args,
                Env = record.Env,
                Url = record.Url,
                Headers = record.Headers,
                Auth = new McpAuthConfig { Kind = McpAuthKind.OAuth2, OAuth = new McpOAuthConfig() },
                Enabled = record.Enabled,
                AllowReadOnlyPersonas = record.AllowReadOnlyPersonas,
                AllowOutsideProjects = record.AllowOutsideProjects,
                Source = record.Source,
            };
            registry.Update(ServiceOwnerId, record.Id, draft);
        }

        statuses.Remove(ServiceOwnerId, Key);
        var st = LoadState();
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
        var record = TryGetServiceRecord();
        if (record is null || record.Auth.Kind != McpAuthKind.OAuth2) return null;

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var fresh = oauth.EnsureFreshAsync(ServiceOwnerId, record, cts.Token).GetAwaiter().GetResult();
            if (fresh?.Auth.OAuth is not { } oauthConfig) return null;
            if (string.IsNullOrEmpty(oauthConfig.AccessTokenRef)) return null;

            var token = secrets.Resolve(ServiceOwnerId, oauthConfig.AccessTokenRef);
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

    // ── Одноразовая миграция ф.2.1 ────────────────────────────────────────────────────────

    /// <summary>
    /// Усыновляет самый свежий валидный OAuth-токен per-owner записей higgsfield
    /// под фиксированный <see cref="ServiceOwnerId"/>, затем удаляет все per-owner
    /// higgsfield-записи + секреты + статусы. Идемпотентна: повторный вызов
    /// без per-owner записей вернёт <c>false</c>.
    /// </summary>
    public bool RunMigration()
    {
        var allOwners = EnumerateAllOwners().ToList();

        // Шаг 1: найти запись с самым свежим валидным access-токеном
        McpServerRecord? bestRecord = null;
        string? bestOwner = null;
        McpSecretEntry? bestEntry = null;
        string? bestAccessTokenId = null;
        DateTime bestExpiry = DateTime.MinValue;

        foreach (var owner in allOwners)
        {
            if (string.Equals(owner, ServiceOwnerId, StringComparison.Ordinal)) continue;
            var rec = registry.GetByOwner(owner)
                .FirstOrDefault(r => string.Equals(r.Key, Key, StringComparison.OrdinalIgnoreCase));
            if (rec is null || rec.Auth.Kind != McpAuthKind.OAuth2) continue;
            if (rec.Auth.OAuth?.AccessTokenRef is not { Length: > 0 } atRef) continue;

            var atId = McpSecretStore.TryParseRef(atRef, out var id) ? id : atRef;
            var entry = secrets.GetEntry(owner, atId);
            if (entry is null || string.IsNullOrEmpty(entry.Value)) continue;

            var expiry = entry.ExpiresAt ?? DateTime.MaxValue;
            if (expiry > bestExpiry)
            {
                bestExpiry = expiry;
                bestRecord = rec;
                bestOwner = owner;
                bestEntry = entry;
                bestAccessTokenId = atId;
            }
        }

        var state = LoadState();

        // Шаг 2: усыновить лучший токен под ServiceOwnerId
        if (bestEntry is not null && bestRecord is not null && bestOwner is not null)
        {
            // Access-токен: переносим секрет, сохраняя тот же id
            secrets.SetEntry(ServiceOwnerId, bestEntry, bestAccessTokenId!);

            // Client secret (DCR): переносим, если есть
            var oauthCfg = bestRecord.Auth.OAuth;
            if (oauthCfg?.ClientSecretRef is { Length: > 0 } csRef)
            {
                var csId = McpSecretStore.TryParseRef(csRef, out var cid) ? cid : csRef;
                var csEntry = secrets.GetEntry(bestOwner, csId);
                if (csEntry is not null)
                {
                    secrets.SetEntry(ServiceOwnerId, csEntry, csId);
                    oauthCfg.ClientSecretRef = McpSecretStore.Placeholder(csId);
                }
            }

            // Обновить сервисную запись: OAuth-конфиг + ссылки на секреты
            var svcRecord = EnsureServiceRecord();
            if (oauthCfg is not null)
            {
                oauthCfg.AccessTokenRef = McpSecretStore.Placeholder(bestAccessTokenId!);
                oauthCfg.RefreshTokenRef = bestEntry.RefreshToken is not null
                    ? McpSecretStore.Placeholder(bestAccessTokenId!) : null;
                svcRecord.Auth = new McpAuthConfig { Kind = McpAuthKind.OAuth2, OAuth = oauthCfg };
            }
            registry.Update(ServiceOwnerId, svcRecord.Id, svcRecord);

            state.Connected = true;
            state.AdminOwnerId = bestOwner;
            state.ExpiresAt = bestEntry.ExpiresAt;
            state.AuthVersion++;
            log.LogInformation("Higgsfield миграция: токен усыновлён от owner={Owner}, срок={Exp}",
                bestOwner, bestEntry.ExpiresAt?.ToString("O"));
        }

        // Шаг 3: удалить ВСЕ per-owner higgsfield-записи (включая bestOwner)
        foreach (var owner in allOwners)
        {
            if (string.Equals(owner, ServiceOwnerId, StringComparison.Ordinal)) continue;
            var rec = registry.GetByOwner(owner)
                .FirstOrDefault(r => string.Equals(r.Key, Key, StringComparison.OrdinalIgnoreCase));
            if (rec is null) continue;

            var secretRefs = McpRegistry.SecretRefsOf(rec).ToList();
            if (secretRefs.Count > 0)
                secrets.Remove(owner, secretRefs);
            registry.Delete(owner, rec.Id);
            statuses.Remove(owner, Key);
            log.LogInformation("Higgsfield миграция: per-owner запись удалена, owner={Owner}", owner);
        }

        SaveState(state);
        return bestEntry is not null;
    }

    private IEnumerable<string> EnumerateAllOwners() => registry.GetAllOwners();
}
