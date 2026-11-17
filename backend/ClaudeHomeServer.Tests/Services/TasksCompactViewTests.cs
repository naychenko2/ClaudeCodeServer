using System.Text.Json.Nodes;
using ClaudeHomeServer.Services.Mcp.Http;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Компактный вид карточки tasks_* (задача 85fbaee8): чистая функция над JSON карточки.
// Мутации (каждая краснит названный тест):
// • убрать из ServiceKeys "reminderSentAt" — ForRead_УбираетСлужебное;
// • убрать вызов Prune — ForRead_УбираетNullИПустое;
// • добавить "awaitingHumanSince" в ServiceKeys — ForRead_СохраняетСигналыКонвейера;
// • убрать "description" из MutationNeverEcho — ForMutation_НеЭхоТекстов;
// • убрать условие sent.Contains у acceptance — ForMutation_ТяжёлыеСписки_ТолькоЕслиПрисланы.
public class TasksCompactViewTests
{
    private const string Full = """
    {
      "id":"85fbaee8-813c-4d9d-9077-809ba3fff182","projectId":"p1","ownerId":"o1","title":"Задача",
      "description":"Длинное описание","status":"inProgress","columnId":null,"priority":"high",
      "dueDate":null,"reminderSentAt":null,"assignee":"claude","createdByUserId":"u1","recurrence":null,
      "linkedSessionId":"s1","personaId":"pr1","executionExpiresAfterMinutes":1440,"claudeStartedAt":"2026-11-17T08:30:54Z",
      "claudeResult":"итог исполнителя","executorStoppedAt":"2026-11-17T09:00:00Z","executorStopReason":"limit",
      "holdResumeDeliveredAt":null,"deviceWaitSince":null,"droppedByHumanAt":"2026-11-17T09:10:00Z",
      "executorNudgedAt":"2026-11-17T09:05:00Z","completionDelivered":false,"resultMarkdown":"Итог работы",
      "linkedFiles":["a.cs"],"subtasks":[{"id":"st1","title":"п1","isDone":false}],"labels":[],
      "featureId":"f1","role":"step","stepRef":"B1","lane":"back","reviewOf":null,"planSteps":[{"stepRef":"B1","title":"t","lane":null}],
      "awaitingHumanSince":"2026-11-17T09:20:00Z","awaitingHumanReason":"выбрать вариант","hold":null,
      "note":{"text":"голос","at":"2026-11-17T09:00:00Z","byUser":false},"workflowProposal":{"routeId":"r1"},
      "routeRequired":true,"stage":{"id":"st","kind":"production"},"delegationDepth":0,"order":16408000,"kind":"task",
      "acceptance":[{"id":"c1","text":"критерий"}],"evidence":[{"criterionId":"c1","how":"тест","result":"зелёный"}],
      "pregate":{"sha":"abc","green":true,"summary":"ок"},"createdAt":"2026-11-17T08:30:51Z","shortId":"85fbaee8",
      "updatedAt":"2026-11-17T08:30:54Z","completedAt":null
    }
    """;

    private static JsonObject Card() => JsonNode.Parse(Full)!.AsObject();

    [Fact]
    public void ForRead_УбираетСлужебное()
    {
        var card = TasksCompactView.ForRead(Card());
        foreach (var key in new[] { "ownerId", "reminderSentAt", "createdByUserId", "executionExpiresAfterMinutes",
                     "claudeStartedAt", "executorNudgedAt", "completionDelivered", "delegationDepth", "order" })
            card.ContainsKey(key).Should().BeFalse($"служебное поле {key} агенту не нужно");
    }

    [Fact]
    public void ForRead_УбираетNullИПустое()
    {
        var card = TasksCompactView.ForRead(Card());
        foreach (var key in new[] { "columnId", "dueDate", "recurrence", "labels", "reviewOf", "hold", "completedAt" })
            card.ContainsKey(key).Should().BeFalse($"пустое поле {key} не едет");
        // Вложенное: null-поле шага плана тоже не едет
        card["planSteps"]![0]!.AsObject().ContainsKey("lane").Should().BeFalse();
    }

    [Fact]
    public void ForRead_СохраняетСигналыКонвейера()
    {
        var card = TasksCompactView.ForRead(Card());
        // Всё, что читают роли конвейера, фронт и оркестратор: ничего не убрано молча
        foreach (var key in new[] { "id", "shortId", "projectId", "title", "description", "status", "priority",
                     "assignee", "personaId", "linkedSessionId", "claudeResult", "resultMarkdown", "linkedFiles",
                     "subtasks", "featureId", "role", "stepRef", "lane", "planSteps", "awaitingHumanSince",
                     "awaitingHumanReason", "note", "workflowProposal", "routeRequired", "stage", "kind",
                     "acceptance", "evidence", "pregate", "executorStoppedAt", "executorStopReason",
                     "droppedByHumanAt", "createdAt", "updatedAt" })
            card.ContainsKey(key).Should().BeTrue($"читаемое поле {key} обязано остаться");
        card["id"]!.GetValue<string>().Should().HaveLength(36, "фронт достаёт guid регуляркой по \"id\"");
    }

    [Fact]
    public void ForMutation_НеЭхоТекстов()
    {
        var card = TasksCompactView.ForMutation(Card(), ["description", "resultMarkdown", "evidence"]);
        foreach (var key in new[] { "description", "resultMarkdown", "claudeResult" })
            card.ContainsKey(key).Should().BeFalse($"{key} вызывающий прислал сам — не повторяем");
        card["status"]!.GetValue<string>().Should().Be("inProgress");
        card["shortId"]!.GetValue<string>().Should().Be("85fbaee8");
        card["awaitingHumanReason"]!.GetValue<string>().Should().Be("выбрать вариант", "сигналы остаются");
    }

    [Fact]
    public void ForMutation_Свидетельства_ТолькоCriterionId_ИТолькоЕслиСвидетельстваИлиКритерииМенялись()
    {
        var sent = TasksCompactView.ForMutation(Card(), ["evidence"]);
        var item = sent["evidence"]!.AsArray().Single()!.AsObject();
        item["criterionId"]!.GetValue<string>().Should().Be("c1");
        item.ContainsKey("how").Should().BeFalse("тексты how/result вызывающий прислал сам");
        item.ContainsKey("result").Should().BeFalse();

        // Смена критериев может сбросить свидетельство — покрытие показываем
        TasksCompactView.ForMutation(Card(), ["acceptance"])["evidence"]!.AsArray().Should().HaveCount(1);
        // Ничего из этого не менялось — свидетельств в ответе нет
        TasksCompactView.ForMutation(Card(), ["status"]).ContainsKey("evidence").Should().BeFalse();
    }

    [Fact]
    public void ForMutation_ТяжёлыеСписки_ТолькоЕслиПрисланы()
    {
        var none = TasksCompactView.ForMutation(Card(), ["status"]);
        foreach (var key in new[] { "acceptance", "planSteps", "subtasks", "linkedFiles" })
            none.ContainsKey(key).Should().BeFalse($"{key} не менялся — не едет");

        var sent = TasksCompactView.ForMutation(Card(), ["acceptance", "subtasks"]);
        sent.ContainsKey("acceptance").Should().BeTrue("серверные id критериев нужны вызывающему");
        sent.ContainsKey("subtasks").Should().BeTrue("id подзадач нужны для toggle");
        sent.ContainsKey("planSteps").Should().BeFalse();
    }

    [Fact]
    public void ForRead_ПустаяКарточка_НеПадает()
    {
        var card = TasksCompactView.ForRead(new JsonObject { ["id"] = "x", ["labels"] = new JsonArray(), ["title"] = "" });
        card.Count.Should().Be(1);
        card["id"]!.GetValue<string>().Should().Be("x");
    }
}
