using System.Globalization;
using System.Text;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Backgrounds;
using ClaudeHomeServer.Services.Llm;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services;

// Разовая генерация фонов существующим проектам (ADR-008 §10): идемпотентность прогона,
// потолок параллелизма, повтор транзиентного отказа и сводка.
public class ProjectBackgroundBackfillTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ProjectManager _projects;
    private readonly UserStore _users;
    private readonly User _owner;

    public ProjectBackgroundBackfillTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "cc_bgfill_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_tempDir, "projects.json"),
                ["DefaultProjectsPath"] = _tempDir,
            })
            .Build();
        _users = new UserStore(config, new Helpers.FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        _projects = new ProjectManager(config, _users, new AppSettingsService(config));
        _owner = _users.Add("backfill_" + Guid.NewGuid().ToString("N")[..8], "pwd", "admin");
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private Project NewProject()
    {
        var root = Path.Combine(_tempDir, "proj_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return _projects.Create("Проект", root, _owner.Id, "tester");
    }

    // Ответ модели с n годными фигурами
    private static string Answer(int shapes = 10)
    {
        var sb = new StringBuilder();
        sb.Append("{\"colorKey\":\"green\",\"shapes\":[");
        for (var i = 0; i < shapes; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(CultureInfo.InvariantCulture,
                $$"""{"x":{{10 + i * 3}},"y":{{20 + i}},"rotate":-5,"paths":["M0 0h20v14H0z"],"circles":[{"cx":5,"cy":5,"r":3}]}""");
        }
        sb.Append("]}");
        return sb.ToString();
    }

    private ProjectBackgroundBackfill Backfill(ICheapTextRunner cheap)
    {
        var service = new ProjectBackgroundService(_projects, cheap,
            NullLogger<ProjectBackgroundService>.Instance);
        return new ProjectBackgroundBackfill(_projects, service, _users,
            NullLogger<ProjectBackgroundBackfill>.Instance)
        {
            RetryDelay = TimeSpan.Zero,
        };
    }

    [Fact]
    public async Task Прогон_генерирует_фоны_всем_существующим_проектам()
    {
        var projects = Enumerable.Range(0, 3).Select(_ => NewProject()).ToList();
        var cheap = new CountingCheap(_ => Answer());

        var summary = await Backfill(cheap).RunAsync(_owner.Id);

        Assert.Equal(new BackfillSummary(3, 0, 0), summary);
        Assert.Equal(3, summary.Total);
        Assert.Equal(3, cheap.Calls);
        foreach (var project in projects)
        {
            var saved = _projects.GetById(project.Id)!.Background!;
            Assert.Equal(ProjectBackgroundKind.Generated, saved.Kind);
            Assert.True(File.Exists(Path.Combine(_projects.BackgroundsDir, project.Id, saved.TileFile!)));
        }
    }

    [Fact]
    public async Task Повторный_прогон_ничего_не_перетирает_и_не_дёргает_модель()
    {
        var project = NewProject();
        var cheap = new CountingCheap(_ => Answer());
        var backfill = Backfill(cheap);

        await backfill.RunAsync(_owner.Id);
        var tileAfterFirst = _projects.GetById(project.Id)!.Background!.TileFile;

        var second = await backfill.RunAsync(_owner.Id);

        Assert.Equal(BackfillSummary.Empty, second);   // кандидатов не осталось
        Assert.Equal(1, cheap.Calls);
        Assert.Equal(tileAfterFirst, _projects.GetById(project.Id)!.Background!.TileFile);
        // Второго файла в папке проекта не появилось
        Assert.Single(Directory.GetFiles(Path.Combine(_projects.BackgroundsDir, project.Id)));
    }

    [Fact]
    public async Task Стандартный_и_упавший_фон_прогон_не_трогает()
    {
        var standard = NewProject();
        var failed = NewProject();
        _projects.SetBackgroundStandard(standard.Id);
        _projects.SetBackgroundFailed(failed.Id, "bad-json");
        var fresh = NewProject();
        var cheap = new CountingCheap(_ => Answer());

        var summary = await Backfill(cheap).RunAsync(_owner.Id);

        Assert.Equal(new BackfillSummary(1, 0, 0), summary);
        Assert.Equal(1, cheap.Calls);
        Assert.Equal(ProjectBackgroundKind.Standard, _projects.GetById(standard.Id)!.Background!.Kind);
        Assert.Equal(ProjectBackgroundKind.Failed, _projects.GetById(failed.Id)!.Background!.Kind);
        Assert.Equal(ProjectBackgroundKind.Generated, _projects.GetById(fresh.Id)!.Background!.Kind);
    }

    [Fact]
    public async Task Протухший_Pending_перезабирается_а_свежий_нет()
    {
        var stale = NewProject();
        var busy = NewProject();
        _projects.TryBeginBackground(stale.Id);
        _projects.TryBeginBackground(busy.Id);
        // Двигаем StartedAt в прошлое — сервер упал посреди прогона
        _projects.GetById(stale.Id)!.Background!.StartedAt = DateTime.UtcNow.AddHours(-1);

        var summary = await Backfill(new CountingCheap(_ => Answer())).RunAsync(_owner.Id);

        Assert.Equal(new BackfillSummary(1, 0, 0), summary);
        Assert.Equal(ProjectBackgroundKind.Generated, _projects.GetById(stale.Id)!.Background!.Kind);
        Assert.Equal(ProjectBackgroundKind.Pending, _projects.GetById(busy.Id)!.Background!.Kind);
    }

    [Fact]
    public async Task Транзиентный_отказ_повторяется_и_вторая_попытка_проходит()
    {
        var project = NewProject();
        // Первая попытка — модели нет, вторая отвечает
        var cheap = new CountingCheap(call => call == 1 ? null : Answer());

        var summary = await Backfill(cheap).RunAsync(_owner.Id);

        Assert.Equal(new BackfillSummary(1, 0, 0), summary);
        Assert.Equal(2, cheap.Calls);
        Assert.Equal(ProjectBackgroundKind.Generated, _projects.GetById(project.Id)!.Background!.Kind);
    }

    [Fact]
    public async Task Битый_ответ_не_повторяется_и_проект_остаётся_на_стандартном_фоне()
    {
        var project = NewProject();
        var cheap = new CountingCheap(_ => "не сегодня");
        var backfill = Backfill(cheap);

        var summary = await backfill.RunAsync(_owner.Id);

        Assert.Equal(new BackfillSummary(0, 0, 1), summary);
        Assert.Equal(1, cheap.Calls);   // повтор бесполезен — причина в модели, а не в попытке
        var saved = _projects.GetById(project.Id)!.Background!;
        Assert.Equal(ProjectBackgroundKind.Failed, saved.Kind);
        Assert.Null(saved.TileFile);    // фронт рисует стандартный дудл

        // …и повторный прогон упавший проект не берёт
        var second = await backfill.RunAsync(_owner.Id);
        Assert.Equal(BackfillSummary.Empty, second);
        Assert.Equal(1, cheap.Calls);
    }

    [Fact]
    public async Task Одновременно_идёт_не_больше_двух_генераций_на_инстанс()
    {
        // Два владельца — прогоны идут параллельно, потолок держит общий семафор
        var second = _users.Add("backfill2_" + Guid.NewGuid().ToString("N")[..8], "pwd", "admin");
        foreach (var _ in Enumerable.Range(0, 4)) NewProject();
        foreach (var i in Enumerable.Range(0, 4))
        {
            var root = Path.Combine(_tempDir, "proj2_" + i);
            Directory.CreateDirectory(root);
            _projects.Create("Проект", root, second.Id, "tester");
        }
        var cheap = new CountingCheap(_ => Answer(), holdMs: 30);
        var summary = await Backfill(cheap).RunAllAsync();

        Assert.Equal(8, summary.Generated);
        Assert.True(cheap.MaxConcurrent <= 2, $"одновременных вызовов модели: {cheap.MaxConcurrent}");
    }

    [Fact]
    public async Task RunAllAsync_идёт_по_всем_владельцам()
    {
        // Ни у кого ничего не «включено»: стартовый прогон обходит владельцев поголовно
        var second = _users.Add("backfill3_" + Guid.NewGuid().ToString("N")[..8], "pwd", "user");
        var mine = NewProject();
        var root = Path.Combine(_tempDir, "proj3_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var theirs = _projects.Create("Проект", root, second.Id, "tester");
        var cheap = new CountingCheap(_ => Answer());

        var summary = await Backfill(cheap).RunAllAsync();

        Assert.Equal(2, summary.Generated);
        Assert.Equal(ProjectBackgroundKind.Generated, _projects.GetById(mine.Id)!.Background!.Kind);
        Assert.Equal(ProjectBackgroundKind.Generated, _projects.GetById(theirs.Id)!.Background!.Kind);
    }

    [Fact]
    public void Кандидат_прогона_только_нетронутый_проект_или_протухший_Pending()
    {
        Assert.True(ProjectBackgroundBackfill.IsCandidate(null));
        Assert.True(ProjectBackgroundBackfill.IsCandidate(new ProjectBackground
        {
            Kind = ProjectBackgroundKind.Pending,
            StartedAt = DateTime.UtcNow.AddHours(-1),
        }));
        Assert.False(ProjectBackgroundBackfill.IsCandidate(new ProjectBackground
        {
            Kind = ProjectBackgroundKind.Pending,
            StartedAt = DateTime.UtcNow,
        }));
        foreach (var kind in new[]
                 {
                     ProjectBackgroundKind.Generated, ProjectBackgroundKind.Standard,
                     ProjectBackgroundKind.Failed,
                 })
            Assert.False(ProjectBackgroundBackfill.IsCandidate(new ProjectBackground { Kind = kind }));

        // Failed не кандидат независимо от причины: прогон идёт при каждом рестарте, и
        // мёртвая модель долбилась бы в неё бесконечно. Повтор даёт только кнопка.
        foreach (var reason in new[] { "no-model", "io", "bad-json", "rejected" })
            Assert.False(ProjectBackgroundBackfill.IsCandidate(new ProjectBackground
            {
                Kind = ProjectBackgroundKind.Failed, FailReason = reason, Attempts = 1,
            }));
    }

    // Failed-проект с заданной причиной и счётчиком попыток. Чередуем захват и неудачу —
    // Attempts растёт честно, как в живом прогоне: повторный TryBegin на свежем Pending
    // заблокирован бы защитой от двойного захвата, но после SetBackgroundFailed проект в
    // Failed, а Failed под эту защиту не попадает, так что цикл инкрементит Attempts.
    private void MakeFailed(Project project, string reason, int attempts)
    {
        for (var i = 0; i < attempts; i++)
        {
            _projects.TryBeginBackground(project.Id);
            _projects.SetBackgroundFailed(project.Id, reason);
        }
    }

    [Fact]
    public async Task Failed_не_возвращается_в_прогон_ни_транзиентный_ни_фатальный()
    {
        var transient = NewProject();
        var fatal = NewProject();
        MakeFailed(transient, "no-model", attempts: 2);
        MakeFailed(fatal, "bad-json", attempts: 1);
        var cheap = new CountingCheap(_ => Answer());

        // Прогон владельца — модель не дёргается вовсе
        Assert.Equal(BackfillSummary.Empty, await Backfill(cheap).RunAsync(_owner.Id));
        // И через реальный стартовый путь RunAllAsync — то же
        Assert.Equal(BackfillSummary.Empty, await Backfill(cheap).RunAllAsync());

        Assert.Equal(0, cheap.Calls);
        Assert.Equal(ProjectBackgroundKind.Failed, _projects.GetById(transient.Id)!.Background!.Kind);
        Assert.Equal(ProjectBackgroundKind.Failed, _projects.GetById(fatal.Id)!.Background!.Kind);
    }

    [Fact]
    public async Task Потолок_attempts_не_даёт_перезабирать_протухший_Pending_бесконечно()
    {
        // Единственный путь Failed обратно в прогон — протухший Pending (сервер упал в
        // середине). Пожизненный потолок обязан остановить и его.
        var project = NewProject();
        MakeFailed(project, "no-model", attempts: 3);   // = MaxTotalAttempts
        _projects.TryBeginBackground(project.Id);
        _projects.GetById(project.Id)!.Background!.StartedAt = DateTime.UtcNow.AddHours(-1);
        var cheap = new CountingCheap(_ => Answer());

        var summary = await Backfill(cheap).RunAsync(_owner.Id);

        Assert.Equal(new BackfillSummary(0, 1, 0), summary);   // кандидат взят, но пропущен
        Assert.Equal(0, cheap.Calls);
    }

    // Ответ модели зависит от номера вызова (null = вызов падает); считает вызовы и пик
    // одновременности, чтобы проверить потолок параллелизма
    private sealed class CountingCheap(Func<int, string?> answer, int holdMs = 0) : ICheapTextRunner
    {
        private int _calls;
        private int _concurrent;
        private int _maxConcurrent;

        public int Calls => Volatile.Read(ref _calls);
        public int MaxConcurrent => Volatile.Read(ref _maxConcurrent);

        public bool UsesLocal(string actionKey) => false;
        public string DescribeRoute(string actionKey, string? fallbackModel) => "claude";

        public async Task<string> RunAsync(string actionKey, string prompt, string? fallbackModel = null,
            string? ownerId = null, object? jsonFormat = null, CancellationToken ct = default)
        {
            var call = Interlocked.Increment(ref _calls);
            var now = Interlocked.Increment(ref _concurrent);
            int peak;
            while (now > (peak = Volatile.Read(ref _maxConcurrent))
                   && Interlocked.CompareExchange(ref _maxConcurrent, now, peak) != peak) { }
            try
            {
                if (holdMs > 0) await Task.Delay(holdMs, ct);
                return answer(call) ?? throw new InvalidOperationException("модель недоступна");
            }
            finally
            {
                Interlocked.Decrement(ref _concurrent);
            }
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
