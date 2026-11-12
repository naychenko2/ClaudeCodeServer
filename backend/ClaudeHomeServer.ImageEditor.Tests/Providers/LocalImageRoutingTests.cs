using ClaudeHomeServer.Services.ImageEditor;
using ClaudeHomeServer.Services.ImageEditor.Controllers;
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
    public void Генерация_ПоУмолчанию_Klein()
    {
        var r = Pick(ImageEditOp.Generate, T("кот на подоконнике"));
        r.Model.Id.Should().Be(LocalImageEditor.FluxKlein);
        r.Reason.Should().Be("Авто → Klein: по умолчанию");
    }

    // Ш10: запасные генерации по умолчанию — MOSS, затем Qwen Turbo
    [Fact]
    public void Генерация_ПоУмолчанию_БезKlein_Moss_БезОбоих_QwenTurbo()
    {
        var noKlein = _editor.Models.Where(m => m.Id != LocalImageEditor.FluxKlein).ToList();
        LocalImageRouting.Pick(LocalImageRouting.Table, noKlein, ImageEditOp.Generate, T("кот"))!.Model.Id
            .Should().Be(LocalImageEditor.MossImage);
        var neither = noKlein.Where(m => m.Id != LocalImageEditor.MossImage).ToList();
        LocalImageRouting.Pick(LocalImageRouting.Table, neither, ImageEditOp.Generate, T("кот"))!.Model.Id
            .Should().Be(LocalImageEditor.QwenImageTurbo);
    }

    [Theory]
    [InlineData("портрет старика у окна")]
    [InlineData("a woman walking in the rain")]
    [InlineData("реалистичное фото улицы")]
    public void Генерация_ЛюдиИФото_Krea(string prompt) =>
        Pick(ImageEditOp.Generate, T(prompt)).Model.Id.Should().Be(LocalImageEditor.Krea2Turbo);

    // Режимов «Быстро / Точно / Фото» у local нет (решение Андрея 12.11): выбор от режима не зависит,
    // а в каталоге у «Авто» local переключателя нет
    [Theory]
    [InlineData(ImageEditOp.Generate, "пейзаж", 0.8)]
    [InlineData(ImageEditOp.Edit, "сделай теплее", 1.0)]
    [InlineData(ImageEditOp.Edit, "поправь", 0.8)]
    public void Режим_НеВлияетНаВыбор(ImageEditOp op, string prompt, double mp)
    {
        var auto = Pick(op, T(prompt, mp: mp));
        foreach (var mode in Enum.GetValues<EditMode>())
            Pick(op, T(prompt, mp: mp), mode).Should().Be(auto);
    }

    [Fact]
    public void Каталог_АвтоLocal_БезРежимов()
    {
        var local = ImageEditCatalog.Build([_editor], null, null).Providers.Single(p => p.Key == LocalImageEditor.ProviderKey);
        local.Models[0].Id.Should().Be(ImageEditCatalog.AutoModelId);
        local.Models[0].Modes.Should().BeNull();
    }

    [Fact]
    public void Генерация_ЛюдиСОбразцом_КреаНеБерётся() =>
        Pick(ImageEditOp.Generate, T("портрет", refs: 1)).Model.Id.Should().Be(LocalImageEditor.FluxKlein);

    [Fact]
    public void Генерация_ЛюдиБезKrea_ЗапасныйKlein()
    {
        var noKrea = _editor.Models.Where(m => m.Id != LocalImageEditor.Krea2Turbo).ToList();
        var r = LocalImageRouting.Pick(LocalImageRouting.Table, noKrea, ImageEditOp.Generate, T("портрет старика"))!;
        r.Model.Id.Should().Be(LocalImageEditor.FluxKlein);
        r.Reason.Should().Be("люди или фото");
    }

    // Ш10: правка стиля мимо Fibo Turbo (3,0 из 5) — даже при маленьком исходнике
    [Theory]
    [InlineData("перерисуй в стиле акварели")]
    [InlineData("make it watercolor style")]
    public void Правка_Стиль_НеFibo(string prompt) =>
        Pick(ImageEditOp.Edit, T(prompt, mp: 0.8)).Model.Id.Should().Be(LocalImageEditor.QwenImageTurbo);

    [Fact]
    public void Генерация_НадписьВКавычках_Moss_ПричинаВОтвете()
    {
        var r = Pick(ImageEditOp.Generate, T("вывеска кафе с текстом «Добро пожаловать»"));
        r.Model.Id.Should().Be(LocalImageEditor.MossImage);
        r.Reason.Should().Be("Авто → MOSS: в запросе надпись");
    }

    [Theory]
    [InlineData("афиша концерта")]
    [InlineData("слайд про продажи")]
    [InlineData("вывеска магазина")]
    public void Генерация_ВывескаАфишаСлайд_Moss(string prompt) =>
        Pick(ImageEditOp.Generate, T(prompt)).Model.Id.Should().Be(LocalImageEditor.MossImage);

    [Fact]
    public void Надпись_Выигрывает_ЗаЛюдей() =>
        Pick(ImageEditOp.Generate, T("девушка с табличкой \"Привет\"")).Model.Id.Should().Be(LocalImageEditor.MossImage);

    [Fact]
    public void Надпись_MossНетВКаталоге_ЗапасноеKlein()
    {
        var catalog = _editor.Models.Where(m => m.Id != LocalImageEditor.MossImage).ToList();
        LocalImageRouting.Pick(LocalImageRouting.Table, catalog, ImageEditOp.Generate, T("«Привет»"))!.Model.Id
            .Should().Be(LocalImageEditor.FluxKlein);
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
    public void Правка_ОдеждаИлиТочная_QwenКачественно()
    {
        Pick(ImageEditOp.Edit, T("перекрась куртку в красный", mp: 0.8)).Model.Id.Should().Be(LocalImageEditor.QwenImage);
        var r = Pick(ImageEditOp.Edit, T("сделай точнее", mp: 0.8));
        r.Model.Id.Should().Be(LocalImageEditor.QwenImage);
        r.Reason.Should().Be("Авто → Qwen «качественно»: одежда или точная правка");
    }

    // Корни «фон» и «точн» ловили «фонарь», «фонтан», «точно» (ревью 29d36971)
    [Theory]
    [InlineData("добавь фонарь у двери")]
    [InlineData("поставь фонтан в саду")]
    [InlineData("поставь точку на карте")]
    [InlineData("сделай точно такой же")]
    public void Правка_ПохожиеСлова_НеПризнакиПравки(string prompt)
    {
        var h = PromptHints.Of(prompt);
        h.Fast.Should().BeFalse();
        h.Precise.Should().BeFalse();
        Pick(ImageEditOp.Edit, T(prompt, mp: 0.8)).Model.Id.Should().Be(LocalImageEditor.QwenImageTurbo);
    }

    [Theory]
    [InlineData("поменяй фон", true, false)]
    [InlineData("на фоне моря", true, false)]
    [InlineData("убери Фон", true, false)]
    [InlineData("сделай точнее", false, true)]
    [InlineData("нужна точная правка руки", false, true)]
    public void Правка_ФонИТочная_Срабатывают(string prompt, bool fast, bool precise)
    {
        var h = PromptHints.Of(prompt);
        h.Fast.Should().Be(fast);
        h.Precise.Should().Be(precise);
    }

    // Незакрытая кавычка в огромном промпте давала откат регулярки и RegexMatchTimeoutException → 500
    [Theory]
    [InlineData("«")]
    [InlineData("\"")]
    [InlineData("“")]
    public async Task Котировка_ДлинныйПромптСНезакрытойКавычкой_Отвечает(string quote)
    {
        var prompt = string.Concat(Enumerable.Repeat(quote + "кот в шляпе ", 5_000));
        var hints = () => PromptHints.Of(prompt);
        hints.Should().NotThrow();
        using var service = Service();
        var quoted = await service.QuoteAsync("u", "p", Quote(ImageEditCatalog.AutoModelId, prompt), default);
        quoted.Value.Should().NotBeNull();
    }

    [Fact]
    public void Признаки_ТолькоПоНачалуПромпта()
    {
        PromptHints.Of(new string('х', PromptHints.MaxPromptChars) + " вывеска «Бар»").Should().Be(default(PromptHints));
        PromptHints.Of("вывеска «Бар» " + new string('х', 50_000)).QuotedText.Should().BeTrue();
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
    public void МаскаДорисовкаПодстановкаНаЛицо_QwenTurbo()
    {
        Pick(ImageEditOp.Inpaint, T("убери")).Model.Id.Should().Be(LocalImageEditor.QwenImageTurbo);
        Pick(ImageEditOp.Outpaint, T()).Model.Id.Should().Be(LocalImageEditor.QwenImageTurbo);
        Pick(ImageEditOp.Edit, T("замени фон", mask: true, mp: 0.5)).Model.Id.Should().Be(LocalImageEditor.QwenImageTurbo);
        var swap = Pick(ImageEditOp.Edit, T(character: true, faceSwap: true));
        swap.Model.Id.Should().Be(LocalImageEditor.QwenImageTurbo);
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
    public async Task Котировка_АвтоНадпись_Moss_ПричинаВОтвете()
    {
        using var service = Service();
        var quote = await service.QuoteAsync("u", "p", Quote(ImageEditCatalog.AutoModelId, "вывеска «Бар»"), default);
        quote.Value!.Model.Should().Be(LocalImageEditor.MossImage);
        quote.Value.RouteReason.Should().Be("Авто → MOSS: в запросе надпись");
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
    public async Task Котировка_ПодстановкаНаЛицо_QwenTurbo()
    {
        using var service = Service();
        var faceTarget = new FaceTarget("v1", "k", [new FaceAssignment(1, "slug")]);
        var req = Quote(ImageEditCatalog.AutoModelId, null, ImageEditOp.Edit) with { FaceTarget = faceTarget, HasMask = true };
        var quote = await service.QuoteAsync("u", "p", req, default);
        quote.Value!.Model.Should().Be(LocalImageEditor.QwenImageTurbo);
        quote.Value.RouteReason.Should().Contain("подстановка на лицо");
    }

    // Чип «Формат» при «Авто» показывал формат умолчания, хотя по тексту «Авто» уходит на другую модель
    [Fact]
    public async Task ЧипФормата_АвтоСПромптом_МодельКакВКотировке()
    {
        using var service = Service();
        var scope = new ImageEditScope("p", null) { TenantOwnerId = "u" };
        var byText = await ImageEditorEndpoints.FormatModelAsync(_editor, service, scope, ImageEditOp.Generate,
            ImageEditCatalog.AutoModelId, "портрет старика у окна", default);
        var noText = await ImageEditorEndpoints.FormatModelAsync(_editor, service, scope, ImageEditOp.Generate,
            ImageEditCatalog.AutoModelId, null, default);
        var quoted = await service.QuoteAsync("u", "p", Quote(ImageEditCatalog.AutoModelId, "портрет старика у окна"), default);

        byText!.Id.Should().Be(quoted.Value!.Model).And.Be(LocalImageEditor.Krea2Turbo);
        noText!.Id.Should().Be(LocalImageEditor.FluxKlein);
    }
}
