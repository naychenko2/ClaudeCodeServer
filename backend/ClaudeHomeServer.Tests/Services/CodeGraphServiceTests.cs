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
}
