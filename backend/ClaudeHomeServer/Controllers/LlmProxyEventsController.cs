using System.Security.Cryptography;
using System.Text;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Controllers;

// Вход для прокси локальной модели (tools/thinking-strip-proxy): он рассказывает, что обрезал
// контекст чата, а мы показываем об этом карточку в ленте. Сдвиг границы прунинга стоит полного
// пересчёта префикса — 80–90 с тишины, которые человек видит как зависший чат.
//
// Почему не JWT пользователя: зовёт нас не браузер, а системная служба рядом, у которой токена
// пользователя нет и быть не должно. Почему не «просто открытая ручка»: веб-морда торчит наружу,
// а ручка пишет в ЧУЖУЮ ленту — без замка любой снаружи рисовал бы людям карточки. Замок —
// общий секрет из конфига бэкенда (LlmProxy:EventSecret), который прокси читает из того же
// файла: вторая копия секрета в юните службы разошлась бы с первой.
//
// Секрет не задан = ручки нет (404 на любой запрос): выключенная фича не должна оставлять
// открытый вход, а пустая строка при небрежном сравнении дала бы ровно его.
[ApiController]
[Route("api/internal/llm-proxy")]
[AllowAnonymous]
public class LlmProxyEventsController(
    SessionManager sessions,
    IConfiguration config,
    ILogger<LlmProxyEventsController> log) : ControllerBase
{
    public const string SecretHeader = "X-Proxy-Secret";
    public const string SecretKey = "LlmProxy:EventSecret";

    // Тело запроса от прокси. Kind — "prune" (сдвиг границы) или "compact_cloud" (автосжатие
    // ушло в облако). Числа приходят по мерке прокси: точного счётчика токенов у него нет,
    // и карточка честно показывает оценку.
    public record ProxyEvent(
        string? SessionId,
        string? Kind,
        int TokensBefore,
        int TokensAfter,
        int Blocks,
        int ResultBlocks,
        int InputBlocks,
        int ThinkingBlocks,
        double? PrefillSeconds,
        int? CacheReadTokens,
        int? PromptTokens);

    private static readonly string[] KnownKinds = ["prune", "compact_cloud"];

    [HttpPost("events")]
    public async Task<IActionResult> PostEvent([FromBody] ProxyEvent body)
    {
        var secret = config[SecretKey];
        if (string.IsNullOrWhiteSpace(secret)) return NotFound();

        var given = Request.Headers[SecretHeader].ToString();
        if (string.IsNullOrEmpty(given) || !FixedTimeEquals(given, secret)) return Unauthorized();

        if (string.IsNullOrWhiteSpace(body.SessionId)) return BadRequest();
        var kind = body.Kind is { } k && KnownKinds.Contains(k) ? k : null;
        if (kind is null) return BadRequest();

        // Сессии нет — 404. Прокси живёт своей жизнью и вправе прислать событие чата, который
        // успели удалить: это не ошибка, а гонка, и лечится она молчанием.
        if (sessions.GetSessionInfo(body.SessionId) is null) return NotFound();

        var stored = new StoredContextPrunedMessage(kind, body.TokensBefore, body.TokensAfter,
            body.Blocks, body.ResultBlocks, body.InputBlocks, body.ThinkingBlocks,
            body.PrefillSeconds, body.CacheReadTokens, body.PromptTokens);
        var broadcast = new ContextPrunedMessage(kind, body.TokensBefore, body.TokensAfter,
            body.Blocks, body.ResultBlocks, body.InputBlocks, body.ThinkingBlocks,
            body.PrefillSeconds, body.CacheReadTokens, body.PromptTokens);

        // Ход в этот момент ИДЁТ: AppendStoredAsync кладёт запись в аккумулятор текущего хода
        // (или прямо в историю, если чат не активен) и рассылает сообщение клиентам сессии.
        await sessions.AppendStoredAsync(body.SessionId, stored, broadcast);
        log.LogInformation("Прокси локальной модели: {Kind} в сессии {SessionId}, {Blocks} блоков, {Before}k -> {After}k ток",
            kind, body.SessionId, body.Blocks, body.TokensBefore / 1000, body.TokensAfter / 1000);
        return Ok();
    }

    private static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}
