using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using ClaudeHomeServer.Services.Images.LocalMedia;

namespace ClaudeHomeServer.Tests.Helpers;

// Фейковый ComfyUI: очередь, история, загрузки и выдача файлов в памяти. В живую очередь
// стенда тесты не ходят никогда — там идут чужие прогоны
public sealed class FakeComfy : HttpMessageHandler
{
    public List<string> Running { get; } = [];
    public List<string> Pending { get; } = [];
    public Dictionary<string, JsonObject> History { get; } = [];
    public Dictionary<string, byte[]> Files { get; } = [];
    public List<JsonObject> Prompts { get; } = [];
    public List<string> Uploads { get; } = [];
    // Куда легла загрузка: «подпапка/файл», у корня input — просто файл
    public List<string> UploadPaths { get; } = [];
    public Dictionary<string, byte[]> UploadedBytes { get; } = [];
    public List<string> Requests { get; } = [];
    public bool Down { get; set; }
    public string? RejectPrompt { get; set; }

    private int _promptSeq;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var path = request.RequestUri!.PathAndQuery;
        Requests.Add($"{request.Method} {path}");
        if (Down) throw new HttpRequestException("connection refused");

        if (request.Method == HttpMethod.Get && path == "/queue")
            return Json(new JsonObject
            {
                ["queue_running"] = new JsonArray([.. Running.Select((id, i) => (JsonNode)new JsonArray(i, id, new JsonObject()))]),
                ["queue_pending"] = new JsonArray([.. Pending.Select((id, i) => (JsonNode)new JsonArray(100 + i, id, new JsonObject()))]),
            });

        if (request.Method == HttpMethod.Post && path == "/prompt")
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!.AsObject();
            Prompts.Add(body);
            if (RejectPrompt is not null)
                return new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent(RejectPrompt, Encoding.UTF8, "application/json"),
                };
            var id = $"prompt-{++_promptSeq}";
            Pending.Add(id);
            return Json(new JsonObject { ["prompt_id"] = id, ["number"] = _promptSeq });
        }

        if (request.Method == HttpMethod.Post && path == "/upload/image")
        {
            var form = (MultipartFormDataContent)request.Content!;
            var file = form.First(c => c.Headers.ContentDisposition?.Name?.Trim('"') == "image");
            var name = file.Headers.ContentDisposition!.FileName!.Trim('"');
            var sub = form.FirstOrDefault(c => c.Headers.ContentDisposition?.Name?.Trim('"') == "subfolder") is { } part
                ? await part.ReadAsStringAsync(ct)
                : "";
            Uploads.Add(name);
            var at = sub.Length == 0 ? name : $"{sub}/{name}";
            UploadPaths.Add(at);
            UploadedBytes[at] = await file.ReadAsByteArrayAsync(ct);
            return Json(new JsonObject { ["name"] = name, ["subfolder"] = sub, ["type"] = "input" });
        }

        if (request.Method == HttpMethod.Get && path.StartsWith("/history/", StringComparison.Ordinal))
        {
            var id = Uri.UnescapeDataString(path["/history/".Length..]);
            return Json(History.TryGetValue(id, out var entry) ? new JsonObject { [id] = entry.DeepClone() } : new JsonObject());
        }

        if (request.Method == HttpMethod.Get && path.StartsWith("/view?", StringComparison.Ordinal))
        {
            var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query);
            var key = $"{query["subfolder"]}/{query["filename"]}";
            return Files.TryGetValue(key, out var bytes)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }
                : new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    // Задача закончилась успешно: выходы SaveImage/SaveVideo (видео ComfyUI тоже кладёт в images)
    public void Complete(string promptId, params (string Name, byte[] Bytes)[] outputs)
    {
        Pending.Remove(promptId);
        Running.Remove(promptId);
        foreach (var (name, bytes) in outputs) Files[$"ccs-local-media/{name}"] = bytes;
        History[promptId] = new JsonObject
        {
            ["status"] = new JsonObject { ["status_str"] = "success", ["completed"] = true, ["messages"] = new JsonArray() },
            ["outputs"] = new JsonObject
            {
                ["save"] = new JsonObject
                {
                    ["images"] = new JsonArray([.. outputs.Select(o => (JsonNode)new JsonObject
                    {
                        ["filename"] = o.Name, ["subfolder"] = "ccs-local-media", ["type"] = "output",
                    })]),
                },
            },
        };
    }

    // Видеозадача закончилась: mp4 в images у SaveVideo и два латента у SaveLatent (как у t2v/i2v)
    public void CompleteVideo(string promptId, string jobId, byte[] mp4, bool withLatents = true)
    {
        Complete(promptId, ($"{jobId}_00001_.mp4", mp4));
        if (!withLatents) return;
        var outputs = History[promptId]["outputs"]!.AsObject();
        foreach (var (node, kind) in new[] { ("lat_save_v", "video"), ("lat_save_a", "audio") })
        {
            var name = $"{jobId}_{kind}_00001_.latent";
            Files[$"ccs-local-media/latents/{name}"] = Encoding.ASCII.GetBytes($"latent-{kind}-{jobId}");
            outputs[node] = new JsonObject
            {
                ["latents"] = new JsonArray(new JsonObject
                {
                    ["filename"] = name, ["subfolder"] = "ccs-local-media/latents", ["type"] = "output",
                }),
            };
        }
    }

    public void Fail(string promptId, string message)
    {
        Pending.Remove(promptId);
        Running.Remove(promptId);
        History[promptId] = new JsonObject
        {
            ["status"] = new JsonObject
            {
                ["status_str"] = "error",
                ["completed"] = false,
                ["messages"] = new JsonArray
                {
                    new JsonArray("execution_start", new JsonObject()),
                    new JsonArray("execution_error", new JsonObject
                    {
                        ["node_type"] = "KSampler", ["exception_message"] = message,
                    }),
                },
            },
            ["outputs"] = new JsonObject(),
        };
    }

    private static HttpResponseMessage Json(JsonNode body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
}

public sealed class FakeComfyFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
}

// Шов «папка проекта»: корни по проектам и журнал уведомлений
public sealed class FakeProjectAccess : ILocalMediaProjectAccess
{
    public Dictionary<(string Owner, string Project), string> Roots { get; } = [];
    public List<string> Notified { get; } = [];

    public string? ResolveRoot(string ownerId, string projectId) =>
        Roots.TryGetValue((ownerId, projectId), out var root) ? root : null;

    public void NotifyWritten(string root, string relativePath) => Notified.Add(relativePath);
}

public static class LocalMediaTestImages
{
    // Минимальный PNG: сигнатура + IHDR (размеры читает ImageDimensions)
    public static byte[] Png(int width, int height)
    {
        var bytes = new List<byte> { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R' };
        bytes.AddRange(BigEndian(width));
        bytes.AddRange(BigEndian(height));
        bytes.AddRange(new byte[] { 8, 6, 0, 0, 0, 0, 0, 0, 0 });
        return [.. bytes];

        static byte[] BigEndian(int v) => [(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v];
    }

    // Минимальный mp4: ftyp + moov (mvhd, trak с tkhd и hdlr vide, звуковая дорожка) + mdat —
    // ровно то, что читает MediaProbe
    public static byte[] Mp4(int width, int height, double seconds)
    {
        const uint timescale = 1000;
        var mvhd = new byte[100];
        Put(mvhd, 12, timescale);
        Put(mvhd, 16, (uint)Math.Round(seconds * timescale));
        var tkhd = new byte[84];
        Put(tkhd, 76, (uint)width << 16);
        Put(tkhd, 80, (uint)height << 16);
        var audioTkhd = new byte[84];
        return
        [
            .. Box("ftyp", [.. "isom"u8.ToArray(), 0, 0, 2, 0, .. "isomavc1"u8.ToArray()]),
            .. Box("moov",
            [
                .. Box("mvhd", mvhd),
                .. Box("trak", [.. Box("tkhd", audioTkhd), .. Box("mdia", Box("hdlr", Handler("soun")))]),
                .. Box("trak", [.. Box("tkhd", tkhd), .. Box("mdia", Box("hdlr", Handler("vide")))]),
            ]),
            .. Box("mdat", new byte[64]),
        ];

        static byte[] Handler(string type) => [0, 0, 0, 0, 0, 0, 0, 0, .. Encoding.ASCII.GetBytes(type), .. new byte[13]];

        static byte[] Box(string type, byte[] body)
        {
            var box = new byte[8 + body.Length];
            Put(box, 0, (uint)box.Length);
            Encoding.ASCII.GetBytes(type).CopyTo(box, 4);
            body.CopyTo(box, 8);
            return box;
        }

        static void Put(byte[] target, int offset, uint value)
        {
            target[offset] = (byte)(value >> 24);
            target[offset + 1] = (byte)(value >> 16);
            target[offset + 2] = (byte)(value >> 8);
            target[offset + 3] = (byte)value;
        }
    }

    // Заголовок WAV — сигнатуры хватает MediaProbe
    public static byte[] Wav() => [.. "RIFF"u8.ToArray(), 36, 0, 0, 0, .. "WAVEfmt "u8.ToArray(), .. new byte[28]];
}
