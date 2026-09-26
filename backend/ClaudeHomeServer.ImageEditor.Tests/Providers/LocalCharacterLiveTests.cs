using System.Security.Cryptography;
using System.Text;
using ClaudeHomeServer.Services.ImageEditor;
using ClaudeHomeServer.Services.Images.LocalMedia;
using ClaudeHomeServer.Services.Images.Editing.Raster;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.ImageEditor.Providers;

// Живой прогон персонажа на «Локальных моделях»: тот же конвейер, что у исполнителя задач
// (персонаж → InputFitter → EditRequestComposer → LocalImageEditor → адаптер → ComfyUI).
// Запускается только с LOCAL_COMFY_URL и LOCAL_COMFY_OUT (папка результатов); данные — во
// временной папке. В ComfyUI уходящие графы пишутся рядом с результатами
public class LocalCharacterLiveTests
{
    private const string Name = "Тимур";

    [Fact]
    public async Task Character_goes_to_local_generate_and_edit()
    {
        var url = Environment.GetEnvironmentVariable("LOCAL_COMFY_URL");
        var output = Environment.GetEnvironmentVariable("LOCAL_COMFY_OUT");
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(output)) return;
        Directory.CreateDirectory(output);

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["LocalMedia:Enabled"] = "true",
            ["LocalMedia:ComfyUrl"] = url,
            ["LocalMedia:MaxComfyQueue"] = "8",
        }).Build();
        var recorder = new GraphRecorder(output);
        var raster = new SkiaImageRaster();
        var adapter = new LocalImageMediaAdapter(new ComfyClient(new Factory(recorder), config), config,
            NullLogger<LocalImageMediaAdapter>.Instance, raster);
        var editor = new LocalImageEditor(adapter) { PollInterval = TimeSpan.FromSeconds(2) };
        var model = ImageEditCatalog.WithInputLimits(editor.Models[0]);

        // Подготовка: три фото одного человека и чужая сцена — генерируются один раз
        var photo1 = await Cached(output, "src-photo1.png", () => Raw(editor,
            new(ImageEditOp.Generate, "Фотопортрет мужчины 35 лет: короткая тёмная борода, шрам над левой бровью, " +
                "родинка на правой щеке, зелёные глаза, нос с горбинкой, короткая стрижка. Анфас, нейтральный фон, студийный свет.",
                null, null, [], 1, "1:1", null, model.Id, null)));
        var photo2 = await Cached(output, "src-photo2.png", () => Raw(editor,
            new(ImageEditOp.Edit, "Тот же мужчина, поворот головы три четверти вправо, улыбается, улица, дневной свет. Лицо и черты не менять.",
                new ImageBytes(photo1, "image/png"), null, [], 1, null, null, model.Id, null)));
        var photo3 = await Cached(output, "src-photo3.png", () => Raw(editor,
            new(ImageEditOp.Edit, "Тот же мужчина в профиль влево, серый свитер, в помещении. Лицо и черты не менять.",
                new ImageBytes(photo1, "image/png"), null, [], 1, null, null, model.Id, null)));
        var scene = await Cached(output, "src-scene.png", () => Raw(editor,
            new(ImageEditOp.Generate, "Скамейка в осеннем парке, рядом с ней стоит пожилая женщина в красном пальто, фото.",
                null, null, [], 1, "16:9", null, model.Id, null)));

        var root = Path.Combine(Path.GetTempPath(), "ie-live-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        var created = CharacterStore.Create(root, new CharacterDraft(Name, "мужчина 35 лет, борода, шрам над левой бровью",
            [new(photo1), new(photo2), new(photo3)]), DateTime.UtcNow);
        created.Value.Should().NotBeNull(created.Error);
        var found = CharacterStore.ForRequest(root, created.Value!.Manifest.Slug)!;

        var known = new Dictionary<string, string>
        {
            [Hash(photo1)] = "фото персонажа 1", [Hash(photo2)] = "фото персонажа 2",
            [Hash(photo3)] = "фото персонажа 3", [Hash(scene)] = "чужая сцена",
        };

        await Run("gen", editor, model, raster, ImageEditOp.Generate,
            new ImageEditJobInput("q", $"{Name} сидит в кафе за столиком у окна и пьёт кофе, фото", null, null, null, null,
                found.Photos, null, found.Ref, AspectRatio: "1:1"), output, recorder, known);
        await Run("edit", editor, model, raster, ImageEditOp.Edit,
            new ImageEditJobInput("q", $"Поставь сюда {Name}: пусть сидит на скамейке", null,
                new ImageBytes(scene, "image/png"), null, null, found.Photos, null, found.Ref), output, recorder, known);
    }

    private static async Task Run(string tag, LocalImageEditor editor, ImageEditModelInfo model, SkiaImageRaster raster,
        ImageEditOp op, ImageEditJobInput input, string output, GraphRecorder recorder, Dictionary<string, string> known)
    {
        var fitted = await new InputFitter(raster).FitAsync(input, model.Caps, CancellationToken.None);
        var composed = EditRequestComposer.Compose(fitted.Value!.Input, op, model, 1, input.AspectRatio);
        var request = composed.Value!;
        var log = new StringBuilder();
        log.AppendLine($"# {tag}: op={request.Op}");
        log.AppendLine("## Запрос");
        log.AppendLine(request.Prompt);
        log.AppendLine("## Картинки запроса драйверу");
        if (request.Source is { } s) log.AppendLine($"source: {Describe(s.Bytes, raster, known, input)}");
        foreach (var r in request.References) log.AppendLine($"ref {r.Role}/{r.Label}: {Describe(r.Bytes, raster, known, input)}");
        recorder.Tag = tag;
        var result = await editor.RunAsync(request, new Progress<EditProgress>(), CancellationToken.None);
        log.AppendLine($"## Итог: {result.Outcome} {result.Error}");
        for (var i = 0; i < result.Images.Count; i++)
            await File.WriteAllBytesAsync(Path.Combine(output, $"{tag}-result{i + 1}.png"), result.Images[i].Bytes);
        await File.WriteAllTextAsync(Path.Combine(output, $"{tag}-request.md"), log.ToString());
    }

    private static string Describe(byte[] bytes, SkiaImageRaster raster, Dictionary<string, string> known, ImageEditJobInput input)
    {
        var p = raster.Probe(bytes);
        var who = known.TryGetValue(Hash(bytes), out var k) ? k : "ужатая копия";
        return $"{who}, {p?.DisplayWidth}×{p?.DisplayHeight}, {bytes.Length / 1024} КБ";
    }

    private static string Hash(byte[] b) => Convert.ToHexString(SHA256.HashData(b));

    private static async Task<byte[]> Raw(LocalImageEditor editor, ImageEditRequest request)
    {
        var result = await editor.RunAsync(request, new Progress<EditProgress>(), CancellationToken.None);
        result.Images.Should().NotBeEmpty(result.Error);
        return result.Images[0].Bytes;
    }

    private static async Task<byte[]> Cached(string dir, string file, Func<Task<byte[]>> make)
    {
        var path = Path.Combine(dir, file);
        if (File.Exists(path)) return await File.ReadAllBytesAsync(path);
        var bytes = await make();
        await File.WriteAllBytesAsync(path, bytes);
        return bytes;
    }

    private sealed class Factory(GraphRecorder handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    // Пишет граф каждого POST /prompt рядом с результатами
    private sealed class GraphRecorder(string dir) : DelegatingHandler(new HttpClientHandler())
    {
        public string Tag { get; set; } = "prep";
        private int _n;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath.EndsWith("/prompt") && request.Content is { } c)
            {
                var body = await c.ReadAsStringAsync(ct);
                await File.WriteAllTextAsync(Path.Combine(dir, $"{Tag}-graph{++_n}.json"), body, ct);
            }
            return await base.SendAsync(request, ct);
        }
    }
}
