using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Tasks;
using ClaudeHomeServer.Services.Tasks.Journal;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Tasks.Tests;

// И2 (D1b, мини-разрез D0 §2): ожидание этапа пишет только пара SetAwaitingHuman/ClearAwaitingHuman,
// каждое изменение пары ложится в журнал фичи (waiting / waiting_cleared) — по нему метрики этапа
// считают минуты человека. Закрытые здесь пути: перезапуск исполнителя, переход в Done, REST-тела.
// Мутации: убрать ClearAwaitingHuman из MarkClaudeStarted / из перехода в Done — краснеют первые два
// теста; снять [JsonIgnore] с поля DTO — краснеет третий.
public class AwaitingHumanJournalGuardTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ahg_" + Guid.NewGuid().ToString("N"));
    private readonly TaskManager _tasks;
    private readonly FeatureJournalStore _journal;

    public AwaitingHumanJournalGuardTests()
    {
        Directory.CreateDirectory(_dir);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DataPath"] = Path.Combine(_dir, "projects.json") })
            .Build();
        _journal = new FeatureJournalStore(Path.Combine(_dir, "feature-journal"));
        _tasks = new TaskManager(config, journal: _journal);
    }

    public void Dispose()
    {
        _tasks.Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private (TaskItem Epic, TaskItem Step) AwaitingStep()
    {
        var epic = _tasks.Create(null, "u", new CreateTaskRequest("эпик", Role: TaskRoles.Feature));
        var step = _tasks.Create(null, "u", new CreateTaskRequest("шаг",
            FeatureId: epic.Id, Role: TaskRoles.Step, StepRef: "E1", Lane: "бэк"));
        _tasks.MarkClaudeStarted(step.Id, "sess", DateTime.UtcNow);
        _tasks.SetAwaitingHuman(step.Id, "ждём");
        return (epic, step);
    }

    private IEnumerable<FeatureJournalEntry> Entries(TaskItem epic, string kind, string taskId) =>
        _journal.Read("u", epic.Id).Entries.Where(e => e.Kind == kind && e.TaskId == taskId);

    [Fact]
    public void Set_ПишетWaitingВЖурнал()
    {
        var (epic, step) = AwaitingStep();
        Entries(epic, "waiting", step.Id).Should().ContainSingle();
    }

    [Fact]
    public void ПерезапускИсполнителя_СнимаетОжиданиеИПишетWaitingCleared()
    {
        var (epic, step) = AwaitingStep();

        _tasks.MarkClaudeStarted(step.Id, "sess-2", DateTime.UtcNow);

        _tasks.GetById(step.Id)!.AwaitingHumanSince.Should().BeNull();
        Entries(epic, "waiting_cleared", step.Id).Should().ContainSingle();
    }

    [Fact]
    public void ПереходВDone_СнимаетОжиданиеИПишетWaitingCleared()
    {
        var (epic, step) = AwaitingStep();

        _tasks.Update(step.Id, new UpdateTaskRequest(Status: TaskItemStatus.Done));

        _tasks.GetById(step.Id)!.AwaitingHumanSince.Should().BeNull();
        Entries(epic, "waiting_cleared", step.Id).Should().ContainSingle();
    }

    // Сторож REST: тела POST/PUT /api/tasks (System.Text.Json, как у MVC) поля ожидания не принимают —
    // запись пары идёт только через SetAwaitingHuman под _awaitLock.
    [Fact]
    public void ТелаRestПоляОжиданияНеПринимают()
    {
        var opts = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        const string body = "\"awaitingHumanSince\":\"2026-11-12T10:00:00Z\",\"awaitingHumanReason\":\"x\"";

        var create = JsonSerializer.Deserialize<CreateTaskRequest>("{\"title\":\"t\"," + body + "}", opts)!;
        var update = JsonSerializer.Deserialize<UpdateTaskRequest>("{" + body + "}", opts)!;

        create.AwaitingHumanSince.Should().BeNull();
        create.AwaitingHumanReason.Should().BeNull();
        update.AwaitingHumanSince.Should().BeNull();
        update.AwaitingHumanReason.Should().BeNull();
    }
}
