using System.Text.Json;
using ClaudeHomeServer.Services.Deploy;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services;

// Приём заявок на выкатку (ADR-010). Проверяем ровно то, что ломается: белый список ref
// (ручка исполняет код на хосте — это граница привилегий), guard'ы «один деплой за раз» и
// «грязное дерево», отказ на машине без контура и формат журнала — шва с внешним агентом.
//
// Хостовые операции (git, schtasks) подменены фейком: на раннере CI нет ни Task Scheduler,
// ни боевого репозитория, а предмет проверки — решения сервиса, а не наличие утилит.
public class DeployServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly FakeDeployHost _host = new();

    public DeployServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ccs_deploy_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose() => TestFs.DeleteDirectoryResilient(_tempDir);

    private sealed class FakeDeployHost : IDeployHost
    {
        public DeployGitSnapshot Snapshot { get; set; } = new("4dc7ddab", [], null);
        public string? WakeError { get; set; }
        public int WakeCalls { get; private set; }

        public Task<DeployGitSnapshot> GitSnapshotAsync(string repoDir, CancellationToken ct = default) =>
            Task.FromResult(Snapshot);

        public Task<string?> WakeAgentAsync(DeployOptions options, CancellationToken ct = default)
        {
            WakeCalls++;
            return Task.FromResult(WakeError);
        }
    }

    private DeployService Build(bool enabled = true, Action<Dictionary<string, string?>>? tweak = null)
    {
        var values = new Dictionary<string, string?>
        {
            ["Deploy:Enabled"] = enabled ? "true" : "false",
            ["Deploy:RepoDir"] = Path.Combine(_tempDir, "repo"),
            ["Deploy:AgentDir"] = Path.Combine(_tempDir, "agent"),
            ["Deploy:PublishDir"] = Path.Combine(_tempDir, "publish"),
            ["Deploy:StagingDir"] = Path.Combine(_tempDir, "staging"),
            ["Deploy:ReleasesDir"] = Path.Combine(_tempDir, "releases"),
            ["Deploy:TaskName"] = "CCS-Deploy-Test",
        };
        tweak?.Invoke(values);
        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        return new DeployService(config, _host, NullLogger<DeployService>.Instance);
    }

    private string StatePath => Path.Combine(_tempDir, "releases", DeployState.FileName);

    private DeployState ReadJournal() =>
        JsonSerializer.Deserialize<DeployState>(File.ReadAllText(StatePath), DeployState.Json)!;

    // ---------- Белый список ref ----------

    [Theory]
    [InlineData("master")]
    [InlineData("feature/deploy-from-chat")]
    [InlineData("v1.2.3")]
    [InlineData("release_2026.08")]
    [InlineData("4dc7ddab")]
    public void Ref_допустимые_проходят(string value) =>
        DeployValidation.IsValidRef(value).Should().BeTrue();

    [Theory]
    // Попытка подсунуть аргумент агенту/git
    [InlineData("--upload-pack=calc")]
    [InlineData("-fmaster")]
    // Обход пути
    [InlineData("../../etc/passwd")]
    [InlineData("master..dev")]
    [InlineData("..")]
    // Инъекция команды оболочки
    [InlineData("master; rm -rf /")]
    [InlineData("master && calc")]
    [InlineData("master|calc")]
    [InlineData("$(whoami)")]
    [InlineData("master\ndeploy")]
    [InlineData("master\"")]
    [InlineData("master'")]
    // Прочее вне белого списка
    [InlineData("")]
    [InlineData("/master")]
    [InlineData("master/")]
    [InlineData("master.lock")]
    [InlineData("ветка")]
    public void Ref_недопустимые_отвергаются(string value) =>
        DeployValidation.IsValidRef(value).Should().BeFalse();

    [Fact]
    public void Ref_длиннее_ста_символов_отвергается()
    {
        DeployValidation.IsValidRef(new string('a', 100)).Should().BeTrue();
        DeployValidation.IsValidRef(new string('a', 101)).Should().BeFalse();
    }

    [Fact]
    public void Ref_с_обратным_слэшем_отвергается() =>
        // Windows-путь в ref — тот же обход, только в другую сторону
        DeployValidation.IsValidRef("..\\..\\windows\\system32").Should().BeFalse();

    [Fact]
    public async Task Заявка_с_плохим_ref_отклоняется_и_агента_не_будит()
    {
        var deploy = Build();

        var result = await deploy.StartAsync(new DeployStartRequest("--upload-pack=calc"), "u1", null);

        result.Status.Should().Be(DeployStartStatus.InvalidRef);
        _host.WakeCalls.Should().Be(0);
        File.Exists(StatePath).Should().BeFalse();
    }

    // ---------- Контур выключен ----------

    [Fact]
    public async Task Выключенный_контур_отказывает_и_не_трогает_планировщик()
    {
        var deploy = Build(enabled: false);

        var start = await deploy.StartAsync(new DeployStartRequest("master"), "u1", null);
        var rollback = await deploy.RollbackAsync(null, "u1", null);

        start.Status.Should().Be(DeployStartStatus.Disabled);
        rollback.Status.Should().Be(DeployStartStatus.Disabled);
        _host.WakeCalls.Should().Be(0);
    }

    [Fact]
    public async Task Неполная_секция_отказывает_до_планировщика()
    {
        var deploy = Build(tweak: v => v["Deploy:ReleasesDir"] = "");

        var result = await deploy.StartAsync(new DeployStartRequest("master"), "u1", null);

        result.Status.Should().Be(DeployStartStatus.Misconfigured);
        _host.WakeCalls.Should().Be(0);
    }

    // ---------- Guard'ы ----------

    [Fact]
    public async Task Идущая_выкатка_даёт_отказ_и_второго_агента_не_поднимает()
    {
        var deploy = Build();
        var first = await deploy.StartAsync(new DeployStartRequest("master"), "u1", "s1");
        first.Status.Should().Be(DeployStartStatus.Accepted);

        var second = await deploy.StartAsync(new DeployStartRequest("master"), "u1", "s1");

        second.Status.Should().Be(DeployStartStatus.AlreadyRunning);
        second.DeployId.Should().Be(first.DeployId);
        _host.WakeCalls.Should().Be(1);
    }

    [Fact]
    public async Task Завершённая_выкатка_не_блокирует_следующую()
    {
        var deploy = Build();
        var first = await deploy.StartAsync(new DeployStartRequest("master"), "u1", "s1");
        // Агент дописал итог — с этого момента выкатка не «идёт»
        var state = ReadJournal();
        state.Current!.Phase = DeployPhases.Succeeded;
        state.Current.Result = new DeployResult { Ok = true, Status = DeployPhases.Succeeded };
        File.WriteAllText(StatePath, JsonSerializer.Serialize(state, DeployState.Json));

        var second = await deploy.StartAsync(new DeployStartRequest("master"), "u1", "s1");

        second.Status.Should().Be(DeployStartStatus.Accepted);
        second.DeployId.Should().NotBe(first.DeployId);
        // Прошлая запись уехала в историю, current — новая заявка
        var after = ReadJournal();
        after.History.Should().ContainSingle(h => h.Id == first.DeployId);
        after.Current!.Id.Should().Be(second.DeployId);
    }

    [Fact]
    public async Task Грязное_дерево_без_allowDirty_отказ_со_списком_файлов()
    {
        _host.Snapshot = new DeployGitSnapshot("4dc7ddab",
            ["frontend/src/lib/design.ts", "docs/mockups/tablet-adaptive-proposal.md"], null);
        var deploy = Build();

        var result = await deploy.StartAsync(new DeployStartRequest("master"), "u1", null);

        result.Status.Should().Be(DeployStartStatus.DirtyTree);
        result.DirtyFiles.Should().BeEquivalentTo(
            ["frontend/src/lib/design.ts", "docs/mockups/tablet-adaptive-proposal.md"]);
        _host.WakeCalls.Should().Be(0);
    }

    [Fact]
    public async Task Грязное_дерево_с_allowDirty_едет_и_пишет_sha_и_dirty()
    {
        _host.Snapshot = new DeployGitSnapshot("4dc7ddab", ["frontend/src/lib/design.ts"], null);
        var deploy = Build();

        var result = await deploy.StartAsync(
            new DeployStartRequest("master", AllowDirty: true), "u1", null);

        result.Status.Should().Be(DeployStartStatus.Accepted);
        var current = ReadJournal().Current!;
        current.Sha.Should().Be("4dc7ddab");
        current.Dirty.Should().BeTrue();
        current.DirtyFiles.Should().ContainSingle();
    }

    [Fact]
    public async Task Git_не_отработал_ехать_вслепую_не_даём()
    {
        _host.Snapshot = new DeployGitSnapshot(null, [], "это не git-репозиторий");
        var deploy = Build();

        var result = await deploy.StartAsync(new DeployStartRequest("master"), "u1", null);

        result.Status.Should().Be(DeployStartStatus.GitFailed);
        _host.WakeCalls.Should().Be(0);
    }

    [Fact]
    public async Task Планировщик_не_запустился_заявка_не_виснет_в_очереди()
    {
        _host.WakeError = "задача CCS-Deploy-Test не найдена";
        var deploy = Build();

        var result = await deploy.StartAsync(new DeployStartRequest("master"), "u1", null);

        result.Status.Should().Be(DeployStartStatus.LaunchFailed);
        var current = ReadJournal().Current!;
        current.Phase.Should().Be(DeployPhases.Failed);
        current.Result!.Ok.Should().BeFalse();
        // Заказчик получил ошибку синхронно — докладывать нечего
        current.Reported.Should().BeTrue();

        // И следующая попытка не упирается в призрак предыдущей
        _host.WakeError = null;
        (await deploy.StartAsync(new DeployStartRequest("master"), "u1", null))
            .Status.Should().Be(DeployStartStatus.Accepted);
    }

    // ---------- Откат ----------

    [Fact]
    public async Task Откат_без_снимков_отклоняется()
    {
        var deploy = Build();

        var result = await deploy.RollbackAsync(null, "u1", null);

        result.Status.Should().Be(DeployStartStatus.NoRelease);
        _host.WakeCalls.Should().Be(0);
    }

    [Fact]
    public async Task Откат_на_известный_снимок_пишет_заявку_с_releaseId()
    {
        WriteJournal(new DeployState
        {
            Releases = [new DeployReleaseInfo { Id = "20260818-135500", Sha = "8e28cba9" }],
        });
        var deploy = Build();

        var result = await deploy.RollbackAsync("20260818-135500", "u1", "s1");

        result.Status.Should().Be(DeployStartStatus.Accepted);
        var current = ReadJournal().Current!;
        current.Kind.Should().Be(DeployKinds.Rollback);
        current.Request.ReleaseId.Should().Be("20260818-135500");
        _host.WakeCalls.Should().Be(1);
    }

    [Fact]
    public async Task Откат_на_неизвестный_снимок_отклоняется()
    {
        WriteJournal(new DeployState
        {
            Releases = [new DeployReleaseInfo { Id = "20260818-135500" }],
        });
        var deploy = Build();

        (await deploy.RollbackAsync("20260101-000000", "u1", null))
            .Status.Should().Be(DeployStartStatus.NoRelease);
        // Идентификатор релиза тоже под белым списком — путь к папке снимка строится по нему
        (await deploy.RollbackAsync("../../windows", "u1", null))
            .Status.Should().Be(DeployStartStatus.InvalidRef);
        _host.WakeCalls.Should().Be(0);
    }

    // ---------- Журнал ----------

    [Fact]
    public async Task Заявка_ложится_в_журнал_в_формате_шва_с_агентом()
    {
        var deploy = Build();

        var result = await deploy.StartAsync(
            new DeployStartRequest("master", SkipFrontend: true, SkipSandbox: true), "user-1", "sess-1");

        // Читаем файл как это сделает агент — по именам полей camelCase
        using var doc = JsonDocument.Parse(File.ReadAllText(StatePath));
        var current = doc.RootElement.GetProperty("current");
        current.GetProperty("id").GetString().Should().Be(result.DeployId);
        current.GetProperty("phase").GetString().Should().Be("queued");
        current.GetProperty("kind").GetString().Should().Be("deploy");
        current.GetProperty("ref").GetString().Should().Be("master");
        current.GetProperty("sha").GetString().Should().Be("4dc7ddab");
        current.GetProperty("dirty").GetBoolean().Should().BeFalse();
        current.GetProperty("reported").GetBoolean().Should().BeFalse();
        current.GetProperty("initiatedBy").GetProperty("userId").GetString().Should().Be("user-1");
        current.GetProperty("initiatedBy").GetProperty("sessionId").GetString().Should().Be("sess-1");
        // Параметры заявки едут агенту журналом, а не командной строкой
        var request = current.GetProperty("request");
        request.GetProperty("skipFrontend").GetBoolean().Should().BeTrue();
        request.GetProperty("skipSandbox").GetBoolean().Should().BeTrue();
        request.GetProperty("allowDirty").GetBoolean().Should().BeFalse();
        doc.RootElement.GetProperty("history").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public void Журнал_агента_читается_целиком()
    {
        // Ровно тот вид, что записан в ADR-010 — включая незнакомые серверу поля
        Directory.CreateDirectory(Path.Combine(_tempDir, "releases"));
        File.WriteAllText(StatePath, """
        {
          "current": {
            "id": "20260818-141230",
            "phase": "verifying",
            "ref": "master", "sha": "4dc7ddab", "dirty": false,
            "initiatedBy": { "userId": "u1", "sessionId": "s1" },
            "steps": [ { "name": "frontend", "status": "ok", "ms": 41200 } ],
            "result": null, "reported": false,
            "unknownFutureField": 42
          },
          "history": [ { "id": "20260818-120000", "phase": "failed" } ],
          "releases": [ { "id": "20260818-135500", "sha": "8e28cba9", "path": "C:/deploy/claude.releases/20260818-135500" } ]
        }
        """);

        var state = Build().Load();

        state.Current!.Id.Should().Be("20260818-141230");
        state.Current.Phase.Should().Be(DeployPhases.Verifying);
        state.Current.IsActive.Should().BeTrue();
        state.Current.Steps.Should().ContainSingle(s => s.Name == "frontend" && s.Ms == 41200);
        state.History.Should().ContainSingle(h => h.Id == "20260818-120000");
        state.Releases.Should().ContainSingle(r => r.Sha == "8e28cba9");
    }

    [Fact]
    public async Task Незадоложенный_итог_поднимается_и_гасится_отметкой()
    {
        WriteJournal(new DeployState
        {
            Current = new DeployRecord
            {
                Id = "20260818-141230",
                Phase = DeployPhases.RolledBack,
                InitiatedBy = new DeployInitiator { UserId = "u1", SessionId = "s1" },
                Result = new DeployResult { Ok = false, Status = DeployPhases.RolledBack, Message = "health не сошёлся" },
            },
        });
        var deploy = Build();

        var pending = deploy.PendingReport();
        pending!.Id.Should().Be("20260818-141230");

        await deploy.MarkReportedAsync(pending.Id);

        deploy.PendingReport().Should().BeNull();
        var after = ReadJournal();
        // Доложенная и завершённая выкатка уходит в историю — current свободен
        after.Current.Should().BeNull();
        after.History.Should().ContainSingle(h => h.Id == "20260818-141230" && h.Reported);
    }

    [Fact]
    public void Идущая_выкатка_докладом_не_считается()
    {
        WriteJournal(new DeployState
        {
            Current = new DeployRecord { Id = "20260818-141230", Phase = DeployPhases.Building },
        });

        Build().PendingReport().Should().BeNull();
    }

    [Fact]
    public void Битый_журнал_не_роняет_чтение()
    {
        Directory.CreateDirectory(Path.Combine(_tempDir, "releases"));
        File.WriteAllText(StatePath, "{ это не json ");

        var state = Build().Load();

        state.Current.Should().BeNull();
        state.History.Should().BeEmpty();
    }

    // ---------- Разбор git status ----------

    [Fact]
    public void Разбор_porcelain_даёт_пути_без_кодов_статуса()
    {
        var files = DeployHost.ParseDirty(
            " M frontend/src/lib/design.ts\n?? docs/adr/ADR-010-deploy-from-chat.md\nR  a.txt -> b.txt\n");

        files.Should().BeEquivalentTo(
            ["frontend/src/lib/design.ts", "docs/adr/ADR-010-deploy-from-chat.md", "a.txt -> b.txt"]);
    }

    [Fact]
    public void Разбор_пустого_вывода_даёт_чистое_дерево() =>
        DeployHost.ParseDirty("").Should().BeEmpty();

    private void WriteJournal(DeployState state)
    {
        Directory.CreateDirectory(Path.Combine(_tempDir, "releases"));
        File.WriteAllText(StatePath, JsonSerializer.Serialize(state, DeployState.Json));
    }
}
