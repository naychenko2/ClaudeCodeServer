using System.Collections;
using System.Reflection;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Tests.Helpers;
using ClaudeHomeServer.Tests.ImageEditor.Characters;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Services;

// Дефект волны 2 (ADR-019): запись модуля в ленту свежего чата, где агент ещё ни разу не
// ответил и ClaudeSessionId нет, не ложилась на диск — после перезагрузки карточки нитей
// пропадали. История до первого ответа ключуется id чата, а первый ответ переносит её под
// ClaudeSessionId; запись не должна теряться ни на одном из этих шагов.
public class ModuleRecordFreshChatTests : IDisposable
{
    private readonly TestWebApplicationFactory _factory = new();
    private readonly string _projectId;

    public ModuleRecordFreshChatTests()
    {
        (_projectId, _) = CharacterEndpointsTests.CreateProject(_factory, TestWebApplicationFactory.TestUsername);
    }

    public void Dispose()
    {
        _factory.Dispose();
        GC.SuppressFinalize(this);
    }

    private SessionManager Sessions => _factory.Services.GetRequiredService<SessionManager>();
    private ChatHistoryService History => _factory.Services.GetRequiredService<ChatHistoryService>();
    private IChatFeed Feed => _factory.Services.GetRequiredService<IChatFeed>();

    private static StoredModuleRecord Record(string threadId) => new()
    {
        Module = "imageeditor",
        RecordType = "image_thread",
        Data = JsonSerializer.SerializeToElement(new { threadId, stackId = "s1" }),
        Fallback = "Картинка: hero.png",
    };

    private static IEnumerable<string?> ThreadIds(IEnumerable<StoredMessage> history) =>
        history.OfType<StoredModuleRecord>().Select(r => r.Data?.GetProperty("threadId").GetString());

    // Живой аккумулятор чата — приватное состояние ядра; рестарт сервера его обнуляет
    private object Entry(string sessionId)
    {
        var field = typeof(SessionManager).GetField("_sessions", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return ((IDictionary)field.GetValue(Sessions)!)[sessionId]!;
    }

    private void DropAccumulator(string sessionId) =>
        Entry(sessionId).GetType().GetField("Accumulator")!.SetValue(Entry(sessionId), null);

    private TurnAccumulator? Accumulator(string sessionId) =>
        (TurnAccumulator?)Entry(sessionId).GetType().GetField("Accumulator")!.GetValue(Entry(sessionId));

    private async Task EnsureAccumulatorAsync(string sessionId)
    {
        var method = typeof(SessionManager).GetMethod("EnsureAccumulatorAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)method.Invoke(Sessions, [Entry(sessionId)])!;
    }

    private async Task OnMessageAsync(string sessionId, TurnAccumulator acc, ServerMessage msg)
    {
        var method = typeof(SessionManager).GetMethod("OnMessageAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)method.Invoke(Sessions, [sessionId, acc, msg, 0L])!;
    }

    [Fact]
    public async Task Запись_в_свежий_чат_ложится_на_диск_читается_после_рестарта_и_переживает_первый_ответ()
    {
        var chat = await Sessions.CreateAsync(_projectId, ClaudeMode.AcceptEdits, name: "Свежий чат");
        chat.ClaudeSessionId.Should().BeNull("агент ещё не отвечал");

        // 1. Живой аккумулятор свежего чата: запись обязана лечь на диск, а не только в память
        (await Feed.AppendRecordAsync(chat.Id, Record("t1"))).Should().BeTrue();
        ThreadIds(await History.LoadAsync(chat.Id)).Should().Equal(["t1"], "до первого ответа история ключуется id чата");

        // 2. Рестарт: аккумулятора нет — чтение идёт с диска, а запись — прямой дисковой веткой
        DropAccumulator(chat.Id);
        ThreadIds(await Sessions.GetHistoryAsync(chat.Id)).Should().Equal(["t1"], "после перезагрузки карточка на месте");
        (await Feed.AppendRecordAsync(chat.Id, Record("t2"))).Should().BeTrue();
        ThreadIds(await Sessions.GetHistoryAsync(chat.Id)).Should().Equal(["t1", "t2"]);

        // 3. Первый ответ агента: ход поднимает историю по id чата, CLI выдаёт свой session_id,
        // и дальше история пишется под ним — записи до ответа никуда не деваются
        await EnsureAccumulatorAsync(chat.Id);
        var acc = Accumulator(chat.Id)!;
        Sessions.GetById(chat.Id)!.ClaudeSessionId = "csid-first-answer";
        await OnMessageAsync(chat.Id, acc, new SessionStartedMessage("csid-first-answer", false, "claude", "acceptedits"));
        (await Feed.AppendRecordAsync(chat.Id, Record("t3"))).Should().BeTrue();

        ThreadIds(await History.LoadAsync("csid-first-answer")).Should().Equal(["t1", "t2", "t3"]);
        DropAccumulator(chat.Id);
        ThreadIds(await Sessions.GetHistoryAsync(chat.Id)).Should().Equal(["t1", "t2", "t3"],
            "после первого ответа и рестарта лента читается по ClaudeSessionId целиком");
    }
}
