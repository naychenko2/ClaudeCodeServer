using ClaudeHomeServer.Services.ImageEditor;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Tests.ImageEditor.Fakes;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.ImageEditor.Providers;

// Классификатор неоднозначных запросов «Авто» (Ш3): зовётся только когда правила молчат, ждёт не дольше
// потолка, кэширует ответ по тексту, сбой — выбор по правилам
public class ImageRouteClassifierTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ie-classify-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly LocalImageEditor _editor = new(new LocalImageEditorTests.FakeMedia());

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (DirectoryNotFoundException) { }
        GC.SuppressFinalize(this);
    }

    private sealed class FakeRunner(Func<string, CancellationToken, Task<string?>> run) : ICheapTextRunner
    {
        public int Calls;
        public string? LastActionKey;
        public bool UsesLocal(string actionKey) => true;
        public bool HasFreeRoute(string actionKey) => true;
        public bool HasRoute(string actionKey) => true;
        public string DescribeRoute(string actionKey, string? fallbackModel) => "fake";
        public Task<string> RunAsync(string actionKey, string prompt, string? fallbackModel = null,
            string? ownerId = null, object? jsonFormat = null, CancellationToken ct = default)
            => throw new NotSupportedException();
        public async Task<string?> RunLocalOrChosenAsync(string actionKey, string prompt, string? ownerId = null,
            object? jsonFormat = null, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Calls);
            LastActionKey = actionKey;
            return await run(prompt, ct);
        }
        public Task<OneShotResult> RunDetailedAsync(string actionKey, string prompt, string? fallbackModel = null,
            string? ownerId = null, TimeSpan? timeout = null, int? maxTokens = null,
            object? jsonFormat = null, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private static FakeRunner Answer(string? raw) => new((_, _) => Task.FromResult(raw));

    private static EditTraits T(string? prompt, int refs = 0) => new(false, refs, false, false, false, prompt);

    [Theory]
    [InlineData(ImageEditOp.Generate, "{\"category\":\"арт\"}", "арт")]
    [InlineData(ImageEditOp.Generate, "{\"category\":\"People\"}", "люди")]
    [InlineData(ImageEditOp.Generate, "{\"category\":\"text\"}", "надписи")]
    [InlineData(ImageEditOp.Edit, "{\"category\":\"style\"}", "стиль")]
    [InlineData(ImageEditOp.Edit, "{\"category\":\"text\"}", "текст")]
    [InlineData(ImageEditOp.Generate, "{\"category\":\"фон\"}", null)]
    [InlineData(ImageEditOp.Generate, "{\"category\":\"космос\"}", null)]
    [InlineData(ImageEditOp.Generate, "не JSON", null)]
    [InlineData(ImageEditOp.Generate, "[\"арт\"]", null)]
    public void Ответ_СловарьИСинонимы(ImageEditOp op, string raw, string? expected) =>
        ImageRouteClassifier.ParseModelAnswer(op, raw).Should().Be(expected);

    [Fact]
    public async Task Ответ_Кэшируется_ПоТексту()
    {
        var runner = Answer("{\"category\":\"арт\"}");
        var c = new ImageRouteClassifier(runner);
        (await c.ClassifyAsync(ImageEditOp.Generate, "лиса в шарфе")).Should().Be("арт");
        (await c.ClassifyAsync(ImageEditOp.Generate, " лиса в шарфе ")).Should().Be("арт");
        runner.Calls.Should().Be(1);
        runner.LastActionKey.Should().Be(LocalActionCatalog.ImageRouteClassify);
    }

    [Fact]
    public async Task Сбой_НеКэшируется()
    {
        var fail = true;
        var runner = new FakeRunner((_, _) => fail ? throw new HttpRequestException("нет движка") : Task.FromResult<string?>("{\"category\":\"арт\"}"));
        var c = new ImageRouteClassifier(runner);
        (await c.ClassifyAsync(ImageEditOp.Generate, "лиса")).Should().BeNull();
        fail = false;
        (await c.ClassifyAsync(ImageEditOp.Generate, "лиса")).Should().Be("арт");
        runner.Calls.Should().Be(2);
    }

    [Fact]
    public async Task Потолок_НеДождались_Null_АОтветДоходитВКэш()
    {
        var gate = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new FakeRunner((_, _) => gate.Task);
        var c = new ImageRouteClassifier(runner, TimeSpan.FromMilliseconds(50));
        (await c.ClassifyAsync(ImageEditOp.Generate, "лиса")).Should().BeNull();
        gate.SetResult("{\"category\":\"арт\"}");
        (await c.ClassifyAsync(ImageEditOp.Generate, "лиса")).Should().Be("арт");
        runner.Calls.Should().Be(1);
    }

    // ── Таблица с категорией ─────────────────────────────────────────────────────

    [Theory]
    [InlineData(ImageEditOp.Generate, "кот на подоконнике", true)]
    [InlineData(ImageEditOp.Generate, "вывеска «Бар»", false)]
    [InlineData(ImageEditOp.Generate, "портрет старика", false)]
    [InlineData(ImageEditOp.Generate, "", false)]
    [InlineData(ImageEditOp.Edit, "дай ему кружку", true)]
    [InlineData(ImageEditOp.Edit, "замени фон", false)]
    public void Классификатор_ТолькоКогдаПравилаМолчат(ImageEditOp op, string prompt, bool expected) =>
        _editor.NeedsClassifier(op, EditMode.Auto, T(prompt)).Should().Be(expected);

    [Theory]
    [InlineData(ImageEditOp.Generate, "люди", LocalImageEditor.Krea2Turbo, "Авто → Krea 2 Turbo: люди (по смыслу запроса)")]
    [InlineData(ImageEditOp.Generate, "товар", LocalImageEditor.Krea2Turbo, "Авто → Krea 2 Turbo: товар (по смыслу запроса)")]
    [InlineData(ImageEditOp.Generate, "надписи", LocalImageEditor.MossImage, "Авто → MOSS: надписи (по смыслу запроса)")]
    [InlineData(ImageEditOp.Generate, "арт", LocalImageEditor.FluxKlein, "Авто → Klein: иллюстрация (по смыслу запроса)")]
    [InlineData(ImageEditOp.Edit, "одежда", LocalImageEditor.QwenImage, "Авто → Qwen «качественно»: одежда (по смыслу запроса)")]
    [InlineData(ImageEditOp.Edit, "стиль", LocalImageEditor.QwenImageTurbo, "Авто → Qwen Turbo: стиль (по смыслу запроса)")]
    [InlineData(ImageEditOp.Edit, "убрать", LocalImageEditor.QwenImageTurbo, "Авто → Qwen Turbo: убрать предмет (по смыслу запроса)")]
    public void Категория_ДобавляетПризнаки_ТаблицаВыбираетМодель(ImageEditOp op, string category, string model, string reason)
    {
        var r = _editor.Route(op, EditMode.Auto, T("что-то без ключевых слов"), category)!;
        r.Model.Id.Should().Be(model);
        r.Reason.Should().Be(reason);
    }

    [Fact]
    public void Категория_НеОтменяетЗапреты_ЛюдиСОбразцом_НеKrea() =>
        _editor.Route(ImageEditOp.Generate, EditMode.Auto, T("кто-то", refs: 1), "люди")!.Model.Id
            .Should().Be(LocalImageEditor.FluxKlein);

    // ── Котировка ────────────────────────────────────────────────────────────────

    private ImageEditJobService Service(ImageRouteClassifier? classifier) =>
        new([_editor], new ImageEditWorkspace(Path.Combine(_dir, "image-editor")),
            NullLogger<ImageEditJobService>.Instance, new MemorySpendStore(), routeClassifier: classifier);

    private static ImageEditQuoteRequest Quote(string? prompt) =>
        new("local", ImageEditCatalog.AutoModelId, EditMode.Auto, ImageEditOp.Generate, 1, false, Prompt: prompt);

    [Fact]
    public async Task Котировка_ПравилаМолчат_РешаетКлассификатор()
    {
        var runner = Answer("{\"category\":\"люди\"}");
        using var service = Service(new ImageRouteClassifier(runner));
        var quote = await service.QuoteAsync("u", "p", Quote("семья лепит пельмени на кухне"), default);
        quote.Value!.Model.Should().Be(LocalImageEditor.Krea2Turbo);
        quote.Value.RouteReason.Should().Be("Авто → Krea 2 Turbo: люди (по смыслу запроса)");
    }

    [Fact]
    public async Task Котировка_ПравилаРешили_КлассификаторНеЗовётся()
    {
        var runner = Answer("{\"category\":\"арт\"}");
        using var service = Service(new ImageRouteClassifier(runner));
        var quote = await service.QuoteAsync("u", "p", Quote("вывеска «Бар»"), default);
        quote.Value!.RouteReason.Should().Be("Авто → MOSS: в запросе надпись");
        runner.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Котировка_КлассификаторМолчит_ВыборПоПравилам()
    {
        using var service = Service(new ImageRouteClassifier(Answer(null)));
        var quote = await service.QuoteAsync("u", "p", Quote("кот на подоконнике"), default);
        quote.Value!.Model.Should().Be(LocalImageEditor.FluxKlein);
        quote.Value.RouteReason.Should().Be("Авто → Klein: по умолчанию");
    }
}
