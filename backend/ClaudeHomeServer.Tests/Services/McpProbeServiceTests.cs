using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Execution;
using ClaudeHomeServer.Services.Mcp;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Проба stdio-сервера целиком: настоящий процесс, настоящее рукопожатие. Мокать здесь
/// нечего — ценность пробы ровно в том, что она поднимает сервер так же, как это сделал бы ход.
/// Сервер берём свой (mcp/widgets-server — чистый Node без зависимостей и без сети).
/// </summary>
[Trait("Category", "Slow")]
public class McpProbeServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ccs-mcp-probe-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* уборка best-effort */ }
    }

    // Локальная среда для любого владельца: песочницу в тестах не поднять
    private sealed class LocalOnlyLaunchers : ILauncherFactory
    {
        public IProcessLauncher Local => LocalProcessRunner.Instance;
        public IProcessLauncher ForOwner(string? ownerId) => LocalProcessRunner.Instance;
        public IProcessLauncher ForProject(ClaudeHomeServer.Models.Project project) => LocalProcessRunner.Instance;
    }

    private sealed class PlainHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private (McpProbeService Probe, McpStatusStore Status) NewProbe(int timeoutSeconds = 30)
    {
        Directory.CreateDirectory(_dir);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(_dir, "projects.json"),
            ["Mcp:ProbeTimeoutSeconds"] = timeoutSeconds.ToString(),
        }).Build();
        var status = new McpStatusStore(config);
        var probe = new McpProbeService(new McpSecretStore(config), new LocalOnlyLaunchers(), status,
            new PlainHttpClientFactory(), config, NullLogger<McpProbeService>.Instance);
        return (probe, status);
    }

    // Путь к MCP-серверу продукта от корня репозитория; null — сборка вне дерева
    private static string? FindWidgetsServer()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "mcp", "widgets-server", "index.js");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    private static bool NodeAvailable() =>
        !OperatingSystem.IsWindows()
        || LocalProcessRunner.FindInPath("node",
            Environment.GetEnvironmentVariable("PATH"),
            Environment.GetEnvironmentVariable("PATHEXT")) is not null;

    [SkippableFact]
    public async Task ЖивойСервер_ОтдаётИмяИСписокИнструментов()
    {
        var server = FindWidgetsServer();
        Skip.If(server is null, "mcp/widgets-server/index.js не найден (сборка вне дерева репозитория)");
        Skip.IfNot(NodeAvailable(), "node не найден в PATH");

        var (probe, status) = NewProbe();
        var record = new McpServerRecord
        {
            Key = "widgets-probe", Transport = McpTransport.Stdio,
            Command = "node", Args = [server!],
        };

        var result = await probe.ProbeAsync("owner1", record);

        result.Error.Should().BeNull();
        result.Ok.Should().BeTrue();
        result.Status.Should().Be(McpServerStatuses.Connected);
        result.ServerName.Should().Be("widgets");
        result.ToolCount.Should().BeGreaterThan(0);
        result.ToolNames.Should().Contain("widget_show");
        // Наблюдение обязано осесть в сторе — ради него проба и нужна
        var observed = status.Get("owner1", "widgets-probe")!;
        observed.Status.Should().Be(McpServerStatuses.Connected);
        observed.Source.Should().Be(McpObservationSource.Probe);
    }

    [SkippableFact]
    public async Task НесуществующаяКоманда_ЭтоОтказСПричиной()
    {
        Skip.IfNot(NodeAvailable(), "node не найден в PATH");
        var (probe, status) = NewProbe(timeoutSeconds: 5);
        var record = new McpServerRecord
        {
            Key = "broken", Transport = McpTransport.Stdio,
            Command = "ccs-such-command-does-not-exist", Args = [],
        };

        var result = await probe.ProbeAsync("owner1", record);

        result.Ok.Should().BeFalse();
        result.Error.Should().NotBeNullOrWhiteSpace();
        status.Get("owner1", "broken")!.Status.Should().Be(McpServerStatuses.Failed);
    }

    [SkippableFact]
    public async Task ВтораяПробаТогоЖеСервера_НеПлодитПроцесс()
    {
        Skip.IfNot(NodeAvailable(), "node не найден в PATH");
        // Конец первой пробы задаёт тест (отменой), а не её таймаут: с таймаутом 5 с проба под
        // нагрузкой гасила сервер раньше, чем node успевал стартовать, и вторая проба уже не
        // видела занятого гейта. Таймаут — максимальный, он лишь страховка от вечного зависания.
        var (probe, _) = NewProbe(timeoutSeconds: 120);
        Directory.CreateDirectory(_dir);
        // Сервер, который молчит: первая проба висит, пока её не отменят. Стартовав, он
        // отмечается файлом — по нему ждём СОБЫТИЕ, а не фиксированную паузу
        var marker = Path.Combine(_dir, "started.marker");
        var record = new McpServerRecord
        {
            Key = "silent", Transport = McpTransport.Stdio,
            Command = "node",
            Args = ["-e", "require('fs').writeFileSync(process.argv[1],'1');process.stdin.resume()", marker],
        };

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watcher = new FileSystemWatcher(_dir, Path.GetFileName(marker));
        watcher.Created += (_, _) => started.TrySetResult();
        watcher.Changed += (_, _) => started.TrySetResult();
        watcher.EnableRaisingEvents = true;

        using var stopFirst = new CancellationTokenSource();
        var first = probe.ProbeAsync("owner1", record, stopFirst.Token);
        // Файл мог появиться до подписки — тогда события не будет, но он уже есть
        if (File.Exists(marker)) started.TrySetResult();

        // Первая проба завершилась раньше старта сервера — это провал, а не ожидание
        (await Task.WhenAny(started.Task, first)).Should().BeSameAs(started.Task,
            "проба обязана поднять сервер и держать его до отмены");

        var second = await probe.ProbeAsync("owner1", record);

        second.Error.Should().Contain("уже идёт");
        // Отказ «занято» — не наблюдение: статус сервера от него меняться не должен
        second.Status.Should().Be(McpServerStatuses.Unknown);
        stopFirst.Cancel();
        (await first).Ok.Should().BeFalse();
    }
}
