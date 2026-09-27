using ClaudeHomeServer.Services.CodeGraph;
using ClaudeHomeServer.Services.CodeGraph.Core;
using GraphModel = ClaudeHomeServer.Services.CodeGraph.Core.CodeGraph;
using ClaudeHomeServer.Services.Composition;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Гонка Д2: сброс графа (перенос папки) во время идущего фонового перестроения. Сборка,
/// начатая до сброса, не должна дописать граф со старыми путями поверх него.
/// Детерминированно: провайдер держит сборку на TCS, отмену намеренно игнорирует
/// (худший случай — провайдер не заметил токен и доехал до записи).
/// </summary>
public class CodeGraphInvalidateRaceTests : IDisposable
{
    private readonly string _tempDir =
        Path.Combine(Path.GetTempPath(), "ccs_cgr_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            try { Directory.Delete(_tempDir, recursive: true); }
            catch { /* уборка temp — не предмет теста */ }
    }

    // Провайдер-шлюз: сообщает о входе в сборку и ждёт отпускания, токен не слушает
    private sealed class GatedProvider : ICodeGraphProvider
    {
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<GraphModel> BuildAsync(string rootPath, CancellationToken ct) => GateAsync();
        public Task<GraphModel> UpdateAsync(string rootPath, IEnumerable<string> changedFiles, CancellationToken ct) => GateAsync();

        private async Task<GraphModel> GateAsync()
        {
            Entered.TrySetResult();
            await Release.Task;
            return new GraphModel { Nodes = new Dictionary<string, CodeGraphNode>(), Edges = new List<CodeGraphEdge>() };
        }
    }

    [Fact]
    public async Task СбросВоВремяФоновойСборки_СборкаГрафНеПишет()
    {
        var root = Path.Combine(_tempDir, "proj");
        Directory.CreateDirectory(root);
        var file = Path.Combine(root, "Foo.cs");
        File.WriteAllText(file, "class Foo {}");

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["CodeGraph:RebuildDebounceMs"] = "1",
        }).Build();
        var lookup = new Mock<IProjectRootLookup>();
        lookup.Setup(l => l.GetByRootPath(It.IsAny<string>())).Returns(Array.Empty<ProjectRootLocation>());
        using var graphs = new CodeGraphService(NullLogger<CodeGraphService>.Instance, lookup.Object,
            new GraphPersistence(Path.Combine(_tempDir, "graphs"), NullLogger<GraphPersistence>.Instance), config);
        var provider = new GatedProvider();
        graphs.RegisterProvider(".cs", provider);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        graphs.BackgroundRebuildFinished = _ => finished.TrySetResult();

        graphs.InvalidateIncremental(root, [file]);
        (await Task.WhenAny(provider.Entered.Task, Task.Delay(TimeSpan.FromSeconds(10))))
            .Should().Be(provider.Entered.Task, "фоновая сборка по дебаунсу обязана стартовать");

        graphs.Invalidate(root);
        provider.Release.SetResult();

        (await Task.WhenAny(finished.Task, Task.Delay(TimeSpan.FromSeconds(10))))
            .Should().Be(finished.Task, "фоновая сборка обязана завершиться");
        graphs.GetCacheSignature(root).Should().BeNull(
            "сборка, начатая до сброса, не должна дописать граф со старыми путями");
    }

    [Fact]
    public async Task СбросВоВремяЯвнойСборки_СборкаГрафНеПишет()
    {
        var root = Path.Combine(_tempDir, "proj2");
        Directory.CreateDirectory(root);
        var lookup = new Mock<IProjectRootLookup>();
        using var graphs = new CodeGraphService(NullLogger<CodeGraphService>.Instance, lookup.Object,
            new GraphPersistence(Path.Combine(_tempDir, "graphs2"), NullLogger<GraphPersistence>.Instance),
            new ConfigurationBuilder().Build());
        var provider = new GatedProvider();
        graphs.RegisterProvider(".cs", provider);

        var build = graphs.RebuildAsync(root, CancellationToken.None);
        (await Task.WhenAny(provider.Entered.Task, Task.Delay(TimeSpan.FromSeconds(10))))
            .Should().Be(provider.Entered.Task);

        graphs.Invalidate(root);
        provider.Release.SetResult();
        await build;

        graphs.GetCacheSignature(root).Should().BeNull("сброс сменил поколение — результат старой сборки выброшен");
    }
}
