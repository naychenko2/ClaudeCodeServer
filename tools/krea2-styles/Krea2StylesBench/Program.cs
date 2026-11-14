using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeHomeServer.Services.ImageEditor;
using ClaudeHomeServer.Services.Images.Editing.Raster;
using ClaudeHomeServer.Services.Images.LocalMedia;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

// Замер стилей Krea 2 Turbo (Ш4): каждый из девяти стилей × 2 промпта и «стиль по образцу» × 3 образца.
// Запрос собирается так же, как его собирает LocalImageEditor, граф строит адаптер CCS.
// Использование: Krea2StylesBench --out <каталог> [--comfy http://127.0.0.1:18188] [--phase styles|reference|all]
// Образцы стиля берутся из результатов фазы styles (img/<стиль>__<промпт>.png) — фаза reference идёт после неё.

var opts = new Dictionary<string, string>(StringComparer.Ordinal);
for (var i = 0; i + 1 < args.Length; i += 2) opts[args[i].TrimStart('-')] = args[i + 1];
var outDir = opts.GetValueOrDefault("out") ?? throw new ArgumentException("Нужен --out");
var comfyUrl = opts.GetValueOrDefault("comfy") ?? "http://127.0.0.1:18188";
var phase = opts.GetValueOrDefault("phase") ?? "all";
const long Seed = 20261112;

string[] prompts =
[
    "Старый каменный маяк на скале во время шторма, над ним кричат чайки, огромные волны",
    "Девушка с жёлтым зонтом идёт по ночной улице города, в лужах отражаются огни",
];
// Образцы стиля — готовые результаты трёх разных стилей, сюжеты новые
(string Name, string Sample)[] references =
[
    ("watercolor", "softwatercolor__p1"),
    ("retroanime", "retroanime__p2"),
    ("tarot", "vintagetarot__p1"),
];
string[] referencePrompts =
[
    "белый йети с рогами читает книгу",
    "кот спит на подоконнике у окна",
    "старый паровоз на мосту в горах",
];

Directory.CreateDirectory(Path.Combine(outDir, "img"));
var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
{
    ["LocalMedia:Enabled"] = "true",
    ["LocalMedia:ComfyUrl"] = comfyUrl,
}).Build();
var services = new ServiceCollection();
services.AddHttpClient(ComfyClient.HttpClientName, c => c.Timeout = TimeSpan.FromMinutes(5));
var factory = services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>();
ILocalImageMedia media = new LocalImageMediaAdapter(new ComfyClient(factory, config), config,
    NullLogger<LocalImageMediaAdapter>.Instance, new SkiaImageRaster());
using var http = new HttpClient { BaseAddress = new Uri(comfyUrl.TrimEnd('/') + "/"), Timeout = TimeSpan.FromMinutes(1) };
var results = Path.Combine(outDir, "results.jsonl");
var json = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, WriteIndented = false };

void Log(string text) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {text}");

async Task WaitIdle()
{
    while (await media.QueueLengthAsync(CancellationToken.None) is not 0)
    {
        Log("В очереди ComfyUI чужие задачи, жду");
        await Task.Delay(TimeSpan.FromSeconds(10));
    }
}

async Task<double?> ExecSeconds(string promptId)
{
    try
    {
        var messages = JsonNode.Parse(await http.GetStringAsync($"history/{promptId}"))?[promptId]?["status"]?["messages"]?.AsArray();
        long? start = null, end = null;
        foreach (var m in messages ?? [])
        {
            var name = m?[0]?.GetValue<string>();
            var ts = m?[1]?["timestamp"]?.GetValue<long>();
            if (name == "execution_start") start = ts;
            if (name is "execution_success" or "execution_error") end = ts;
        }
        return start is { } s && end is { } e ? (e - s) / 1000.0 : null;
    }
    catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException) { return null; }
}

async Task Run(string kind, string label, LocalImageRequest request, string file)
{
    await WaitIdle();
    var wall = Stopwatch.StartNew();
    var submitted = await media.SubmitAsync(request, CancellationToken.None);
    if (submitted.Ticket is null) { Log($"{label}: отказ {submitted.Error}"); return; }
    LocalImagePoll poll;
    do
    {
        await Task.Delay(500);
        poll = await media.PollAsync(submitted.Ticket, CancellationToken.None);
    } while (poll.State is LocalImageState.Queued or LocalImageState.Running && wall.Elapsed < TimeSpan.FromMinutes(10));
    wall.Stop();
    var exec = await ExecSeconds(submitted.Ticket);
    var ok = poll.State == LocalImageState.Completed && poll.Files.Count > 0;
    if (ok) await File.WriteAllBytesAsync(file, poll.Files[0].Bytes);
    File.AppendAllText(results, JsonSerializer.Serialize(new
    {
        kind, label, ok, error = poll.Error, wall_s = Math.Round(wall.Elapsed.TotalSeconds, 1), exec_s = exec,
        eta_s = submitted.EtaSeconds, file = ok ? Path.GetRelativePath(outDir, file) : null,
    }, json) + "\n", Encoding.UTF8);
    Log($"{label}: {(ok ? $"{exec:F1} с (стенка {wall.Elapsed.TotalSeconds:F1}, прогноз {submitted.EtaSeconds})" : "СБОЙ " + poll.Error)}");
}

if (phase is "all" or "styles")
{
    // Сначала без стиля (прогрев модели и база для сравнения), затем девять стилей
    for (var p = 0; p < prompts.Length; p++)
        await Run("plain", $"без стиля p{p + 1}",
            new LocalImageRequest(LocalImageOp.Generate, prompts[p], [], "1:1", 1, Seed: Seed, Model: LocalImageModels.Krea2Turbo),
            Path.Combine(outDir, "img", $"plain__p{p + 1}.png"));
    foreach (var style in Krea2Styles.All)
        for (var p = 0; p < prompts.Length; p++)
            await Run("style", $"{style.Id} p{p + 1}",
                new LocalImageRequest(LocalImageOp.Generate, prompts[p], [], "1:1", 1, Seed: Seed, Model: LocalImageModels.Krea2Turbo, KreaStyle: style.Id),
                Path.Combine(outDir, "img", $"{style.Id}__p{p + 1}.png"));
}

if (phase is "all" or "reference")
{
    for (var r = 0; r < references.Length; r++)
    {
        var sample = Path.Combine(outDir, "img", references[r].Sample + ".png");
        var bytes = await File.ReadAllBytesAsync(sample);
        await Run("reference", $"по образцу {references[r].Name}",
            new LocalImageRequest(LocalImageOp.Generate, referencePrompts[r], [bytes], "1:1", 1, Seed: Seed, Model: LocalImageModels.Krea2Turbo, StyleReference: true),
            Path.Combine(outDir, "img", $"ref_{references[r].Name}.png"));
    }
}
Log("Готово.");
