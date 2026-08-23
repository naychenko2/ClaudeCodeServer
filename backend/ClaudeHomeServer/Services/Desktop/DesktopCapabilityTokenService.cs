using System.Collections.Concurrent;

namespace ClaudeHomeServer.Services.Desktop;

/// <summary>
/// Выдача capability-токена грани десктопа (ADR-008, «Авторизация канала»): отдельный
/// токен чата с audience desktop и claims ownerId + sessionId, а НЕ сервисный JWT
/// владельца — иначе руками ходил бы любой его чат, включая ночной tasks-executor.
///
/// Токен кешируется по чату и перевыпускается только у порога истечения: ADR требует
/// константности «в пределах чата на момент запуска», и хотя значение --mcp-config в
/// отпечаток запуска CLI не входит (BuildLaunchSignature), лишний перевыпуск на каждый
/// ход означал бы новый секрет в каждом временном конфиге хода без единой причины.
/// </summary>
public sealed class DesktopCapabilityTokenService(JwtService jwt)
{
    // Перевыпуск за минуту до истечения: ход может начаться прямо на границе срока
    private static readonly TimeSpan Slack = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<string, (string Token, DateTime IssuedAt)> _tokens = new();

    /// <summary>Токен чата: живой из кеша либо свежевыпущенный.</summary>
    public string TokenFor(string ownerId, string sessionId) =>
        _tokens.AddOrUpdate(sessionId,
            _ => (jwt.IssueDesktopToken(ownerId, sessionId), DateTime.UtcNow),
            (_, old) => DateTime.UtcNow - old.IssuedAt > JwtService.DesktopTokenLifetime - Slack
                ? (jwt.IssueDesktopToken(ownerId, sessionId), DateTime.UtcNow)
                : old).Token;

    /// <summary>Чат исчез (удаление, истечение) — держать его токен в памяти незачем.</summary>
    public void Forget(string sessionId) => _tokens.TryRemove(sessionId, out _);
}
