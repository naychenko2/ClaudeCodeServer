using System.Text.Json;
using System.Text.Json.Serialization;
using ClaudeHomeServer.Controllers;
using ClaudeHomeServer.Models;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Признак «Готово» для фильтра чатов (wire-поле taskDone): логика резолвера
// TaskId → статус задачи и присутствие поля в JSON обеих точек отдачи списка чатов
// (Session напрямую — проектный список/SignalR; HomeSessionDto — глобальный summary).
// Платформонезависимый unit по модели, без DI.
public class SessionTaskDoneTests
{
    // Те же настройки JSON, что в Program.cs для AddControllers (camelCase + строки-enum).
    private static readonly JsonSerializerOptions WireOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private static Session SessionWithTask(string? taskId) => new()
    {
        ProjectId = "p",
        OwnerId = "u",
        TaskId = taskId,
    };

    [Fact]
    public void TaskDone_БезЗадачи_False()
    {
        var prev = Session.TaskDoneResolver;
        try
        {
            Session.TaskDoneResolver = _ => true; // даже если бы резолвер сказал true
            SessionWithTask(null).TaskDone
                .Should().BeFalse("нет задачи — признак неприменим, чат не «Готово» по задаче");
        }
        finally { Session.TaskDoneResolver = prev; }
    }

    [Fact]
    public void TaskDone_ЖиваяЗадача_False()
    {
        var prev = Session.TaskDoneResolver;
        try
        {
            Session.TaskDoneResolver = _ => false;
            SessionWithTask("t-live").TaskDone.Should().BeFalse("задача не Done");
        }
        finally { Session.TaskDoneResolver = prev; }
    }

    [Fact]
    public void TaskDone_ВыполненнаяЗадача_True()
    {
        var prev = Session.TaskDoneResolver;
        try
        {
            Session.TaskDoneResolver = id => id == "t-done";
            SessionWithTask("t-done").TaskDone
                .Should().BeTrue("задача Done — чат уходит в чип «Готово»");
        }
        finally { Session.TaskDoneResolver = prev; }
    }

    [Fact]
    public void TaskDone_СериализуетсяВSessionJson()
    {
        // Резолвер не трогаем: проверяем лишь, что свойство вообще попадает в wire JSON
        // проектного списка/SignalR (Session отдаётся напрямую). Значение здесь не важно —
        // без резолвера TaskDone=false, но поле обязано присутствовать. Намеренно не задаём
        // Session.TaskDoneResolver, чтобы тест не зависел от глобальной статики и её гонок
        // с параллельными fixture-тестами TaskManager (конструктор переназначает резолвер).
        var json = JsonSerializer.Serialize(SessionWithTask("t-done"), WireOpts);
        json.Should().Contain("\"taskDone\":",
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
