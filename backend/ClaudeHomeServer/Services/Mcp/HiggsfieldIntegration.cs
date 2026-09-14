using System.Text.Json;
using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Mcp;

/// <summary>
/// Единая точка встроенной интеграции Higgsfield: запись реестра, вход/выход OAuth,
/// инжект в конфиг хода.
///
/// Запись заводится <b>только</b> при явном действии человека (POST login) — не при
/// просмотре настроек. <see cref="TryGetRecord"/> возвращает существующую запись или
/// null без создания; <see cref="EnsureRecord"/> создаёт черновик для старта OAuth.
/// </summary>
public sealed class HiggsfieldIntegration(
    McpRegistry registry,
    McpSecretStore secrets,
    McpStatusStore statuses,
    McpOAuthService oauth,
    ILogger<HiggsfieldIntegration> log)
{
    public const string Key = "higgsfield";
    public const string Url = "https://mcp.higgsfield.ai/mcp";
    public const string Label = "Higgsfield";
    public const string Description = "Видео и картинки по вашей подписке";

    /// <summary>Существующая запись владельца или null — без создания.</summary>
    public McpServerRecord? TryGetRecord(string ownerId)
    {
        foreach (var r in registry.GetByOwner(ownerId))
            if (string.Equals(r.Key, Key, StringComparison.OrdinalIgnoreCase)) return r;
        return null;
    }

    /// <summary>Запись владельца: создаёт черновик, если отсутствует (для старта OAuth).</summary>
    public McpServerRecord EnsureRecord(string ownerId)
    {
        var existing = TryGetRecord(ownerId);
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
        // CreateBuiltIn: ключ «higgsfield» в ReservedKeys, обычный Create его отвергнет —
        // а нам нужна запись реестра (через неё идут OAuth, секреты, статус, конфиг хода).
        return registry.CreateBuiltIn(ownerId, draft);
    }

    /// <summary>Старт OAuth-входа: создаёт запись и запускает discovery + DCR.</summary>
    public Task<McpOAuthStart> LoginAsync(string ownerId, string redirectUri, CancellationToken ct = default)
    {
        var record = EnsureRecord(ownerId);
        return oauth.StartAsync(ownerId, record, redirectUri, input: null, ct);
    }

    /// <summary>Завершение OAuth-входа: обмен кода на токены.</summary>
    public async Task<McpOAuthCompleted> CompleteAsync(string? state, string? code,
        string expectedOwnerId, CancellationToken ct = default)
    {
        var record = TryGetRecord(expectedOwnerId)
            ?? throw new InvalidOperationException("Запись Higgsfield не найдена");
        var done = await oauth.CompleteAsync(state, code, expectedOwnerId, record.Id, ct: ct);
        return done;
    }

    /// <summary>Выход: чистит токены из секрет-стора и сбрасывает статус авторизации.</summary>
    public bool Logout(string ownerId)
    {
        var record = TryGetRecord(ownerId);
        if (record is null) return false;

        // Чистим токены
        if (record.Auth.OAuth is { } oauth)
        {
            var toRemove = new List<string>();
            if (!string.IsNullOrEmpty(oauth.AccessTokenRef)) toRemove.Add(oauth.AccessTokenRef);
            if (!string.IsNullOrEmpty(oauth.RefreshTokenRef)) toRemove.Add(oauth.RefreshTokenRef);
            if (toRemove.Count > 0) secrets.Remove(ownerId, toRemove);
        }

        // Сбрасываем Auth.OAuth в записи
        var draft = JsonSerializer.Deserialize<McpServerRecord>(JsonSerializer.Serialize(record))!;
        draft.Auth ??= new McpAuthConfig();
        draft.Auth.OAuth = new McpOAuthConfig();
        draft.Auth.Kind = McpAuthKind.OAuth2; // запись остаётся OAuth, но без токенов
        registry.Update(ownerId, record.Id, draft);
        statuses.Remove(ownerId, Key);
        log.LogInformation("Higgsfield: пользователь вышел (ownerId={OwnerId})", ownerId);
        return true;
    }

    /// <summary>
    /// Синхронное получение живого access-токена для отображения состояния на фронте.
    /// Возвращает null, если токен отсутствует или протух.
    /// </summary>
    public string? TryGetAccessToken(string ownerId)
    {
        var record = TryGetRecord(ownerId);
        if (record?.Auth.OAuth is not { } oauth) return null;
        if (string.IsNullOrEmpty(oauth.AccessTokenRef)) return null;
        return secrets.Resolve(ownerId, oauth.AccessTokenRef);
    }
}
