using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.CodeGraph;
using ClaudeHomeServer.Services.CodeGraph.Core;
using ClaudeHomeServer.Services.Mcp.Http;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Прогрев графа worktree и ответ по основному дереву, пока граф дерева строится (ADR-003):
/// инспектор не трогает свежий граф, а тулсет не отказывает, если у проекта граф есть.
/// </summary>
[Trait("Category", "Host")]
public class CodeGraphWorktreeWarmTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public CodeGraphWorktreeWarmTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    // Провайдер: на каждое построение отдаёт один узел Demo.Foo и даёт знать, что построение запрошено
    private sealed class FooProvider : ICodeGraphProvider
    {
        public int BuildCalls;
        public TaskCompletionSource Built { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<CodeGraph> BuildAsync(string rootPath, CancellationToken ct)
        {
            Interlocked.Increment(ref BuildCalls);
            Built.TrySetResult();
            return Task.FromResult(new CodeGraph
            {
                Nodes = new()
                {
                    ["Demo.Foo"] = new CodeGraphNode
                    {
                        Id = "Demo.Foo", Label = "Foo", FullyQualifiedName = "Demo.Foo",
                        SourceFile = "Foo.cs", SourceLocation = "line 1", Kind = NodeKind.Class,
                    },
                },
                Edges = new List<CodeGraphEdge>(),
            });
        }

        public Task<CodeGraph> UpdateAsync(string rootPath, IEnumerable<string> changedFiles, CancellationToken ct)
            => BuildAsync(rootPath, ct);
    }

    private string MkDirWithCs(string name)
    {
        var dir = Path.Combine(_factory.TempDir, "warm_" + name + "_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Foo.cs"), "namespace Demo { public class Foo {} }");
        return dir;
    }

    private FooProvider RegisterProvider()
    {
        var provider = new FooProvider();
        _factory.Services.GetRequiredService<CodeGraphService>().RegisterProvider(".cs", provider);
        return provider;
    }

    private static async Task WithTimeout(Task task)
    {
        var done = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(30)));
        done.Should().BeSameAs(task, "построение графа должно быть запрошено");
        await task;
    }

    [Fact]
    public async Task Прогрев_ГрафаНет_ЗапрашиваетПостроение()
    {
        var provider = RegisterProvider();
        var inspector = _factory.Services.GetRequiredService<ICodeGraphInspector>();

        await inspector.WarmIfNeeded(MkDirWithCs("fresh"));
        await WithTimeout(provider.Built.Task);

        provider.BuildCalls.Should().Be(1);
    }

    [Fact]
    public async Task Прогрев_ГрафСвеж_НичегоНеДелает()
    {
        var provider = RegisterProvider();
        var service = _factory.Services.GetRequiredService<CodeGraphService>();
        var inspector = _factory.Services.GetRequiredService<ICodeGraphInspector>();
        var dir = MkDirWithCs("live");
        await service.RebuildAsync(dir, CancellationToken.None);
        // Исходники не новее графа: mtime файла сдвигаем в прошлое
        File.SetLastWriteTimeUtc(Path.Combine(dir, "Foo.cs"), DateTime.UtcNow.AddMinutes(-10));

        await inspector.WarmIfNeeded(dir);
        // Провайдер синхронно считает вызов внутри StartRebuildIfIdle: к завершению задачи прогрева
        // запрошенное построение уже учтено

        provider.BuildCalls.Should().Be(1, "граф уже построен и свеж — повторного построения нет");
    }

    private (CodeGraphToolset Toolset, McpToolCallContext Context, string Worktree) Env(string mainDir)
    {
        using var client = _factory.CreateAuthenticatedClient();
        var created = client.PostAsJsonAsync("/api/projects", new { name = "warm-" + Guid.NewGuid().ToString("N")[..6], rootPath = mainDir })
            .GetAwaiter().GetResult();
        created.EnsureSuccessStatusCode();
        var projectId = JsonSerializer.Deserialize<JsonElement>(created.Content.ReadAsStringAsync().GetAwaiter().GetResult())
            .GetProperty("id").GetString()!;
        var project = _factory.Services.GetRequiredService<ProjectManager>().GetById(projectId)!;

        var sessions = _factory.Services.GetRequiredService<SessionManager>();
        var session = sessions.CreateAsync(projectId, ClaudeMode.Auto).GetAwaiter().GetResult();
        var worktree = MkDirWithCs("wt");
        sessions.GetById(session.Id)!.WorktreePath = worktree;

        var toolset = ActivatorUtilities.CreateInstance<CodeGraphToolset>(_factory.Services);
        var ownerId = project.OwnerId ?? throw new InvalidOperationException("Проект без владельца — контекст вызова не соберётся");
        return (toolset, new McpToolCallContext(ownerId, session.Id, session.Id), worktree);
    }

    [Fact]
    public async Task Find_ГрафДереваСтроится_ОтвечаетПоОсновномуДеревуСПометкой()
    {
        RegisterProvider();
        var main = MkDirWithCs("main");
        await _factory.Services.GetRequiredService<CodeGraphService>().RebuildAsync(main, CancellationToken.None);
        var (toolset, context, _) = Env(main);

        var result = await toolset.CallAsync("codegraph_find", new JsonObject { ["query"] = "Foo" }, context, default);

        result.IsError.Should().BeFalse();
        result.Text.Should().StartWith(CodeGraphToolset.BuildingNote);
        result.Text.Should().Contain("Demo.Foo");
    }

    [Fact]
    public async Task Find_НиУДереваНиУОсновного_ПроситПовторитьЧерезМинуту()
    {
        RegisterProvider();
        var (toolset, context, _) = Env(MkDirWithCs("nomain"));

        var result = await toolset.CallAsync("codegraph_find", new JsonObject { ["query"] = "Foo" }, context, default);

        result.IsError.Should().BeTrue();
        result.Text.Should().Contain("через минуту");
        result.Text.Should().NotContain("1–2");
    }
}
