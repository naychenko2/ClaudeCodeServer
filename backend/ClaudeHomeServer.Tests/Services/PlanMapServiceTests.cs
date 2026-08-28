using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Llm;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services;

// Тесты шагов 5–9 плана «Контекстные замечания к плану + визуальный разворот» (часть B):
// место plan-map в каталоге, две обязательные валидации (потолок флагнутых блоков и якоря),
// кэш по SHA-256 текста per-owner, тихий отказ при кривом ответе модели, защита от
// повторного клика. Сборка сервиса не требует SessionManager — только раннер и конфиг.
public class PlanMapServiceTests : IDisposable
{
    private const string Owner = "test-user-id";
    private const string OtherOwner = "другой-владелец";

    private readonly string _tempDir;

    public PlanMapServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "plan_map_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    // ─── Место каталога ───────────────────────────────────────────────────────

    [Fact]
    public void МестоPlanMap_Зарегистрировано_LargeВГруппеСессии()
    {
        var place = LocalActionCatalog.Find(LocalActionCatalog.PlanMap);

        place.Should().NotBeNull();
        place!.Profile.Should().Be(CheapProfile.Large);
        place.Group.Should().Be("Сессии");
        place.Agentic.Should().BeFalse();
        place.DefaultLocal.Should().BeFalse();
        place.Title.Should().Be("Карта плана");
    }

    // ─── Валидация 1: потолок флагнутых блоков ────────────────────────────────

    [Fact]
    public async Task BuildMap_ФлаговНеБольшеПяти_ЛишниеСняты()
    {
        var json = MapJson(Blocks(
            Flagged("b1", "Контекст"),
            Flagged("b2", "Состав работ"),
            Flagged("b3", "Шаг 1: белый список"),
            Flagged("b4", "Шаг 2: подбор"),
            Flagged("b5", "Проверка"),
            Flagged("b6", "Риски"),
            Flagged("b7", "Границы")));
        var runner = new CountingRunner(json);
        var sut = BuildSut(runner);

        var map = await sut.BuildMapAsync(Owner, SamplePlan, CancellationToken.None);

        map.Should().NotBeNull();
        map!.Blocks.Should().HaveCount(7, "блоки остаются — снимаются только флаги");
        map.Blocks.Count(b => b.Flags.Count > 0).Should().Be(PlanMapService.MaxFlaggedBlocks);
        // первые пять по порядку модели — с флагами, хвост — без
        map.Blocks.Take(5).Should().OnlyContain(b => b.Flags.Count > 0);
        map.Blocks.Skip(5).Should().OnlyContain(b => b.Flags.Count == 0);
    }

    [Fact]
    public async Task BuildMap_ФлагиНормализуются_НеизвестныеОтброшены()
    {
        var json = """
            {"genre":"волшебство","oneLine":"План.","numbers":[],"blocks":[
              {"id":"b1","title":"Контекст","type":"mega-step","flags":["blocking","срочно"],"anchor":"Контекст","dependsOn":[]}]}
            """;
        var sut = BuildSut(new CountingRunner(json));

        var map = await sut.BuildMapAsync(Owner, SamplePlan, CancellationToken.None);

        map.Should().NotBeNull();
        map!.Genre.Should().Be("feature", "неизвестный жанр нормализуется в нейтральный");
        map.Blocks[0].Type.Should().Be("step", "неизвестный тип нормализуется");
        map.Blocks[0].Flags.Should().BeEquivalentTo(["blocking"], "неизвестный флаг отброшен");
    }

    // ─── Валидация 2: якорь обязан быть заголовком плана ──────────────────────

    [Fact]
    public async Task BuildMap_НесуществующийЯкорь_БлокОтброшен()
    {
        var json = """
            {"genre":"feature","oneLine":"План.","numbers":[],"blocks":[
              {"id":"b1","title":"Контекст","type":"step","flags":[],"anchor":"Контекст","dependsOn":[]},
              {"id":"b2","title":"Выдуманный раздел","type":"risk","flags":["blocking"],"anchor":"Такого раздела нет","dependsOn":["b1"]}]}
            """;
        var sut = BuildSut(new CountingRunner(json));

        var map = await sut.BuildMapAsync(Owner, SamplePlan, CancellationToken.None);

        map.Should().NotBeNull();
        map!.Blocks.Should().ContainSingle(b => b.Id == "b1");
        map.Blocks.Should().NotContain(b => b.Id == "b2");
    }

    [Fact]
    public async Task BuildMap_ЗависимостьНаОтброшенныйБлок_Убирается()
    {
        var json = """
            {"genre":"feature","oneLine":"План.","numbers":[],"blocks":[
              {"id":"b1","title":"Контекст","type":"step","flags":[],"anchor":"Контекст","dependsOn":[]},
              {"id":"b2","title":"Выдумка","type":"step","flags":[],"anchor":"Нет такого","dependsOn":[]},
              {"id":"b3","title":"Состав работ","type":"step","flags":[],"anchor":"Состав работ","dependsOn":["b2"]}]}
            """;
        var sut = BuildSut(new CountingRunner(json));

        var map = await sut.BuildMapAsync(Owner, SamplePlan, CancellationToken.None);

        map!.Blocks.Single(b => b.Id == "b3").DependsOn.Should().BeEmpty(
            "ссылка на отброшенный блок не должна висеть мёртвой");
    }

    [Fact]
    public async Task BuildMap_ВсеЯкоряМимо_Null()
    {
        var json = """
            {"genre":"feature","oneLine":"План.","numbers":[],"blocks":[
              {"id":"b1","title":"Выдумка","type":"step","flags":[],"anchor":"Нет такого","dependsOn":[]}]}
            """;
        var sut = BuildSut(new CountingRunner(json));

        var map = await sut.BuildMapAsync(Owner, SamplePlan, CancellationToken.None);

        map.Should().BeNull("карта без блоков бесполезна — фронт остаётся на тексте");
    }

    // ─── Кэш per-owner по SHA-256 текста ──────────────────────────────────────

    [Fact]
    public async Task BuildMap_Кэш_ПовторныйКликНеЗовётМодель()
    {
        var runner = new CountingRunner(ValidJson);
        var sut = BuildSut(runner);

        var first = await sut.BuildMapAsync(Owner, SamplePlan, CancellationToken.None);
        var second = await sut.BuildMapAsync(Owner, SamplePlan, CancellationToken.None);

        runner.Calls.Should().Be(1);
        second.Should().BeEquivalentTo(first);
    }

    [Fact]
    public async Task BuildMap_ДругойТекст_НовыйВызов()
    {
        var runner = new CountingRunner(ValidJson);
        var sut = BuildSut(runner);

        await sut.BuildMapAsync(Owner, SamplePlan, CancellationToken.None);
        await sut.BuildMapAsync(Owner, SamplePlan + "\n## Новый раздел\n", CancellationToken.None);

        runner.Calls.Should().Be(2, "ключ кэша — хеш текста: новый текст = новая карта");
    }

    [Fact]
    public async Task BuildMap_ДругойВладелец_ЧужойКэшНеЧитается()
    {
        var runner = new CountingRunner(ValidJson);
        var sut = BuildSut(runner);

        await sut.BuildMapAsync(Owner, SamplePlan, CancellationToken.None);
        await sut.BuildMapAsync(OtherOwner, SamplePlan, CancellationToken.None);

        runner.Calls.Should().Be(2, "слот моделей у каждого владельца свой — чужую карту не показываем");
    }

    [Fact]
    public async Task BuildMap_КэшПереживаетПересозданиеСервиса()
    {
        var runner = new CountingRunner(ValidJson);
        await BuildSut(runner).BuildMapAsync(Owner, SamplePlan, CancellationToken.None);

        // рестарт бэкенда: стор на диске, второй вызов не платит модели
        var reloaded = BuildSut(new CountingRunner(ValidJson));
        var map = await reloaded.BuildMapAsync(Owner, SamplePlan, CancellationToken.None);

        map.Should().NotBeNull();
    }

    // ─── Тихий отказ ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("модель проговорилась вместо JSON")]
    [InlineData("{\"genre\":\"feature\",\"oneLine\":\"обрывано")]
    [InlineData("")]
    public async Task BuildMap_КривойОтвет_NullБезИсключения(string raw)
    {
        var sut = BuildSut(new CountingRunner(raw));

        var act = () => sut.BuildMapAsync(Owner, SamplePlan, CancellationToken.None);

        await act.Should().NotThrowAsync("любой сбой молчит: фронт остаётся на тексте");
        (await act()).Should().BeNull();
    }

    [Fact]
    public async Task BuildMap_ОтветВКодовомЗаборе_Разобран()
    {
        // Модель обернула JSON в ```json — забор не мешает разбору (ExtractJsonObject)
        var raw = "```json\n{\"genre\":\"fix\",\"oneLine\":\"Починка\",\"numbers\":[],\"blocks\":[\n" +
                  "  {\"id\":\"b1\",\"title\":\"Риски\",\"type\":\"risk\",\"flags\":[],\"anchor\":\"Риски\",\"dependsOn\":[]}]}\n```";
        var sut = BuildSut(new CountingRunner(raw));

        var map = await sut.BuildMapAsync(Owner, SamplePlan, CancellationToken.None);

        map.Should().NotBeNull();
        map!.Genre.Should().Be("fix");
        map.Blocks.Should().ContainSingle(b => b.Anchor == "Риски");
    }

    [Fact]
    public async Task BuildMap_ОтказМодели_NullБезИсключения()
    {
        var sut = BuildSut(new ThrowingRunner());

        var map = await sut.BuildMapAsync(Owner, SamplePlan, CancellationToken.None);

        map.Should().BeNull();
    }

    [Fact]
    public async Task BuildMap_ПустойПлан_NullБезВызова()
    {
        var runner = new CountingRunner(ValidJson);
        var sut = BuildSut(runner);

        (await sut.BuildMapAsync(Owner, "   ", CancellationToken.None)).Should().BeNull();
        runner.Calls.Should().Be(0);
    }

    // ─── Защита от повторного клика ───────────────────────────────────────────

    [Fact]
    public async Task BuildMap_ПараллельныеКлики_ОдинВызовМодели()
    {
        var runner = new GatedRunner(ValidJson);
        var sut = BuildSut(runner);

        var first = sut.BuildMapAsync(Owner, SamplePlan, CancellationToken.None);
        var second = () => sut.BuildMapAsync(Owner, SamplePlan, CancellationToken.None);
        await second.Should().ThrowExactlyAsync<PlanMapInProgressException>();

        runner.Release();
        var map = await first;

        runner.Calls.Should().Be(1);
        map.Should().NotBeNull();
    }

    // ─── Заголовки markdown ───────────────────────────────────────────────────

    [Fact]
    public void ExtractHeadingOccurrences_СнимаетРешётки_иПропускаетКод_иСохраняетПорядок()
    {
        var md = """
            # План: значки

            ## Состав работ

            ```text
            ## не заголовок
            ```

            ### Шаг 1: список
            """;
        var headings = PlanMapService.ExtractHeadingOccurrences(md);

        headings.Should().ContainInOrder("План: значки", "Состав работ", "Шаг 1: список");
    }

    // ─── AnchorIndex: номер вхождения одноимённого заголовка ──────────────────

    [Fact]
    public async Task BuildMap_ДваБлокаСОдинаковымЯкорем_ПолучаютAnchorIndex0и1()
    {
        var json = MapJson(Blocks(
            """{"id":"b1","title":"Тесты #1","type":"step","flags":[],"anchor":"Тесты","dependsOn":[]}""",
            """{"id":"b2","title":"Тесты #2","type":"step","flags":[],"anchor":"Тесты","dependsOn":[]}"""));
        var sut = BuildSut(new CountingRunner(json));

        var map = await sut.BuildMapAsync(Owner, PlanWithTwoTests, CancellationToken.None);

        map.Should().NotBeNull();
        map!.Blocks.Should().HaveCount(2);
        map.Blocks[0].AnchorIndex.Should().Be(0, "первый блок с якорем «Тесты» — нулевое вхождение");
        map.Blocks[1].AnchorIndex.Should().Be(1, "второй блок с тем же якорем — следующее вхождение");
    }

    [Fact]
    public async Task BuildMap_УникальныйЯкорь_ПолучаетAnchorIndex0()
    {
        var json = ValidJson; // «Контекст» и «Состав работ» — по одному разу в SamplePlan
        var sut = BuildSut(new CountingRunner(json));

        var map = await sut.BuildMapAsync(Owner, SamplePlan, CancellationToken.None);

        map.Should().NotBeNull();
        map!.Blocks.Should().OnlyContain(b => b.AnchorIndex == 0);
    }

    [Fact]
    public async Task BuildMap_БлоковБольшеЧемВхождений_ЛишниеОтбрасываются()
    {
        // В плане PlanWithTwoTests «Тесты» встречается дважды, третий блок с тем же якорем
        // должен быть отброшен (как и блок с несуществующим якорем — Anchor обязан существовать)
        var json = MapJson(Blocks(
            """{"id":"b1","title":"Тесты #1","type":"step","flags":[],"anchor":"Тесты","dependsOn":[]}""",
            """{"id":"b2","title":"Тесты #2","type":"step","flags":[],"anchor":"Тесты","dependsOn":[]}""",
            """{"id":"b3","title":"Тесты #3","type":"step","flags":[],"anchor":"Тесты","dependsOn":[]}"""));
        var sut = BuildSut(new CountingRunner(json));

        var map = await sut.BuildMapAsync(Owner, PlanWithTwoTests, CancellationToken.None);

        map.Should().NotBeNull();
        map!.Blocks.Should().HaveCount(2, "третий блок «Тесты» уходит — в плане только два вхождения");
        map.Blocks.Select(b => b.AnchorIndex).Should().BeEquivalentTo([0, 1]);
    }

    [Fact]
    public async Task BuildMap_ОдинБлокСДублем_БерётПервоеВхождение()
    {
        // Модель вернула ОДИН блок с якорем «Тесты», а в плане их два — берётся первое
        // вхождение (угадать намерение модели нельзя). Блок всё равно ведёт в раздел
        // с верным заголовком, AnchorIndex == 0.
        var json = MapJson(Blocks(
            """{"id":"b1","title":"Тесты","type":"step","flags":[],"anchor":"Тесты","dependsOn":[]}"""));
        var sut = BuildSut(new CountingRunner(json));

        var map = await sut.BuildMapAsync(Owner, PlanWithTwoTests, CancellationToken.None);

        map.Should().NotBeNull();
        map!.Blocks.Should().ContainSingle();
        map.Blocks[0].AnchorIndex.Should().Be(0, "один блок на дубль — первое вхождение");
    }

    [Fact]
    public void ParseAndValidate_ПорядокНазначенияДетерминирован()
    {
        // Тот же вход даёт тот же AnchorIndex при повторе
        var json = """
            {"genre":"feature","oneLine":"План.","numbers":[],"blocks":[
              {"id":"b1","title":"X","type":"step","flags":[],"anchor":"Тесты","dependsOn":[]},
              {"id":"b2","title":"Y","type":"step","flags":[],"anchor":"Дизайн","dependsOn":[]},
              {"id":"b3","title":"Z","type":"step","flags":[],"anchor":"Тесты","dependsOn":[]},
              {"id":"b4","title":"W","type":"step","flags":[],"anchor":"Дизайн","dependsOn":[]}]}
            """;
        const string plan = """
            # P

            ## Тесты
            text

            ## Дизайн
            text

            ## Тесты
            text

            ## Дизайн
            text
            """;

        var first = PlanMapService.ParseAndValidate(json, plan);
        var second = PlanMapService.ParseAndValidate(json, plan);

        first.Should().NotBeNull();
        second.Should().NotBeNull();
        first!.Blocks.Select(b => (b.Id, b.Anchor, b.AnchorIndex)).Should().Equal(
            second!.Blocks.Select(b => (b.Id, b.Anchor, b.AnchorIndex)),
            "тот же вход — тот же порядок назначения AnchorIndex");
        // Назначение идёт по порядку массива: первый «Тесты» → 0, первый «Дизайн» → 0,
        // второй «Тесты» → 1, второй «Дизайн» → 1
        first.Blocks.Should().SatisfyRespectively(
            b => { b.Id.Should().Be("b1"); b.AnchorIndex.Should().Be(0); },
            b => { b.Id.Should().Be("b2"); b.AnchorIndex.Should().Be(0); },
            b => { b.Id.Should().Be("b3"); b.AnchorIndex.Should().Be(1); },
            b => { b.Id.Should().Be("b4"); b.AnchorIndex.Should().Be(1); });
    }

    // ─── Фикстуры ─────────────────────────────────────────────────────────────

    private PlanMapService BuildSut(ICheapTextRunner runner)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(_tempDir, "projects.json"),
        }).Build();
        return new PlanMapService(config, runner, NullLogger<PlanMapService>.Instance);
    }

    private const string SamplePlan = """
        # План: значки проектов

        ## Контекст
        Значки подбираются моделью по названию проекта.

        ## Состав работ
        ### Шаг 1: белый список
        ### Шаг 2: подбор

        ## Проверка
        Гейт lint:design.

        ## Риски
        Модель выдумывает имена.

        ## Границы
        Рисование path'ов не делаем.
        """;

    // План с одноимёнными заголовками — имитирует план из двух независимых задач
    // («Тесты» и «Дизайн» встречаются по два раза)
    private const string PlanWithTwoTests = """
        # План: двойка

        ## Контекст
        Задача А и задача Б.

        ## Тесты
        Тесты для задачи А.

        ## Дизайн
        Дизайн для задачи А.

        ## Тесты
        Тесты для задачи Б.

        ## Дизайн
        Дизайн для задачи Б.
        """;

    private const string ValidJson = """
        {"genre":"feature","oneLine":"Значки проектов из белого списка","numbers":[{"value":"2","label":"шага"}],"blocks":[
          {"id":"b1","title":"Контекст","type":"step","flags":[],"anchor":"Контекст","dependsOn":[]},
          {"id":"b2","title":"Состав работ","type":"step","flags":["blocking"],"anchor":"Состав работ","dependsOn":["b1"]}]}
        """;

    private static string MapJson(string blocks) =>
        $$"""{"genre":"feature","oneLine":"План.","numbers":[],"blocks":[{{blocks}}]}""";

    private static string Blocks(params string[] items) => string.Join(",", items);

    private static string Flagged(string id, string anchor) =>
        $$"""{"id":"{{id}}","title":"{{anchor}}","type":"step","flags":["blocking"],"anchor":"{{anchor}}","dependsOn":[]}""";

    // Стаб раннера: считает вызовы, отвечает фиксированным текстом
    private sealed class CountingRunner(string answer) : ICheapTextRunner
    {
        public int Calls;

        public bool UsesLocal(string actionKey) => false;
        public string DescribeRoute(string actionKey, string? fallbackModel) => "stub";

        public Task<string> RunAsync(string actionKey, string prompt, string? fallbackModel = null,
            string? ownerId = null, object? jsonFormat = null, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(answer);
        }

        public Task<string?> RunFreeAsync(string actionKey, string prompt, object? jsonFormat = null,
            CancellationToken ct = default) => Task.FromResult<string?>(answer);

        public Task<string?> RunLocalOnlyAsync(string actionKey, string prompt,
            CancellationToken ct = default) => Task.FromResult<string?>(null);

        public Task<OneShotResult> RunDetailedAsync(string actionKey, string prompt,
            string? fallbackModel = null, string? ownerId = null, TimeSpan? timeout = null,
            int? maxTokens = null, object? jsonFormat = null, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    // Стаб раннера, падающий как умерший провайдер
    private sealed class ThrowingRunner : ICheapTextRunner
    {
        public bool UsesLocal(string actionKey) => false;
        public string DescribeRoute(string actionKey, string? fallbackModel) => "stub";

        public Task<string> RunAsync(string actionKey, string prompt, string? fallbackModel = null,
            string? ownerId = null, object? jsonFormat = null, CancellationToken ct = default) =>
            throw new InvalidOperationException("провайдер недоступен");

        public Task<string?> RunFreeAsync(string actionKey, string prompt, object? jsonFormat = null,
            CancellationToken ct = default) => Task.FromResult<string?>(null);

        public Task<string?> RunLocalOnlyAsync(string actionKey, string prompt,
            CancellationToken ct = default) => Task.FromResult<string?>(null);

        public Task<OneShotResult> RunDetailedAsync(string actionKey, string prompt,
            string? fallbackModel = null, string? ownerId = null, TimeSpan? timeout = null,
            int? maxTokens = null, object? jsonFormat = null, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    // Стаб раннера с гейтом: вызов модели висит, пока тест не отпустит
    private sealed class GatedRunner(string answer) : ICheapTextRunner
    {
        public int Calls;
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _release.TrySetResult();

        public bool UsesLocal(string actionKey) => false;
        public string DescribeRoute(string actionKey, string? fallbackModel) => "stub";

        public async Task<string> RunAsync(string actionKey, string prompt, string? fallbackModel = null,
            string? ownerId = null, object? jsonFormat = null, CancellationToken ct = default)
        {
            Calls++;
            await _release.Task.WaitAsync(ct);
            return answer;
        }

        public Task<string?> RunFreeAsync(string actionKey, string prompt, object? jsonFormat = null,
            CancellationToken ct = default) => Task.FromResult<string?>(answer);

        public Task<string?> RunLocalOnlyAsync(string actionKey, string prompt,
            CancellationToken ct = default) => Task.FromResult<string?>(null);

        public Task<OneShotResult> RunDetailedAsync(string actionKey, string prompt,
            string? fallbackModel = null, string? ownerId = null, TimeSpan? timeout = null,
            int? maxTokens = null, object? jsonFormat = null, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
