using System.Security.Cryptography;
using System.Text;

namespace ClaudeHomeServer.Telemetry;

/// <summary>Что делать с атрибутом телеметрии.</summary>
public enum PiiAction
{
    /// <summary>Заменить на sha256-префикс (нужен для корреляции, но не раскрывает значение).</summary>
    Hash,

    /// <summary>Пропустить как есть (operational metadata, не PII).</summary>
    Keep,

    /// <summary>Удалить.</summary>
    Drop,
}

/// <summary>
/// Единые правила очистки PII для трейсов и логов.
///
/// Вынесены из <see cref="PiiSanitizingProcessor"/>, чтобы у спанов и логов был ОДИН
/// набор правил: иначе тег с именем персоны дропался бы в спане и уезжал в логе
/// (ровно так и было, пока санитайзер существовал только для трейсов).
///
/// Стратегия — allowlist + drop-by-default: неизвестный атрибут удаляется.
/// Порядок проверок важен, первое совпадение выигрывает:
/// 1. Hash-паттерны (file_path, *.path) → sha256(value)[..8]
/// 2. Keep-список (operational metadata) → пропустить
/// 3. Drop-паттерны (persona, user_id, prompt…) → удалить
/// 4. Всё остальное → удалить (safe default)
///
/// Имена сравниваются нормализованно (без разделителей, регистронезависимо), потому что
/// один и тот же смысл приходит в разных стилях: тег спана <c>session_id</c> и параметр
/// лога <c>{SessionId}</c> — это одно и то же, и правило для них должно быть одно.
/// </summary>
public static class PiiRules
{
    /// <summary>Атрибуты, которые ХЭШИРУЮТСЯ (не дропаются — нужны для корреляции).</summary>
    private static readonly HashSet<string> HashTags = Normalize(
    [
        "file_path", "file.name", "filepath", "path",
        "working_dir", "working.directory", "root_path", "cwd",
        // Те же сущности под именами из структурных логов: {Root} — это RootPath проекта,
        // {Dir} — папка, {File} — имя файла. Нормализация схлопывает регистр и разделители,
        // но НЕ синонимы, поэтому root_path выше от {Root} не спасал: путь уходил в
        // default-deny и дропался. Дроп безопасен, но корреляция «одна и та же папка
        // в разных событиях» терялась молча — а ради неё хэш и заведён.
        "root", "dir", "file",
        // Хвост аудита: {Repo} и {Worktree} — тоже пути на диске.
        "repo", "worktree",
    ]);

    /// <summary>Атрибуты, которые ОСТАЮТСЯ как есть (operational metadata, не PII).</summary>
    private static readonly HashSet<string> KeepTags = Normalize(
    [
        // Идентификаторы (не PII — opaque GUIDs).
        // chat_id держит связку «инцидент → чат» (Telemetry/Incidents): правила здесь
        // работают по default-deny, поэтому без этой строки тег выбрасывался бы МОЛЧА,
        // а разбор инцидента переставал бы находить чаты — без единой ошибки в логе.
        // Сторож — PiiSanitizerTests.ChatId_IsKept (падает, если строку убрать).
        "session_id", "chat_id", "turn_id", "trace_id", "span_id",
        // project — ИДЕНТИФИКАТОР проекта из структурных логов ({Project}), а не название:
        // по всем ~36 местам туда уходит project.Id/projectId, а имя живёт отдельным
        // плейсхолдером {Name} и продолжает дропаться. Без этой строки тело лога в SigNoz
        // выглядело как «Консолидация памяти команды проекта {Project}» — событие видно,
        // а какого проекта касается, непонятно.
        "project",
        // Диагностика опциональных зависимостей и Kestrel: subject/consequence — наши
        // же тексты из профиля клиента, host — схема+хост+порт вызываемого сервиса,
        // status — код ответа, connectionid — opaque-идентификатор соединения.
        // Пользовательских данных ни в одном нет, а без них строки логов в SigNoz
        // читались как «Ошибка на стороне {Subject} ({Host}): HTTP {Status}».
        //
        // ВАЖНО: endpoint сюда НЕ входит. Под этим именем PushService логирует адрес
        // push-подписки — идентификатор устройства пользователя (Services/PushService.cs).
        "subject", "consequence", "host", "status", "connectionid",
        // trace_identifier — второй плейсхолдер того же сообщения Kestrel о необработанном
        // исключении («Connection id "{ConnectionId}", Request id "{TraceIdentifier}"»).
        // Без него запись в SigNoz читалась обрубком: половина шаблона со значением,
        // половина с плейсхолдером. Значение — «{ConnectionId}:{номер запроса}», тот же
        // opaque-идентификатор соединения, что уже разрешён строкой выше.
        "trace_identifier",
        // Идентификаторы сущностей из структурных логов — тот же класс, что session_id и
        // chat_id выше: opaque-значения без пользовательских данных. Перечислены явно,
        // потому что нормализация схлопывает регистр и разделители, но не синонимы:
        // {Session} и session_id, {ProjectId} и project, {Tool} и tool_name — одни и те же
        // значения под разными именами, и до этой строки одна половина событий приезжала
        // читаемой, а вторая обезличенной без всякой системы.
        "task_id", "plan_id", "note_id", "service_id", "terminal_id",
        "session", "sid", "project_id", "tool", "id",
        // Счётчики и объёмы — PII невозможен по типу значения.
        "count", "attempt", "wave", "total", "nodes", "edges", "skipped",
        // Версии, ревизии и коды: {Sha}/{Ref} — git, {Code} — код процесса или HTTP,
        // {Version}/{FileVersion} — версия схемы стора.
        "version", "file_version", "code", "sha", "ref",
        // Операционные ярлыки: {Action} — ключ действия LocalActionCatalog, {Label} — цель
        // reconcile, {Route} — маршрут места, {Tier} — слот модели, {BaseUrl} — адрес
        // сервиса (тот же класс, что host).
        "action", "label", "type", "route", "base_url", "tier", "skill",
        // Хвост аудита 2026-08-19: единичные счётчики и ярлыки, из-за которых половина
        // фоновых событий («сброшено {Count} хешей», «{Merged} записей слито») читалась
        // без единой цифры. Числа и длительности — PII невозможен по типу значения.
        "merged", "evicted", "before", "changed", "generated", "failed", "candidates",
        "migrated", "updated", "added", "orphans", "attempts", "hist_count", "total_chars",
        "total_tokens", "max_tokens", "len", "size", "files", "max", "min", "cap", "ceiling",
        "threshold", "number", "days", "day", "date", "minutes", "seconds", "sec", "ms",
        "elapsed_ms", "idle", "interval", "timeout", "ttl", "port", "places", "line",
        "major", "minor", "first", "second", "next", "current",
        // Ярлыки, значения которых задаёт КОД, а не пользователь: место применения модели,
        // причина и источник доставки хода, стадия, тип агента, opaque-идентификаторы джоб.
        "place", "job_id", "mode", "stage", "scope", "module", "preset", "slot", "src",
        "cause", "origin", "trigger", "verdict", "decision", "state", "op", "method",
        "glyph", "extension", "dataset", "agent_type", "agent_id", "card_id",
        "claude_session_id", "kid", "callsite", "interaction", "winner", "fixed_provider",
        "fingerprint", "layer", "tools", "tile", "bg", "alive", "dirty", "stuck", "stopped",
        "deferred", "cont", "expected", "db", "image", "entity", "target",
        // Узкие имена вместо generic-собратьев (аудит 2026-08-19, доводка 2026-08-20).
        // Каждое заведено под КОНКРЕТНОЕ значение, а место логирования переименовано под него:
        // rule_id — идентификатор правила автоматизации (в {Rule} везде лежал rule.Id, а имя
        // намекало на название, которое сочиняет пользователь); specialty — ключ специальности
        // из enum; entry — составной операционный ключ записи (label:entryKey карантина,
        // id записи памяти); fallback_step — текст шага фолбэка моделей.
        //
        // fallback_step стоит особняком: три обёртки FallbackLlmSessionAdapter (LogWarn, LogInfo,
        // LogDebug) складывали ВСЮ диагностику подмен в {Message}, то есть в имя, закрытое
        // паттерном. Наружу это выглядело как «[ModelFallback] {Message}» — предупреждения о
        // подменах, переполнении и исчерпании цепочки приезжали пустыми, причём именно они
        // попадают в досье инцидента.
        "rule_id", "specialty", "entry", "fallback_step",
        // Тексты отказов — родня разрешённого reason. Без них warning читается как
        // «действие {Action} — {Message}», то есть не читается вовсе. Сюда идут ex.Message
        // и ответ провайдера.
        //
        // ВАЖНО: generic-имена сюда НЕ входят намеренно — ни при каком аудите. Кроме
        // перечисленных ниже, закрытыми осознанно оставлены {Rule}/{RuleName} (имя правила
        // автоматизации сочиняет пользователь), {Task}, {Query}, {Note}, {Summary}, {Problem},
        // {Result}, {Entry}, {Handle}, {Login}, {Domain}, {Url}, {Old}/{New} и {Dump}.
        // {Message} закрыт паттерном ниже
        // (под ним по коду уезжает что угодно, включая текст пользователя), {Key} может
        // притянуть секрет, {Source} в skills add несёт пользовательский ввод, а {Name}
        // и {Title} — это названия проектов и заметок. Место, которому нужен видимый
        // текст отказа, переименовывает свой параметр в Reason/Error, а не открывает
        // generic-имя всему коду разом — тем же приёмом, что Endpoint → Host.
        "error", "err",
        // Operational
        "provider", "model", "direction", "tool_name", "outcome",
        "error_type", "reason", "kind", "command",
        // OTel standard
        "otel.status_code", "otel.status_description", "otel.name", "otel.kind",
        // HTTP — стабильные semconv-имена (их пишут инструментации AspNetCore/Http 1.17.0).
        // ВАЖНО: url.full и url.query сюда НЕ входят намеренно — в query-строке уезжают
        // API-ключи (Dify, OpenRouter). Путь запроса виден через http.route — это шаблон
        // роута без значений параметров, поэтому он безопасен.
        "http.request.method", "http.response.status_code", "http.route",
        "url.scheme", "server.address", "server.port",
        "network.protocol.version", "error.type",
        // HTTP — легаси-имена semconv доOTel-1.0, на случай сторонней инструментации
        "http.method", "http.url", "http.status_code", "http.scheme", "http.host",
        // RPC — спаны SignalR-хаба (rpc.method = имя метода хаба, не пользовательские данные)
        "rpc.system", "rpc.service", "rpc.method",
        // Логи: уровень/категория/шаблон сообщения — без значений параметров
        "{OriginalFormat}", "log.level", "category.name", "event.id", "event.name",
        // Custom operational
        "mcp_config_hash", "duration_ms", "tokens_input", "tokens_output",
    ]);

    /// <summary>Подстроки PII-паттернов для дропа (проверяются через Contains по нормализованному имени).</summary>
    private static readonly string[] DropSubstrings =
    [
        "persona", "userid", "username", "ownerid", "ownername",
        "prompt", "content", "body", "message",
        "email", "phone", "address", "apikey", "password", "secret",
        // "text" и "token" — отдельно (точный матч, чтобы не задеть tool_name/tokens_*)
    ];

    /// <summary>Точные имена атрибутов, дропаемых как чистый пользовательский текст.</summary>
    private static readonly HashSet<string> DropExactText = Normalize(["text", "token"]);

    /// <summary>Классифицировать атрибут по его имени.</summary>
    public static PiiAction Classify(string key)
    {
        var norm = Norm(key);

        // 1. Пути хэшируем: и точные имена, и суффикс path (file_path, project.path, FilePath)
        if (HashTags.Contains(norm) || norm.EndsWith("path", StringComparison.Ordinal))
            return PiiAction.Hash;

        // 2. Разрешённые — как есть
        if (KeepTags.Contains(norm))
            return PiiAction.Keep;

        // 3. Явные PII-паттерны
        foreach (var substring in DropSubstrings)
        {
            if (norm.Contains(substring, StringComparison.Ordinal))
                return PiiAction.Drop;
        }

        if (DropExactText.Contains(norm))
            return PiiAction.Drop;

        // 4. Неизвестное — удаляем (safe default)
        return PiiAction.Drop;
    }

    /// <summary>Детерминированный sha256-префикс (8 hex-символов) для корреляции значений.</summary>
    public static string ComputeHash(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes)[..8].ToLowerInvariant();
    }

    /// <summary>
    /// Нормализация имени: нижний регистр без разделителей.
    /// <c>session_id</c>, <c>SessionId</c> и <c>session.id</c> становятся одним ключом.
    /// </summary>
    private static string Norm(string key)
    {
        Span<char> buffer = key.Length <= 128 ? stackalloc char[key.Length] : new char[key.Length];
        var length = 0;
        foreach (var c in key)
        {
            if (c is '_' or '.' or '-') continue;
            buffer[length++] = char.ToLowerInvariant(c);
        }
        return new string(buffer[..length]);
    }

    private static HashSet<string> Normalize(IEnumerable<string> keys)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in keys) set.Add(Norm(key));
        return set;
    }
}
