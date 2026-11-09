using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Plugins;
using ClaudeHomeServer.Services.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using PluginBridge = ClaudeHomeServer.Services.Plugins.PluginBridge;

namespace ClaudeHomeServer.Tests.Plugins;

// Обработчик tasks:read (ADR-031 §2.4, §3.4, шаг 3.5): операция tasks.list.
// Тесты (критерий):
//   - изоляция: владелец A не видит задачи владельца B;
//   - фильтр по проекту слота: null — личные, guid — задачи того проекта;
//   - лимит и truncated;
//   - сквозной через мост: с правом → данные, без права → denied.
// Мутации (критерий): убрать фильтр по владельцу → тест изоляции красный;
// убрать усечение → тест лимита красный.
public class TasksPluginCapabilityTests : IDisposable
{
    private const string OwnerA = "owner-a";
    private const string OwnerB = "owner-b";
    private const string ProjectX = "proj-x";
    private const string ProjectY = "proj-y";

    private readonly string _dir;
    private readonly TaskManager _tasks;
    private readonly TasksPluginCapability _sut;

    public TasksPluginCapabilityTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ccs_tasks_cap_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_dir, "projects.json"),
                ["Tasks:SaveDebounceMs"] = "0", // синхронная запись — тест
            })
            .Build();
        _tasks = new TaskManager(config);
        _sut = new TasksPluginCapability(_tasks);
    }

    public void Dispose()
    {
        try { _tasks.Dispose(); } catch { }
        try { Directory.Delete(_dir, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    // --- Хелперы ---

    private void CreateTask(string? project, string owner, string title) =>
        _tasks.Create(project, owner, new CreateTaskRequest(title));

    private static PluginCallContext Ctx(string owner, string? project = null) =>
        new(owner, "local-a/pkg", project, null);

    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement;

    // --- 1. Изоляция: владелец A не видит задачи владельца B ---
    // Мутация: убрать фильтр по владельцу (OwnerId) → этот тест красный.

    [Fact]
    public async Task Изоляция_ВладелецA_НеВидитЗадачиB()
    {
        CreateTask(ProjectX, OwnerA, "a-task");
        CreateTask(ProjectX, OwnerB, "b-task");

        var result = await _sut.HandleAsync(Ctx(OwnerA, ProjectX), "tasks.list", Args("{}"),
            [PluginPermission.TasksRead], CancellationToken.None);

        result.Ok.Should().BeTrue();
        var json = JsonSerializer.SerializeToElement(result.Data!);
        var tasks = json.GetProperty("tasks");
        tasks.GetArrayLength().Should().Be(1, "A видит только свою задачу");
        tasks[0].GetProperty("title").GetString().Should().Be("a-task");
    }

    // --- 2. Фильтр по проекту слота: null — только личные, guid — только того проекта ---

    [Fact]
    public async Task ФильтрПоПроектуСлота()
    {
        CreateTask(ProjectX, OwnerA, "x-task");
        CreateTask(ProjectY, OwnerA, "y-task");
        CreateTask(null, OwnerA, "personal-task");

        // Слот = X: только задачи X.
        var result = await _sut.HandleAsync(Ctx(OwnerA, ProjectX), "tasks.list", Args("{}"),
            [PluginPermission.TasksRead], CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(result.Data!);
        json.GetProperty("tasks").GetArrayLength().Should().Be(1);
        json.GetProperty("tasks")[0].GetProperty("title").GetString().Should().Be("x-task");

        // Слот = null: только личные (ProjectId == null).
        var result2 = await _sut.HandleAsync(Ctx(OwnerA, null), "tasks.list", Args("{}"),
            [PluginPermission.TasksRead], CancellationToken.None);
        var json2 = JsonSerializer.SerializeToElement(result2.Data!);
        json2.GetProperty("tasks").GetArrayLength().Should().Be(1, "без слота — только личные");
        json2.GetProperty("tasks")[0].GetProperty("title").GetString().Should().Be("personal-task");
    }

    // --- 3. Лимит: >100 задач → усечение, truncated=true ---
    // Мутация: убрать усечение (всегда вернуть все) → этот тест красный.

    [Fact]
    public async Task ЛимитЗаписей_Больше100_УсекаетСTruncated()
    {
        for (var i = 0; i < 150; i++)
            CreateTask(ProjectX, OwnerA, $"task-{i:D3}");

        var result = await _sut.HandleAsync(Ctx(OwnerA, ProjectX), "tasks.list", Args("{}"),
            [PluginPermission.TasksRead], CancellationToken.None);

        result.Ok.Should().BeTrue();
        var json = JsonSerializer.SerializeToElement(result.Data!);
        json.GetProperty("tasks").GetArrayLength().Should().Be(100, "потолок 100 записей");
        json.GetProperty("truncated").GetBoolean().Should().BeTrue();
    }

    // --- 4. Сквозной через мост: с правом → данные, без права → denied ---

    [Fact]
    public async Task СквознойЧерезМост_СПравом_ОтдаётДанные()
    {
        CreateTask(ProjectX, OwnerA, "target");

        var bridge = new PluginBridge([_sut]);
        var result = await bridge.DispatchAsync(Ctx(OwnerA, ProjectX), "tasks.list", Args("{}"),
            [PluginPermission.TasksRead], CancellationToken.None);

        result.Ok.Should().BeTrue("право tasks:read выдано → обработчик возвращает задачи");
        var json = JsonSerializer.SerializeToElement(result.Data!);
        json.GetProperty("tasks").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task СквознойЧерезМост_БезПрава_ОтдаётDenied()
    {
        CreateTask(ProjectX, OwnerA, "target");

        var bridge = new PluginBridge([_sut]);
        var result = await bridge.DispatchAsync(Ctx(OwnerA, ProjectX), "tasks.list", Args("{}"),
            [], CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.Error!.Code.Should().Be("denied");
    }
}
