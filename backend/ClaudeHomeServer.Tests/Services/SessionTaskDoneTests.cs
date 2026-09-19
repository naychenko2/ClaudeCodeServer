using System.Text.Json;
using System.Text.Json.Serialization;
using ClaudeHomeServer.Controllers;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Признак «Готово» для фильтра чатов (wire-поле taskDone): логика SessionTaskLinks.IsTaskDone
// (резолв TaskId → статус задачи через ITaskLookup) и присутствие поля в JSON обеих точек
// отдачи (Session — проектный список/SignalR; HomeSessionDto — глобальный summary).
// Раньше это читало статический Session.TaskDoneResolver, который ставил TaskManager — отсюда
// тест жил в безпараллельной коллекции. Теперь lookup явный, статик нет → коллекция снята.
public class SessionTaskDoneTests
{
    // Те же настройки JSON, что в Program.cs для AddControllers (camelCase + строки-enum).
    private static readonly JsonSerializerOptions WireOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    // Стерильный ITaskLookup: единственный известный id → Done, остальные → не найдена.
    private sealed class DoneTaskLookup : ITaskLookup
    {
        public TaskItem? GetById(string id) =>
            id == "t-done" ? new TaskItem { Id = id, Status = TaskItemStatus.Done } : null;
    }

    private static Session SessionWithTask(string? taskId) => new()
    {
        ProjectId = "p",
        OwnerId = "u",
        TaskId = taskId,
    };

    [Fact]
    public void TaskDone_БезЗадачи_False()
    {
        // Даже если бы lookup сказал true — без TaskId признак неприменим.
        SessionTaskLinks.IsTaskDone(SessionWithTask(null), new DoneTaskLookup())
            .Should().BeFalse("нет задачи — признак неприменим, чат не «Готово» по задаче");
    }

    [Fact]
    public void TaskDone_ЖиваяЗадача_False()
    {
        SessionTaskLinks.IsTaskDone(SessionWithTask("t-live"), new DoneTaskLookup())
            .Should().BeFalse("задача не Done");
    }

    [Fact]
    public void TaskDone_ВыполненнаяЗадача_True()
    {
        SessionTaskLinks.IsTaskDone(SessionWithTask("t-done"), new DoneTaskLookup())
            .Should().BeTrue("задача Done — чат уходит в чип «Готово»");
    }

    [Fact]
    public void TaskDone_СериализуетсяВSessionJson()
    {
        // Wire-поле taskDone подставляется в контроллере (SessionWire), поэтому здесь проверяем,
        // что SessionWire добавляет его в JSON Session (проектный список/SignalR).
        var wire = SessionWire.ToWire(SessionWithTask("t-done"), new DoneTaskLookup());
        var json = wire.ToJsonString();
        json.Should().Contain("\"taskDone\":true",
            "проектный список и SignalR отдают Session напрямую — поле должно ехать в wire");
    }

    [Fact]
    public void TaskDone_СериализуетсяВHomeSessionDtoJson()
    {
        var dto = new HomeSessionDto(
            Id: "s1", ProjectId: "p", ProjectName: "P", Name: "n",
            Status: SessionStatus.Finished, LastMessage: null, PersonaId: null,
            TaskId: "t-done", TaskDone: true, MessageCount: 0, UpdatedAt: DateTime.UtcNow,
            Origin: ChatOrigin.Manual, IsPinned: false, Tags: [], Participants: null,
            ExpiresAfterMinutes: null);
        var json = JsonSerializer.Serialize(dto, WireOpts);
        json.Should().Contain("\"taskDone\":true",
            "глобальный summary /api/home/summary — projection, поле проброшено явно");
    }
}
