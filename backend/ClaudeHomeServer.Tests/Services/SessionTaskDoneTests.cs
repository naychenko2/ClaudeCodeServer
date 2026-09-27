using System.Text.Json;
using System.Text.Json.Serialization;
using ClaudeHomeServer.Controllers;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Признак «Готово» для фильтра чатов (wire-поле taskDone): логика SessionTaskLinks.IsTaskDone
// (резолв TaskId → статус задачи через ITaskLookup) и присутствие поля в JSON обеих точек
// отдачи (Session — конвертером на границе сериализации; HomeSessionDto — проекцией
// контроллера в глобальном summary). Раньше это читало статический Session.TaskDoneResolver,
// который ставил TaskManager — отсюда тест жил в безпараллельной коллекции. Теперь lookup
// явный, статик нет → коллекция снята.
// Сторож ФАКТА отдачи полей из HTTP-эндпоинтов — SessionLinkFieldsWireTests (интеграционный);
// здесь — юнит на сам конвертер и его правила wire.
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
            id == "t-done"
                ? new TaskItem { Id = id, Status = TaskItemStatus.Done, SourceSessionId = "s-author" }
                : null;
    }

    // Минимальный провайдер под конвертер: он резолвит ITaskLookup лениво из DI.
    private sealed class LookupProvider(ITaskLookup tasks) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType == typeof(ITaskLookup) ? tasks : null;
    }

    // Опции wire с конвертером Session — то же, что собирает SessionJsonOptionsSetup в MVC.
    private static JsonSerializerOptions WireOptsWithConverter(ITaskLookup tasks)
    {
        var opts = new JsonSerializerOptions(WireOpts);
        opts.Converters.Add(new SessionJsonConverter(new LookupProvider(tasks)));
        return opts;
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
        // Wire-поля дописывает конвертер Session на границе сериализации (одна точка на все
        // эндпоинты) — проверяем, что оба поля есть в JSON и значения верные.
        var json = JsonSerializer.Serialize(
            SessionWithTask("t-done"), WireOptsWithConverter(new DoneTaskLookup()));

        json.Should().Contain("\"taskDone\":true",
            "любая отдача Session идёт через конвертер — поле должно ехать в wire");
        json.Should().Contain("\"parentSessionId\":\"s-author\"",
            "родитель чата-исполнителя — чат, в котором создали задачу");
    }

    [Fact]
    public void Конвертер_НеТеряетПоляМодели_иСоблюдаетПравилаWire()
    {
        // Конвертер сериализует Session КЛОНОМ тех же опций (camelCase + enum строками), а не
        // самодельными: расхождение правил разъехалось бы с фронт-типом Session молча.
        var s = SessionWithTask("t-live");
        s.Mode = ClaudeMode.Plan;

        var json = JsonSerializer.Serialize(s, WireOptsWithConverter(new DoneTaskLookup()));

        json.Should().Contain("\"mode\":\"plan\"", "enum — строкой в camelCase, как в MVC-опциях");
        json.Should().Contain("\"ownerId\":\"u\"", "обычные поля модели уходят без изменений");
        json.Should().Contain("\"taskDone\":false").And.Contain("\"parentSessionId\":null",
            "у живой задачи признак false, родитель не резолвится");
    }

    [Fact]
    public void Конвертер_ЧитаетSessionКакПрежде()
    {
        // Read обязан работать: тот же тип читается из тела запроса и (своими опциями) из
        // sessions.json — конвертер не должен ломать десериализацию.
        var opts = WireOptsWithConverter(new DoneTaskLookup());
        var json = JsonSerializer.Serialize(SessionWithTask("t-done"), opts);

        var back = JsonSerializer.Deserialize<Session>(json, opts);

        back!.TaskId.Should().Be("t-done");
        back.OwnerId.Should().Be("u", "вычисленные поля читаются как неизвестные и игнорируются");
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
