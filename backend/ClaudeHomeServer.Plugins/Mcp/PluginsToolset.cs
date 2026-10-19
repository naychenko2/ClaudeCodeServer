using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Mcp.Http;
using Microsoft.Extensions.Logging;

namespace ClaudeHomeServer.Services.Plugins.Mcp;

/// <summary>
/// MCP-сервер plugins (ADR-031, шаг 4.1, пачка D «создание плагина по запросу»): агент собирает
/// плагин build-it-yourself в черновик владельца <c>data/plugins/{owner}/_drafts/{slug}/</c>
/// ТОЛЬКО через этот тулсет (встроенные Write/Edit не годятся: в контейнере каталог не смонтирован
/// на запись, у local-владельцев Write пишет куда угодно). Инструментов установки/согласия/выдачи
/// прав у агента нет по построению — принять и выдать права может только человек (шаг 4.3).
///
/// Маршрут — <c>POST /mcp/plugins/{sessionId}</c>: хвост несёт чат, по нему тулсет находит проект
/// (для проверки локальности) и владельца. Владелец — из claim <c>sub</c> сервисного JWT
/// (<see cref="McpToolCallContext.OwnerId"/>), не из аргументов; черновики владельца A недоступны
/// владельцу B (изоляция по OwnerId).
///
/// ИНВАРИАНТ состава: tools/list (5 инструментов) не зависит от флага, хода и сессии
/// (McpToolsetStabilityTests). Ограничения проверяет бэкенд на вызове: флаг
/// <c>plugins-build-yourself</c>, выключенная подсистема, локальный проект (ProjectCapabilities) —
/// честный отказ с причиной, не исключение.
/// </summary>
public sealed partial class PluginsToolset : IMcpParameterizedToolset
{
    public const string BuildYourselfFlag = "plugins-build-yourself";
    public const string SubsystemKey = "plugins";

    // Якорь черновика в ленте чата (ADR-019 §2, шаг 4.5a): запись module_record с
    // module = "plugins" (ключ подсистемы) и recordType = "plugin_draft"; data — только
    // { slug }, содержимое карточки фронт грузит по REST.
    public const string ModuleKey = SubsystemKey;
    public const string RecordDraft = "plugin_draft";

    private readonly IMcpSessionAccessor _sessions;
    private readonly IProjectManager _projects;
    private readonly PluginDraftService _drafts;
    private readonly IFeatureFlagGate? _flags;
    private readonly IConfiguration? _config;
    // Лента чата (Core-шов, адаптер ChatFeed в Main): якорь черновика пишет сюда.
    // Необязательный параметр: без шва (тесты, выключенный адаптер) записи нет, ошибки нет.
    private readonly IChatFeed? _feed;
    private readonly ILogger<PluginsToolset>? _log;

    public PluginsToolset(
        IMcpSessionAccessor sessions,
        IProjectManager projects,
        PluginDraftService drafts,
        IFeatureFlagGate? flags = null,
        IConfiguration? config = null,
        IChatFeed? feed = null,
        ILogger<PluginsToolset>? log = null)
    {
        _sessions = sessions;
        _projects = projects;
        _drafts = drafts;
        _flags = flags;
        _config = config;
        _feed = feed;
        _log = log;
    }

    public string Name => ServerName;
    public string Version => "1.0.0";

    // Состав фиксирован (5 инструментов): не зависит от флага, хода и сессии.
    public IReadOnlyList<McpToolSchema> ToolsFor(McpToolCallContext context) => Schemas;

    public async Task<McpToolCallResult> CallAsync(string tool, JsonObject arguments,
        McpToolCallContext context, CancellationToken ct)
    {
        // Хвост маршрута = id сессии; по ней — проект (для проверки локальности).
        if (!TryResolve(context, out var project, out var resolveError))
            return Deny(resolveError);

        // Выключенная подсистема (belt-and-suspenders: обычно сервер просто не в конфиге).
        if (_config is not null && !SubsystemGate.IsEnabled(_config, SubsystemKey))
            return Deny("Подсистема плагинов выключена");

        // Флаг владельца: выключен — отказ с причиной.
        if (_flags is not null && !_flags.IsEnabled(context.OwnerId, BuildYourselfFlag))
            return Deny("Функция «Собрать плагин» выключена для вашего аккаунта");

        // Локальный проект (MVP): плагины выключены (каталог и секреты на устройство не едут).
        var plugins = ProjectCapabilities.PluginsFor(project);
        if (!plugins.Available)
            return Deny(plugins.Reason ?? "Плагины недоступны");

        if (Schemas.All(s => s.Name != tool))
            return Deny($"Инструмент {tool} недоступен на этом сервере.");

        var slug = Str(arguments, "slug");
        if (slug is null)
            return Deny("Не указан slug плагина.");

        return tool switch
        {
            ToolCreate => await Create(context.OwnerId, slug, context.RouteTail, ct),
            ToolWriteFile => WriteFile(arguments, context.OwnerId, slug),
            ToolValidate => Validate(context.OwnerId, slug),
            ToolPreview => Preview(context.OwnerId, slug),
            ToolDeleteFile => Op(_drafts.DeleteFile(context.OwnerId, slug, Str(arguments, "path") ?? "")),
            _ => Deny($"Неизвестный инструмент: {tool}"),
        };
    }

    // ── draft_create ──────────────────────────────────────────────────────────────
    // Создать черновик из шаблона и дописать якорь в ленту чата-вызывателя: карточка
    // черновика (шаг 4.5a). Запись идемпотентна: не больше одной на (сессию, slug) —
    // повторный create (после reject/restore) дубль не дописывает, дальнейшие изменения
    // карточка получает по событию plugin_draft_changed. Без сессии-вызывателя — записи
    // нет и ошибки нет. Сбой ленты не роняет создание: черновик уже на диске.
    private async Task<McpToolCallResult> Create(string ownerId, string slug, string? sessionId, CancellationToken ct)
    {
        var result = _drafts.Create(ownerId, slug);
        if (result.Ok)
            await AppendDraftRecordAsync(sessionId, slug, ct);
        return Op(result);
    }

    private async Task AppendDraftRecordAsync(string? sessionId, string slug, CancellationToken ct)
    {
        if (_feed is null || sessionId is null) return;
        try
        {
            // Замок на пару (сессия, slug): проверка HasRecordAsync и запись AppendRecordAsync
            // атомарны относительно других draft_create той же пары (M3, гонка check-then-act).
            // Ключ — (sessionId, slug) с NUL-разделителем; замок общий на процесс (см. RecordLocks).
            using (await RecordLocks.AcquireAsync(sessionId + "\u0000" + slug, ct))
            {
                if (await _feed.HasRecordAsync(sessionId, ModuleKey, RecordDraft,
                    data => data.TryGetProperty("slug", out var s) && s.GetString() == slug, ct))
                    return;
                await _feed.AppendRecordAsync(sessionId, new StoredModuleRecord
                {
                    Module = ModuleKey,
                    RecordType = RecordDraft,
                    Data = JsonSerializer.SerializeToElement(new { slug }),
                    Fallback = $"Плагин: {slug}",
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                }, ct);
            }
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "Плагины: запись черновика {Slug} не легла в ленту чата {SessionId}", slug, sessionId);
        }
    }

    // ── Замок идемпотентности записи в ленту (M3, check-then-act) ─────────────────
    // Два параллельных draft_create одной пары (сессия, slug) без замка оба проходят
    // HasRecordAsync (записи ещё нет) и пишут по записи. Замок на пару делает проверку
    // и запись атомарными. Общий на ПРОЦЕСС (static): тулсет может быть не синглтоном,
    // и экземплярный замок был бы бесполезен. Семафор живёт, пока есть держатель или
    // ожидающий (счётчик ссылок), и удаляется, когда последний отпустил — словарь не
    // растёт вечно. Примитив по образцу KeyedLocks (ImageEditor), но локальный:
    // KeyedLocks живёт в чужой вертикали, а граница вертикалей default-deny.
    private sealed class RecordLocks
    {
        private sealed class Entry
        {
            public readonly SemaphoreSlim Gate = new(1, 1);
            public int Refs;
        }

        private static readonly object Sync = new();
        private static readonly Dictionary<string, Entry> Entries = new();

        public static async Task<IDisposable> AcquireAsync(string key, CancellationToken ct = default)
        {
            Entry entry;
            lock (Sync)
            {
                if (!Entries.TryGetValue(key, out entry!)) Entries[key] = entry = new Entry();
                entry.Refs++;
            }
            try
            {
                await entry.Gate.WaitAsync(ct);
            }
            catch
            {
                Unref(key, entry);
                throw;
            }
            return new Releaser(key, entry);
        }

        private static void Unref(string key, Entry entry)
        {
            lock (Sync)
            {
                if (--entry.Refs == 0) Entries.Remove(key);
            }
        }

        private sealed class Releaser(string key, Entry entry) : IDisposable
        {
            private int _released;
            public void Dispose()
            {
                if (Interlocked.Exchange(ref _released, 1) != 0) return;
                entry.Gate.Release();
                Unref(key, entry);
            }
        }
    }

    // ── draft_write_file ───────────────────────────────────────────────────────

    private McpToolCallResult WriteFile(JsonObject args, string ownerId, string slug)
    {
        var path = Str(args, "path");
        if (path is null) return Deny("Не указан path — относительный путь файла в черновике.");
        var content = Str(args, "content") ?? "";
        var result = _drafts.WriteFile(ownerId, slug, path, System.Text.Encoding.UTF8.GetBytes(content));
        return Op(result);
    }

    // ── draft_validate ─────────────────────────────────────────────────────────

    private McpToolCallResult Validate(string ownerId, string slug)
    {
        PluginPackageValidationResult validation;
        try { validation = _drafts.Validate(ownerId, slug); }
        catch (Exception e) when (e is FileNotFoundException or ArgumentException)
        { return Deny(e.Message); }
        return Json(new
        {
            valid = validation.IsValid,
            problems = validation.Problems.Select(p => new { code = p.Code, message = p.Message }),
            note = validation.IsValid
                ? "Черновик валиден: готов к установке (установит человек)."
                : "Исправьте проблемы и проверьте снова.",
        });
    }

    // ── draft_preview ──────────────────────────────────────────────────────────

    private McpToolCallResult Preview(string ownerId, string slug)
    {
        PluginDraftPreview p;
        try { p = _drafts.Preview(ownerId, slug); }
        catch (Exception e) when (e is FileNotFoundException or ArgumentException)
        { return Deny(e.Message); }
        return Json(new
        {
            name = p.Name,
            version = p.Version,
            contributes = new
            {
                panels = p.Panels.Select(x => new { x.Id, x.Slot, x.Title }),
                commands = p.Commands,
                skills = p.Skills,
            },
            permissions = p.RequestedPermissions,
            secrets = p.Secrets.Select(s => new { s.Name, required = s.Required }),
            files = p.Files.Select(f => new { f.Path, isDir = f.IsDir, size = f.Size }),
            validation = new { p.Validation.IsValid, problems = p.Validation.Problems.Select(x => new { x.Code, x.Message }) },
            note = "Сводка «что будет после установки». Секреты — только имена и признак обязательности; "
                + "установит и выдаст права человек.",
        });
    }

    // ── Резолв хвоста: сессия → проект ─────────────────────────────────────────

    // Хвост = id сессии; по ней (с проверкой владельца) — проект. Чужая/несуществующая сессия
    // неотличима от несуществующей: fail-closed.
    private bool TryResolve(McpToolCallContext context, out Project? project, out string error)
    {
        project = null;
        error = "Чат не найден — инструменты plugins недоступны.";
        var tail = context.RouteTail;
        if (tail is null || tail.Length is < 1 or > 128
            || !tail.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            return false;
        var session = _sessions.GetOwned(tail, context.OwnerId);
        if (session is null) return false;
        if (session.ProjectId is { } projectId)
            project = _projects.GetById(projectId);
        return true;
    }

    // ── Аргументы и ответы ─────────────────────────────────────────────────────

    private static McpToolCallResult Op(PluginDraftOpResult result) =>
        result.Ok ? new McpToolCallResult(result.Message ?? "Готово")
                   : new McpToolCallResult($"{result.Message} (код: {result.Code})", IsError: true);

    private static McpToolCallResult Json<T>(T value) =>
        new(JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }));

    private static McpToolCallResult Deny(string text) => new(text, IsError: true);

    private static string? Str(JsonObject args, string name) =>
        args[name] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;
}
