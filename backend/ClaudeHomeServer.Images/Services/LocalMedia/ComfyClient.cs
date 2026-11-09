using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeHomeServer.Services.Media;

namespace ClaudeHomeServer.Services.Images.LocalMedia;

// Отказ ComfyUI: недоступен, отклонил граф, вернул не то. Текст — для человека и модели
public sealed class ComfyException(string message, Exception? inner = null) : Exception(message, inner);

public sealed record ComfyQueued(string PromptId);

// Файл-выход ноды: SaveImage и SaveVideo оба кладут его в outputs.{node}.images
public sealed record ComfyOutputFile(string FileName, string Subfolder, string Type);

// Latents — файлы SaveLatent (outputs.{node}.latents): служебные, в проект не идут.
// StartedAtMs/FinishedAtMs — метки status.messages по часам ComfyUI (мс): сравнивать их
// можно только друг с другом, не с часами бэкенда. CachedNodes — ноды, взятые из кэша
public sealed record ComfyHistoryEntry(bool Completed, bool Failed, string? Error, IReadOnlyList<ComfyOutputFile> Files,
    IReadOnlyList<ComfyOutputFile> Latents)
{
    public long? StartedAtMs { get; init; }
    public long? FinishedAtMs { get; init; }
    public IReadOnlyList<string> CachedNodes { get; init; } = [];
    // Текстовые выходы нод (PreviewAny: outputs.{node}.text) — партитура ABC у YuE2
    public IReadOnlyList<string> Texts { get; init; } = [];
    // Выходы узла CcsAudioWorker при обучении LoRA (ADR-026): имя весов и статистика прогона
    public JsonArray? CcsLora { get; init; }
    public JsonArray? CcsStats { get; init; }

    // Длительность прогона по меткам ComfyUI; null — меток нет или они несостоятельны
    public double? RunSeconds => StartedAtMs is { } start && FinishedAtMs is { } end && end >= start
        ? (end - start) / 1000.0
        : null;
}

// Элемент очереди ComfyUI: prompt_id, client_id (из extra_data) и множество class_type узлов
// графа. Только типы — текст промптов, имена файлов и входы узлов не читаются и не сохраняются
public sealed record ComfyQueueItem(string PromptId, string? ClientId, IReadOnlyList<string> ClassTypes);

// Снимок очереди: элементы по порядку — идущая первой
public sealed record ComfyQueueState(IReadOnlyList<ComfyQueueItem> Running, IReadOnlyList<ComfyQueueItem> Pending)
{
    public int Length => Running.Count + Pending.Count;

    // Ids для старых потребителей (PositionOf, Contains)
    public IReadOnlyList<string> RunningIds => Running.Select(i => i.PromptId).ToList();
    public IReadOnlyList<string> PendingIds => Pending.Select(i => i.PromptId).ToList();

    // Сколько задач впереди (0 — идёт сейчас); null — задачи в очереди нет
    public int? PositionOf(string promptId)
    {
        if (Running.Any(i => i.PromptId == promptId)) return 0;
        var i = PendingIds.ToList().IndexOf(promptId);
        return i < 0 ? null : Running.Count + i;
    }
}

// Снимок /system_stats ComfyUI: версия и живость. null — ComfyUI недоступен
public sealed record ComfySystemStats(string? Version, bool Alive);

// Снимок маршрута /ccs/stats узла ComfyUI (шаг 31): что загружено в память сейчас (имена
// моделей) и карты узла. null — данных нет (маршрут 404 / сеть / таймаут / не-JSON).
// Ненулевой с Reason="unsupported" — маршрут ответил, но версия != 1 (различимая причина,
// чтобы панель честно сказала «не поддерживает», а не «не сообщает»).
public sealed record ComfyCcsStats(
    IReadOnlyList<string> LoadedModels,
    IReadOnlyList<ComfyGpuInfo> Gpus,
    string? Reason)
{
    // Данные есть: маршрут ответил, версия 1, разобрали (Gpus может быть пусто при pynvml-сбое)
    public bool HasData => Reason is null;
}

// Тонкий HTTP-клиент ComfyUI. Граф приходит только из ComfyWorkflows — здесь его не строят
// и не принимают снаружи
public sealed class ComfyClient(IHttpClientFactory http, IConfiguration config)
{
    public const string HttpClientName = "local-media-comfy";

    // Подпапка input ComfyUI для наших загрузок
    public const string InputFolder = "ccs-local-media";

    // client_id постановки: события прогресса ComfyUI шлёт только сокету с этим clientId
    // (ComfyProgressListener). Свой на каждый процесс: ComfyUI держит на sid один сокет, и
    // общий id дева и боя на одном стенде вытеснял бы сокет соседа — тот молча глох без Close
    // Префикс client_id наших инстансов: чужой прогон с таким префиксом — другой инстанс AI Home
    public const string ClientPrefix = "ccs-local-media-";

    public static readonly string ClientId = ClientPrefix + Guid.NewGuid().ToString("N");

    private HttpClient Client()
    {
        var client = http.CreateClient(HttpClientName);
        client.BaseAddress ??= new Uri(LocalMediaOptions.Read(config).ComfyUrl.TrimEnd('/') + "/");
        return client;
    }

    // Загрузка картинки в input ComfyUI; результат — имя для LoadImage («подпапка/файл»)
    public Task<string> UploadImageAsync(byte[] bytes, string fileName, CancellationToken ct) =>
        UploadInputAsync(bytes, fileName, InputFolder, ct);

    // Загрузка любого входа (видео, звук, латент) через тот же /upload/image — ComfyUI не
    // проверяет формат. subfolder "" — корень input: LoadLatent без VALIDATE_INPUTS видит
    // только файлы корня, подпапку он отвергает на валидации
    public async Task<string> UploadInputAsync(byte[] bytes, string fileName, string subfolder, CancellationToken ct)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "image", fileName);
        form.Add(new StringContent("input"), "type");
        form.Add(new StringContent(subfolder), "subfolder");
        form.Add(new StringContent("true"), "overwrite");

        var json = await SendJsonAsync(c => c.PostAsync("upload/image", form, ct), "загрузка входа", ct);
        var name = StrOrNull(json?["name"]);
        if (string.IsNullOrEmpty(name)) throw new ComfyException("ComfyUI не вернул имя загруженного файла");
        var saved = StrOrNull(json?["subfolder"]) ?? "";
        return saved.Length == 0 ? name : $"{saved}/{name}";
    }

    // preview_method=latent2rgb — ОБЯЗАТЕЛЬНО: без него TAEHV-превью под DynamicVRAM роняет
    // процесс ComfyUI на H3 (README ноды), а процесс общий для всех. front — в голову очереди
    // (штатная возможность ComfyUI: ключ "front" в теле POST /prompt); при false ключа в теле нет
    public async Task<ComfyQueued> QueuePromptAsync(JsonObject graph, CancellationToken ct, bool front = false)
    {
        var body = new JsonObject
        {
            ["prompt"] = graph,
            ["client_id"] = ClientId,
            ["extra_data"] = new JsonObject { ["preview_method"] = "latent2rgb" },
        };
        if (front) body["front"] = true;
        using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        var json = await SendJsonAsync(c => c.PostAsync("prompt", content, ct), "постановка задачи", ct);
        // prompt_id — чужой ответ: не-строка (C1) — считаем «не вернул», не бросаем
        var id = json?["prompt_id"] is JsonValue pid && pid.TryGetValue<string>(out var s) ? s : null;
        if (string.IsNullOrEmpty(id)) throw new ComfyException("ComfyUI не вернул prompt_id");
        return new ComfyQueued(id);
    }

    // null — истории по задаче ещё нет (стоит в очереди или идёт)
    public async Task<ComfyHistoryEntry?> GetHistoryAsync(string promptId, CancellationToken ct)
    {
        var json = await SendJsonAsync(c => c.GetAsync("history/" + Uri.EscapeDataString(promptId), ct),
            "чтение истории", ct);
        return json?[promptId] is JsonObject entry ? ParseHistory(entry) : null;
    }

    internal static ComfyHistoryEntry ParseHistory(JsonObject entry)
    {
        var status = entry["status"] as JsonObject;
        // status/completed — чужой ответ: тип может не совпасть (C1) — GetValue<T>() заменяем на TryGetValue
        var statusStr = status?["status_str"] is JsonValue sv && sv.TryGetValue<string>(out var sstr) ? sstr : null;
        var completed = status?["completed"] is JsonValue cv && cv.TryGetValue<bool>(out var comp) && comp;
        var failed = statusStr == "error";

        string? error = null;
        long? startedAt = null, finishedAt = null;
        var cached = new List<string>();
        if (status?["messages"] is JsonArray messages)
            foreach (var m in messages)
            {
                if (m is not JsonArray { Count: >= 2 } pair) continue;
                var kind = pair[0] is JsonValue k && k.TryGetValue<string>(out var s) ? s : null;
                var data = pair[1] as JsonObject;
                switch (kind)
                {
                    case "execution_start":
                        startedAt = Timestamp(data);
                        break;
                    case "execution_cached" when (data?["nodes"]) is JsonArray nodes:
                        cached.AddRange(nodes.OfType<JsonValue>().Select(n => n.ToString()).Where(n => n.Length > 0));
                        break;
                    case "execution_success" or "execution_interrupted":
                        finishedAt = Timestamp(data);
                        break;
                    case "execution_error":
                        finishedAt = Timestamp(data);
                        if (failed)
                        {
                            var text = data?["exception_message"] is JsonValue ev && ev.TryGetValue<string>(out var t) ? t : null;
                            var node = data?["node_type"] is JsonValue nv && nv.TryGetValue<string>(out var n) ? n : null;
                            var full = $"{node}: {text}".Trim(' ', ':');
                            error = Trim(full, 400);
                            // Маркер воркера (ccs_error=код) не должен потеряться в обрезанном хвосте
                            if (CcsErrorMarker.Match(full) is { Success: true } marker && !error.Contains(marker.Value, StringComparison.Ordinal))
                                error = $"{Trim(full, 400 - marker.Length - 2)} {marker.Value}";
                        }
                        break;
                }
            }

        var files = new List<ComfyOutputFile>();
        var latents = new List<ComfyOutputFile>();
        if (entry["outputs"] is JsonObject outputs)
            foreach (var (_, output) in outputs)
                // audio — SaveAudio* и узел CcsAudioWorker (у него там и MIDI, и модели голоса)
                foreach (var key in new[] { "images", "gifs", "videos", "latents", "audio" })
                    if (output?[key] is JsonArray list)
                        foreach (var item in list)
                        {
                            var name = item?["filename"] is JsonValue fn && fn.TryGetValue<string>(out var n) ? n : null;
                            if (string.IsNullOrEmpty(name)) continue;
                            // temp-выходы — превью, в результат не идут
                            var type = item?["type"] is JsonValue tv && tv.TryGetValue<string>(out var t) ? t : "output";
                            if (type != "output") continue;
                            var subfolder = item?["subfolder"] is JsonValue sf && sf.TryGetValue<string>(out var s) ? s : "";
                            var file = new ComfyOutputFile(name, subfolder, type);
                            (key == "latents" ? latents : files).Add(file);
                        }

        var texts = new List<string>();
        if (entry["outputs"] is JsonObject textOutputs)
            foreach (var (_, output) in textOutputs)
                if (output?["text"] is JsonArray list)
                    texts.AddRange(list.OfType<JsonValue>().Select(v => v.TryGetValue<string>(out var t) ? t : null)
                        .Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t!));

        JsonArray? ccsLora = null, ccsStats = null;
        if (entry["outputs"] is JsonObject ccsOutputs)
            foreach (var (_, output) in ccsOutputs)
            {
                ccsLora ??= (output?["ccs_lora"] as JsonArray)?.DeepClone() as JsonArray;
                ccsStats ??= (output?["ccs_stats"] as JsonArray)?.DeepClone() as JsonArray;
            }

        return new ComfyHistoryEntry(completed && !failed, failed, error ?? (failed ? "ComfyUI завершил задачу ошибкой" : null),
            files, latents)
        {
            StartedAtMs = startedAt,
            FinishedAtMs = finishedAt,
            CachedNodes = cached,
            Texts = texts,
            CcsLora = ccsLora,
            CcsStats = ccsStats,
        };

        // Метка сообщения — миллисекунды эпохи; число бывает и целым, и дробным
        static long? Timestamp(JsonObject? data) =>
            data?["timestamp"] is not JsonValue v ? null
            : v.TryGetValue<long>(out var l) ? l
            : v.TryGetValue<double>(out var d) ? (long)d
            : null;
    }

    // Сообщение сокета: {"type":"progress","data":{"value":5,"max":8,"prompt_id":…}}; чужие задачи и прочие типы
    // пропускаем. Done — задача закончилась (успех, ошибка, прерывание)
    internal static (double? Fraction, bool Done) ParseProgressMessage(string text, string promptId)
    {
        try
        {
            if (JsonNode.Parse(text) is not JsonObject root || root["data"] is not JsonObject data) return (null, false);
            if (data["prompt_id"]?.ToString() != promptId) return (null, false);
            switch (root["type"]?.ToString())
            {
                case "execution_success" or "execution_error" or "execution_interrupted":
                    return (null, true);
                case "progress"
                    when data["value"] is JsonValue v && v.TryGetValue<double>(out var value)
                        && data["max"] is JsonValue m && m.TryGetValue<double>(out var max) && max > 0:
                    return (Math.Clamp(value / max, 0, 1), false);
                default:
                    return (null, false);
            }
        }
        catch (JsonException)
        {
            return (null, false);
        }
    }

    public async Task<byte[]> DownloadAsync(ComfyOutputFile file, CancellationToken ct)
    {
        var query = $"view?filename={Uri.EscapeDataString(file.FileName)}"
            + $"&subfolder={Uri.EscapeDataString(file.Subfolder)}&type={Uri.EscapeDataString(file.Type)}";
        try
        {
            using var response = await Client().GetAsync(query, ct);
            if (!response.IsSuccessStatusCode)
                throw new ComfyException($"ComfyUI не отдал файл результата ({(int)response.StatusCode})");
            return await response.Content.ReadAsByteArrayAsync(ct);
        }
        catch (HttpRequestException ex)
        {
            throw new ComfyException("ComfyUI недоступен", ex);
        }
    }

    // Имена LoRA, которые ComfyUI видит в своём каталоге: GET /models/loras — JSON-массив строк
    public async Task<IReadOnlyList<string>> ListLorasAsync(CancellationToken ct)
    {
        var json = await SendJsonAsync(c => c.GetAsync("models/loras", ct), "чтение списка LoRA", ct);
        return json is JsonArray items
            ? items.Select(i => StrOrNull(i) ?? "").Where(n => n.Length > 0).ToList()
            : [];
    }

    public async Task<ComfyQueueState> GetQueueAsync(CancellationToken ct)
    {
        var json = await SendJsonAsync(c => c.GetAsync("queue", ct), "чтение очереди", ct);
        return new ComfyQueueState(Items(json?["queue_running"]), Items(json?["queue_pending"]));

        // Элемент очереди — [номер, prompt_id, граф, extra_data, …]; ждущие упорядочены по номеру.
        // Из графа берём ТОЛЬКО class_type узлов (текст промптов/входов не читаем), из extra_data — client_id.
        // Разбор устойчив к мусору (C1): чужие клиенты стенда кладут в extra_data/позиции любой
        // JSON-тип, поэтому GetValue<T>() (бросает на несовпадении типа) заменяем на TryGetValue.
        static IReadOnlyList<ComfyQueueItem> Items(JsonNode? node) =>
            node is JsonArray items
                ? items.OfType<JsonArray>()
                    .Where(i => i.Count >= 2)
                    .OrderBy(i => i[0] is JsonValue n0 && n0.TryGetValue<double>(out var num) ? num : 0)
                    .Select(i => new ComfyQueueItem(
                        i[1] is JsonValue n1 && n1.TryGetValue<string>(out var s) ? s : "",
                        i.Count >= 4 && i[3] is JsonObject extra && extra["client_id"] is JsonValue cid && cid.TryGetValue<string>(out var cs) ? cs : null,
                        ClassTypes(i.Count >= 3 ? i[2] : null)))
                    .Where(item => item.PromptId.Length > 0)
                    .ToList()
                : [];

        // Множество class_type узлов графа; null/не-объект — пусто
        static IReadOnlyList<string> ClassTypes(JsonNode? graph)
        {
            if (graph is not JsonObject nodes) return [];
            var types = new List<string>();
            foreach (var (_, node) in nodes)
                if (node is JsonObject n && n["class_type"] is JsonValue v && v.TryGetValue<string>(out var t) && t.Length > 0)
                    types.Add(t);
            return types;
        }
    }

    // GET /system_stats: версия ComfyUI и живость. null — ComfyUI недоступен (ComfyException)
    public async Task<ComfySystemStats?> GetSystemStatsAsync(CancellationToken ct)
    {
        try
        {
            var json = await SendJsonAsync(c => c.GetAsync("system_stats", ct), "чтение system_stats", ct);
            // system_stats — чужой ответ: system может быть не-объектом, comfyui_version — не-строкой (C1)
            var version = json?["system"] is JsonObject sys && sys["comfyui_version"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
            return new ComfySystemStats(version, Alive: true);
        }
        catch (ComfyException)
        {
            return null;
        }
    }

    // GET /ccs/stats узла ComfyUI (шаг 31): «загружено сейчас» (имена моделей) и карты узла.
    // Короткий таймаут (≈2 с): маршрут опрашивает фоновый монитор, а не запрос панели. null —
    // данных нет (маршрут 404 / сеть / таймаут / не-JSON / версия != 1); Reason — различимая
    // причина (unsupported) для пометки в unsupported. Ответ узла НЕДОВЕРЕННЫЙ: тело ≤ 64 КБ,
    // ≤ 16 моделей, ≤ 8 карт, имена ≤ 96 (basename, без управляющих символов) — иначе «мусорный»
    // узел раздувает снимок. Таймаут — CancellationTokenSource, не трогая внешний ct (он может
    // быть бесконечным).
    public async Task<ComfyCcsStats?> GetCcsStatsAsync(CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(2));
        string text;
        try
        {
            using var response = await Client().GetAsync("ccs/stats", cts.Token);
            if (response.StatusCode == HttpStatusCode.NotFound) return null;   // маршрут не установлен
            if (!response.IsSuccessStatusCode) return null;                  // прочие коды — «не сообщает»
            text = await response.Content.ReadAsStringAsync(cts.Token);
        }
        catch (HttpRequestException)
        {
            return null;   // сеть: узел недоступен
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;   // таймаут 2 с
        }

        // Разбор недоверенного тела — страховочный catch: ни одно исключение парсинга (GetValue
        // на мусоре, битый JSON) не доходит до монитора. Отмена внешним ct — пропускаем (монитор
        // сам знает, что его остановили; таймаут 2 с выше уже вернул null).
        try
        {
            // Тело недоверенное: больше 64 КБ — не парсим (JsonNode.Parse на 10 МБ — DoS)
            if (text.Length > CcsMaxBodyChars) return null;
            JsonNode? root;
            try
            {
                root = JsonNode.Parse(text);
            }
            catch (JsonException)
            {
                return null;   // не-JSON
            }
            if (root is not JsonObject obj) return null;

            // Версия контракта: 1 — поддерживаем; 2+ или её отсутствие — «unsupported» (различимая
            // причина, чтобы панель честно сказала «узел не поддерживает», а не «не сообщает»).
            // 404/сеть/не-JSON выше уже вернули null — сюда доходят только разобранные объекты.
            int? version = null;
            if (obj["version"] is JsonValue v && v.TryGetValue<int>(out var ver))
                version = ver;
            if (version is not 1)
                return new ComfyCcsStats([], [], "unsupported");

            return new ComfyCcsStats(
                LoadedModels: CcsModelNames(obj["loadedModels"]),
                Gpus: CcsGpus(obj["gpus"]),
                Reason: null);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return null;   // мусорный/кривой ответ узла — данных нет, монитор жив
        }
    }

    // Имена загруженных моделей (loadedModels[].name): basename, без управляющих символов, ≤ 96.
    // ≤ 16 записей. null/не-массив → пусто.
    private static IReadOnlyList<string> CcsModelNames(JsonNode? node)
    {
        if (node is not JsonArray items) return [];
        var names = new List<string>();
        foreach (var item in items)
        {
            if (names.Count >= CcsMaxModels) break;
            if (item is not JsonObject o) continue;
            var name = CcsName(StrOrNull(o["name"]));
            if (name is not null) names.Add(name);
        }
        return names;
    }

    // Карты узла (gpus[]): index, имя, утилизация, память, температура, мощность. ≤ 8 карт.
    // null (pynvml-сбой) → пусто. Числа — только int/double, иначе null (мусорный узел).
    private static IReadOnlyList<ComfyGpuInfo> CcsGpus(JsonNode? node)
    {
        if (node is not JsonArray items) return [];
        var gpus = new List<ComfyGpuInfo>();
        foreach (var item in items)
        {
            if (gpus.Count >= CcsMaxGpus) break;
            if (item is not JsonObject o) continue;
            gpus.Add(new ComfyGpuInfo(
                Index: o["index"] is JsonValue iv && iv.TryGetValue<int>(out var i) ? i : 0,
                Uuid: "",
                Name: CcsName(StrOrNull(o["name"])) ?? "",
                UtilPerc: IntOrNull(o["utilPerc"]),
                MemUsedMb: IntOrNull(o["memUsedMb"]),
                MemTotalMb: IntOrNull(o["memTotalMb"]),
                TempC: IntOrNull(o["tempC"]),
                PowerW: o["powerW"] is JsonValue pv && pv.TryGetValue<double>(out var pw) ? pw : null,
                PowerLimitW: o["powerLimitW"] is JsonValue plv && plv.TryGetValue<double>(out var plw) ? plw : null));
        }
        return gpus;
    }

    // Число (int) из недоверенного JSON; null/не-число/дробное → null
    private static int? IntOrNull(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<int>(out var n) ? n : null;

    // Строка из недоверенного JSON; null/не-строка (число/объект/массив) → null. GetValue<string>
    // на нестроковом значении бросает InvalidOperationException — мусорный узел ронял бы монитор.
    private static string? StrOrNull(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    // Имя модели/карты из недоверенного ответа: basename (пути ФС не пропускаем), управляющие
    // символы → '_', ≤ 96. Пустое → null.
    private static string? CcsName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        // basename: последний компонент после / или \ (пути к файлам/LoRA наружу не отдаём)
        var name = raw;
        var slash = Math.Max(name.LastIndexOf('/'), name.LastIndexOf('\\'));
        if (slash >= 0) name = name[(slash + 1)..];
        // управляющие символы (C0/C1, табы, переводы строк) → '_'
        var cleaned = new string(name.Select(c => char.IsControl(c) ? '_' : c).ToArray());
        if (cleaned.Length == 0) return null;
        return cleaned.Length > CcsMaxName ? cleaned[..CcsMaxName] : cleaned;
    }

    // Потолки недоверенного ответа /ccs/stats: тело 64 КБ, 16 моделей, 8 карт, имя 96
    private const int CcsMaxBodyChars = 64 * 1024;
    private const int CcsMaxModels = 16;
    private const int CcsMaxGpus = 8;
    private const int CcsMaxName = 96;

    // Снять задачу из ожидающих: POST /queue {"delete":[id]}. Идущую задачу ComfyUI так не
    // снимает — для неё InterruptAsync
    public Task DeletePendingAsync(string promptId, CancellationToken ct) =>
        PostAsync("queue", new JsonObject { ["delete"] = new JsonArray(promptId) }, "снятие задачи", ct);

    // Прервать идущую задачу: POST /interrupt {"prompt_id":id}. С prompt_id ComfyUI (проверено на
    // 0.37) прерывает прогон, только если идёт именно он, — чужой не заденет. Без prompt_id не
    // звать: так прерывается любой текущий прогон
    public Task InterruptAsync(string promptId, CancellationToken ct) =>
        PostAsync("interrupt", new JsonObject { ["prompt_id"] = promptId }, "прерывание задачи", ct);

    private async Task PostAsync(string path, JsonObject body, string what, CancellationToken ct)
    {
        using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        try
        {
            using var response = await Client().PostAsync(path, content, ct);
            if (!response.IsSuccessStatusCode)
                throw new ComfyException($"ComfyUI отклонил {what} ({(int)response.StatusCode})");
        }
        catch (HttpRequestException ex)
        {
            throw new ComfyException($"ComfyUI недоступен ({what})", ex);
        }
    }

    private async Task<JsonNode?> SendJsonAsync(Func<HttpClient, Task<HttpResponseMessage>> send, string what,
        CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await send(Client());
        }
        catch (HttpRequestException ex)
        {
            throw new ComfyException($"ComfyUI недоступен ({what})", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new ComfyException($"ComfyUI не ответил вовремя ({what})", ex);
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                throw new ComfyException($"ComfyUI отклонил запрос ({what}, {(int)response.StatusCode}): {Describe(text)}");
            try
            {
                return JsonNode.Parse(text);
            }
            catch (JsonException ex)
            {
                throw new ComfyException($"ComfyUI вернул не JSON ({what})", ex);
            }
        }
    }

    // Отказ /prompt: {"error":{"message":…},"node_errors":{…}} — в текст идут сообщение и
    // первые ошибки нод, но не весь ответ (он большой и несёт граф)
    private static string Describe(string body)
    {
        try
        {
            var json = JsonNode.Parse(body);
            var message = StrOrNull(json?["error"]?["message"]) ?? "";
            if (json?["node_errors"] is JsonObject nodes)
                foreach (var (node, value) in nodes.Take(3))
                    if (value?["errors"] is JsonArray errors && errors.FirstOrDefault() is { } first)
                        message += $"; {node}: {StrOrNull(first["message"])} {StrOrNull(first["details"])}";
            return Trim(message.Length > 0 ? message : body, 500);
        }
        catch (JsonException)
        {
            return Trim(body, 300);
        }
    }

    private static readonly System.Text.RegularExpressions.Regex CcsErrorMarker = new(@"ccs_error=[a-z_]+", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static string Trim(string text, int max) => text.Length <= max ? text : text[..max] + "…";
}
