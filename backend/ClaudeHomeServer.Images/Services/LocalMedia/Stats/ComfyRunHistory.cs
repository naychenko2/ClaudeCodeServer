using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeHomeServer.Services.Media;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ClaudeHomeServer.Services.Images.LocalMedia;

// Журнал истории медиа-генераций (шаг 2): единственный писатель. События enqueued/started/
// finished — в data/local-media-history/YYYY-MM-DD.jsonl (строки v:1), дозапись под lock.
// Читатель (шаг 3) склеивает события по promptId, битую строку пропускает. Исходы: completed/
// cancelled/failed (+error, недоверенный) / interrupted (был enqueued, нет finished, после
// рестарта не в очереди). Ожидание считается по часам бэкенда, длительность — по меткам ComfyUI.
// Исключение не выходит наружу: журнал — наблюдение, не критичный путь (BackgroundService не роняет)
public sealed class ComfyRunHistory
{
    // Каталог журнала: data/local-media-history (рядом с остальными сторами)
    public const string DirName = "local-media-history";
    // Потолок текста ошибки в журнале: 500 символов (недоверенный текст ComfyUI)
    public const int MaxErrorLength = 500;

    // Опции сериализации: Web (camelCase), null-поля пишутся как null (правило модуля)
    private static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web);

    private readonly IServiceProvider _services;
    private readonly TimeProvider _time;
    private readonly ILogger<ComfyRunHistory>? _log;
    private readonly string _dir;
    private readonly object _lock = new();

    public ComfyRunHistory(
        IServiceProvider services,
        TimeProvider? time = null,
        IConfiguration? config = null,
        ILogger<ComfyRunHistory>? log = null)
    {
        _services = services;
        _time = time ?? TimeProvider.System;
        _log = log;
        // Путь — только от DataPath, как у соседних сторов: рядом с исполняемым файлом потерялся бы
        var dataPath = config?["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json");
        _dir = Path.Combine(Path.GetDirectoryName(dataPath)!, DirName);
    }

    // Каталог журнала (для ротации LocalMediaCleanup и тестов)
    internal string Dir => _dir;

    // Карта из снимка /ccs/stats монитора; null — монитора нет или не сообщает
    private ComfyCcsStats? Card() => _services.GetService<ComfyQueueMonitor>()?.Ccs;

    // Индекс в памяти (шаг 3): необязательный наблюдатель; без него журнал пишется, а индекс
    // перечитает файл при старте
    private IMediaHistoryEventSink? Sink() => _services.GetService<IMediaHistoryEventSink>();

    // Событие постановки: модель/промпт/параметры из details
    public void Enqueued(string promptId, LocalJobOrigin? origin, LocalRunDetails? details)
    {
        Write("enqueued", promptId, origin,
            model: details?.Model, prompt: details?.Prompt, @params: details?.Params,
            card: null, outcome: null, runSeconds: null, error: null);
    }

    // Событие старта: карта из снимка /ccs/stats на момент started
    public void Started(string promptId, LocalJobOrigin? origin)
    {
        Write("started", promptId, origin,
            model: null, prompt: null, @params: null,
            card: Card(), outcome: null, runSeconds: null, error: null);
    }

    // Событие завершения: исход, длительность по меткам ComfyUI, ошибка (недоверенная, ≤ 500)
    public void Finished(string promptId, LocalJobOrigin? origin, string outcome, int? runSeconds, string? error)
    {
        Write("finished", promptId, origin,
            model: null, prompt: null, @params: null,
            card: null, outcome: outcome, runSeconds: runSeconds,
            error: error is { Length: > MaxErrorLength } e ? e[..MaxErrorLength] : error);
    }

    // Запись строки v:1 в файл дня; дозапись под lock, каталог создаётся при первой записи.
    // ts считается один раз: строка и событие индекса несут одно и то же время
    private void Write(string type, string promptId, LocalJobOrigin? origin,
        string? model, string? prompt, LocalRunParams? @params,
        ComfyCcsStats? card, string? outcome, int? runSeconds, string? error)
    {
        var ts = _time.GetUtcNow();
        try
        {
            var line = BuildLine(ts, type, promptId, origin, model, prompt, @params, card, outcome, runSeconds, error);
            lock (_lock)
            {
                Directory.CreateDirectory(_dir);
                var file = Path.Combine(_dir, ts.UtcDateTime.ToString("yyyy-MM-dd") + ".jsonl");
                File.AppendAllText(file, line + "\n");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Сбой записи (диск, права) — Debug: запись редкая, не на каждый тик, троттлинг не нужен
            _log?.LogDebug(ex, "Журнал истории медиа-генераций: сбой записи — {Error}", ex.Message);
        }
        // Индекс в памяти (шаг 3): новое событие дописывается без перечитывания файла. Сбой
        // наблюдателя не роняет запись: журнал на диске уже лежит
        try
        {
            Sink()?.Append(new MediaHistoryEvent(promptId, type, ts, origin, model, prompt,
                @params is null ? null : (JsonObject)JsonSerializer.SerializeToNode(@params, Wire)!,
                card is null ? null : (JsonObject)JsonSerializer.SerializeToNode(card, Wire)!,
                outcome, runSeconds, error));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log?.LogDebug(ex, "Журнал истории медиа-генераций: сбой уведомления индекса — {Error}", ex.Message);
        }
    }

    // Строка v:1: ts (ISO-8601 UTC), id, origin, и поля события. null-поля пишутся как null
    private string BuildLine(DateTimeOffset ts, string type, string promptId, LocalJobOrigin? origin,
        string? model, string? prompt, LocalRunParams? @params,
        ComfyCcsStats? card, string? outcome, int? runSeconds, string? error)
    {
        var line = new JsonObject
        {
            ["v"] = 1,
            ["t"] = type,
            ["ts"] = ts.UtcDateTime.ToString("O"),
            ["id"] = promptId,
            ["origin"] = origin is null ? null : (JsonNode)JsonSerializer.SerializeToNode(origin, Wire)!,
            ["model"] = model,
            ["prompt"] = prompt,
            ["params"] = @params is null ? null : (JsonNode)JsonSerializer.SerializeToNode(@params, Wire)!,
            ["card"] = card is null ? null : (JsonNode)JsonSerializer.SerializeToNode(card, Wire)!,
            ["outcome"] = outcome,
            ["runSeconds"] = runSeconds,
            ["error"] = error,
        };
        return line.ToJsonString();
    }
}
