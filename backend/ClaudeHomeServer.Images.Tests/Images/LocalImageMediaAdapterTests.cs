using System.Diagnostics;
using ClaudeHomeServer.Services.ImageEditor;
using ClaudeHomeServer.Services.Images.LocalMedia;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services;

// Шов «Локальные модели» редактора поверх фейкового ComfyUI: живость без синхронного ожидания,
// кеш живости по любому ответу очереди, потолок общей очереди, постановка правки и сбор файлов
public class LocalImageMediaAdapterTests
{
    private readonly FakeComfy _comfy = new();

    private LocalImageMediaAdapter Build(bool enabled = true, int maxQueue = 4, HttpMessageHandler? handler = null)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["LocalMedia:Enabled"] = enabled ? "true" : "false",
            ["LocalMedia:ComfyUrl"] = "http://comfy.test:8188",
            ["LocalMedia:MaxComfyQueue"] = maxQueue.ToString(),
        }).Build();
        var client = new ComfyClient(new FakeComfyFactory(handler ?? _comfy), config);
        return new LocalImageMediaAdapter(client, config, NullLogger<LocalImageMediaAdapter>.Instance);
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
        _comfy.Uploads.Should().ContainSingle().Which.Should().StartWith("ie_").And.EndWith("-in1.png");
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
    public async Task Стирание_по_маске_без_растра_отказ_до_ComfyUI()
    {
        var adapter = Build();
        if (adapter.Probing is { } probing) await probing;
        var requestsBefore = _comfy.Requests.Count;

        var submitted = await adapter.SubmitAsync(Edit() with { EraseMask = LocalMediaTestImages.Png(8, 8) }, default);

        submitted.Error.Should().Be("Стирание по маске здесь недоступно.");
        _comfy.Requests.Should().HaveCount(requestsBefore);
    }
}
