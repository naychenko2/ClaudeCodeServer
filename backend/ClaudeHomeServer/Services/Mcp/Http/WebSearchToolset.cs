using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Reader;
using ClaudeHomeServer.Services.Spend;
using ClaudeHomeServer.Services.WebSearch;

namespace ClaudeHomeServer.Services.Mcp.Http;

/// <summary>
/// Веб-поиск и чтение страниц (websearch: web_search + web_read) поверх HTTP-транспорта
/// (ADR-012). Заводился ради локальной модели: под <c>--bare</c> CLI не отдаёт ни
/// <c>WebSearch</c>, ни <c>WebFetch</c> (флаг <c>--tools</c> умеет только сужать), а среди
/// продуктовых MCP веб-поиска не было вовсе.
///
/// Инструментов сознательно ДВА, а не три: у локальной модели окно маленькое, и каждая
/// схема в tools/list стоит токенов. Deep research (дорогой отдельный эндпоинт Perplexity)
/// не заводим — понадобится, будет отдельным решением.
///
/// Форма — как у <c>DifyToolset</c>: ключ живёт в конфиге бэкенда, во внешний API ходим сами
/// (<see cref="PerplexitySearchService"/>), наружу — ни в env процесса CLI, ни в конфиг хода —
/// ключ не уезжает. Чтение страницы НЕ пишется заново: это существующий
/// <see cref="ReaderService"/> панели «Чтение» вместе с его рубежами (SsrfGuard на каждом хопе
/// редиректа, белый список схем и портов, потолки тела и времени — ADR-005) и его же
/// пер-владельческой квотой <see cref="ReaderQuotaService"/> (ADR-005 §5): у одного человека
/// ходы CLI и панель делят одни и те же 2 одновременных чтения и 30 в минуту.
///
/// Маршрут — <c>POST /mcp/websearch/{sessionId}</c>: хвост несёт СЕССИЮ-ВЫЗЫВАТЕЛЬ (как watch
/// и волны 2–4). Данных, зависящих от чата, у поиска нет — сессия нужна ради двух вещей:
/// fail-closed (чужой чат не получает ни состава, ни вызова) и разрезов траты
/// (проект/задача/персона), которые иначе пришлось бы брать из подставляемого заголовка.
///
/// ИНВАРИАНТ состава (IMcpToolset): tools/list зависит только от настройки инстанса
/// (есть ключ или нет) и от сессии-вызывателя — от свойств ХОДА не зависит. Пустой
/// <c>Perplexity:ApiKey</c> = сервер ходу не объявляется вовсе (SessionManager не строит
/// контекст), а если запрос всё же дошёл — состав пустой и вызов отвечает честным текстом.
/// stdio-ветки отката НЕТ (node-сервера никогда не существовало), как у watch.
/// </summary>
public sealed class WebSearchToolset(
    PerplexitySearchService search,
    ReaderService reader,
    ReaderQuotaService quota,
    SessionManager sessions,
    ISpendCollector spend) : IMcpParameterizedToolset
{
    // Имя сервера = первый сегмент маршрута POST /mcp/websearch/{sessionId}. Константа —
    // единственная точка правды для URL конфига хода (ClaudeSession) и для ключей
    // KeepMcpServers/KeepMcpTools профиля провайдера
    public const string ServerName = "websearch";

    // Потолок markdown одной страницы. Ридер режет ИСХОДНИК (2 МБ html), а после извлечения
    // длинная статья всё равно даёт десятки тысяч символов — окно локальной модели это
    // сносит целиком. Значение в конфиг не выносим: это свойство потребителя (модели),
    // а не инстанса, и лишний рубильник тут дороже пользы
    private const int MaxPageChars = 20_000;

    // Ответы — как у соседних тулсетов (JSON.stringify): camelCase, кириллица без \u
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public string Name => ServerName;
    public string Version => "1.0.0";

    public IReadOnlyList<McpToolSchema> ToolsFor(McpToolCallContext context) =>
        search.IsConfigured && TryResolve(context, out _, out _) ? Tools : [];

    public async Task<McpToolCallResult> CallAsync(string tool, JsonObject arguments,
        McpToolCallContext context, CancellationToken ct)
    {
        // Право на чат — на КАЖДЫЙ вызов, как у watch: чужая сессия не видит ни состава,
        // ни инструментов
        if (!TryResolve(context, out var session, out var error)) return Deny(error);
        // Ключ мог исчезнуть из конфига между сборкой конфига хода и вызовом (reloadOnChange):
        // честный текст вместо исключения
        if (!search.IsConfigured)
            return Deny("Веб-поиск не настроен: задайте Perplexity:ApiKey в конфигурации сервера.");

        switch (tool)
        {
            case "web_search":
            {
                var query = StringArg(arguments, "query").Trim();
                if (query.Length == 0) return Deny("Нужен параметр query");

                var sw = Stopwatch.StartNew();
                var outcome = await search.SearchAsync(query,
                    PerplexitySearchService.NormalizeRecency(OptionalArg(arguments, "recency")), ct);
                if (!outcome.Success) return Deny(outcome.Error!);

                RecordSpend(context.OwnerId, session, outcome, sw.Elapsed);
                // index — 1-based номер источника: Sonar расставляет в тексте ответа маркеры
                // вида «[2][4]», и без явного номера модели пришлось бы считать позиции
                // самой (а ошибившись — сослаться не на тот источник)
                return Json(new
                {
                    answer = outcome.Answer,
                    citations = outcome.Citations.Select((c, i) => new
                    {
                        index = i + 1, c.Url, c.Title, c.Date,
                    }),
                });
            }

            case "web_read":
            {
                var url = StringArg(arguments, "url").Trim();
                if (url.Length == 0) return Deny("Нужен параметр url");

                // Квота владельца — общая с панелью «Чтение» (ADR-005 §5). Отказ текстом,
                // а не 429: у инструмента нет HTTP-кода, модель должна прочитать причину
                if (!quota.TryAcquireRate(context.OwnerId))
                    return Deny("Слишком много чтений: лимит "
                        + $"{ReaderQuotaService.MaxPerMinutePerOwner} страниц в минуту. Подождите минуту.");
                using var slot = quota.TryAcquireConcurrency(context.OwnerId);
                if (slot is null)
                    return Deny("Одновременных чтений уже "
                        + $"{ReaderQuotaService.MaxConcurrentPerOwner} — дождитесь окончания предыдущего.");

                var page = await reader.ReadAsync(url, ct);
                if (!page.Success) return Deny(ReadErrorText(page.Error!.Value));

                var markdown = page.Markdown ?? "";
                var truncated = markdown.Length > MaxPageChars;
                return Json(new
                {
                    title = page.Title,
                    siteName = page.SiteName,
                    byline = page.Byline,
                    markdown = truncated ? markdown[..MaxPageChars] + "\n\n…(страница обрезана)" : markdown,
                    truncated,
                });
            }

            default:
                throw new ArgumentException($"Неизвестный инструмент: {tool}", nameof(tool));
        }
    }

    // Трата поиска: Sonar тарифицируется по токенам, поэтому источник НЕ tokenless (в отличие
    // от tts/fal) и едет в общие суммы токенов. Деньги считаются, только если хозяин инстанса
    // проставил цены — выдуманный тариф в отчёте о деньгах хуже отсутствующего.
    // Пустую запись (ноль токенов) отбрасывает сам SpendStore.
    private void RecordSpend(string ownerId, Session session, WebSearchOutcome outcome, TimeSpan elapsed)
    {
        var cost = (outcome.InputTokens * search.PriceInPer1M + outcome.OutputTokens * search.PriceOutPer1M)
            / 1_000_000d;
        spend.Record(new SpendRecord
        {
            OwnerId = ownerId,
            ProjectId = session.ProjectId,
            SessionId = session.Id,
            TaskId = session.TaskId,
            PersonaId = session.PersonaId,
            Provider = "perplexity",
            Model = search.Model,
            Source = SpendSources.WebSearch,
            InputTokens = outcome.InputTokens,
            OutputTokens = outcome.OutputTokens,
            CostUsd = cost > 0 ? cost : null,
            DurationMs = (long)elapsed.TotalMilliseconds,
            Label = "web_search",
        });
    }

    // Коды ридера (ADR-005 §6) — текстом для модели: она должна понять, повторять ли попытку
    // и стоит ли искать другой источник. Тексты для человека пишет фронт, у нас свои
    private static string ReadErrorText(ReaderErrorCode code) => code switch
    {
        ReaderErrorCode.InvalidUrl => "Некорректный адрес: нужен http(s)-URL на порт 80 или 443, без логина в адресе.",
        ReaderErrorCode.LocalAddress => "Адрес ведёт во внутреннюю сеть — чтение таких адресов запрещено.",
        ReaderErrorCode.DnsFailed => "Домен не резолвится.",
        ReaderErrorCode.Unreachable => "Сайт недоступен.",
        ReaderErrorCode.TlsInvalid => "Проблема с сертификатом сайта.",
        ReaderErrorCode.Timeout => "Сайт не ответил вовремя.",
        ReaderErrorCode.AuthRequired => "Страница требует входа.",
        ReaderErrorCode.BlockedBySite => "Сайт заблокировал запрос (бот-щит или лимит).",
        ReaderErrorCode.NotFound => "Страница не найдена.",
        ReaderErrorCode.ServerError => "Сайт ответил ошибкой.",
        ReaderErrorCode.TooManyRedirects => "Слишком много редиректов.",
        ReaderErrorCode.NotAPage => "Это не текстовая страница.",
        ReaderErrorCode.Pdf => "Это PDF — ридер его не разбирает.",
        ReaderErrorCode.TooLarge => "Страница слишком велика.",
        ReaderErrorCode.NotReadable => "Из страницы не удалось извлечь текст.",
        _ => "Страницу прочитать не удалось.",
    };

    // --- Маршрут: /mcp/websearch/{sessionId} ---

    /// <summary>URL эндпоинта в конфиге хода: базовый адрес + маршрут тулсета с хвостом.</summary>
    public static string EndpointFor(string apiUrl, string sessionId) =>
        McpHttpTransport.EndpointFor(apiUrl, ServerName) + "/" + sessionId;

    // Один сегмент — id сессии; форма как у соседних тулсетов (белый список resumeSessionId)
    private static bool TryParseRoute(string? route, out string sessionId)
    {
        sessionId = "";
        if (route is null || route.Split('/').Length != 1) return false;
        if (route.Length is < 1 or > 128 || !route.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            return false;
        sessionId = route;
        return true;
    }

    // Резолв хвоста в сессию ВЛАДЕЛЬЦА токена (свойство сессии — составу tools/list зависеть
    // от хода нельзя, ADR-012)
    private bool TryResolve(McpToolCallContext context,
        out Session session, [System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out string? error)
    {
        session = null!;
        if (!TryParseRoute(context.RouteTail, out var sessionId))
        {
            error = "Некорректный маршрут сервера веб-поиска — вызов отклонён.";
            return false;
        }
        var owned = sessions.GetOwned(sessionId, context.OwnerId);
        if (owned is null)
        {
            error = "Чат-вызыватель не найден или принадлежит другому владельцу — веб-поиск закрыт.";
            return false;
        }
        session = owned;
        error = null;
        return true;
    }

    // --- Ответы и аргументы ---

    private static McpToolCallResult Json<T>(T value) =>
        new(JsonSerializer.Serialize(value, JsonOpts));

    private static McpToolCallResult Deny(string text) => new(text, IsError: true);

    private static string StringArg(JsonObject arguments, string name) =>
        arguments[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";

    private static string? OptionalArg(JsonObject arguments, string name)
    {
        var value = StringArg(arguments, name).Trim();
        return value.Length == 0 ? null : value;
    }

    // --- Состав: два инструмента ---

    internal static readonly IReadOnlyList<McpToolSchema> Tools =
    [
        new("web_search",
            "Поиск в интернете с ответом по существу и списком источников-ссылок. "
            + "Зови, когда нужны свежие или внешние сведения: события, версии, документация, цены. "
            + "Маркеры «[2]» в тексте ответа — номера источников из citations (поле index). "
            + "Ответ обязательно проверяй по citations — ссылки и есть доказательство; "
            + "нужна подробность со страницы источника — прочитай её через web_read.",
            new JsonObject
            {
                ["type"] = "object",
                ["required"] = new JsonArray { "query" },
                ["properties"] = new JsonObject
                {
                    ["query"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Запрос на естественном языке (можно вопросом)",
                    },
                    ["recency"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["enum"] = new JsonArray { "day", "week", "month", "year" },
                        ["description"] = "Ограничить выдачу свежестью источников; "
                            + "пусто — без ограничения",
                    },
                },
            }),
        new("web_read",
            "Прочитать веб-страницу по ссылке: возвращает её текст в markdown. "
            + "Для ссылок из web_search и любых известных URL. "
            + "Внутренние адреса (localhost, локальная сеть) запрещены, PDF не разбирается.",
            new JsonObject
            {
                ["type"] = "object",
                ["required"] = new JsonArray { "url" },
                ["properties"] = new JsonObject
                {
                    ["url"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Полный адрес страницы (http/https)",
                    },
                },
            }),
    ];
}
