using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.DeviceAgent.Composition;
using ClaudeHomeServer.DeviceAgent.Exec;
using ClaudeHomeServer.DeviceAgent.Hosting;
using ClaudeHomeServer.DeviceAgent.Relay;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Execution;
using ClaudeHomeServer.Services.Files;
using ClaudeHomeServer.Services.Git;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Controllers;

/// <summary>
/// Локальные проекты в REST (ADR-016, задача 3.1): создание только за флагом и только на
/// устройстве с exec, матрица и устройство в DTO (контракт для фронта, задача 3.4),
/// перепривязка при чатах — 409. Папку на устройстве создание и перепривязка проверяют ДО
/// сохранения настоящим обработчиком агента (RelayHandler + AgentPathPolicy) через канал
/// ретранслятора: нет папки, файл, вне корней, офлайн — 400; старый агент — пропуск.
///
/// Агент с выдачей папки (<see cref="DeviceCapabilities.BindFolder"/>, решение владельца
/// 2026-09-27) — настоящий <see cref="ProjectFolderBinder"/>: нет папки — создана и разрешена,
/// вне корней — разрешена, запретный путь и выключенная автовыдача — 400. Устройства без
/// этой возможности («dev-exec») — старый агент: прежний отказ с подсказкой команды.
/// </summary>
public sealed class ProjectsControllerLocalProjectTests : IDisposable
{
    private sealed class FakeDeviceExec : IDeviceExecChannel
    {
        public Dictionary<string, DeviceExecStatus> Devices { get; } = [];
        public DeviceExecStatus? GetStatus(string ownerId, string deviceId) => Devices.GetValueOrDefault(deviceId);
        public Task<IDeviceExecStream> OpenAsync(string ownerId, string deviceId, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    /// <summary>Канал ретранслятора по устройствам: у каждого свой агент.</summary>
    private sealed class RelayByDevice : IDeviceRelayChannel
    {
        public Dictionary<string, LoopbackRelayChannel> Agents { get; } = [];
        public Task<IDeviceExecStream> OpenRelayAsync(string ownerId, string deviceId, CancellationToken ct = default) =>
            Agents[deviceId].OpenRelayAsync(ownerId, deviceId, ct);
    }

    /// <summary>Канал выдачи папки по устройствам.</summary>
    private sealed class BindByDevice : IDeviceFolderBindChannel
    {
        public Dictionary<string, LoopbackRelayChannel> Agents { get; } = [];
        public Task<IDeviceExecStream> OpenBindFolderAsync(string ownerId, string deviceId, CancellationToken ct = default) =>
            Agents[deviceId].OpenRelayAsync(ownerId, deviceId, ct);
    }

    private sealed class AllowedRoots(params string[] roots) : IAgentRoots
    {
        public IReadOnlyList<string> Roots { get; } = roots;
    }

    private readonly FakeDeviceExec _devices = new();
    private readonly RelayByDevice _relay = new();
    private readonly BindByDevice _bind = new();
    private readonly TestWebApplicationFactory _factory;
    private readonly HttpClient _client;

    // Машина устройства: разрешённый корень агента и «чужой» каталог рядом
    private readonly string _machine = Path.Combine(Path.GetTempPath(), "lp-agent-" + Guid.NewGuid().ToString("N"));
    private string AllowedRoot => Path.Combine(_machine, "allowed");
    private string Outside => Path.Combine(_machine, "outside");

    // Машина агента с выдачей папки — рядом с тестами, а не в temp: на Windows temp лежит в
    // запретном для выдачи AppData. Профиль этой «машины» — BindHome
    private readonly string _bindMachine = Path.Combine(AppContext.BaseDirectory, "lp-bind-" + Guid.NewGuid().ToString("N"));
    private string BindHome => Path.Combine(_bindMachine, "home");
    private readonly AgentRootsStore _bindRoots;

    public ProjectsControllerLocalProjectTests()
    {
        Directory.CreateDirectory(AllowedRoot);
        Directory.CreateDirectory(Outside);

        _devices.Devices["dev-exec"] = Status("dev-exec", online: true, DeviceCapabilities.Exec, DeviceCapabilities.Relay);
        _devices.Devices["dev-offline"] = Status("dev-offline", online: false, DeviceCapabilities.Exec, DeviceCapabilities.Relay);
        _devices.Devices["dev-old"] = Status("dev-old", online: true, DeviceCapabilities.Exec);
        _devices.Devices["dev-old-relay"] = Status("dev-old-relay", online: true, DeviceCapabilities.Exec, DeviceCapabilities.Relay);
        _devices.Devices["dev-noexec"] = Status("dev-noexec", online: true, DeviceCapabilities.Files);
        _devices.Devices["dev-bind"] = Status("dev-bind", online: true,
            DeviceCapabilities.Exec, DeviceCapabilities.Relay, DeviceCapabilities.BindFolder);
        _devices.Devices["dev-bind-refused"] = Status("dev-bind-refused", online: true,
            DeviceCapabilities.Exec, DeviceCapabilities.Relay, DeviceCapabilities.BindFolder);

        _factory = new TestWebApplicationFactory
        {
            ExtraServices = s =>
            {
                s.AddSingleton<IDeviceExecChannel>(_devices);
                s.AddSingleton<IDeviceRelayChannel>(_relay);
                s.AddSingleton<IDeviceFolderBindChannel>(_bind);
            },
        };
        _client = _factory.CreateAuthenticatedClient();

        // Папки сервера тоже разрешены агенту: «один путь на сервере и устройстве»
        var git = new GitService(AgentLauncherFactory.Instance);
        var policy = new AgentPathPolicy(new AllowedRoots(AllowedRoot, _factory.TempDir));
        var agent = new RelayHandler(new AgentProjectFiles(new FileService(git), policy), git);
        _relay.Agents["dev-exec"] = new LoopbackRelayChannel { Agent = agent.RunAsync };
        _relay.Agents["dev-old-relay"] = new LoopbackRelayChannel { Agent = OldAgentAsync };

        // Агент с выдачей папки: свои корни (пока пусто), профиль и каталог конфига в запретном списке
        PrivateDir(BindHome);
        var config = PrivateDir(Path.Combine(_bindMachine, "config"));
        _bindRoots = new AgentRootsStore(Path.Combine(config, "roots.json"));
        var bindPolicy = new AgentPathPolicy(_bindRoots);
        var binder = new ProjectFolderBinder(_bindRoots, bindPolicy, new AgentForbiddenContext([BindHome], [config], []));
        var bindRelay = new RelayHandler(new AgentProjectFiles(new FileService(git), bindPolicy), git);
        _bind.Agents["dev-bind"] = new LoopbackRelayChannel { Agent = binder.RunAsync };
        _relay.Agents["dev-bind"] = new LoopbackRelayChannel { Agent = bindRelay.RunAsync };
        // Объявил возможность, но канал выдачи отказал как старому агенту — откат на проверку
        _bind.Agents["dev-bind-refused"] = new LoopbackRelayChannel
        {
            Refuse = new DeviceExecRefusedException(DeviceExecRefusal.NoBindFolderCapability, "старый агент"),
        };
        _relay.Agents["dev-bind-refused"] = new LoopbackRelayChannel { Agent = bindRelay.RunAsync };
    }

    // Каталог только владельцу на запись: общий на запись корень агент не добавит
    private static string PrivateDir(string path)
    {
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(path);
        else Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    public void Dispose()
    {
        _factory.Dispose();
        try { Directory.Delete(_machine, recursive: true); } catch { /* временная папка */ }
        try { Directory.Delete(_bindMachine, recursive: true); } catch { /* временная папка */ }
    }

    // Агент до операции check-path: незнакомая операция — 400, как у RelayHandler того времени
    private static async Task OldAgentAsync(ExecLink link, CancellationToken ct)
    {
        await using var owned = link;
        await foreach (var _ in link.ReadAllAsync(ct)) break;
        var body = JsonSerializer.SerializeToUtf8Bytes(new { error = "Операция не поддерживается ретранслятором" }, RelayProtocol.Json);
        await link.SendAsync(DeviceExecFrameChannel.Info,
            JsonSerializer.SerializeToUtf8Bytes(new RelayResponseHead(400, "application/json", body.LongLength), RelayProtocol.Json), ct);
        await link.SendAsync(DeviceExecFrameChannel.Stdout, body, ct);
        await link.SendAsync(DeviceExecFrameChannel.Exit, DeviceExecJson.Serialize(new DeviceExecExit(0)), ct);
        await link.DrainAsync(TimeSpan.FromSeconds(10));
    }

    private static DeviceExecStatus Status(string id, bool online, params string[] caps) =>
        new(id, "home", online, "linux-x64", "1.0.0", "2.0.0", "2.0.0", caps, true, null);

    private async Task SetFlagAsync(bool enabled) =>
        (await _client.PutAsJsonAsync($"/api/feature-flags/{FeatureFlagKeys.LocalProjects}", new { enabled }))
            .EnsureSuccessStatusCode();

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage r) =>
        JsonSerializer.Deserialize<JsonElement>(await r.Content.ReadAsStringAsync());

    private static async Task<string> ErrorAsync(HttpResponseMessage r) =>
        (await BodyAsync(r)).GetProperty("error").GetString()!;

    private string MkDir()
    {
        var dir = Path.Combine(_factory.TempDir, "lp_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    // Папка на «устройстве» под разрешённым корнем агента
    private string DeviceDir()
    {
        var dir = Path.Combine(AllowedRoot, "p_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private Task<HttpResponseMessage> CreateLocalAsync(string deviceId, string rootPath) =>
        _client.PostAsJsonAsync("/api/projects", new { name = "L" + Guid.NewGuid().ToString("N")[..6], rootPath, deviceId });

    private async Task<string> CreateServerAsync()
    {
        var created = await _client.PostAsJsonAsync("/api/projects", new { name = "S" + Guid.NewGuid().ToString("N")[..6], rootPath = MkDir() });
        return (await BodyAsync(created)).GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task Создание_БезФлага_400()
    {
        var r = await CreateLocalAsync("dev-exec", DeviceDir());

        r.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Создание_УстройствоБезExec_400()
    {
        await SetFlagAsync(true);

        var r = await CreateLocalAsync("dev-noexec", DeviceDir());

        r.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorAsync(r)).Should().Be(ProjectCapabilities.NoExecReason);
    }

    [Fact]
    public async Task Создание_НеизвестноеУстройство_400()
    {
        await SetFlagAsync(true);

        var r = await CreateLocalAsync("nope", DeviceDir());

        r.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Создание_ФлагИExec_201_ВDtoМатрицаИУстройство()
    {
        await SetFlagAsync(true);

        var r = await _client.PostAsJsonAsync("/api/projects",
            new { name = "L", rootPath = DeviceDir(), deviceId = "dev-exec", enableGit = true });

        r.StatusCode.Should().Be(HttpStatusCode.Created);
        var id = (await BodyAsync(r)).GetProperty("id").GetString()!;
        // Устройство ушло в офлайн после создания: матрица в DTO это показывает
        _devices.Devices["dev-exec"] = Status("dev-exec", online: false, DeviceCapabilities.Exec, DeviceCapabilities.Relay);
        var body = await BodyAsync(await _client.GetAsync($"/api/projects/{id}"));
        body.GetProperty("deviceId").GetString().Should().Be("dev-exec");
        body.GetProperty("device").GetProperty("online").GetBoolean().Should().BeFalse();
        body.GetProperty("device").GetProperty("name").GetString().Should().Be("home");
        var caps = body.GetProperty("capabilities");
        caps.GetProperty("host").GetString().Should().Be(CapabilityHost.Device);
        caps.GetProperty("files").GetProperty("host").GetString().Should().Be(CapabilityHost.Device);
        caps.GetProperty("platform").GetProperty("available").GetBoolean().Should().BeTrue();
        caps.GetProperty("serverContent").GetProperty("host").GetString().Should().Be(CapabilityHost.Off);
        caps.GetProperty("exec").GetProperty("available").GetBoolean().Should().BeFalse();
        caps.GetProperty("exec").GetProperty("reason").GetString().Should().Be(ProjectCapabilities.DeviceOfflineReason);
    }

    // ---------- проверка папки на устройстве ----------

    [Fact]
    public async Task Создание_НетПапкиНаУстройстве_400_ПроектНеСоздан()
    {
        await SetFlagAsync(true);
        var missing = Path.Combine(AllowedRoot, "nope");

        var r = await CreateLocalAsync("dev-exec", missing);

        r.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorAsync(r)).Should().Be($"Папки «{missing}» нет на устройстве «home». Создайте её или укажите другую");
        (await _client.GetFromJsonAsync<JsonElement>("/api/projects")).EnumerateArray()
            .Should().NotContain(p => p.GetProperty("rootPath").GetString() == missing);
    }

    [Fact]
    public async Task Создание_ПапкаВнеКорней_400СПодсказкойКоманды()
    {
        await SetFlagAsync(true);

        var r = await CreateLocalAsync("dev-exec", Outside);

        r.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorAsync(r)).Should().StartWith("Папка вне разрешённых на устройстве")
            .And.Contain($"ai-home-agent roots add \"{Outside}\"");
    }

    [Fact]
    public async Task Создание_ПутьКФайлу_400()
    {
        await SetFlagAsync(true);
        var file = Path.Combine(DeviceDir(), "a.txt");
        File.WriteAllText(file, "x");

        var r = await CreateLocalAsync("dev-exec", file);

        r.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorAsync(r)).Should().Contain("файл, а не папка");
    }

    [Fact]
    public async Task Создание_УстройствоОфлайн_400_ПроверитьНельзя()
    {
        await SetFlagAsync(true);

        var r = await CreateLocalAsync("dev-offline", DeviceDir());

        r.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorAsync(r)).Should().Be("Устройство не в сети — проверить папку нельзя. Включите компьютер с агентом");
    }

    [Theory]
    [InlineData("dev-old")]       // агент без ретранслятора вовсе
    [InlineData("dev-old-relay")] // ретранслятор есть, операции check-path нет
    public async Task Создание_СтарыйАгент_ПроверкаПропущена_201(string deviceId)
    {
        await SetFlagAsync(true);

        var r = await CreateLocalAsync(deviceId, Path.Combine(AllowedRoot, "nope"));

        r.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Перепривязка_НетПапкиНаУстройстве_400_ПривязкаНеМеняется()
    {
        await SetFlagAsync(true);
        var id = await CreateServerAsync();

        var r = await _client.PutAsJsonAsync($"/api/projects/{id}/device",
            new { deviceId = "dev-exec", rootPath = Path.Combine(AllowedRoot, "nope") });

        r.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorAsync(r)).Should().Contain("нет на устройстве «home»");
        (await BodyAsync(await _client.GetAsync($"/api/projects/{id}"))).GetProperty("deviceId").ValueKind
            .Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Перепривязка_ВнеКорней_400()
    {
        await SetFlagAsync(true);
        var id = await CreateServerAsync();

        var r = await _client.PutAsJsonAsync($"/api/projects/{id}/device", new { deviceId = "dev-exec", rootPath = Outside });

        r.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorAsync(r)).Should().StartWith("Папка вне разрешённых на устройстве");
    }

    [Fact]
    public async Task Перепривязка_УстройствоОфлайн_400()
    {
        await SetFlagAsync(true);
        var id = await CreateServerAsync();

        var r = await _client.PutAsJsonAsync($"/api/projects/{id}/device", new { deviceId = "dev-offline", rootPath = DeviceDir() });

        r.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorAsync(r)).Should().Contain("Устройство не в сети");
    }

    [Fact]
    public async Task Перепривязка_ТаЖеПривязка_УстройствоНеСпрашивается()
    {
        await SetFlagAsync(true);
        var dir = DeviceDir();
        var id = (await BodyAsync(await CreateLocalAsync("dev-exec", dir))).GetProperty("id").GetString()!;
        _devices.Devices["dev-exec"] = Status("dev-exec", online: false, DeviceCapabilities.Exec, DeviceCapabilities.Relay);

        var r = await _client.PutAsJsonAsync($"/api/projects/{id}/device", new { deviceId = "dev-exec", rootPath = dir });

        r.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ---------- выдача папки агентом (bind-project-folder) ----------

    [Fact]
    public async Task Создание_НетПапки_АгентСоздаётИРазрешает_201СУведомлением()
    {
        await SetFlagAsync(true);
        var path = Path.Combine(BindHome, "projects", "app");

        var r = await _client.PostAsJsonAsync("/api/projects", new { name = "Мой проект", rootPath = path, deviceId = "dev-bind" });

        r.StatusCode.Should().Be(HttpStatusCode.Created);
        (await BodyAsync(r)).GetProperty("folderNotice").GetString()
            .Should().Be($"Папка «{path}» создана и разрешена агенту на «home»");
        Directory.Exists(path).Should().BeTrue();
        _bindRoots.Roots.Should().Equal(path);
        _bindRoots.LabelOf(path).Should().Be("добавлен автоматически для проекта «Мой проект»");
    }

    [Fact]
    public async Task Создание_ПапкаВнеКорней_АгентРазрешает_201()
    {
        await SetFlagAsync(true);
        var path = PrivateDir(Path.Combine(BindHome, "existing"));

        var r = await CreateLocalAsync("dev-bind", path);

        r.StatusCode.Should().Be(HttpStatusCode.Created);
        (await BodyAsync(r)).GetProperty("folderNotice").GetString()
            .Should().Be($"Папка «{path}» разрешена агенту на «home»");
        _bindRoots.Roots.Should().Equal(path);
    }

    [Fact]
    public async Task Создание_ПапкаУжеПодКорнем_201БезУведомления()
    {
        await SetFlagAsync(true);
        var root = PrivateDir(Path.Combine(BindHome, "work"));
        _bindRoots.Add(root);
        var path = PrivateDir(Path.Combine(root, "app"));

        var r = await CreateLocalAsync("dev-bind", path);

        r.StatusCode.Should().Be(HttpStatusCode.Created);
        (await BodyAsync(r)).GetProperty("folderNotice").ValueKind.Should().Be(JsonValueKind.Null);
        _bindRoots.Roots.Should().Equal(root);
    }

    [Fact]
    public async Task Создание_ЗапретныйПуть_400_НичегоНеСоздано()
    {
        await SetFlagAsync(true);
        var ssh = Path.Combine(BindHome, ".ssh");

        var r = await CreateLocalAsync("dev-bind", ssh);

        r.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorAsync(r)).Should().StartWith("Агент на «home» не выдаёт эту папку:");
        Directory.Exists(ssh).Should().BeFalse();
        _bindRoots.Roots.Should().BeEmpty();
    }

    [Fact]
    public async Task Создание_АвтовыдачаВыключенаНаМашине_ПрежнийОтказСПодсказкой()
    {
        await SetFlagAsync(true);
        _bindRoots.SetAuto(false);
        var missing = Path.Combine(BindHome, "nope");
        var outside = PrivateDir(Path.Combine(BindHome, "existing"));

        var m = await CreateLocalAsync("dev-bind", missing);
        var o = await CreateLocalAsync("dev-bind", outside);

        m.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorAsync(m)).Should().Be($"Папки «{missing}» нет на устройстве «home». Создайте её или укажите другую");
        o.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorAsync(o)).Should().Contain($"ai-home-agent roots add \"{outside}\"");
        Directory.Exists(missing).Should().BeFalse();
        _bindRoots.Roots.Should().BeEmpty();
    }

    [Fact]
    public async Task Создание_СтарыйАгентБезВыдачи_ПрежнийОтказСПодсказкойКоманды()
    {
        await SetFlagAsync(true);

        // dev-exec возможность bind-folder не объявил; dev-bind-refused объявил, но канал отказал
        var r1 = await CreateLocalAsync("dev-exec", Outside);
        var r2 = await CreateLocalAsync("dev-bind-refused", Outside);

        foreach (var r in new[] { r1, r2 })
        {
            r.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await ErrorAsync(r)).Should().StartWith("Папка вне разрешённых на устройстве")
                .And.Contain($"ai-home-agent roots add \"{Outside}\"");
        }
        _bindRoots.Roots.Should().BeEmpty();
    }

    [Fact]
    public async Task Перепривязка_НетПапки_АгентСоздаётИРазрешает_200СУведомлением()
    {
        await SetFlagAsync(true);
        var id = await CreateServerAsync();
        var path = Path.Combine(BindHome, "moved");

        var r = await _client.PutAsJsonAsync($"/api/projects/{id}/device", new { deviceId = "dev-bind", rootPath = path });

        r.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await BodyAsync(r);
        body.GetProperty("deviceId").GetString().Should().Be("dev-bind");
        body.GetProperty("folderNotice").GetString().Should().Be($"Папка «{path}» создана и разрешена агенту на «home»");
        Directory.Exists(path).Should().BeTrue();
        _bindRoots.Roots.Should().Equal(path);
    }

    [Fact]
    public async Task Перепривязка_ВнеКорней_АгентРазрешает_200()
    {
        await SetFlagAsync(true);
        var id = await CreateServerAsync();
        var path = PrivateDir(Path.Combine(BindHome, "existing"));

        var r = await _client.PutAsJsonAsync($"/api/projects/{id}/device", new { deviceId = "dev-bind", rootPath = path });

        r.StatusCode.Should().Be(HttpStatusCode.OK);
        _bindRoots.Roots.Should().Equal(path);
    }

    // ---------- прежнее поведение ----------

    [Fact]
    public async Task СерверныйПроект_МатрицаВсёНаСервере()
    {
        var r = await _client.PostAsJsonAsync("/api/projects", new { name = "S", rootPath = MkDir() });

        r.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await BodyAsync(r);
        body.GetProperty("deviceId").ValueKind.Should().Be(JsonValueKind.Null);
        body.GetProperty("device").ValueKind.Should().Be(JsonValueKind.Null);
        var caps = body.GetProperty("capabilities");
        caps.GetProperty("host").GetString().Should().Be(CapabilityHost.Server);
        caps.GetProperty("serverContent").GetProperty("available").GetBoolean().Should().BeTrue();
        caps.GetProperty("exec").GetProperty("available").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task ОдинПутьНаСервереИУстройстве_ДваПроекта()
    {
        await SetFlagAsync(true);
        var dir = MkDir();

        (await _client.PostAsJsonAsync("/api/projects", new { name = "S", rootPath = dir }))
            .StatusCode.Should().Be(HttpStatusCode.Created);
        (await _client.PostAsJsonAsync("/api/projects", new { name = "L", rootPath = dir, deviceId = "dev-exec" }))
            .StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Перепривязка_ПриЧатах_409()
    {
        await SetFlagAsync(true);
        var id = await CreateServerAsync();
        (await _client.PostAsJsonAsync($"/api/projects/{id}/sessions", new { mode = "auto" })).EnsureSuccessStatusCode();

        var r = await _client.PutAsJsonAsync($"/api/projects/{id}/device",
            new { deviceId = "dev-exec", rootPath = DeviceDir() });

        r.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Перепривязка_БезЧатов_200()
    {
        await SetFlagAsync(true);
        var id = await CreateServerAsync();

        var r = await _client.PutAsJsonAsync($"/api/projects/{id}/device",
            new { deviceId = "dev-exec", rootPath = DeviceDir() });

        r.StatusCode.Should().Be(HttpStatusCode.OK);
        (await BodyAsync(r)).GetProperty("deviceId").GetString().Should().Be("dev-exec");
    }

    [Fact]
    public async Task Перепривязка_БезФлага_400()
    {
        var id = await CreateServerAsync();

        var r = await _client.PutAsJsonAsync($"/api/projects/{id}/device",
            new { deviceId = "dev-exec", rootPath = DeviceDir() });

        r.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
