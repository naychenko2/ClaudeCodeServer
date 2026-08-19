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
        // Medium: после вырезания генерации path'ов задача — назвать имя из набора
        // (Strong поднимался 2026-08-17 под рисование)
        Assert.Equal(ModelTier.Medium, LocalActionCatalog.EffectiveDefaultTier(action!));
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


// Разбор и валидация ответа модели по контракту ADR-009: имя только из белого списка.
// Рисованные пути вырезаны: ответ с paths вместо имени — негодный кандидат, при нуле
// годных — пустой результат (фолбэк на инициалы), а не значок.
public class ProjectIconGlyphServiceTests
{
    private const string ValidNamesJson =
        """{"glyphs":[{"name":"piggy-bank"},{"name":"chart-line"},{"name":"wallet"},{"name":"rocket"}]}""";

    [Fact]
    public void ГодныйОтвет_ДоЧетырёхИмён()
    {
        var result = ProjectIconGlyphService.Parse(ValidNamesJson);

        Assert.True(result.Ok);
        Assert.Equal(4, result.Candidates.Count);
        Assert.Equal(["piggy-bank", "chart-line", "wallet", "rocket"],
            result.Candidates.Select(c => c.Name));
        Assert.Null(result.FailReason);
    }

    [Fact]
    public void ОтветВМаркдаунЗаборе_Разбирается()
    {
        var result = ProjectIconGlyphService.Parse("```json\n" + ValidNamesJson + "\n```");

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
        Assert.NotNull(ProjectIconGlyphService.ValidateGlyph("haze"));
        Assert.NotNull(ProjectIconGlyphService.ValidateGlyph("x"));
    }

    // Модель ещё может слать рисованные пути (ветка вырезана) — они отбрасываются как
    // негодные кандидаты, годные имена из того же ответа остаются
    [Fact]
    public void ПутиВместоИмени_НегодныйКандидатГодныеИменаОстаются()
    {
        var result = ProjectIconGlyphService.Parse(
            """{"glyphs":[{"paths":["M3 21h18"]},{"name":"wallet"},{"name":"nope"}]}""");

        Assert.True(result.Ok);
        var candidate = Assert.Single(result.Candidates);
        Assert.Equal("wallet", candidate.Name);
    }

    [Fact]
    public void ТолькоПути_ОтказПустымРезультатом()
    {
        var result = ProjectIconGlyphService.Parse(
            """{"glyphs":[{"paths":["M3 21h18","M6 21V9l6-4 6 4v12"]}]}""");

        Assert.False(result.Ok);
        Assert.Empty(result.Candidates);
        Assert.Equal("glyph-shape:paths", result.FailReason);
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
    [InlineData("""{"glyphs":[{"name":"house","paths":["M3 21h18"]}]}""")]   // paths и имя вместе
    [InlineData("""{"glyphs":[{}]}""")]                                       // ни одного поля
    [InlineData("""{"glyphs":[{"paths":[]}]}""")]                             // пустой список путей
    public void НегодныеКандидаты_Отбрасываются(string raw)
    {
        var result = ProjectIconGlyphService.Parse(raw);

        Assert.False(result.Ok);
        Assert.Empty(result.Candidates);
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
    // что именно не прошло
    [Theory]
    [InlineData("""{"glyphs":[]}""", "no-glyphs")]
    [InlineData("""{"glyphs":[{}]}""", "glyph-shape:none")]
    [InlineData("""{"glyphs":[{"name":"nope"}]}""", "name-out:nope")]
    [InlineData("""{"glyphs":[{"paths":["M0 0h1"]}]}""", "glyph-shape:paths")]
    [InlineData("""{"glyphs":[{"name":"house","paths":["M3 21h18"]}]}""", "glyph-shape:paths")]
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
        Assert.NotNull(ProjectIconGlyphService.ValidateGlyph("wallet"));
        Assert.Null(ProjectIconGlyphService.ValidateGlyph("нет-такого"));
        Assert.Null(ProjectIconGlyphService.ValidateGlyph(null));
        Assert.Null(ProjectIconGlyphService.ValidateGlyph("  "));
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
        // (4) ответ с путями вместо имени — ветка рисования вырезана
        await Service(logger, _ => """{"glyphs":[{"paths":["M3 21h18"]}]}""")
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
        Assert.Contains("glyph-shape:paths", logger.Entries[3].Message);
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
