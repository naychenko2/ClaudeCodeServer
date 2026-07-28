using ClaudeHomeServer.Services.CodeGraph;
using ClaudeHomeServer.Services.CodeGraph.Core;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Services;

// CodeGraphService: дебаунс и отмена фоновых перестроений.
// Фабрика ставит CodeGraph:RebuildDebounceMs=50 — окно дебаунса короткое,
// ожидание в сотни миллисекунд надёжно его перекрывает.
public class CodeGraphServiceTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public CodeGraphServiceTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    /// <summary>Провайдер-счётчик: фиксирует, какие перестроения реально запускались.</summary>
    private sealed class TrackingGraphProvider : ICodeGraphProvider
    {
        public int BuildCalls;
        public int UpdateCalls;

        public Task<CodeGraph> BuildAsync(string rootPath, CancellationToken ct)
        {
            Interlocked.Increment(ref BuildCalls);
            return Task.FromResult(CodeGraph.Empty);
        }

        public Task<CodeGraph> UpdateAsync(string rootPath, IEnumerable<string> changedFiles, CancellationToken ct)
        {
            Interlocked.Increment(ref UpdateCalls);
            return Task.FromResult(CodeGraph.Empty);
        }
    }

    /// <summary>
    /// Провайдер с задержкой: держит rebuild достаточно долго, чтобы второй конкурентный
    /// StartRebuildIfIdle гарантированно застал guard «уже бежит» (детерминированность теста).
    /// </summary>
    private sealed class SlowGraphProvider : ICodeGraphProvider
    {
        private readonly int _delayMs;
        public int BuildCalls;

        public SlowGraphProvider(int delayMs) => _delayMs = delayMs;

        public async Task<CodeGraph> BuildAsync(string rootPath, CancellationToken ct)
        {
            Interlocked.Increment(ref BuildCalls);
            await Task.Delay(_delayMs, ct);
            return CodeGraph.Empty;
        }

        public Task<CodeGraph> UpdateAsync(string rootPath, IEnumerable<string> changedFiles, CancellationToken ct)
            => BuildAsync(rootPath, ct);
    }

    [Fact]
    public async Task RebuildAsync_ОтменяетPendingДебаунс()
    {
        var service = _factory.Services.GetRequiredService<CodeGraphService>();
        var tracking = new TrackingGraphProvider();
        service.RegisterProvider(".cs", tracking);

        var dir = Path.Combine(_factory.TempDir, "cgraph_cancel_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "Foo.cs");
        await File.WriteAllTextAsync(file, "public class Foo { }");

        // Инвалидируем (таймер дебаунса взведён) и СРАЗУ перестраиваем явно:
        // явный rebuild обязан погасить pending-таймер — иначе через 50мс фоновый
        // Rebuild наложится дублирующим перестроением.
        service.InvalidateIncremental(dir, new[] { file });
        await service.RebuildAsync(dir, CancellationToken.None);

        tracking.BuildCalls.Should().Be(1, "явный RebuildAsync строит один раз");

        // Ждём заметно дольше окна дебаунса: живой таймер к этому моменту выстрелил бы.
        await Task.Delay(500);

        tracking.BuildCalls.Should().Be(1, "pending-таймер погашен явным rebuild — дубля нет");
        tracking.UpdateCalls.Should().Be(0, "инкремент по отменённому дебаунсу не запускался");
    }

    [Fact]
    public void InvalidateIncremental_ОтсекаетФайлыВнеRoot()
    {
        var service = _factory.Services.GetRequiredService<CodeGraphService>();
        var tracking = new TrackingGraphProvider();
        service.RegisterProvider(".cs", tracking);

        var dir = Path.Combine(_factory.TempDir, "cgraph_escape_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);

        // Все «изменённые» файлы вне rootPath — сервис не должен даже взводить дебаунс.
        service.InvalidateIncremental(dir, new[] { Path.Combine(_factory.TempDir, "stray.cs") });

        // Поле _pendingRebuilds приватное — наблюдаемый эффект: спустя окно дебаунса
        // провайдер так и не вызывался.
        Thread.Sleep(300);
        tracking.BuildCalls.Should().Be(0);
        tracking.UpdateCalls.Should().Be(0);
    }

    /// <summary>
    /// Провайдер, заводящий узел на каждый полученный changedFile: показывает, какие файлы
    /// инкремент реально донёс до графа.
    /// </summary>
    private sealed class FilesToNodesProvider : ICodeGraphProvider
    {
        public Task<CodeGraph> BuildAsync(string rootPath, CancellationToken ct)
            => Task.FromResult(CodeGraph.Empty);

        public Task<CodeGraph> UpdateAsync(string rootPath, IEnumerable<string> changedFiles, CancellationToken ct)
        {
            var nodes = new Dictionary<string, CodeGraphNode>();
            foreach (var f in changedFiles)
            {
                var rel = Path.GetRelativePath(rootPath, f).Replace('\\', '/');
                nodes[rel] = new CodeGraphNode
                {
                    Id = rel,
                    Label = Path.GetFileNameWithoutExtension(f),
                    FullyQualifiedName = rel,
                    SourceFile = rel,
                    SourceLocation = "L1",
                    Kind = NodeKind.Class,
                };
            }
            return Task.FromResult(new CodeGraph { Nodes = nodes, Edges = new List<CodeGraphEdge>() });
        }
    }

    [Fact]
    public async Task InvalidateIncremental_ОтсекаетМусорныеКаталоги()
    {
        var service = _factory.Services.GetRequiredService<CodeGraphService>();
        service.RegisterProvider(".cs", new FilesToNodesProvider());

        var dir = Path.Combine(_factory.TempDir, "cgraph_ignored_" + Guid.NewGuid().ToString("N")[..8]);
        var worktree = Path.Combine(dir, ".claude", "worktrees", "x");
        var src = Path.Combine(dir, "src");
        Directory.CreateDirectory(worktree);
        Directory.CreateDirectory(src);

        var ignored = Path.Combine(worktree, "Foo.cs");
        var normal = Path.Combine(src, "Bar.cs");
        await File.WriteAllTextAsync(ignored, "public class Foo { }");
        await File.WriteAllTextAsync(normal, "public class Bar { }");

        // Watcher фильтрует по FileService.TreeExcludes (там нет .claude), поэтому файл из
        // чужого worktree доходит сюда — отсечь его обязан сам сервис графа.
        service.InvalidateIncremental(dir, new[] { ignored, normal });

        // Ждём заметно дольше окна дебаунса (50мс в тестовой фабрике).
        await Task.Delay(500);

        var snapshot = await service.GetSnapshotAsync(dir, CancellationToken.None);
        snapshot.Should().NotBeNull("инкремент по обычному файлу перестроил граф");
        snapshot!.Nodes.Select(n => n.SourceFile).Should()
            .Contain("src/Bar.cs", "файл вне мусорных каталогов идёт в граф")
            .And.NotContain(p => p.Contains(".claude"),
                "файлы из .claude/worktrees в граф не попадают — полная сборка их тоже не берёт");
    }

    [Fact]
    public async Task StartRebuildIfIdle_НеПлодитКонкурентныеПостроения()
    {
        var service = _factory.Services.GetRequiredService<CodeGraphService>();
        var slow = new SlowGraphProvider(delayMs: 400);
        service.RegisterProvider(".cs", slow);

        var dir = Path.Combine(_factory.TempDir, "cgraph_ondemand_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "Foo.cs"), "public class Foo { }");

        // Два запроса почти одновременно: первый занимает guard, второй обязан застать
        // бегущее построение и НЕ запускать второй rebuild (иначе частые GET UI плодят их).
        var first = service.StartRebuildIfIdle(dir);
        var second = service.StartRebuildIfIdle(dir);

        first.Should().BeTrue("первый запрос запускает фоновое построение");
        second.Should().BeFalse("второй запрос застал бегущее построение — guard сработал");

        // Даём фоновому построению завершиться и проверяем: провайдер вызван ровно один раз.
        await Task.Delay(800);
        slow.BuildCalls.Should().Be(1, "конкурентные запросы не плодят rebuild");

        // Guard снят по завершении — следующий запрос снова может запустить построение.
        var again = service.StartRebuildIfIdle(dir);
        again.Should().BeTrue("после завершения guard свободен для нового запроса");
        await Task.Delay(800);
    }
}
