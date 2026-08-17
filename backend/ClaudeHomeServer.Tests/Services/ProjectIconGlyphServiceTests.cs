using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.ProjectIcons;
using ClaudeHomeServer.Tests.Helpers;
using Microsoft.Extensions.Logging;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

// Место применения «Значок проекта» в каталоге — текстовое, с выбором модели (ADR-009 §9)
public class ProjectIconCatalogTests
{
    [Fact]
    public void МестоЗначокПроекта_ЕстьВКаталоге()
    {
        var action = LocalActionCatalog.Find("project-icon");

        Assert.NotNull(action);
        Assert.Equal("Значок проекта", action.Title);
        Assert.Equal("Проекты", action.Group);
        Assert.Equal(CheapProfile.Large, action.Profile);
        Assert.False(action.DefaultLocal);
        Assert.Equal(ModelTier.Strong, LocalActionCatalog.EffectiveDefaultTier(action!));
        Assert.True(LocalActionCatalog.IsKnown("project-icon"));
    }

    // Собственный лимит ожидания облака для места (прод 17.08: сильная модель отвечает
    // 52–126 с, профиль Large давал 300 с и держал зависший вызов пять минут) — задаче
    // «таймаут сильной модели» значение 180 с обязано быть закреплено явно
    [Fact]
    public void МестоЗначокПроекта_СобственныйЛимитОблака180с()
    {
        var action = LocalActionCatalog.Find("project-icon");

        Assert.NotNull(action);
        Assert.Equal(180_000, action!.CloudTimeoutMs);
    }
}


// Разбор и валидация ответа модели по контракту ADR-009: имя только из белого списка,
// пути — алфавит/форма чисел/синтаксис/габарит, взаимоисключительность видов и сборка
// SVG только на сервере. Критерий задачи: подставной ответ с сырой разметкой или именем
// вне белого списка = пустой результат, а не значок.
public class ProjectIconGlyphServiceTests
{
    // Нарисованные пути — в габарите контракта [-4, 28]: пример из ADR-009 §2.2 с
    // координатами -5/-6 его же лимиту §3.4 не удовлетворяет, здесь данные годные
    private const string ValidPathsJson =
        """{"glyphs":[{"name":"piggy-bank"},{"name":"chart-line"},{"paths":["M3 21h18","M6 21V9l6-4 6 4v12","M10 21v-4h4v4"]},{"paths":["M4 18l5-4 4 4 7-4","M16 8h4v4"]}]}""";

    [Fact]
    public void ГодныйОтвет_ДаДоЧетырёхКандидатовВперемешку()
    {
        var result = ProjectIconGlyphService.Parse(ValidPathsJson);

        Assert.True(result.Ok);
        Assert.Equal(4, result.Candidates.Count);
        // Виды вперемешку: два имени + два нарисованных, порядок модели сохранён
        Assert.Equal(["piggy-bank", "chart-line"],
            result.Candidates.Take(2).Select(c => c.Name));
        Assert.All(result.Candidates.Skip(2), c => Assert.False(c.IsNamed));
        Assert.All(result.Candidates.Skip(2), c => Assert.NotNull(c.Paths));
    }

    // Габарит считается по фактическим точкам, а не по каждому числу: модель законно
    // пишет отрицательные сдвиги относительных команд (l6-5), пока точки в холсте —
    // живая выборка сильной модели 17.08 показала, что «каждое число [-4,28]» выкашивало
    // до половины рисунков целиком
    [Theory]
    [InlineData("M6 21V9l6-5 6 5v12")]          // пример из ADR-009 §2.2 (дельта -5)
    [InlineData("M12 14c0-2 1-4 3-5")]          // кривая с отрицательными дельтами
    [InlineData("M12 22v-8")]
    [InlineData("M12 12l-6-4")]
    [InlineData("M0 0c.5 1 1.5 1 2 0")]         // ведущая точка — форма SVG
    [InlineData("M4 12a8 8 0 0 1 16 0")]        // дуга с флагами 0/1
    [InlineData("M2 2 20 2 20 20")]             // повторная пара M = неявный L
    [InlineData("M0 0L10 10L0 10Z")]            // Z-возврат к старту субпоя
    [InlineData("M0 0c1 1 2 1 3 0s2-1 3 0")]    // S после C с отражением
    public void ОтрицательныеСдвигиИДробиБезНуля_ВалидныПоФактическимТочкам(string d)
    {
        Assert.True(ProjectIconGlyphService.IsValidPath(d));
        Assert.True(ProjectIconGlyphService.Parse(
            "{\"glyphs\":[{\"paths\":[\"" + d + "\"]}]}").Ok);
    }

    [Fact]
    public void ОтветВМаркдаунЗаборе_Разбирается()
    {
        var result = ProjectIconGlyphService.Parse("```json\n" + ValidPathsJson + "\n```");

        Assert.True(result.Ok);
        Assert.Equal(4, result.Candidates.Count);
    }

    [Fact]
    public void ИмяВнеБелогоСписка_ПустойРезультат()
    {
        var result = ProjectIconGlyphService.Parse(
            """{"glyphs":[{"name":"super-kitty-icon"},{"name":"chart-line"}]}""");

        // Негодный кандидат отбрасывается, годный остаётся
        Assert.True(result.Ok);
        var candidate = Assert.Single(result.Candidates);
        Assert.Equal("chart-line", candidate.Name);
    }

    [Fact]
    public void ТолькоИмяВнеБелогоСписка_ОтказПустымРезультатом()
    {
        var result = ProjectIconGlyphService.Parse("""{"glyphs":[{"name":"not-a-lucide-name"}]}""");

        Assert.False(result.Ok);
        Assert.Empty(result.Candidates);
        // Причина называет класс (имя вне белого списка) и само имя-нарушитель
        Assert.Equal("name-out:not-a-lucide-name", result.FailReason);
    }

    [Fact]
    public void ИменаВнеПрежних89_ПроходятПодбор()
    {
        // Полный набор установленного lucide-react (ADR-009 §5.2): haze не было в рукописных
        // 89, x — однобуквенное имя, отсекавшееся прежней формой {1,39}. Оба обязаны
        // проходить валидатор подбора и повторную валидацию icon/select
        var result = ProjectIconGlyphService.Parse("""{"glyphs":[{"name":"haze"},{"name":"x"}]}""");

        Assert.True(result.Ok);
        Assert.Equal(["haze", "x"], result.Candidates.Select(c => c.Name).ToList());
        Assert.NotNull(ProjectIconGlyphService.ValidateGlyph("haze", null));
        Assert.NotNull(ProjectIconGlyphService.ValidateGlyph("x", null));
    }

    [Fact]
    public void СыраяРазметкаВОтвете_ОтказБезЗначка()
    {
        var result = ProjectIconGlyphService.Parse(
            """{"glyphs":[{"paths":["<svg onload=alert(1)><path d='M0 0'/></svg>"]}]}""");

        Assert.False(result.Ok);
        Assert.Empty(result.Candidates);
    }

    [Theory]
    [InlineData("""{"glyphs":[{"name":"house","paths":["M3 21h18"]}]}""")]      // оба поля сразу
    [InlineData("""{"glyphs":[{}]}""")]                                          // ни одного
    [InlineData("""{"glyphs":[{"paths":[]}]}""")]                                // пустой список путей
    [InlineData("""{"glyphs":[{"paths":["M0 0","M2 2","M4 4","M6 6","M8 8","M10 10","M12 12","M14 14","M16 16","M18 18","M20 20","M22 22","M23 23"]}]}""")] // больше 12 путей
    [InlineData("""{"glyphs":[{"paths":["M0 0e5 5"]}]}""")]                      // экспонента
    [InlineData("""{"glyphs":[{"paths":["M100 100h1"]}]}""")]                    // габарит: 100 вне [-4, 28]
    [InlineData("""{"glyphs":[{"paths":["L0 0h1"]}]}""")]                        // первая команда не M
    [InlineData("""{"glyphs":[{"paths":["M0 0h"]}]}""")]                         // арность H не соблюдена
    [InlineData("""{"glyphs":[{"paths":["M0 0h1.234"]}]}""")]                    // три знака после точки
    public void НегодныеКандидаты_Отбрасываются(string raw)
    {
        var result = ProjectIconGlyphService.Parse(raw);

        Assert.False(result.Ok);
        Assert.Empty(result.Candidates);
    }

    [Fact]
    public void ПутьДлиннееЛимита_КандидатОтброшен()
    {
        // Алфавит валиден (M/l и цифры), но строка длиннее 256 символов
        var longPath = "M0 0" + new string('l', 260);
        var result = ProjectIconGlyphService.Parse(
            "{\"glyphs\":[{\"paths\":[\"" + longPath + "\"]}]}");

        Assert.False(result.Ok);
        Assert.Empty(result.Candidates);
        Assert.Equal($"path-length:264>{ProjectIconGlyphService.MaxPathLength}", result.FailReason);
    }

    [Fact]
    public void СуммарнаяДлинаПутейСверхЛимита_ПричинаСЛимитомИЗначением()
    {
        // Пилообразное движение (точки в холсте, команды в лимите) даёт 224 символа на
        // путь × 4 = 896 > 768: ни один путь по отдельности лимит не ломает — только сумма
        var d = "M0 0" + string.Concat(Enumerable.Repeat("l10.5 10.5l-10.5 -10.5", 10));
        Assert.Equal(224, d.Length);
        Assert.True(ProjectIconGlyphService.IsValidPath(d));
        var result = ProjectIconGlyphService.Parse(
            "{\"glyphs\":[{\"paths\":[\"" + d + "\",\"" + d + "\",\"" + d + "\",\"" + d + "\"]}]}");

        Assert.False(result.Ok);
        Assert.Equal($"path-total:896>{ProjectIconGlyphService.MaxPathsTotalLength}", result.FailReason);
    }

    [Fact]
    public void НеОтданНиОдинГодный_ПорогОдин()
    {
        // Один годный из смеси с мусором — уже успех (ADR-009 §3: порог годности один)
        var result = ProjectIconGlyphService.Parse(
            """{"glyphs":[{"paths":["<script>"]},{"name":"wallet"},{"name":"nope"}]}""");

        Assert.True(result.Ok);
        var candidate = Assert.Single(result.Candidates);
        Assert.Equal("wallet", candidate.Name);
    }

    [Fact]
    public void ХвостСверхЧетырёх_Обрезается()
    {
        var result = ProjectIconGlyphService.Parse(
            """{"glyphs":[{"name":"wallet"},{"name":"house"},{"name":"rocket"},{"name":"star"},{"name":"zap"},{"name":"bot"}]}""");

        Assert.True(result.Ok);
        Assert.Equal(4, result.Candidates.Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("мусор без json")]
    [InlineData("""{"nope": 1}""")]
    [InlineData("""[{"name":"wallet"}]""")]   // массив вместо объекта
    public void НеJson_ОтказBadJson(string? raw)
    {
        var result = ProjectIconGlyphService.Parse(raw);

        Assert.False(result.Ok);
        Assert.Equal("bad-json", result.FailReason);
    }

    // Причины отказа различимы по классам (задача «логи причин отказа»): код называет,
    // что именно не прошло; по лимитам — ещё и значение с границей
    [Theory]
    [InlineData("""{"glyphs":[]}""", "no-glyphs")]
    [InlineData("""{"glyphs":[{"name":"house","paths":["M3 21h18"]}]}""", "glyph-shape:both")]
    [InlineData("""{"glyphs":[{}]}""", "glyph-shape:none")]
    [InlineData("""{"glyphs":[{"name":"nope"}]}""", "name-out:nope")]
    [InlineData("""{"glyphs":[{"paths":["M0 0","M2 2","M4 4","M6 6","M8 8","M10 10","M12 12","M14 14","M16 16","M18 18","M20 20","M22 22","M23 23"]}]}""", "path-count:13>12")]
    [InlineData("""{"glyphs":[{"paths":["M29 0h1"]}]}""", "path-coord:29>28")]
    [InlineData("""{"glyphs":[{"paths":["M-5 0h1"]}]}""", "path-coord:-5<-4")]
    // габарит — по фактическим точкам: дельта относительной команды, уводящая точку за холст
    [InlineData("""{"glyphs":[{"paths":["M0 0l30 0"]}]}""", "path-coord:30>28")]
    [InlineData("""{"glyphs":[{"paths":["M20 20l0 -30"]}]}""", "path-coord:-10<-4")]
    // контрольная точка C за допуском
    [InlineData("""{"glyphs":[{"paths":["M0 0C30 0 2 2 2 2"]}]}""", "path-coord:30>28")]
    [InlineData("""{"glyphs":[{"paths":["M0 0a-1 1 0 0 1 2 0"]}]}""", "path-radius:-1<0")]
    [InlineData("""{"glyphs":[{"paths":["M0 0a29 29 0 0 1 2 0"]}]}""", "path-radius:29>28")]
    [InlineData("""{"glyphs":[{"paths":["M0 0a1 1 0 2 1 2 0"]}]}""", "path-arc-flag:2")]
    [InlineData("""{"glyphs":[{"paths":["M100 100h1"]}]}""", "path-number:100")]   // 3 цифры ломают форму раньше габарита
    [InlineData("""{"glyphs":[{"paths":["M0 0e5 5"]}]}""", "path-char:e")]
    [InlineData("""{"glyphs":[{"paths":["L0 0h1"]}]}""", "path-start:L")]
    [InlineData("""{"glyphs":[{"paths":["M0 0h"]}]}""", "path-arity:h")]
    [InlineData("""{"glyphs":[{"paths":["M0 0h1.234"]}]}""", "path-number:1.234")]
    [InlineData("""{"glyphs":[{"paths":[]}]}""", "no-paths")]
    public void ПричинаОтказа_КлассИЗначение(string raw, string expected)
    {
        var result = ProjectIconGlyphService.Parse(raw);

        Assert.False(result.Ok);
        Assert.Equal(expected, result.FailReason);
    }

    [Fact]
    public void ValidateGlyph_ПовторнаяВалидацияТойЖеТочкойВхода()
    {
        // icon/select присылает значок телом — валидация та же, что для модели (ADR-009 §8)
        Assert.NotNull(ProjectIconGlyphService.ValidateGlyph("wallet", null));
        Assert.Null(ProjectIconGlyphService.ValidateGlyph("нет-такого", null));
        Assert.Null(ProjectIconGlyphService.ValidateGlyph("wallet", ["M3 21h18"]));   // оба вида
        Assert.Null(ProjectIconGlyphService.ValidateGlyph(null, null));
        Assert.NotNull(ProjectIconGlyphService.ValidateGlyph(null, new[] { "M3 21h18", "M4 4h6v6" }));
        Assert.Null(ProjectIconGlyphService.ValidateGlyph(null, new[] { "M3 21h18", "<b>" }));
    }

    [Fact]
    public void GlyphSvg_СобираетсяБезРазметкиОтМодели()
    {
        var svg = GlyphSvg.Build(["M3 21h18", "M6 21V9l6-5 6 5v12"]);

        // Шаблонные атрибуты совпадают с ICON_PROPS фронта: штрих, толщина 2, currentColor.
        // Порядок атрибутов у XmlWriter не гарантирован (xmlns может уехать не первым) —
        // проверяем состав, а не последовательность
        Assert.StartsWith("<svg ", svg);
        Assert.Contains("xmlns=\"http://www.w3.org/2000/svg\"", svg);
        Assert.Contains("viewBox=\"0 0 24 24\"", svg);
        Assert.Contains("fill=\"none\"", svg);
        Assert.Contains("stroke=\"currentColor\"", svg);
        Assert.Contains("stroke-width=\"2\"", svg);
        Assert.Contains("stroke-linecap=\"round\"", svg);
        Assert.Contains("stroke-linejoin=\"round\"", svg);
        Assert.Equal(2, svg.Split("<path").Length - 1);
        Assert.DoesNotContain("<text", svg);
    }

    [Fact]
    public void LucideGlyphs_БелыйСписокЦеликомНижнегоРегистра()
    {
        Assert.All(LucideGlyphs.All, name =>
        {
            // {0,39}: в полном наборе есть однобуквенное имя «x» (ADR-009 §5.5)
            Assert.Matches("^[a-z][a-z0-9-]{0,39}$", name);
        });
        // Прежний стартовый набор ADR-009 §5 на месте и пополнился именами вне старых 89
        Assert.Contains("piggy-bank", LucideGlyphs.All);
        Assert.Contains("chart-line", LucideGlyphs.All);
        Assert.Contains("x", LucideGlyphs.All);
        Assert.Contains("haze", LucideGlyphs.All);
        Assert.NotEmpty(LucideGlyphs.All);
    }
}

// Номера ProjectIconKind закреплены ЯВНО (ADR-009 §6): projects.json хранит enum числом,
// и перенумерация после удаления Image молча превращала бы старые записи в «значковые»
// с пустым значком — исключения не было бы, только тихая порча смысла.
public class ProjectIconKindNumberingTests
{
    [Fact]
    public void НомераЗначений_ЗакрепленыЯвно_ЕдиницаВыведенаИзОбращения()
    {
        Assert.Equal(0, (int)ProjectIconKind.Initials);
        Assert.Equal(2, (int)ProjectIconKind.Glyph);
    }

    [Fact]
    public void СтараяЗаписьСРастровойИконкой_ЧитаетсяБезИсключенияИНеСтановитсяЗначком()
    {
        // Kind=1 — бывший Image; ImageFile/OriginalFile/Crop полей у модели больше нет,
        // лишние поля десериализатор игнорирует. Стор ProjectManager читает ровно с этими
        // опциями (PropertyNameCaseInsensitive, без JsonStringEnumConverter).
        var icon = JsonSerializer.Deserialize<ProjectIcon>(
            """{"Kind":1,"Color":"blue","ImageFile":"icon-abc.png","OriginalFile":"original-abc.png","Crop":{"X":1,"Y":2,"Size":3}}""",
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.NotNull(icon);
        Assert.NotEqual(ProjectIconKind.Glyph, icon!.Kind);   // не «значковый с пустым значком»
        Assert.Null(icon.Glyph);
        Assert.Equal("blue", icon.Color);
    }
}

// Лимит ожидания места «Значок проекта»: пер-местные 180 с применяются ко всем облачным
// шагам цепочки (выбранная модель, финальный claude) и НЕ меняют профильный потолок
// остальных Large-мест. Задача «таймаут сильной модели на подборе значка» (прод 17.08:
// отказы no-model при ответах 52–126 с, зависший вызов жил весь профильный потолок 300 с).
public class ProjectIconTimeoutTests
{
    private sealed class NullHttpFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    // Фейковый claude-раннер: отвечает сразу, запоминает применённый таймаут вызова
    private sealed class CaptureTimeoutOneShot : IOneShotRunner
    {
        public readonly List<TimeSpan?> Timeouts = [];

        public string? NormalizeModel(string? model) => model;

        public Task<string> RunAsync(string prompt, string? model = null, TimeSpan? timeout = null,
            CancellationToken ct = default, string? ownerId = null, string? effort = null, string? label = null)
        {
            Timeouts.Add(timeout);
            return Task.FromResult("""{"glyphs":[{"name":"wallet"}]}""");
        }

        public Task<OneShotResult> RunDetailedAsync(string prompt, string? model = null,
            TimeSpan? timeout = null, CancellationToken ct = default, string? ownerId = null,
            string? effort = null, string? label = null)
        {
            Timeouts.Add(timeout);
            return Task.FromResult(new OneShotResult("""{"glyphs":[{"name":"wallet"}]}""", null, 0));
        }
    }

    private static CheapTextRunner Runner(CaptureTimeoutOneShot claude)
    {
        var dir = Path.Combine(Path.GetTempPath(), "cc-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var config = TestConfig.Build(new()
        {
            ["DataPath"] = Path.Combine(dir, "projects.json"),
            ["Ollama:Model"] = "",   // локаль выключена — цепочка сразу идёт на claude
        });
        var ollama = new OllamaClient(new NullHttpFactory(), config,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<OllamaClient>.Instance);
        var router = new LocalActionRouter(ollama,
            new LocalActionOverridesStore(config,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<LocalActionOverridesStore>.Instance),
            config, Microsoft.Extensions.Logging.Abstractions.NullLogger<LocalActionRouter>.Instance);
        var cloud = new CloudCheapClient(new NullHttpFactory(), config, new LlmProviderRegistry(config),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<CloudCheapClient>.Instance);
        return new CheapTextRunner(router, ollama, cloud, claude,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<CheapTextRunner>.Instance);
    }

    [Fact]
    public async Task МестоЗначкаПроекта_ЛимитОблака180с_ВместоПотолкаПрофиля()
    {
        var claude = new CaptureTimeoutOneShot();
        var runner = Runner(claude);

        var raw = await runner.RunAsync(LocalActionCatalog.ProjectIcon, "промпт");

        Assert.Contains("wallet", raw);
        var timeout = Assert.Single(claude.Timeouts);
        Assert.Equal(TimeSpan.FromSeconds(180), timeout);
    }

    [Fact]
    public async Task СоседнееКрупноеМесто_ОстаётсяНаПотолкеПрофиля()
    {
        var claude = new CaptureTimeoutOneShot();
        var runner = Runner(claude);

        await runner.RunAsync(LocalActionCatalog.ProjectBackground, "промпт");

        // Профильный потолок не задран и не урезан: пер-местный лимит — только у значка
        var timeout = Assert.Single(claude.Timeouts);
        var profile = LocalActionCatalog.ProfileDefaults[CheapProfile.Large];
        Assert.Equal(TimeSpan.FromMilliseconds(profile.CloudTimeoutMs), timeout);
        Assert.Null(LocalActionCatalog.Find(LocalActionCatalog.ProjectBackground)!.CloudTimeoutMs);
    }

    // Сообщение отказа по времени называет применённый лимит и фактическую длительность
    // (требование задачи) и сохраняет подстроку-контракт ChangelogService.DescribeFailure
    [Theory]
    [InlineData(180_000, 179_500, "лимит 180 с, ждали 179.5 с")]
    [InlineData(null, 121_400, "лимит 120 с, ждали 121.4 с")]   // null = дефолт раннера 120 с
    public void СообщениеТаймаута_ЛимитИФактическаяДлительность(int? timeoutMs, int elapsedMs, string expected)
    {
        var message = OneShotClaudeRunner.TimeoutMessage(
            timeoutMs is null ? null : TimeSpan.FromMilliseconds(timeoutMs.Value),
            TimeSpan.FromMilliseconds(elapsedMs));

        Assert.Contains("не ответил за отведённое время", message);   // контракт DescribeFailure
        Assert.Contains(expected, message);
    }
}

// Причина отказа обязана уходить уровнем Warning: файловый лог прода режет Information,
// и диагностика «почему значок не подобрался» должна доходить до файла (задача команды).
// Все четыре класса отказа провоцируются подставной моделью и проверяются по одной
// строке лога с именем проекта и конкретной причиной.
public class ProjectIconGlyphLoggingTests
{
    private sealed class CaptureLogger : ILogger<ProjectIconGlyphService>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }

    // Ответ модели решается подставной функцией (null = модель «недоступна»)
    private sealed class StubCheap(Func<string, string?> answer) : ICheapTextRunner
    {
        public bool UsesLocal(string actionKey) => false;
        public string DescribeRoute(string actionKey, string? fallbackModel) => "test";

        public Task<string> RunAsync(string actionKey, string prompt, string? fallbackModel = null,
            string? ownerId = null, object? jsonFormat = null, CancellationToken ct = default)
            => Task.FromResult(answer(prompt) ?? throw new InvalidOperationException("модель недоступна"));

        public Task<string?> RunLocalOnlyAsync(string actionKey, string prompt, CancellationToken ct = default) =>
            throw new NotImplementedException();
        public Task<string?> RunFreeAsync(string actionKey, string prompt, object? jsonFormat = null,
            CancellationToken ct = default) => throw new NotImplementedException();
        public Task<OneShotResult> RunDetailedAsync(string actionKey, string prompt, string? fallbackModel = null,
            string? ownerId = null, TimeSpan? timeout = null, int? maxTokens = null,
            object? jsonFormat = null, CancellationToken ct = default) => throw new NotImplementedException();
    }

    private static ProjectIconGlyphService Service(
        CaptureLogger log, Func<string, string?> answer) => new(new StubCheap(answer), log);

    [Fact]
    public async Task ЧетыреПричиныОтказа_КаждаяОтдельнойСтрокойWarningСИменемПроекта()
    {
        var logger = new CaptureLogger();
        var owner = "user-1";

        // (1) модель не ответила
        await Service(logger, _ => null).SuggestAsync("Проект Альфа", null, owner);
        // (2) ответ не разобрался как JSON
        await Service(logger, _ => "мусор без json").SuggestAsync("Проект Бета", null, owner);
        // (3) имя вне белого списка
        await Service(logger, _ => """{"glyphs":[{"name":"super-kitty"}]}""")
            .SuggestAsync("Проект Гамма", null, owner);
        // (4) путь не прошёл лимит: габарит 29 при границе 28
        await Service(logger, _ => """{"glyphs":[{"paths":["M29 0h1"]}]}""")
            .SuggestAsync("Проект Дельта", null, owner);

        Assert.Equal(4, logger.Entries.Count);
        Assert.All(logger.Entries, e => Assert.Equal(LogLevel.Warning, e.Level));
        Assert.Contains("«Проект Альфа»", logger.Entries[0].Message);
        Assert.Contains("модель не ответила", logger.Entries[0].Message);
        Assert.Contains("«Проект Бета»", logger.Entries[1].Message);
        Assert.Contains("bad-json", logger.Entries[1].Message);
        Assert.Contains("«Проект Гамма»", logger.Entries[2].Message);
        Assert.Contains("name-out:super-kitty", logger.Entries[2].Message);
        Assert.Contains("«Проект Дельта»", logger.Entries[3].Message);
        Assert.Contains("path-coord:29>28", logger.Entries[3].Message);
    }

    [Fact]
    public async Task ГодныйОтвет_НеПишетСтрокуОтказаВЛог()
    {
        var logger = new CaptureLogger();

        var result = await Service(logger, _ => """{"glyphs":[{"name":"wallet"}]}""")
            .SuggestAsync("Проект Омега", null, "user-1");

        Assert.True(result.Ok);
        Assert.Empty(logger.Entries);
    }
}
