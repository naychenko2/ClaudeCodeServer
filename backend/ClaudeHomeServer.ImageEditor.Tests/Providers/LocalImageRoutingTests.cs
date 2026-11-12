using ClaudeHomeServer.Services.ImageEditor;
using ClaudeHomeServer.Tests.ImageEditor.Fakes;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.ImageEditor.Providers;

// Таблица авто-выбора локальных моделей (Ш2 эпика 8b23149d): по тесту на каждое правило,
// запрет Fibo Dev и H3 в «Авто», порог 1 Мп у Fibo Turbo, причина в котировке, приоритет ручного выбора
public class LocalImageRoutingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ie-route-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly LocalImageEditor _editor = new(new LocalImageEditorTests.FakeMedia());

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (DirectoryNotFoundException) { }
        GC.SuppressFinalize(this);
    }

    private static EditTraits T(string? prompt = null, int refs = 0, bool mask = false, bool character = false,
        bool annotations = false, double? mp = null, bool faceSwap = false) =>
        new(mask, refs, character, annotations, false, prompt, mp, faceSwap);

    private ImageRoute Pick(ImageEditOp op, EditTraits t, EditMode mode = EditMode.Auto) =>
        _editor.Route(op, mode, t) ?? throw new InvalidOperationException("маршрута нет");

    [Fact]
    public void Генерация_ПоУмолчанию_QwenTurbo()
    {
        var r = Pick(ImageEditOp.Generate, T("кот на подоконнике"));
        r.Model.Id.Should().Be(LocalImageEditor.QwenImageTurbo);
        r.Reason.Should().Be("Авто → Qwen Turbo: по умолчанию");
    }

    [Theory]
    [InlineData("портрет старика у окна")]
    [InlineData("a woman walking in the rain")]
    [InlineData("реалистичное фото улицы")]
    public void Генерация_ЛюдиИФото_Krea(string prompt) =>
        Pick(ImageEditOp.Generate, T(prompt)).Model.Id.Should().Be(LocalImageEditor.Krea2Turbo);

    [Fact]
    public void Генерация_РежимФотореализм_Krea() =>
        Pick(ImageEditOp.Generate, T("пейзаж"), EditMode.Photoreal).Model.Id.Should().Be(LocalImageEditor.Krea2Turbo);

    [Fact]
    public void Генерация_ЛюдиСОбразцом_КреаНеБерётся() =>
        Pick(ImageEditOp.Generate, T("портрет", refs: 1)).Model.Id.Should().Be(LocalImageEditor.QwenImageTurbo);

    [Fact]
    public void Генерация_НадписьВКавычках_Klein_ПричинаВОтвете()
    {
        var r = Pick(ImageEditOp.Generate, T("вывеска кафе с текстом «Добро пожаловать»"));
        r.Model.Id.Should().Be(LocalImageEditor.FluxKlein);
        r.Reason.Should().Be("Авто → Klein: в запросе надпись");
    }

    [Theory]
    [InlineData("афиша концерта")]
    [InlineData("слайд про продажи")]
    [InlineData("вывеска магазина")]
    public void Генерация_ВывескаАфишаСлайд_Klein(string prompt) =>
        Pick(ImageEditOp.Generate, T(prompt)).Model.Id.Should().Be(LocalImageEditor.FluxKlein);

    [Fact]
    public void Надпись_Выигрывает_ЗаЛюдей() =>
        Pick(ImageEditOp.Generate, T("девушка с табличкой \"Привет\"")).Model.Id.Should().Be(LocalImageEditor.FluxKlein);

    [Fact]
    public void Надпись_КleinНетВКаталоге_ЗапасноеMoss()
    {
        var catalog = _editor.Models.Where(m => m.Id != LocalImageEditor.FluxKlein).ToList();
        LocalImageRouting.Pick(catalog, ImageEditOp.Generate, EditMode.Auto, T("«Привет»"))!.Model.Id
            .Should().Be(LocalImageEditor.MossImage);
    }

    [Fact]
    public void Генерация_Персонаж_QwenTurbo_БезПотериLoRA() =>
        Pick(ImageEditOp.Generate, T("в парке «Привет»", character: true)).Model.Id.Should().Be(LocalImageEditor.QwenImageTurbo);

    [Fact]
    public void Правка_БыстраяНебольшойИсходник_FiboTurbo()
    {
        var r = Pick(ImageEditOp.Edit, T("замени фон на пляж", mp: 0.9));
        r.Model.Id.Should().Be(LocalImageEditor.Fibo2Turbo);
        r.Reason.Should().Be("Авто → Fibo Turbo: быстрая правка");
    }

    [Fact]
    public void Правка_РежимБыстрая_FiboTurbo() =>
        Pick(ImageEditOp.Edit, T("сделай теплее", mp: 1.0), EditMode.Fast).Model.Id.Should().Be(LocalImageEditor.Fibo2Turbo);

    [Theory]
    [InlineData(2.0)]
    [InlineData(1.5)]
    [InlineData(null)]
    public void Правка_ИсходникЗаметноБольше1Мп_ИлиРазмерНеизвестен_FiboНеБерётся(double? mp)
    {
        var r = Pick(ImageEditOp.Edit, T("замени фон на пляж", mp: mp));
        r.Model.Id.Should().Be(LocalImageEditor.QwenImageTurbo);
    }

    [Fact]
    public void Правка_ОдеждаИлиТочная_Klein()
    {
        Pick(ImageEditOp.Edit, T("перекрась куртку в красный", mp: 0.8)).Model.Id.Should().Be(LocalImageEditor.FluxKlein);
        Pick(ImageEditOp.Edit, T("поправь", mp: 0.8), EditMode.Precise).Model.Id.Should().Be(LocalImageEditor.FluxKlein);
    }

    [Fact]
    public void Правка_Надпись_Klein()
    {
        var r = Pick(ImageEditOp.Edit, T("замени надпись на «Открыто»", mp: 0.8));
        r.Model.Id.Should().Be(LocalImageEditor.FluxKlein);
        r.Reason.Should().Be("Авто → Klein: в запросе надпись");
    }

    [Fact]
    public void Правка_ПоУмолчанию_QwenTurbo() =>
        Pick(ImageEditOp.Edit, T("сделай ярче", mp: 0.8)).Model.Id.Should().Be(LocalImageEditor.QwenImageTurbo);

    [Fact]
    public void Правка_СПерсонажем_QwenTurbo_ДажеЕслиФон() =>
        Pick(ImageEditOp.Edit, T("замени фон", mp: 0.8, character: true)).Model.Id.Should().Be(LocalImageEditor.QwenImageTurbo);

    [Fact]
    public void Правка_СОбразцомИлиПометками_FiboНеБерётся()
    {
        Pick(ImageEditOp.Edit, T("замени фон", refs: 3, mp: 0.8)).Model.Id.Should().Be(LocalImageEditor.QwenImageTurbo);
        Pick(ImageEditOp.Edit, T("замени фон", annotations: true, mp: 0.8)).Model.Id.Should().Be(LocalImageEditor.QwenImageTurbo);
    }

    [Fact]
    public void МаскаДорисовкаПодстановкаНаЛицо_QwenКачественно()
    {
        Pick(ImageEditOp.Inpaint, T("убери")).Model.Id.Should().Be(LocalImageEditor.QwenImage);
        Pick(ImageEditOp.Outpaint, T()).Model.Id.Should().Be(LocalImageEditor.QwenImage);
        Pick(ImageEditOp.Edit, T("замени фон", mask: true, mp: 0.5)).Model.Id.Should().Be(LocalImageEditor.QwenImage);
        var swap = Pick(ImageEditOp.Edit, T(character: true, faceSwap: true));
        swap.Model.Id.Should().Be(LocalImageEditor.QwenImage);
        swap.Reason.Should().Contain("подстановка на лицо");
    }

    [Fact]
    public void УлучшитьЛица_FaceDetailer() =>
        _editor.PickModel(ImageEditOp.EnhanceFaces, EditMode.Auto, T()).Should().NotBeNull()
            .And.Subject.As<ImageEditModelInfo>().Id.Should().Be(LocalImageEditor.FaceDetailer);

    [Fact]
    public void FiboDevИH3_ВАвтоНеУчаствуют()
    {
        var banned = new[] { LocalImageEditor.Fibo2Dev, LocalImageEditor.MiniMaxH3Image };
        string[] prompts = ["", "портрет", "«Привет»", "афиша", "замени фон", "перекрась куртку"];
        foreach (var op in new[] { ImageEditOp.Generate, ImageEditOp.Edit, ImageEditOp.Inpaint, ImageEditOp.Outpaint })
        foreach (var mode in Enum.GetValues<EditMode>())
        foreach (var prompt in prompts)
        foreach (var mp in new double?[] { null, 0.5, 4 })
        foreach (var character in new[] { false, true })
            _editor.PickModel(op, mode, T(prompt, mp: mp, character: character))!.Id.Should().NotBe(banned[0]).And.NotBe(banned[1]);
        LocalImageRouting.Table.SelectMany(r => r.Models).Should().NotContain(banned);
    }

    [Fact]
    public void КаждаяМодельТаблицы_УмеетОперациюПравила()
    {
        foreach (var rule in LocalImageRouting.Table)
        foreach (var id in rule.Models)
        foreach (var op in rule.Ops)
        {
            var model = _editor.Models.Single(m => m.Id == id);
            // Правило вправе перечислять шире, чем умеет первая модель, — но запасной среди Models должен быть
            if (!model.Caps.Ops.Contains(op))
                rule.Models.Select(x => _editor.Models.Single(m => m.Id == x)).Should()
                    .Contain(m => m.Caps.Ops.Contains(op), $"правило {rule.Id}, операция {op}");
        }
    }

    // ── Котировка: причина и приоритет ручного выбора ────────────────────────────

    private ImageEditJobService Service()
    {
        var service = new ImageEditJobService([_editor], new ImageEditWorkspace(Path.Combine(_dir, "image-editor")),
            NullLogger<ImageEditJobService>.Instance, new MemorySpendStore());
        return service;
    }

    private static ImageEditQuoteRequest Quote(string model, string? prompt, ImageEditOp op = ImageEditOp.Generate) =>
        new("local", model, EditMode.Auto, op, 1, false, Prompt: prompt);

    [Fact]
    public async Task Котировка_АвтоНадпись_Klein_ПричинаВОтвете()
    {
        using var service = Service();
        var quote = await service.QuoteAsync("u", "p", Quote(ImageEditCatalog.AutoModelId, "вывеска «Бар»"), default);
        quote.Value!.Model.Should().Be(LocalImageEditor.FluxKlein);
        quote.Value.RouteReason.Should().Be("Авто → Klein: в запросе надпись");
    }

    [Fact]
    public async Task Котировка_РучнойВыбор_ГлавнееАвто_БезПричины()
    {
        using var service = Service();
        var quote = await service.QuoteAsync("u", "p", Quote(LocalImageEditor.Krea2Turbo, "вывеска «Бар»"), default);
        quote.Value!.Model.Should().Be(LocalImageEditor.Krea2Turbo);
        quote.Value.RouteReason.Should().BeNull();
    }

    [Fact]
    public async Task Котировка_РучнойВыборH3_Допустим_ХотяВАвтоЕгоНет()
    {
        using var service = Service();
        var quote = await service.QuoteAsync("u", "p", Quote(LocalImageEditor.MiniMaxH3Image, "кот"), default);
        quote.Value!.Model.Should().Be(LocalImageEditor.MiniMaxH3Image);
    }

    [Fact]
    public async Task Котировка_РазмерИсходника_ЗаметноБольше1Мп_FiboНеБерётся()
    {
        using var service = Service();
        var small = Quote(ImageEditCatalog.AutoModelId, "замени фон", ImageEditOp.Edit) with { Width = 1000, Height = 900 };
        var big = small with { Width = 2000, Height = 1500 };
        (await service.QuoteAsync("u", "p", small, default)).Value!.Model.Should().Be(LocalImageEditor.Fibo2Turbo);
        (await service.QuoteAsync("u", "p", big, default)).Value!.Model.Should().Be(LocalImageEditor.QwenImageTurbo);
    }

    [Fact]
    public async Task Котировка_ПодстановкаНаЛицо_QwenКачественно()
    {
        using var service = Service();
        var faceTarget = new FaceTarget("v1", "k", [new FaceAssignment(1, "slug")]);
        var req = Quote(ImageEditCatalog.AutoModelId, null, ImageEditOp.Edit) with { FaceTarget = faceTarget, HasMask = true };
        var quote = await service.QuoteAsync("u", "p", req, default);
        quote.Value!.Model.Should().Be(LocalImageEditor.QwenImage);
        quote.Value.RouteReason.Should().Contain("подстановка на лицо");
    }
}
