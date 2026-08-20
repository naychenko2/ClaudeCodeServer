using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.ProjectIcons;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services;

// Разовая миграция значков существующим проектам (ADR-009 §10): обязательный бэкап перед
// прогоном, идемпотентность состоянием записи, одна попытка на проект и удаление растров.
// Бэкап в тестах — настоящий (BackupCore.Snapshot во временную папку): проверяется сам
// механизм «не снялся — не стартовал», а не заглушка.
public class ProjectIconMigrationTests : IDisposable
{
    private readonly string _tempDir;
    private readonly IConfiguration _config;
    private readonly ProjectManager _projects;
    private readonly User _owner;
    private readonly string _iconsDir;

    public ProjectIconMigrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "cc_iconmig_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _config = BuildConfig(backupPath: Path.Combine(_tempDir, "archives"));
        var users = new UserStore(_config, new Helpers.FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        _owner = users.Add("iconmig_" + Guid.NewGuid().ToString("N")[..8], "pwd", "admin");
        _projects = new ProjectManager(_config, users, new AppSettingsService(_config));
        _iconsDir = ProjectIconMigration.IconsDirOf(_config);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    // Пути бэкапа уводим во временную папку: дефолтный SecretsDir лежит рядом с exe,
    // а Backup:Path специально портится тестом «бэкап не снялся»
    private IConfiguration BuildConfig(string backupPath) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(_tempDir, "projects.json"),
            ["DefaultProjectsPath"] = _tempDir,
            ["Backup:Path"] = backupPath,
            ["Backup:SecretsPath"] = Path.Combine(_tempDir, "secrets"),
            // Контейнера песочницы в тестах нет — docker дёргать незачем
            ["Sandbox:ContainerName"] = "",
        }).Build();

    private Project NewProject()
    {
        var root = Path.Combine(_tempDir, "proj_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return _projects.Create("Проект " + root[^8..], root, _owner.Id, "tester");
    }

    // Старый растровый след проекта: data/project-icons/{id}/icon-{guid}.png
    private void SeedRaster(Project project)
    {
        var dir = Path.Combine(_iconsDir, project.Id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "icon-test.png"), "png-bytes");
    }

    private static string GlyphsJson(params string[] names) =>
        """{"glyphs":[""" + string.Join(",", names.Select(n => $$"""{"name":"{{n}}"}""")) + "]}";

    private static string WordsJson(params string[] words) =>
        """{"words":[""" + string.Join(",", words.Select(w => "\"" + w + "\"")) + "]}";

    // Ответ подставной модели для двухходовой схемы (ревизия 20.08.2026): ходу слов —
    // слова-понятия, ходу выбора — имена. Ход различается контрактным ключом в промпте
    private static string? TwoStepAnswer(string prompt, params string[] names) =>
        prompt.Contains("\"words\"") ? WordsJson(names) : GlyphsJson(names);

    private ProjectIconMigration Migration(ICheapTextRunner cheap, IConfiguration? config = null) =>
        new(_projects, new ProjectIconGlyphService(cheap, NullLogger<ProjectIconGlyphService>.Instance),
            config ?? _config, NullLogger<ProjectIconMigration>.Instance);

    [Fact]
    public async Task Миграция_ПодбираетЗначки_СнимаетБэкап_ИУдаляетРастры()
    {
        var first = NewProject();
        var second = NewProject();
        SeedRaster(first);
        SeedRaster(second);
        var updatedAtBefore = (_projects.GetById(first.Id)!.UpdatedAt, _projects.GetById(second.Id)!.UpdatedAt);
        var cheap = new CountingCheap(prompt => TwoStepAnswer(prompt, "wallet", "chart-line"));

        var summary = await Migration(cheap).RunAsync();

        Assert.Equal(new IconMigrationSummary(2, 0), summary);
        foreach (var project in new[] { first, second })
        {
            var icon = _projects.GetById(project.Id)!.Icon;
            Assert.Equal(ProjectIconKind.Glyph, icon.Kind);
            Assert.Equal("wallet", icon.Glyph!.Name);
        }
        // Бэкап снят штатным механизмом — в папке архивов появился zip
        Assert.NotEmpty(Directory.EnumerateFiles(Path.Combine(_tempDir, "archives"), "*.zip"));
        // Растровые файлы ушли вместе с папкой
        Assert.False(Directory.Exists(_iconsDir));
        // Фоновая запись не тасует список проектов: UpdatedAt не тронут
        Assert.Equal(updatedAtBefore.Item1, _projects.GetById(first.Id)!.UpdatedAt);
        Assert.Equal(updatedAtBefore.Item2, _projects.GetById(second.Id)!.UpdatedAt);
    }

    [Fact]
    public async Task БэкапНеСнялся_МиграцияНеСтартует_ИРастрыНеТронуты()
    {
        var project = NewProject();
        SeedRaster(project);
        // Папка архивов — ПОД существующим файлом: CreateDirectory падает, Snapshot
        // возвращает неуспех (проверяется сам штатный механизм, а не заглушка)
        var blocker = Path.Combine(_tempDir, "blocker");
        File.WriteAllText(blocker, "файл, а не папка");
        var brokenConfig = BuildConfig(Path.Combine(blocker, "archives"));
        var cheap = new CountingCheap(prompt => TwoStepAnswer(prompt, "wallet"));

        var summary = await Migration(cheap, brokenConfig).RunAsync();

        Assert.Equal(IconMigrationSummary.Empty, summary);
        Assert.Equal(0, cheap.Calls);   // модель не дёргалась — прогон не начался
        var icon = _projects.GetById(project.Id)!.Icon;
        Assert.Equal(ProjectIconKind.Initials, icon.Kind);
        Assert.Null(icon.Glyph);
        // Необратимая часть (удаление растров) не выполнена — без бэкапа её нельзя
        Assert.True(Directory.Exists(_iconsDir));
        Assert.True(File.Exists(Path.Combine(_iconsDir, project.Id, "icon-test.png")));
    }

    [Fact]
    public async Task ПовторныйПрогон_НеТрогаетПроектыСоЗначком()
    {
        var project = NewProject();
        SeedRaster(project);
        var cheap = new CountingCheap(prompt => TwoStepAnswer(prompt, "wallet"));
        var migration = Migration(cheap);

        await migration.RunAsync();
        var setAtAfterFirst = _projects.GetById(project.Id)!.Icon.Glyph!.SetAt;

        var second = await migration.RunAsync();

        Assert.Equal(IconMigrationSummary.Empty, second);
        // Один проект — два хода двухходовой схемы (слова + выбор), второй прогон — no-op
        Assert.Equal(2, cheap.Calls);
        Assert.Equal(setAtAfterFirst, _projects.GetById(project.Id)!.Icon.Glyph!.SetAt);
    }

    [Fact]
    public async Task ОтказМодели_ОднаПопытка_ПроектОстаётсяНаИнициалах()
    {
        var project = NewProject();
        SeedRaster(project);
        // Модель «недоступна» — SuggestAsync ловит исключение и возвращает NoModel
        var cheap = new CountingCheap(_ => null);

        var summary = await Migration(cheap).RunAsync();

        Assert.Equal(new IconMigrationSummary(0, 1), summary);
        Assert.Equal(1, cheap.Calls);   // без ретраев (ADR-009 §10)
        var icon = _projects.GetById(project.Id)!.Icon;
        Assert.Equal(ProjectIconKind.Initials, icon.Kind);   // ни без значка, ни без инициал
        Assert.Null(icon.Glyph);
        // Бэкап при этом снят и растры удалены: механизм упразднён независимо от удачи модели
        Assert.False(Directory.Exists(_iconsDir));
    }

    [Fact]
    public async Task Рестарт_ДогоняетТолькоНеполучившихся()
    {
        var lucky = NewProject();
        var unlucky = NewProject();
        // Первый прогон: значок получает только lucky, unlucky — отказ модели. Исход
        // подставной модели привязан к ИМЕНИ проекта в промпте, а не к номеру вызова:
        // порядок обхода projects.GetAll() недетерминирован, привязка к порядку мигала
        var flaky = new CountingCheap(
            prompt => prompt.Contains($"«{lucky.Name}»") ? TwoStepAnswer(prompt, "rocket") : null);
        await Migration(flaky).RunAsync();
        var luckySetAt = _projects.GetById(lucky.Id)!.Icon.Glyph!.SetAt;

        // Рестарт: модель работает — берётся только оставшийся кандидат
        var healed = new CountingCheap(prompt => TwoStepAnswer(prompt, "rocket"));
        var second = await Migration(healed).RunAsync();

        Assert.Equal(new IconMigrationSummary(1, 0), second);
        Assert.Equal(2, healed.Calls);  // один проект — два хода двухходовой схемы, «всё заново» не запускается
        Assert.Equal(luckySetAt, _projects.GetById(lucky.Id)!.Icon.Glyph!.SetAt);
        Assert.NotNull(_projects.GetById(unlucky.Id)!.Icon.Glyph);
    }

    [Fact]
    public async Task ВсеСЗначкамиИБезРастров_ЧистыйNoopБезБэкапа()
    {
        var project = NewProject();
        _projects.SetIconGlyph(project.Id, new ProjectGlyph { Name = "house", SetAt = DateTime.UtcNow });
        var cheap = new CountingCheap(_ => GlyphsJson("wallet"));

        var summary = await Migration(cheap).RunAsync();

        Assert.Equal(IconMigrationSummary.Empty, summary);
        Assert.Equal(0, cheap.Calls);
        // Единственный неявный побочный эффект — снятый бэкап; его тоже нет
        Assert.False(Directory.Exists(Path.Combine(_tempDir, "archives")));
    }

    [Fact]
    public void IsCandidate_ТолькоПроектБезЗначка()
    {
        Assert.True(ProjectIconMigration.IsCandidate(new Project()));
        // Битая запись «Kind = Glyph, а Glyph нет» — тоже кандидат: миграция её вылечит
        Assert.True(ProjectIconMigration.IsCandidate(
            new Project { Icon = new ProjectIcon { Kind = ProjectIconKind.Glyph } }));
        Assert.False(ProjectIconMigration.IsCandidate(new Project
        {
            Icon = new ProjectIcon
            {
                Kind = ProjectIconKind.Initials,
                Glyph = new ProjectGlyph { Name = "wallet" },
            },
        }));
    }

    // Ответ модели решается по ТЕКСТУ ПРОМПТА (null = модель «недоступна»); считает вызовы.
    // Решение по промпту, а не по номеру вызова: порядок обхода проектов в прогоне
    // недетерминирован (ConcurrentDictionary.Values), и привязка исходов к порядку мигает
    private sealed class CountingCheap(Func<string, string?> answer) : ICheapTextRunner
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public bool UsesLocal(string actionKey) => false;
        public string DescribeRoute(string actionKey, string? fallbackModel) => "claude";

        public Task<string> RunAsync(string actionKey, string prompt, string? fallbackModel = null,
            string? ownerId = null, object? jsonFormat = null, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(answer(prompt) ?? throw new InvalidOperationException("модель недоступна"));
        }

        public Task<string?> RunLocalOnlyAsync(string actionKey, string prompt, CancellationToken ct = default) =>
            throw new NotImplementedException();
        public Task<string?> RunFreeAsync(string actionKey, string prompt, object? jsonFormat = null,
            CancellationToken ct = default) => throw new NotImplementedException();
        public Task<OneShotResult> RunDetailedAsync(string actionKey, string prompt, string? fallbackModel = null,
            string? ownerId = null, TimeSpan? timeout = null, int? maxTokens = null,
            object? jsonFormat = null, CancellationToken ct = default) => throw new NotImplementedException();
    }
}
