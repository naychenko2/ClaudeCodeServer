using System.Security.Claims;

namespace ClaudeHomeServer.Services.Composition;

// Узкий шов JwtService для выноса Desktop (Этап 5). Вертикаль трогает у токенов ровно
// две операции: выдать capability-токен чата (кеш токенов) и проверить предъявленный
// (схема аутентификации DesktopCapability). Полный JwtService — корневой сервис
// периметра: пользовательские и сервисные токены, версия сессий, preview-токены; и
// он держит сам ключ подписи, которому в вертикали делать нечего.
//
// Проверка отдаёт `ClaimsPrincipal`, а не разобранного вызывателя: `DesktopCaller` —
// тип вертикали, и шов в спине не может его называть (Core держит ноль
// PackageReference, а запись claims идёт через System.IdentityModel.Tokens.Jwt).
// Разбор принципала в вызывателя остаётся там же, где и был, — в
// `DesktopCaller.FromPrincipal`, единственной точке разбора capability-токена.
//
// Audience и TTL здесь НЕ объявляются: они — часть контракта канала и живут в
// `DesktopProtocol` (Core/Protocol), откуда их читают обе стороны. Иначе литерал
// audience разъехался бы между выдачей и проверкой.
public interface IDesktopCapabilityTokens
{
    // Capability-токен чата: audience desktop, claims sub=ownerId + sid=чат + did=устройство
    // (если известно на момент выдачи).
    string IssueDesktopToken(string ownerId, string sessionId, string? deviceId = null);

    // Принципал предъявленного токена. null — недействителен (подпись, срок, чужая audience).
    ClaimsPrincipal? ValidateDesktopPrincipal(string? token);
}
