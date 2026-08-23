using System.Reflection;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Llm.Claude;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Инвариант: встроенные Task-инструменты CLI закрываются ПО РОЛИ СЕССИИ, а не оптом.
///
/// ЧТЕНИЕ трекера claude.ai (TaskGet/TaskList) закрыто всегда, пока подключён наш
/// tasks-server: там пусто, и модель уходила за задачей туда вместо mcp__tasks__*.
/// ПЛАН ХОДА (TaskCreate/TaskUpdate — преемник TodoWrite, которого CLI 2.1.226 уже не
/// отдаёт) закрыт только у сессий-исполнителей задач: там на руках задача продуктового
/// трекера, и «закрытие» её через TaskUpdate вместо tasks_complete оставило бы её висеть
/// в inProgress. В обычном чате план-инструменты нужны — по ним модель ведёт многошаговую
/// работу, а фронт рисует карточку чек-листа (computeTodos).
///
/// Запрет оптом (как было до 2026-08-20) означал, что todo-инструментов в сессиях продукта
/// нет ВООБЩЕ: TodoWrite CLI не отдаёт, а Task-набор глушили мы сами.
///
/// Тест сторожевой: читает фактический список запретов, который уедет в --disallowedTools.
/// </summary>
public class BuiltInTaskToolsPolicyTests
{
    private static readonly string[] ReadTools = ["TaskGet", "TaskList"];
    private static readonly string[] PlanTools = ["TaskCreate", "TaskUpdate"];

    // Фактический состав запретов сессии (поле считается один раз в конструкторе и уходит
    // в аргументы запуска CLI — см. BuildArgs)
    private static string[] DisallowedOf(Session info, bool withTasksMcp)
    {
        var context = new LlmSessionContext(
            RootPath: Path.GetTempPath(),
            OnMessage: _ => Task.CompletedTask,
            RawSystemPrompt: null,
            PermissionRules: null,
            TasksMcp: withTasksMcp
                ? new TasksMcpContext("http://localhost:5000", "token", null)
                : null);
        var session = new ClaudeSession(info, context);
        var field = typeof(ClaudeSession).GetField("_disallowedTools",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (string[])field.GetValue(session)!;
    }

    [Fact]
    public void ОбычныйЧат_ЧтениеТрекераЗакрыто_АПланХодаДоступен()
    {
        var disallowed = DisallowedOf(new Session(), withTasksMcp: true);

        disallowed.Should().Contain(ReadTools,
            "пустой встроенный трекер claude.ai уводит модель от mcp__tasks__*");
        disallowed.Should().NotContain(PlanTools,
            "без план-инструментов у модели в обычном чате не остаётся НИ ОДНОГО todo-механизма");
    }

    [Fact]
    public void СессияИсполнителяЗадачи_ЗакрытыВсеЧетыре()
    {
        var disallowed = DisallowedOf(new Session { TaskExecution = true }, withTasksMcp: true);

        disallowed.Should().Contain(ReadTools).And.Contain(PlanTools,
            "у исполнителя на руках задача трекера: TaskUpdate вместо tasks_complete оставил бы её в inProgress");
    }

    [Fact]
    public void БезTasksMcp_ВстроенныеTaskИнструментыНеТрогаем()
    {
        var disallowed = DisallowedOf(new Session(), withTasksMcp: false);

        disallowed.Should().NotContain(ReadTools).And.NotContain(PlanTools,
            "запрещать нечего: своего трекера в сессии нет, подменять встроенный нечем");
    }

    [Fact]
    public void ТулЗапускаСубагента_НеПопадаетВЗапретНиВОдномРежиме()
    {
        // «Task» без суффикса — делегирование субагенту, а не трекер. Его блокировка
        // выключила бы субагентов целиком.
        foreach (var info in new[] { new Session(), new Session { TaskExecution = true } })
        foreach (var withMcp in new[] { true, false })
            DisallowedOf(info, withMcp).Should().NotContain("Task");
    }
}
