using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudeHomeServer.Services.Images.LocalMedia;

// Отказ ComfyUI: недоступен, отклонил граф, вернул не то. Текст — для человека и модели
public sealed class ComfyException(string message, Exception? inner = null) : Exception(message, inner);

public sealed record ComfyQueued(string PromptId);

// Файл-выход ноды: SaveImage и SaveVideo оба кладут его в outputs.{node}.images
public sealed record ComfyOutputFile(string FileName, string Subfolder, string Type);

// Latents — файлы SaveLatent (outputs.{node}.latents): служебные, в проект не идут
public sealed record ComfyHistoryEntry(bool Completed, bool Failed, string? Error, IReadOnlyList<ComfyOutputFile> Files,
    IReadOnlyList<ComfyOutputFile> Latents);

// Снимок очереди: id задач по порядку — идущая первой
public sealed record ComfyQueueState(IReadOnlyList<string> Running, IReadOnlyList<string> Pending)
{
    public int Length => Running.Count + Pending.Count;

    // Сколько задач впереди (0 — идёт сейчас); null — задачи в очереди нет
    public int? PositionOf(string promptId)
    {
        if (Running.Contains(promptId)) return 0;
        var i = Pending.ToList().IndexOf(promptId);
        return i < 0 ? null : Running.Count + i;
    }
}

// Тонкий HTTP-клиент ComfyUI. Граф приходит только из ComfyWorkflows — здесь его не строят
// и не принимают снаружи
public sealed class ComfyClient(IHttpClientFactory http, IConfiguration config)
{
    public const string HttpClientName = "local-media-comfy";

    // Подпапка input ComfyUI для наших загрузок
    public const string InputFolder = "ccs-local-media";

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
        var name = json?["name"]?.GetValue<string>();
        if (string.IsNullOrEmpty(name)) throw new ComfyException("ComfyUI не вернул имя загруженного файла");
        var saved = json?["subfolder"]?.GetValue<string>() ?? "";
        return saved.Length == 0 ? name : $"{saved}/{name}";
    }

    // preview_method=latent2rgb — ОБЯЗАТЕЛЬНО: без него TAEHV-превью под DynamicVRAM роняет
    // процесс ComfyUI на H3 (README ноды), а процесс общий для всех
    public async Task<ComfyQueued> QueuePromptAsync(JsonObject graph, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["prompt"] = graph,
            ["client_id"] = "ccs-local-media",
            ["extra_data"] = new JsonObject { ["preview_method"] = "latent2rgb" },
        };
        using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        var json = await SendJsonAsync(c => c.PostAsync("prompt", content, ct), "постановка задачи", ct);
        var id = json?["prompt_id"]?.GetValue<string>();
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
        var statusStr = status?["status_str"]?.GetValue<string>();
        var completed = status?["completed"]?.GetValue<bool>() == true;
        var failed = statusStr == "error";

        string? error = null;
        if (failed && status?["messages"] is JsonArray messages)
            foreach (var m in messages)
                if (m is JsonArray { Count: >= 2 } pair && pair[0]?.GetValue<string>() == "execution_error")
                {
                    var text = pair[1]?["exception_message"]?.GetValue<string>();
                    var node = pair[1]?["node_type"]?.GetValue<string>();
                    error = Trim($"{node}: {text}".Trim(' ', ':'), 400);
                }

        var files = new List<ComfyOutputFile>();
        var latents = new List<ComfyOutputFile>();
        if (entry["outputs"] is JsonObject outputs)
            foreach (var (_, output) in outputs)
                foreach (var key in new[] { "images", "gifs", "videos", "latents" })
                    if (output?[key] is JsonArray list)
                        foreach (var item in list)
                        {
                            var name = item?["filename"]?.GetValue<string>();
                            if (string.IsNullOrEmpty(name)) continue;
                            // temp-выходы — превью, в результат не идут
                            var type = item?["type"]?.GetValue<string>() ?? "output";
                            if (type != "output") continue;
                            var file = new ComfyOutputFile(name, item?["subfolder"]?.GetValue<string>() ?? "", type);
                            (key == "latents" ? latents : files).Add(file);
                        }

        return new ComfyHistoryEntry(completed && !failed, failed, error ?? (failed ? "ComfyUI завершил задачу ошибкой" : null),
            files, latents);
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

    public async Task<ComfyQueueState> GetQueueAsync(CancellationToken ct)
    {
        var json = await SendJsonAsync(c => c.GetAsync("queue", ct), "чтение очереди", ct);
        return new ComfyQueueState(Ids(json?["queue_running"]), Ids(json?["queue_pending"]));

        // Элемент очереди — [номер, prompt_id, граф, …]; ждущие упорядочены по номеру
        static IReadOnlyList<string> Ids(JsonNode? node) =>
            node is JsonArray items
                ? items.OfType<JsonArray>()
                    .Where(i => i.Count >= 2)
                    .OrderBy(i => i[0]?.GetValue<double>() ?? 0)
                    .Select(i => i[1]?.GetValue<string>() ?? "")
                    .Where(id => id.Length > 0)
                    .ToList()
                : [];
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
            var message = json?["error"]?["message"]?.GetValue<string>() ?? "";
            if (json?["node_errors"] is JsonObject nodes)
                foreach (var (node, value) in nodes.Take(3))
                    if (value?["errors"] is JsonArray errors && errors.FirstOrDefault() is { } first)
                        message += $"; {node}: {first["message"]?.GetValue<string>()} {first["details"]?.GetValue<string>()}";
            return Trim(message.Length > 0 ? message : body, 500);
        }
        catch (JsonException)
        {
            return Trim(body, 300);
        }
    }

    private static string Trim(string text, int max) => text.Length <= max ? text : text[..max] + "…";
}
