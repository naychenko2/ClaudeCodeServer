using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Llm;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services;

// Юнит-тесты ChangelogService на публичный контракт БЕЗ git-репозиториев и без claude.
// Git-агрегация (группировка коммитов по дням) требует настоящего git-стенда и подвержена
// флаку окружения/таймзоны — она проверяется вручную и E2E, а не здесь. Тут — поведение
// на пустом наборе проектов, ленивая ветка GetDay без коммитов (не дёргает claude),
// а также инвалидация и очистка кеша.
public class ChangelogServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _cacheDir;
    private readonly ChangelogService _sut;

    public ChangelogServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "changelog_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _cacheDir = Path.Combine(_tempDir, "data", "changelog");

        // Без Changelog:SourceRepoPath — источник не задан, коммитов нет
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_tempDir, "data", "projects.json")
            }).Build();

        _sut = new ChangelogService(new FileService(), config, NullLogger<ChangelogService>.Instance,
            BuildCheapRunner(config));
    }

    // ─── Источник не задан ──────────────────────────────────────────────────

    [Fact]
    public void GetDays_БезИсточника_ПустойСписок()
    {
        _sut.GetDays(30).Should().BeEmpty();
    }

    [Fact]
    public void GetWarmupCandidates_БезИсточника_ПустойСписок()
    {
        _sut.GetWarmupCandidates(5).Should().BeEmpty();
    }

    [Fact]
    public void GetNewCommitCount_БезИсточника_Ноль()
    {
        _sut.GetNewCommitCount(DateTimeOffset.Now.AddDays(-7)).Should().Be(0);
    }

    [Fact]
    public void GetStatus_БезИсточника_НеНастроен()
    {
        var status = _sut.GetStatus();
        status.Configured.Should().BeFalse();
        status.Detail.Should().NotBeNullOrEmpty();
    }

    // ─── Ленивая сводка дня без коммитов не должна дёргать claude ────────────

    [Fact]
    public async Task GetDay_ДатаБезКоммитов_ПустойДеньБезClaude()
    {
        // Нет проектов → нет коммитов дня → ранний возврат пустого дня (claude не запускается)
        var day = await _sut.GetDay("2000-01-01");

        day.Date.Should().Be("2000-01-01");
        day.Items.Should().BeEmpty();
    }

    // ─── Инвалидация дня ────────────────────────────────────────────────────

    [Fact]
    public void InvalidateDay_НесуществующийДень_НеБросает()
    {
        var act = () => _sut.InvalidateDay("2000-01-01");
        act.Should().NotThrow();
    }

    [Fact]
    public void InvalidateDay_УдаляетТолькоУказанныйДеньИзКеша()
    {
        // Готовим кеш-файл с двумя днями вручную (формат: { "date": { shasHash, items } })
        Directory.CreateDirectory(_cacheDir);
        var cacheFile = Path.Combine(_cacheDir, "product.json");
        File.WriteAllText(cacheFile,
            """{"2026-07-01":{"ShasHash":"aaa","Items":[]},"2026-07-02":{"ShasHash":"bbb","Items":[]}}""");

        _sut.InvalidateDay("2026-07-01");

        var json = File.ReadAllText(cacheFile);
        json.Should().NotContain("2026-07-01");
        json.Should().Contain("2026-07-02"); // второй день остался
    }

    // ─── Полная очистка ─────────────────────────────────────────────────────

    [Fact]
    public void ClearAll_УдаляетФайлКеша()
    {
        Directory.CreateDirectory(_cacheDir);
        var cacheFile = Path.Combine(_cacheDir, "product.json");
        File.WriteAllText(cacheFile, """{"2026-07-01":{"ShasHash":"aaa","Items":[]}}""");

        _sut.ClearAll();

        File.Exists(cacheFile).Should().BeFalse();
    }

    [Fact]
    public void ClearAll_БезФайла_НеБросает()
    {
        var act = () => _sut.ClearAll();
        act.Should().NotThrow();
    }

    // ─── Умный fallback (без LLM: чистая функция) ───────────────────────────

    private static GitCommitRaw Commit(string subject, string author = "Григорий") =>
        new("sha1", author, "a@b.c", DateTimeOffset.Now, subject, "", "Claude Home");

    [Theory]
    [InlineData("feat(chat): Добавили кнопку", "feature", "Новое")]
    [InlineData("fix(notes): Починили ссылку", "fix", "Исправления")]
    [InlineData("refactor(llm): Причесали слой", "improvement", "Улучшения")]
    [InlineData("perf(files): Ускорили дерево", "improvement", "Улучшения")]
    [InlineData("Merge branch 'master'", "other", "Прочее")]
    [InlineData("chore: Обновили зависимости", "other", "Прочее")]
    public void FallbackItems_ОбластьИзТипаКоммита(string subject, string expectedType, string expectedArea)
    {
        var items = ChangelogService.FallbackItems([Commit(subject)]);

        items.Should().ContainSingle();
        items[0].Type.Should().Be(expectedType);
        items[0].Area.Should().Be(expectedArea);
    }

    [Fact]
    public void FallbackItems_ОписаниеПустое_ОбоснованиеЧестное()
    {
        var items = ChangelogService.FallbackItems([Commit("feat(chat): Добавили кнопку")]);

        // Тела коммитов технические — в продуктовый раздел их не льём
        items[0].Benefit.Should().BeEmpty();
        // Пузырь Claude не должен пустовать: видно, что пункт сырой
        items[0].ScoreReason.Should().NotBeEmpty();
        items[0].Title.Should().Be("Добавили кнопку"); // conventional-префикс срезан
    }

    [Fact]
    public void FallbackItems_РазныеТипы_ГруппируютсяПоОбластям()
    {
        var items = ChangelogService.FallbackItems([
            Commit("feat(a): Раз"), Commit("feat(b): Два"), Commit("fix(c): Три"),
        ]);

        items.Select(i => i.Area).Should().Equal("Новое", "Новое", "Исправления");
    }

    // ─── Канонизация областей между чанками ─────────────────────────────────

    private static ChangelogItem Item(string area) =>
        new("feature", area, "✨", "Заголовок", "", 3, "", ["Григорий"], ["Claude Home"]);

    [Fact]
    public void NormalizeAreas_РазличияРегистраИПробелов_СхлопываютсяВПервоеНаписание()
    {
        var normalized = ChangelogService.NormalizeAreas([Item("Чат"), Item("чат "), Item("ЧАТ")]);

        normalized.Select(i => i.Area).Should().AllBe("Чат");
    }

    [Fact]
    public void NormalizeAreas_ПустаяОбласть_СтановитсяПрочее()
    {
        var normalized = ChangelogService.NormalizeAreas([Item("  "), Item("")]);

        normalized.Select(i => i.Area).Should().AllBe("Прочее");
    }

    [Fact]
    public void NormalizeAreas_РазныеОбласти_НеСливаются()
    {
        var normalized = ChangelogService.NormalizeAreas([Item("Чат"), Item("Файлы"), Item("чат")]);

        normalized.Select(i => i.Area).Should().Equal("Чат", "Файлы", "Чат");
    }

    // ─── Человеческое объяснение сбоя генерации ─────────────────────────────

    [Theory]
    // Самый коварный случай: CLI пишет это в stdout при пустом stderr
    [InlineData("claude завершился с кодом 1: Not logged in · Please run /login")]
    [InlineData("claude завершился с кодом 1: NOT LOGGED IN")]
    public void DescribeFailure_НеЗалогинен_ОбъясняетКакПочинить(string error)
    {
        var text = ChangelogService.DescribeFailure(error);

        text.Should().Contain("не залогинен");
        text.Should().Contain("claude auth login");
        text.Should().Contain("CLAUDE_CODE_OAUTH_TOKEN");
    }

    [Fact]
    public void DescribeFailure_Таймаут_ПредлагаетУвеличитьЛимит()
    {
        var text = ChangelogService.DescribeFailure("Claude не ответил за отведённое время");

        text.Should().Contain("не уложился");
        text.Should().Contain("Changelog:TimeoutMs");
    }

    [Fact]
    public void DescribeFailure_НеизвестнаяОшибка_ПоказываетПричину()
    {
        var text = ChangelogService.DescribeFailure("claude завершился с кодом 137: OOM");

        text.Should().Contain("сырые коммиты");
        text.Should().Contain("OOM");
    }

    [Fact]
    public void DescribeFailure_ПричиныНет_НеПадает()
    {
        ChangelogService.DescribeFailure(null).Should().NotBeEmpty();
        ChangelogService.DescribeFailure("").Should().NotBeEmpty();
    }

    // ─── Обратная совместимость кеша ────────────────────────────────────────

    [Fact]
    public void LoadCache_СтарыйФорматБезDegraded_ЧитаетсяКакНормальныйДень()
    {
        Directory.CreateDirectory(_cacheDir);
        var cacheFile = Path.Combine(_cacheDir, "product.json");
        // Записи, созданные до появления полей Degraded/DegradedReason
        File.WriteAllText(cacheFile, """
            {"2026-07-01":{"ShasHash":"aaa","Items":[]},"2026-07-02":{"ShasHash":"bbb","Items":[]}}
            """);

        // Если десериализация сломается, LoadCache вернёт пустой словарь и день не удалится
        _sut.InvalidateDay("2026-07-01");

        var json = File.ReadAllText(cacheFile);
        json.Should().NotContain("2026-07-01");
        json.Should().Contain("2026-07-02");
    }

    // Реальный CheapTextRunner поверх claude-раннера: Ollama и openrouter в тестовом конфиге
    // не настроены (Enabled=false), поэтому цепочка вырождается в claude. Здесь коммитов нет,
    // так что раннер вообще не дёргается — нужен лишь для сборки ChangelogService.
    private sealed class NullHttpFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private static ICheapTextRunner BuildCheapRunner(IConfiguration config)
    {
        var httpFactory = new NullHttpFactory();
        var ollama = new OllamaClient(httpFactory, config, NullLogger<OllamaClient>.Instance);
        var store = new LocalActionOverridesStore(config, NullLogger<LocalActionOverridesStore>.Instance);
        var router = new LocalActionRouter(ollama, store, config, NullLogger<LocalActionRouter>.Instance);
        var providers = new LlmProviderRegistry(config);
        var cloud = new CloudCheapClient(httpFactory, config, providers, NullLogger<CloudCheapClient>.Instance);
        var claude = new OneShotClaudeRunner(providers, TestLauncherFactory.Instance);
        return new CheapTextRunner(router, ollama, cloud, claude, NullLogger<CheapTextRunner>.Instance);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }
}
