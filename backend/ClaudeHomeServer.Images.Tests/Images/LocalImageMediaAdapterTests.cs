using System.Diagnostics;
using System.Text;
using ClaudeHomeServer.Services.ImageEditor;
using ClaudeHomeServer.Services.Images.Editing.Raster;
using ClaudeHomeServer.Services.Images.LocalMedia;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;

namespace ClaudeHomeServer.Tests.Services;

// Шов «Локальные модели» редактора поверх фейкового ComfyUI: живость без синхронного ожидания,
// кеш живости по любому ответу очереди, потолок общей очереди, постановка правки и сбор файлов
public class LocalImageMediaAdapterTests
{
    private readonly FakeComfy _comfy = new();

    private LocalImageMediaAdapter Build(bool enabled = true, int maxQueue = 4, HttpMessageHandler? handler = null,
        ClaudeHomeServer.Services.Images.Editing.Raster.IImageRaster? raster = null,
        ClaudeHomeServer.Services.Images.LocalMedia.LocalMediaJobStore? store = null)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["LocalMedia:Enabled"] = enabled ? "true" : "false",
            ["LocalMedia:ComfyUrl"] = "http://comfy.test:8188",
            ["LocalMedia:MaxComfyQueue"] = maxQueue.ToString(),
        }).Build();
        var client = new ComfyClient(new FakeComfyFactory(handler ?? _comfy), config);
        return new LocalImageMediaAdapter(client, config, NullLogger<LocalImageMediaAdapter>.Instance, raster, store);
    }

    private static LocalImageRequest Edit(string prompt = "убери провод") =>
        new(LocalImageOp.Edit, prompt, [LocalMediaTestImages.Png(8, 8)], Aspect: null, Count: 1);

    // Висящий ComfyUI: /queue отвечает, только когда тест отпустит
    private sealed class HangingComfy(FakeComfy inner) : DelegatingHandler(inner)
    {
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Entered.TrySetResult();
            await Release.Task;
            return await base.SendAsync(request, ct);
        }
    }

    [Fact]
    public void Выключенный_тумблер_недоступен_и_в_ComfyUI_не_ходит()
    {
        var adapter = Build(enabled: false);

        adapter.Available.Should().BeFalse();
        adapter.Probing.Should().BeNull();
        _comfy.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Живость_не_ждёт_висящий_ComfyUI_а_ответ_догоняет_опросом()
    {
        var hanging = new HangingComfy(_comfy);
        var adapter = Build(handler: hanging);
        await hanging.Entered.Task;

        // Прежний геттер ждал ответа до 2 с синхронно — поток пула стоял на каждом каталоге
        var watch = Stopwatch.StartNew();
        var first = adapter.Available;
        watch.Stop();

        first.Should().BeFalse("ответа ComfyUI ещё нет");
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1), "геттер не ждёт опроса");

        var probing = adapter.Probing!;
        hanging.Release.SetResult();
        await probing;
        adapter.Available.Should().BeTrue("прогрев дождался ComfyUI");
    }

    [Fact]
    public async Task Прогрев_при_создании_один_опрос_на_много_вопросов()
    {
        var adapter = Build();
        if (adapter.Probing is { } probing) await probing;

        for (var i = 0; i < 5; i++) adapter.Available.Should().BeTrue();

        _comfy.Requests.Should().ContainSingle(r => r == "GET /queue", "свежий ответ живёт 30 с");
    }

    [Fact]
    public async Task Упавшая_очередь_гасит_поставщика_без_нового_опроса()
    {
        var adapter = Build();
        if (adapter.Probing is { } probing) await probing;
        adapter.Available.Should().BeTrue();

        _comfy.Down = true;
        (await adapter.QueueLengthAsync(default)).Should().BeNull();

        adapter.Available.Should().BeFalse("ответ очереди — тоже знание о живости");
        adapter.Probing.Should().BeNull("кеш свежий — фоновый опрос не нужен");
    }

    [Fact]
    public async Task Занятая_очередь_GPU_честный_отказ_busy_без_постановки()
    {
        _comfy.Running.Add("чужой");
        _comfy.Pending.Add("чужой-2");
        var adapter = Build(maxQueue: 2);

        var submitted = await adapter.SubmitAsync(Edit(), default);

        submitted.Ticket.Should().BeNull();
        submitted.Busy.Should().BeTrue();
        submitted.Error.Should().Contain("Очередь локальной видеокарты занята (2 задач)");
        _comfy.Prompts.Should().BeEmpty();
    }

    [Fact]
    public async Task Правка_ставится_в_очередь_и_собирается_картинкой()
    {
        _comfy.Running.Add("чужой");
        var adapter = Build();

        var submitted = await adapter.SubmitAsync(Edit(), default);

        submitted.Error.Should().BeNull();
        submitted.Ticket.Should().NotBeNullOrEmpty();
        _comfy.Uploads.Should().ContainSingle().Which.Should().StartWith("lm_").And.EndWith("-in1.png");
        _comfy.Prompts.Should().ContainSingle();

        var waiting = await adapter.PollAsync(submitted.Ticket!, default);
        waiting.State.Should().Be(LocalImageState.Queued, "впереди чужой прогон");
        waiting.QueuePosition.Should().Be(1);

        var png = LocalMediaTestImages.Png(16, 16);
        _comfy.Complete(submitted.Ticket!, ("ie_out_00001_.png", png));
        var done = await adapter.PollAsync(submitted.Ticket!, default);

        done.State.Should().Be(LocalImageState.Completed);
        done.Files.Should().ContainSingle().Which.Bytes.Should().Equal(png);
    }

    // Генерация по тексту с фото персонажа не теряет фото: граф правки на пустом холсте, где
    // фото — образец энкодера, а не граф генерации без картинок
    [Fact]
    public async Task Генерация_с_фото_персонажа_идёт_графом_правки_на_пустом_холсте()
    {
        var adapter = Build();
        var photo = LocalMediaTestImages.Png(8, 8);

        var submitted = await adapter.SubmitAsync(
            new LocalImageRequest(LocalImageOp.Generate, "Тимур сидит в кафе", [photo], "16:9", 1), default);

        submitted.Error.Should().BeNull();
        _comfy.UploadedBytes.Values.Should().ContainSingle().Which.Should().Equal(photo);
        var graph = _comfy.Prompts.Should().ContainSingle().Subject["prompt"]!.AsObject();
        graph["img1"]!["class_type"]!.GetValue<string>().Should().Be("LoadImage");
        var enc = graph["enc"]!["inputs"]!.AsObject();
        enc["images.image_1"]!.AsArray()[0]!.GetValue<string>().Should().Be("img1");
        enc["prompt"]!.GetValue<string>().Should().Be("Тимур сидит в кафе");
        var lat = graph["lat"]!["inputs"]!.AsObject();
        (lat["width"]!.GetValue<int>(), lat["height"]!.GetValue<int>()).Should().Be((1664, 928));
        graph["ks"]!["inputs"]!["latent_image"]!.AsArray()[0]!.GetValue<string>().Should().Be("lat");
    }

    [Fact]
    public async Task Правка_с_фото_эталона_кладёт_узел_sim_и_грузит_фото_с_префиксом_ref()
    {
        var adapter = Build();
        var ref1 = LocalMediaTestImages.Png(8, 8);
        var ref2 = LocalMediaTestImages.Png(9, 9);

        var submitted = await adapter.SubmitAsync(Edit() with { FaceReferences = [ref1, ref2] }, default);

        submitted.Error.Should().BeNull();
        _comfy.Uploads.Where(u => u.Contains("-ref")).Should().HaveCount(2)
            .And.OnlyContain(u => u.StartsWith("lm_"));
        _comfy.Uploads.Should().Contain(u => u.EndsWith("-ref1.png")).And.Contain(u => u.EndsWith("-ref2.png"));
        var graph = _comfy.Prompts.Should().ContainSingle().Subject["prompt"]!.AsObject();
        graph["sim"]!["class_type"]!.GetValue<string>().Should().Be("CcsFaceSimilarity");
        graph["sim"]!["inputs"]!["references"]!.GetValue<string>().Split('\n').Should().HaveCount(2);
        graph["enc"]!["inputs"]!.AsObject().ContainsKey("images.image_2").Should().BeFalse("фото эталона не идут в энкодер");
    }

    [Fact]
    public async Task Правка_без_фото_эталона_без_узла_sim_и_без_лишних_загрузок()
    {
        var adapter = Build();

        (await adapter.SubmitAsync(Edit(), default)).Error.Should().BeNull();

        _comfy.Uploads.Should().ContainSingle().Which.Should().EndWith("-in1.png");
        _comfy.Prompts.Should().ContainSingle().Subject["prompt"]!.AsObject().ContainsKey("sim").Should().BeFalse();
    }

    [Fact]
    public async Task Фото_эталона_не_картинка_отказ_до_постановки()
    {
        var adapter = Build();

        var submitted = await adapter.SubmitAsync(Edit() with { FaceReferences = [[1, 2, 3]] }, default);

        submitted.Error.Should().Contain("Фото эталона 1");
        _comfy.Prompts.Should().BeEmpty();
    }

    [Theory]
    [InlineData("ccs_face_similarity=0.771", 0.771)]
    [InlineData("ccs_face_similarity=none", null)]
    [InlineData("ccs_face_similarity=abc", null)]
    [InlineData("другая строка", null)]
    public async Task Сходство_разбирается_из_текстового_выхода_графа(string text, double? expected)
    {
        var adapter = Build();
        var submitted = await adapter.SubmitAsync(Edit() with { FaceReferences = [LocalMediaTestImages.Png(8, 8)] }, default);
        _comfy.Complete(submitted.Ticket!, ("ie_out_00001_.png", LocalMediaTestImages.Png(16, 16)));
        _comfy.History[submitted.Ticket!]["outputs"]!.AsObject()["sim"] =
            new System.Text.Json.Nodes.JsonObject { ["text"] = new System.Text.Json.Nodes.JsonArray(text) };

        var done = await adapter.PollAsync(submitted.Ticket!, default);

        done.Files.Should().ContainSingle().Which.Similarity.Should().Be(expected);
    }

    [Fact]
    public async Task Без_текстового_выхода_сходство_null()
    {
        var adapter = Build();
        var submitted = await adapter.SubmitAsync(Edit(), default);
        _comfy.Complete(submitted.Ticket!, ("ie_out_00001_.png", LocalMediaTestImages.Png(16, 16)));

        (await adapter.PollAsync(submitted.Ticket!, default)).Files.Should().ContainSingle().Which.Similarity.Should().BeNull();
    }

    [Fact]
    public async Task Стирание_по_маске_без_растра_отказ_до_ComfyUI()
    {
        var adapter = Build();
        if (adapter.Probing is { } probing) await probing;
        var requestsBefore = _comfy.Requests.Count;

        var submitted = await adapter.SubmitAsync(Edit() with { EraseMask = LocalMediaTestImages.Png(8, 8) }, default);

        submitted.Error.Should().Be("Стирание по маске здесь недоступно.");
        _comfy.Requests.Should().HaveCount(requestsBefore);
    }

    [Fact]
    public async Task Правка_с_Lora_кладёт_узел_lora_в_граф()
    {
        var adapter = Build(handler: new LorasComfy(_comfy, ["hero.safetensors"]));

        var submitted = await adapter.SubmitAsync(Edit() with { Lora = new LocalImageLora("hero.safetensors", 0.8) }, default);

        submitted.Error.Should().BeNull();
        var graph = _comfy.Prompts.Should().ContainSingle().Subject["prompt"]!.AsObject();
        graph["lora"]!["inputs"]!["lora_name"]!.GetValue<string>().Should().Be("hero.safetensors");
        graph["lora"]!["inputs"]!["strength_model"]!.GetValue<double>().Should().Be(0.8);
    }

    [Fact]
    public async Task Правка_без_Lora_узла_lora_нет()
    {
        var adapter = Build();

        (await adapter.SubmitAsync(Edit(), default)).Error.Should().BeNull();

        _comfy.Prompts.Should().ContainSingle().Subject["prompt"]!.AsObject().ContainsKey("lora").Should().BeFalse();
    }

    [Fact]
    public async Task Генерация_по_образцам_с_Lora_кладёт_узел_lora()
    {
        var adapter = Build(handler: new LorasComfy(_comfy, ["hero.safetensors"]));

        var submitted = await adapter.SubmitAsync(Edit() with
        {
            Op = LocalImageOp.Generate,
            Aspect = "1:1",
            Lora = new LocalImageLora("hero.safetensors", 0.8),
        }, default);

        submitted.Error.Should().BeNull();
        _comfy.Prompts.Should().ContainSingle().Subject["prompt"]!.AsObject().ContainsKey("lora").Should().BeTrue();
    }

    // Фейк ComfyUI не знает /models/loras — отдаём список поверх него, журнал запросов общий
    private sealed class LorasComfy(FakeComfy inner, string[]? loras) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.PathAndQuery != "/models/loras") return base.SendAsync(request, ct);
            inner.Requests.Add($"{request.Method} /models/loras");
            if (loras is null) throw new HttpRequestException("connection refused");
            var body = new System.Text.Json.Nodes.JsonArray([.. loras.Select(n => (System.Text.Json.Nodes.JsonNode)n)]);
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }

    [Fact]
    public async Task Список_Lora_отдаётся_в_порядке_ComfyUI()
    {
        var adapter = Build(handler: new LorasComfy(_comfy, ["b.safetensors", "a.safetensors"]));

        (await adapter.ListLorasAsync(default)).Should().Equal("b.safetensors", "a.safetensors");
    }

    [Fact]
    public async Task Список_Lora_при_ошибке_ComfyUI_null_без_исключения()
    {
        var adapter = Build(handler: new LorasComfy(_comfy, null));

        (await adapter.ListLorasAsync(default)).Should().BeNull();
    }

    [Fact]
    public async Task Lora_из_списка_ComfyUI_уходит_в_граф()
    {
        var adapter = Build(handler: new LorasComfy(_comfy, ["hero.safetensors"]));

        var submitted = await adapter.SubmitAsync(Edit() with { Lora = new LocalImageLora("hero.safetensors", 0.8) }, default);

        submitted.Error.Should().BeNull();
        _comfy.Prompts.Should().ContainSingle();
    }

    [Fact]
    public async Task Lora_не_из_списка_отказ_без_загрузки_и_очереди()
    {
        var adapter = Build(handler: new LorasComfy(_comfy, ["other.safetensors"]));

        var submitted = await adapter.SubmitAsync(Edit() with { Lora = new LocalImageLora("hero.safetensors", 0.8) }, default);

        submitted.Error.Should().Be("LoRA «hero.safetensors» не найдена в ComfyUI");
        _comfy.Prompts.Should().BeEmpty();
        _comfy.Requests.Should().NotContain(r => r.Contains("/upload/") || r.Contains("/prompt"));
    }

    [Theory]
    [InlineData("../x.safetensors")]
    [InlineData("/abs/x.safetensors")]
    [InlineData("\\abs\\x.safetensors")]
    [InlineData("C:x.safetensors")]
    public async Task Lora_с_опасным_именем_отказ_без_запроса_списка(string name)
    {
        var adapter = Build(handler: new LorasComfy(_comfy, [name]));
        if (adapter.Probing is { } probing) await probing;
        var before = _comfy.Requests.Count;

        var submitted = await adapter.SubmitAsync(Edit() with { Lora = new LocalImageLora(name, 0.8) }, default);

        submitted.Error.Should().NotBeNull();
        _comfy.Requests.Skip(before).Should().BeEmpty();
    }

    [Fact]
    public async Task Lora_с_силой_вне_диапазона_отказ()
    {
        var adapter = Build(handler: new LorasComfy(_comfy, ["hero.safetensors"]));

        (await adapter.SubmitAsync(Edit() with { Lora = new LocalImageLora("hero.safetensors", 2.5) }, default))
            .Error.Should().NotBeNull();
        _comfy.Prompts.Should().BeEmpty();
    }

    [Fact]
    public async Task Список_Lora_недоступен_занято()
    {
        var adapter = Build(handler: new LorasComfy(_comfy, null));

        var submitted = await adapter.SubmitAsync(Edit() with { Lora = new LocalImageLora("hero.safetensors", 0.8) }, default);

        submitted.Error.Should().NotBeNull();
        submitted.Busy.Should().BeTrue();
        _comfy.Prompts.Should().BeEmpty();
    }

    // «Дорисовать за края»: холст расширяется серыми полями до ComfyUI, латент — в пропорции холста
    [Fact]
    public async Task Дорисовка_расширяет_холст_серыми_полями_и_латент_по_холсту()
    {
        var adapter = Build(raster: new ClaudeHomeServer.Services.Images.Editing.Raster.SkiaImageRaster());
        using var red = new SkiaSharp.SKBitmap(20, 10);
        red.Erase(SkiaSharp.SKColors.Red);
        using var encoded = red.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);

        var submitted = await adapter.SubmitAsync(Edit() with
        {
            Images = [encoded.ToArray()],
            Pad = new LocalImagePad(0, 3, 0, 7),
        }, default);

        submitted.Error.Should().BeNull();
        using (var canvas = SkiaSharp.SKBitmap.Decode(_comfy.UploadedBytes.Values.Should().ContainSingle().Subject))
        {
            (canvas.Width, canvas.Height).Should().Be((20, 20));
            canvas.GetPixel(10, 1).Should().Be(new SkiaSharp.SKColor(0x80, 0x80, 0x80), "верхнее поле — серое");
            canvas.GetPixel(10, 3).Should().Be(SkiaSharp.SKColors.Red, "картинка начинается с 3-й строки");
            canvas.GetPixel(10, 12).Should().Be(SkiaSharp.SKColors.Red);
            canvas.GetPixel(10, 13).Should().Be(new SkiaSharp.SKColor(0x80, 0x80, 0x80), "нижнее поле — серое");
        }
        var graph = _comfy.Prompts.Should().ContainSingle().Subject["prompt"]!.AsObject();
        var lat = graph["lat"]!["inputs"]!.AsObject();
        (lat["width"]!.GetValue<int>(), lat["height"]!.GetValue<int>()).Should().Be((1328, 1328));
        graph["ks"]!["inputs"]!["latent_image"]!.AsArray()[0]!.GetValue<string>().Should().Be("lat");
    }

    [Theory]
    [InlineData(1820, 1024, 1776, 992)]  // живой прогон w15: квадрат 1024 до 16:9
    [InlineData(1024, 1820, 992, 1776)]
    [InlineData(20, 20, 1328, 1328)]
    public void Размер_дорисовки_площадь_таблицы_стороны_кратны_16(int w, int h, int ew, int eh) =>
        LocalImageMediaAdapter.OutpaintSize(w, h).Should().Be((ew, eh));

    [Fact]
    public async Task Дорисовка_без_растра_отказ_до_ComfyUI()
    {
        var adapter = Build();

        var submitted = await adapter.SubmitAsync(Edit() with { Pad = new LocalImagePad(1, 0, 1, 0) }, default);

        submitted.Error.Should().Be("Дорисовка за края здесь недоступна.");
        _comfy.Prompts.Should().BeEmpty();
    }

    // Реальный PNG (SkiaSharp): минимальный заголовок LocalMediaTestImages.Png не декодируется растром,
    // а стирание/дорисовка требуют именно декодирования, чтобы блок прошёл до проверки несовместимости
    private static byte[] RealPng(int width, int height)
    {
        using var bitmap = new SkiaSharp.SKBitmap(width, height);
        bitmap.Erase(SkiaSharp.SKColors.White);
        using var encoded = bitmap.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }

    // Канал «вне маски вернуть исходник»: несовместимость с другими операциями правки
    [Fact]
    public async Task Маска_вне_маски_с_дорисовкой_отказ()
    {
        var adapter = Build(raster: new ClaudeHomeServer.Services.Images.Editing.Raster.SkiaImageRaster());

        var submitted = await adapter.SubmitAsync(Edit() with
        {
            Images = [RealPng(8, 8)],
            KeepMask = LocalMediaTestImages.Png(8, 8),
            Pad = new LocalImagePad(1, 0, 1, 0),
        }, default);

        submitted.Error.Should().Be("Возврат исходника вне маски — только для правки без стирания и дорисовки.");
        _comfy.Prompts.Should().BeEmpty();
    }

    [Fact]
    public async Task Маска_вне_маски_с_стиранием_отказ()
    {
        var adapter = Build(raster: new ClaudeHomeServer.Services.Images.Editing.Raster.SkiaImageRaster());

        var submitted = await adapter.SubmitAsync(Edit() with
        {
            Images = [RealPng(8, 8)],
            EraseMask = RealPng(8, 8),
            KeepMask = LocalMediaTestImages.Png(8, 8),
        }, default);

        submitted.Error.Should().Be("Возврат исходника вне маски — только для правки без стирания и дорисовки.");
        _comfy.Prompts.Should().BeEmpty();
    }

    [Fact]
    public async Task Маска_вне_маски_при_генерации_отказ()
    {
        var adapter = Build();

        var submitted = await adapter.SubmitAsync(new LocalImageRequest(
            LocalImageOp.Generate, "Тимур сидит в кафе", [LocalMediaTestImages.Png(8, 8)], "1:1", 1,
            KeepMask: LocalMediaTestImages.Png(8, 8)), default);

        submitted.Error.Should().Be("Возврат исходника вне маски — только для правки без стирания и дорисовки.");
        _comfy.Prompts.Should().BeEmpty();
    }

    // Маска — PNG: сигнатура не PNG → отказ до постановки
    [Fact]
    public async Task Маска_вне_маски_не_png_отказ()
    {
        var adapter = Build();

        var submitted = await adapter.SubmitAsync(Edit() with
        {
            KeepMask = [1, 2, 3],
        }, default);

        submitted.Error.Should().Be("Маска — не PNG.");
        _comfy.Prompts.Should().BeEmpty();
    }

    // Размер маски = размер первой картинки: не совпал → отказ
    [Fact]
    public async Task Маска_вне_маски_размер_не_совпал_отказ()
    {
        var adapter = Build();

        var submitted = await adapter.SubmitAsync(Edit() with
        {
            Images = [LocalMediaTestImages.Png(8, 8)],
            KeepMask = LocalMediaTestImages.Png(16, 16),
        }, default);

        submitted.Error.Should().Be("Маска не совпадает размером с первой картинкой.");
        _comfy.Prompts.Should().BeEmpty();
    }

    // Валидный случай: маска загружена, имя записано в стор для чистки, граф склеивает вне маски
    [Fact]
    public async Task Маска_вне_маски_загружена_в_стор_и_граф_склеивает()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "keepmask_" + Guid.NewGuid().ToString("N"));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(tempDir, "data", "projects.json"),
        }).Build();
        var store = new ClaudeHomeServer.Services.Images.LocalMedia.LocalMediaJobStore(config);
        var adapter = Build(store: store);

        var submitted = await adapter.SubmitAsync(Edit() with
        {
            Images = [LocalMediaTestImages.Png(8, 8)],
            KeepMask = LocalMediaTestImages.Png(8, 8),
        }, default);

        submitted.Error.Should().BeNull();
        // Маска загружена в input ComfyUI под именем {jobId}-mask.png
        _comfy.Uploads.Should().Contain(u => u.EndsWith("-mask.png"));
        // Имя записано среди входов для общей чистки
        var job = store.ListByOp(ClaudeHomeServer.Services.Images.LocalMedia.LocalMediaOps.EditorInputs)
            .Should().ContainSingle().Subject;
        job.ComfyInputs.Should().Contain(u => u.EndsWith("-mask.png"));
        // Граф: LoadImageMask с именем маски и ImageCompositeMasked (склейка вне маски)
        var graph = _comfy.Prompts.Should().ContainSingle().Subject["prompt"]!.AsObject();
        var maskName = _comfy.UploadPaths.Should().Contain(u => u.EndsWith("-mask.png")).Subject;
        graph["mask"]!["class_type"]!.GetValue<string>().Should().Be("LoadImageMask");
        graph["mask"]!["inputs"]!["image"]!.GetValue<string>().Should().Be(maskName);
        graph["overlay"]!["class_type"]!.GetValue<string>().Should().Be("ImageCompositeMasked");
        ClaudeHomeServer.Tests.Helpers.TestFs.DeleteDirectoryResilient(tempDir);
    }

    // Без KeepMask граф прежний: ни LoadImageMask, ни ImageCompositeMasked
    [Fact]
    public async Task Без_маски_вне_маски_граф_без_склейки()
    {
        var adapter = Build();

        (await adapter.SubmitAsync(Edit(), default)).Error.Should().BeNull();

        var graph = _comfy.Prompts.Should().ContainSingle().Subject["prompt"]!.AsObject();
        graph.ContainsKey("mask").Should().BeFalse();
        graph.ContainsKey("overlay").Should().BeFalse();
    }

    // Маска сверх лимита входов → отказ до загрузки: в input ComfyUI ничего не легло
    [Fact]
    public async Task Маска_вне_маски_сверх_лимита_отказ_до_загрузки()
    {
        var adapter = Build();

        var submitted = await adapter.SubmitAsync(Edit() with
        {
            KeepMask = new byte[LocalMediaService.MaxInputBytes + 1],
        }, default);

        submitted.Error.Should().Be($"Маска больше {LocalMediaService.MaxInputBytes / 1024 / 1024} МБ.");
        _comfy.Uploads.Should().BeEmpty("отказ случился до загрузки файлов");
        _comfy.Prompts.Should().BeEmpty();
    }

    // Пустой массив маски = «маски нет»: обычная правка проходит, склейки в графе нет
    [Fact]
    public async Task Маска_вне_маски_пустой_массив_как_без_маски()
    {
        var adapter = Build();

        var submitted = await adapter.SubmitAsync(Edit() with
        {
            KeepMask = [],
        }, default);

        submitted.Error.Should().BeNull();
        var graph = _comfy.Prompts.Should().ContainSingle().Subject["prompt"]!.AsObject();
        graph.ContainsKey("mask").Should().BeFalse();
        graph.ContainsKey("overlay").Should().BeFalse();
    }

    // Кроп вокруг цели (ADR-028, решение 12): только у Edit, вместе с KeepMask;
    // прямоугольник внутри холста, X/Y/Width/Height кратны 8, размер увеличения кратно 16
    [Fact]
    public async Task Кроп_БезМаски_отказ()
    {
        var adapter = Build();

        var submitted = await adapter.SubmitAsync(Edit() with
        {
            Crop = new LocalImageCrop(0, 0, 8, 8, 16, 16),
        }, default);

        submitted.Error.Should().Be("Кроп вокруг цели — только для правки с возвратом исходника вне маски.");
        _comfy.Prompts.Should().BeEmpty();
    }

    [Fact]
    public async Task Кроп_ВнеХолста_отказ()
    {
        var adapter = Build();

        var submitted = await adapter.SubmitAsync(Edit() with
        {
            Images = [LocalMediaTestImages.Png(8, 8)],
            KeepMask = LocalMediaTestImages.Png(8, 8),
            Crop = new LocalImageCrop(0, 0, 16, 8, 16, 16),
        }, default);

        submitted.Error.Should().Be("Кроп выходит за пределы холста.");
        _comfy.Prompts.Should().BeEmpty();
    }

    [Fact]
    public async Task Кроп_НулевойРазмер_отказ()
    {
        var adapter = Build();

        var submitted = await adapter.SubmitAsync(Edit() with
        {
            Images = [LocalMediaTestImages.Png(8, 8)],
            KeepMask = LocalMediaTestImages.Png(8, 8),
            Crop = new LocalImageCrop(0, 0, 0, 8, 16, 16),
        }, default);

        submitted.Error.Should().Be("Размер кропа должен быть положительным.");
        _comfy.Prompts.Should().BeEmpty();
    }

    [Fact]
    public async Task Кроп_НеКратноВосеми_отказ()
    {
        var adapter = Build();

        var submitted = await adapter.SubmitAsync(Edit() with
        {
            Images = [LocalMediaTestImages.Png(8, 8)],
            KeepMask = LocalMediaTestImages.Png(8, 8),
            Crop = new LocalImageCrop(4, 0, 8, 8, 16, 16),
        }, default);

        submitted.Error.Should().Be("Координаты и размер кропа должны быть кратны 8.");
        _comfy.Prompts.Should().BeEmpty();
    }

    [Fact]
    public async Task Кроп_УвеличениеНеКратноШестнадцати_отказ()
    {
        var adapter = Build();

        var submitted = await adapter.SubmitAsync(Edit() with
        {
            Images = [LocalMediaTestImages.Png(8, 8)],
            KeepMask = LocalMediaTestImages.Png(8, 8),
            Crop = new LocalImageCrop(0, 0, 8, 8, 8, 16),
        }, default);

        submitted.Error.Should().Be("Размер увеличения кропа должен быть кратно 16.");
        _comfy.Prompts.Should().BeEmpty();
    }

    // Кроп у правого/нижнего края холста некратной ширины/высоты (холст 13×11): проходит проверку
    // и доходит до графа с тем же прямоугольником — округление вниз срезало бы маску у края
    // (живой прогон 13.10: полоса старого лица). Мутация «кратность 8 везде» → красный
    [Fact]
    public async Task Кроп_УКраяХолста_НеКратныйВосьми_доходитДоГрафа()
    {
        var adapter = Build();

        var submitted = await adapter.SubmitAsync(Edit() with
        {
            Images = [LocalMediaTestImages.Png(13, 11)],
            KeepMask = LocalMediaTestImages.Png(13, 11),
            Crop = new LocalImageCrop(8, 8, 5, 3, 16, 16),
        }, default);

        submitted.Error.Should().BeNull();
        var graph = _comfy.Prompts.Should().ContainSingle().Subject["prompt"]!.AsObject();
        var crop = graph["crop"]!["inputs"]!.AsObject();
        crop["width"]!.GetValue<int>().Should().Be(5);
        crop["height"]!.GetValue<int>().Should().Be(3);
        var maskCrop = graph["mask_crop"]!["inputs"]!.AsObject();
        maskCrop["width"]!.GetValue<int>().Should().Be(5);
        maskCrop["height"]!.GetValue<int>().Should().Be(3);
        graph["dec_fit"]!["inputs"]!["width"]!.GetValue<int>().Should().Be(5);
    }

    // Внутри холста (не у края) некратная ширина по-прежнему отказ
    [Fact]
    public async Task Кроп_ВнутриХолста_НеКратнаяШирина_отказ()
    {
        var adapter = Build();

        var submitted = await adapter.SubmitAsync(Edit() with
        {
            Images = [LocalMediaTestImages.Png(16, 8)],
            KeepMask = LocalMediaTestImages.Png(16, 8),
            Crop = new LocalImageCrop(0, 0, 12, 8, 16, 16),
        }, default);

        submitted.Error.Should().Be("Координаты и размер кропа должны быть кратны 8.");
        _comfy.Prompts.Should().BeEmpty();
    }

    // Валидный кроп: граф режет кроп из холста, увеличивает его, вклеивает результат в месте кропа
    [Fact]
    public async Task Кроп_Валидный_граф_режет_увеличивает_и_вклеивает()
    {
        var adapter = Build();

        var submitted = await adapter.SubmitAsync(Edit() with
        {
            Images = [LocalMediaTestImages.Png(8, 8)],
            KeepMask = LocalMediaTestImages.Png(8, 8),
            Crop = new LocalImageCrop(0, 0, 8, 8, 16, 16),
        }, default);

        submitted.Error.Should().BeNull();
        var graph = _comfy.Prompts.Should().ContainSingle().Subject["prompt"]!.AsObject();
        graph["crop"]!["class_type"]!.GetValue<string>().Should().Be("ImageCrop");
        graph["crop_scale"]!["class_type"]!.GetValue<string>().Should().Be("ImageScale");
        graph["dec_fit"]!["class_type"]!.GetValue<string>().Should().Be("ImageScale");
        graph["mask_crop"]!["class_type"]!.GetValue<string>().Should().Be("CropMask");
        var overlay = graph["overlay"]!["inputs"]!.AsObject();
        overlay["x"]!.GetValue<int>().Should().Be(0);
        overlay["y"]!.GetValue<int>().Should().Be(0);
        overlay["resize_source"]!.GetValue<bool>().Should().BeFalse();
        overlay["source"]!.AsArray()[0]!.GetValue<string>().Should().Be("dec_fit");
    }

    // (г) Вуаль: модель видит холст с серой вуалью (img1_veil), а не белую маску-образец;
    // склейка «вне маски — исходник» берёт ЧИСТЫЙ img1. Мутация «destination = veil» → красный
    [Fact]
    public async Task Вуаль_Граф_ЭнкодерВуаль_СклейкаЧистый()
    {
        var adapter = Build(raster: new ClaudeHomeServer.Services.Images.Editing.Raster.SkiaImageRaster());
        var image = RealPng(8, 8);
        var mask = RealPng(8, 8);

        var submitted = await adapter.SubmitAsync(Edit() with
        {
            Images = [image],
            KeepMask = mask,
            VeilMask = mask,
        }, default);

        submitted.Error.Should().BeNull();
        var graph = _comfy.Prompts.Should().ContainSingle().Subject["prompt"]!.AsObject();
        // Энкодер: первая картинка — вуаль, а не чистый холст
        graph["enc"]!["inputs"]!.AsObject()["images.image_1"]!.AsArray()[0]!.GetValue<string>().Should().Be("img1_veil");
        // Склейка: destination — чистый img1, source — результат модели
        var overlay = graph["overlay"]!["inputs"]!.AsObject();
        overlay["destination"]!.AsArray()[0]!.GetValue<string>().Should().Be("img1");
        overlay["source"]!.AsArray()[0]!.GetValue<string>().Should().Be("dec");
        // Вуаль загружена как отдельный вход
        _comfy.Uploads.Should().Contain(u => u.EndsWith("-veil.png"));
    }

    // Вуаль несовместима со стиранием: адаптер отказывает до постановки в очередь
    [Fact]
    public async Task Вуаль_Стирание_Отказ()
    {
        var adapter = Build(raster: new ClaudeHomeServer.Services.Images.Editing.Raster.SkiaImageRaster());

        var submitted = await adapter.SubmitAsync(Edit() with
        {
            Images = [RealPng(8, 8)],
            EraseMask = RealPng(8, 8),
            VeilMask = RealPng(8, 8),
        }, default);

        submitted.Error.Should().Be("Вуаль — только для правки без стирания, дорисовки и подстановки на лицо.");
        _comfy.Prompts.Should().BeEmpty();
    }

    // Вуаль несовместима с дорисовкой: адаптер отказывает до постановки в очередь
    [Fact]
    public async Task Вуаль_Дорисовка_Отказ()
    {
        var adapter = Build(raster: new ClaudeHomeServer.Services.Images.Editing.Raster.SkiaImageRaster());

        var submitted = await adapter.SubmitAsync(Edit() with
        {
            Images = [RealPng(8, 8)],
            Pad = new LocalImagePad(1, 0, 1, 0),
            VeilMask = RealPng(8, 8),
        }, default);

        submitted.Error.Should().Be("Вуаль — только для правки без стирания, дорисовки и подстановки на лицо.");
        _comfy.Prompts.Should().BeEmpty();
    }

    // Без растра вуаль недоступна: отказ до постановки
    [Fact]
    public async Task Вуаль_БезРастра_Отказ()
    {
        var adapter = Build();

        var submitted = await adapter.SubmitAsync(Edit() with
        {
            Images = [RealPng(8, 8)],
            VeilMask = RealPng(8, 8),
        }, default);

        submitted.Error.Should().Be("Вуаль здесь недоступна.");
        _comfy.Prompts.Should().BeEmpty();
    }

    // Маска вуали другого размера, чем холст: отказ, а не сдвиг заливки
    [Fact]
    public async Task Вуаль_РазмерМаскиНеСовпал_Отказ()
    {
        var adapter = Build(raster: new ClaudeHomeServer.Services.Images.Editing.Raster.SkiaImageRaster());

        var submitted = await adapter.SubmitAsync(Edit() with
        {
            Images = [RealPng(8, 8)],
            VeilMask = RealPng(16, 16),
        }, default);

        submitted.Error.Should().Contain("Вуаль не удалось наложить");
        _comfy.Prompts.Should().BeEmpty();
    }

    // Детекция лиц (ADR-028): узел CcsFaceDetect печатает ccs_face_detect={...}; первый
    // POST /prompt получает prompt-1, поэтому ответ кладём в историю заранее, без гонок
    private void AnswerDetect(string text)
    {
        _comfy.History["prompt-1"] = new System.Text.Json.Nodes.JsonObject
        {
            ["status"] = new System.Text.Json.Nodes.JsonObject
            {
                ["status_str"] = "success",
                ["completed"] = true,
                ["messages"] = new System.Text.Json.Nodes.JsonArray(),
            },
            ["outputs"] = new System.Text.Json.Nodes.JsonObject
            {
                ["detect"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["text"] = new System.Text.Json.Nodes.JsonArray(text),
                },
            },
        };
    }

    [Fact]
    public async Task Детекция_два_лица_порядок_номера_и_флаг_мелкого()
    {
        var image = LocalMediaTestImages.Png(8, 8);
        AnswerDetect("ccs_face_detect={\"width\":8,\"height\":8,\"faces\":[{\"bbox\":[1,1,3,3],\"score\":0.9,\"small\":false},{\"bbox\":[4,4,6,6],\"score\":0.7,\"small\":true}]}");

        var result = await Build().DetectFacesAsync(image, CancellationToken.None);

        result.Error.Should().BeNull();
        result.Busy.Should().BeFalse();
        result.Width.Should().Be(8);
        result.Height.Should().Be(8);
        result.Faces.Should().HaveCount(2);
        result.Faces[0].Should().Be(new LocalDetectedFace(1, 1, 1, 3, 3, 0.9, false));
        result.Faces[1].Should().Be(new LocalDetectedFace(2, 4, 4, 6, 6, 0.7, true));
    }

    [Fact]
    public async Task Детекция_без_лиц_успех_с_пустыми_Faces_и_без_ошибки()
    {
        var image = LocalMediaTestImages.Png(8, 8);
        AnswerDetect("ccs_face_detect={\"width\":8,\"height\":8,\"faces\":[]}");

        var result = await Build().DetectFacesAsync(image, CancellationToken.None);

        result.Error.Should().BeNull();
        result.Busy.Should().BeFalse();
        result.Faces.Should().BeEmpty();
    }

    [Fact]
    public async Task Детекция_error_узла_это_ошибка_а_не_лица_нет()
    {
        var image = LocalMediaTestImages.Png(8, 8);
        AnswerDetect("ccs_face_detect=error");

        var result = await Build().DetectFacesAsync(image, CancellationToken.None);

        result.Error.Should().NotBeNullOrEmpty();
        result.Faces.Should().BeEmpty();
    }

    // Битый JSON, нулевой/отрицательный размер, нечисловой и вырожденный bbox — «неожиданный
    // ответ» (null), а не «лиц нет»: адаптер превратит null в Error
    [Theory]
    [InlineData("ccs_face_detect={\"width\":8,\"height\":8,\"faces\":")]
    [InlineData("ccs_face_detect={\"width\":0,\"height\":8,\"faces\":[]}")]
    [InlineData("ccs_face_detect={\"width\":-1,\"height\":8,\"faces\":[]}")]
    [InlineData("ccs_face_detect={\"width\":8,\"height\":8,\"faces\":[{\"bbox\":[3,3,1,1],\"score\":0.9,\"small\":false}]}")]
    [InlineData("ccs_face_detect={\"width\":8,\"height\":8,\"faces\":[{\"bbox\":[\"a\",1,3,3],\"score\":0.9,\"small\":false}]}")]
    [InlineData("ccs_face_detect={\"width\":8,\"height\":8,\"faces\":[{\"bbox\":[1,1,3],\"score\":0.9,\"small\":false}]}")]
    [InlineData("ccs_face_detect={\"width\":8,\"height\":8}")]
    public void Детекция_негодный_ответ_это_неожиданный_а_не_лица_нет(string text)
    {
        LocalImageMediaAdapter.ParseFaces([text], LocalMediaTestImages.Png(8, 8)).Should().BeNull();
    }

    // EXIF-поворот: воркер (cv2) повернул JPEG, ImageDimensions — нет; размер не совпал → отказ
    [Fact]
    public async Task Детекция_размер_воркера_не_совпал_с_байтами_отказ_сопоставить()
    {
        var image = LocalMediaTestImages.Png(8, 8);
        AnswerDetect("ccs_face_detect={\"width\":16,\"height\":8,\"faces\":[]}");

        var result = await Build().DetectFacesAsync(image, CancellationToken.None);

        result.Error.Should().Be("Не удалось сопоставить лица с картинкой.");
        result.Faces.Should().BeEmpty();
    }

    // EXIF Orientation 6 (iPhone): сырые байты 1312×640, воркер (cv2) поворачивает → 640×1312.
    // Нормализация в DetectFacesAsync применяет EXIF и обнуляет тег → ImageDimensions.Read видит
    // 640×1312, совпадает с воркером → детекция проходит, а не «Не удалось сопоставить»
    [Fact]
    public async Task Детекция_EXIF6_нормализация_поворачивает_и_детекция_проходит()
    {
        var image = JpegWithExif(1312, 640, orientation: 6);
        AnswerDetect("ccs_face_detect={\"width\":640,\"height\":1312,\"faces\":[{\"bbox\":[10,10,100,100],\"score\":0.9,\"small\":false}]}");

        var result = await Build(raster: new SkiaImageRaster()).DetectFacesAsync(image, CancellationToken.None);

        result.Error.Should().BeNull();
        result.Busy.Should().BeFalse();
        result.Width.Should().Be(640);
        result.Height.Should().Be(1312);
        result.Faces.Should().HaveCount(1);
    }

    // EXIF Orientation 8 (iPhone, поворот в другую сторону): та же нормализация, детекция проходит
    [Fact]
    public async Task Детекция_EXIF8_нормализация_поворачивает_и_детекция_проходит()
    {
        var image = JpegWithExif(1312, 640, orientation: 8);
        AnswerDetect("ccs_face_detect={\"width\":640,\"height\":1312,\"faces\":[]}");

        var result = await Build(raster: new SkiaImageRaster()).DetectFacesAsync(image, CancellationToken.None);

        result.Error.Should().BeNull();
        result.Busy.Should().BeFalse();
        result.Width.Should().Be(640);
        result.Height.Should().Be(1312);
    }

    // Orientation 1 (без EXIF-поворота): нормализация no-op, детекция как раньше — регрессия
    [Fact]
    public async Task Детекция_Orientation1_без_EXIF_нормализация_noop_регрессия()
    {
        var image = JpegWithExif(800, 600, orientation: 1);
        AnswerDetect("ccs_face_detect={\"width\":800,\"height\":600,\"faces\":[]}");

        var result = await Build(raster: new SkiaImageRaster()).DetectFacesAsync(image, CancellationToken.None);

        result.Error.Should().BeNull();
        result.Width.Should().Be(800);
        result.Height.Should().Be(600);
    }

    // JPEG с EXIF Orientation: SkiaSharp кодирует bitmap → JPEG, вставляем APP1/EXIF после SOI
    private static byte[] JpegWithExif(int width, int height, int orientation)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Gray);
        canvas.Flush();
        using var pixmap = bitmap.PeekPixels();
        using var data = pixmap.Encode(new SKJpegEncoderOptions(90, SKJpegEncoderDownsample.Downsample420, SKJpegEncoderAlphaOption.Ignore));
        var jpeg = data!.ToArray();

        // TIFF little-endian: II + 00 2A + offset IFD0=8, 1 entry (Orientation)
        // Tag 0x0112 — тот же, что в SkiaImageRasterTests.WithExif (SkiaSharp читает ориентацию оттуда)
        var tiff = new List<byte> { (byte)'I', (byte)'I', 0x2A, 0x00, 8, 0, 0, 0 };
        Le16(tiff, 1); // 1 entry
        Le16(tiff, 0x0112); // tag Orientation (тот же, что в WithExif)
        Le16(tiff, 3); // type SHORT
        Le32(tiff, 1); // count
        Le32(tiff, (uint)orientation); // value
        Le32(tiff, 0); // next IFD offset = 0

        var payload = "Exif\0\0"u8.ToArray().Concat(tiff).ToArray();
        var length = payload.Length + 2;
        var app1 = new byte[] { 0xFF, 0xE1, (byte)(length >> 8), (byte)length }.Concat(payload);
        return jpeg.Take(2).Concat(app1).Concat(jpeg.Skip(2)).ToArray();

        static void Le16(List<byte> b, int v) { b.Add((byte)v); b.Add((byte)(v >> 8)); }
        static void Le32(List<byte> b, uint v) { for (var i = 0; i < 4; i++) b.Add((byte)(v >> (8 * i))); }
    }

    // Таймаут: потолок ожидания укорочен, история не появляется — Busy, а не исключение
    [Fact]
    public async Task Детекция_потолок_ожидания_это_Busy()
    {
        var adapter = Build();
        adapter.FaceDetectTimeoutOverride = TimeSpan.FromMilliseconds(200);

        var result = await adapter.DetectFacesAsync(LocalMediaTestImages.Png(8, 8), CancellationToken.None);

        result.Busy.Should().BeTrue();
    }

    // Детекцию запускает человек — задача в голову очереди: ключ front в теле POST /prompt
    [Fact]
    public async Task Детекция_ставится_в_голову_очереди_front()
    {
        var image = LocalMediaTestImages.Png(8, 8);
        AnswerDetect("ccs_face_detect={\"width\":8,\"height\":8,\"faces\":[]}");

        await Build().DetectFacesAsync(image, CancellationToken.None);

        _comfy.Prompts.Should().ContainSingle();
        _comfy.Prompts[0].ContainsKey("front").Should().BeTrue();
        _comfy.Prompts[0]["front"]!.GetValue<bool>().Should().BeTrue();
    }

    // Вход — биометрия: имя {id}-detect.<ext> записано в стор на общую чистку
    [Fact]
    public async Task Детекция_вход_зарегистрирован_на_чистку()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "detect_" + Guid.NewGuid().ToString("N"));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(tempDir, "data", "projects.json"),
        }).Build();
        var store = new ClaudeHomeServer.Services.Images.LocalMedia.LocalMediaJobStore(config);
        var adapter = Build(store: store);
        var image = LocalMediaTestImages.Png(8, 8);
        AnswerDetect("ccs_face_detect={\"width\":8,\"height\":8,\"faces\":[]}");

        await adapter.DetectFacesAsync(image, CancellationToken.None);

        _comfy.Uploads.Should().ContainSingle().Which.Should().StartWith("lm_").And.EndWith("-detect.png");
        store.ListByOp(ClaudeHomeServer.Services.Images.LocalMedia.LocalMediaOps.EditorInputs)
            .Should().ContainSingle().Subject.ComfyInputs.Should().Contain(u => u.EndsWith("-detect.png"));
        ClaudeHomeServer.Tests.Helpers.TestFs.DeleteDirectoryResilient(tempDir);
    }
}
