using System.Reflection;
using System.Runtime.CompilerServices;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Composition;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services.Composition;

// Сторож контракта ISessionMessageObserver.Attach/Detach: без кэширования обёртки
// делегата `s => handler(s.ProjectId, m)` Detach не находит свой подписчик в мультикасте
// (сравнение идёт по Target+Method, у двух разных лямбд Target разный), и обработчик
// остаётся подписанным навсегда — N-кратный синк Dify на одно событие при первом же
// динамическом потребителе. Регрессия зафиксирована в задаче b6ffa6a5.
public class SessionMessageObserverTests
{
    // Эвент SessionManager.OnSessionMessage нельзя Invoke-нуть извне по правилам C#,
    // и публичного Fire-метода в самом SessionManager нет (он жжётся только из
    // BroadcastAsync внутри). Достаём backing-поле рефлексией и дёргаем напрямую —
    // на контракт Attach/Detach это не влияет, подписчик видит тот же самый делегат.
    private static void FireOnSessionMessage(SessionManager sessions, Session s, ServerMessage m)
    {
        var field = typeof(SessionManager).GetField(nameof(SessionManager.OnSessionMessage),
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        field.Should().NotBeNull("backing field авто-event хранится под тем же именем");
        var del = (Func<Session, ServerMessage, Task>?)field!.GetValue(sessions);
        if (del is null) return;
        foreach (var d in del.GetInvocationList().Cast<Func<Session, ServerMessage, Task>>())
        {
            var task = d(s, m);
            if (!task.IsCompleted) task.GetAwaiter().GetResult();
        }
    }

    // Конструктор SessionManager тянет реальные зависимости (ArchivedTranscriptStore,
    // FalCostService, PersonaManager и т.д.) — для проверки контракта Attach/Detach всё
    // это лишнее, нужен только сам event. RuntimeHelpers.GetUninitializedObject даёт
    // «голый» экземпляр без вызова конструктора: backing field OnSessionMessage = null,
    // add/remove аксессоры на авто-event работают штатно, и наблюдатель не отличает
    // такой объект от боевого.
    private static SessionManager CreateSessionManager() =>
        (SessionManager)RuntimeHelpers.GetUninitializedObject(typeof(SessionManager));

    private static Session MakeSession(string projectId) => new()
    {
        Id = Guid.NewGuid().ToString(),
        ProjectId = projectId,
        Status = SessionStatus.Active,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    [Fact]
    public void Detach_ПослеAttach_СнимаетОбработчик()
    {
        // Без кэша обёртки (старая реализация) Detach не находит делегат в мультикасте —
        // этот тест на ней падает. С новой — зелёный.
        var sessions = CreateSessionManager();
        var observer = new SessionMessageObserver(sessions);

        var calls = 0;
        Func<Session, ServerMessage, Task> handler = (_, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.CompletedTask;
        };
        observer.Attach(handler);
        observer.Detach(handler);

        var session = MakeSession("proj-1");
        var msg = new TextDeltaMessage("hello");
        FireOnSessionMessage(sessions, session, msg);

        calls.Should().Be(0, "Detach обязан снять обработчик — иначе он остаётся в мультикасте и зовётся на каждом событии");
    }

    [Fact]
    public void Attach_БезDetach_ОбработчикПолучаетСобытие()
    {
        // Положительный контроль: после Attach (без Detach) обработчик вызывается, и
        // ProjectId пробрасывается из Session в handler.
        var sessions = CreateSessionManager();
        var observer = new SessionMessageObserver(sessions);

        string? receivedProjectId = null;
        Func<Session, ServerMessage, Task> handler = (session, _) =>
        {
            receivedProjectId = session.ProjectId;
            return Task.CompletedTask;
        };
        observer.Attach(handler);

        var session = MakeSession("proj-42");
        var msg = new TextDeltaMessage("hi");
        FireOnSessionMessage(sessions, session, msg);

        receivedProjectId.Should().Be("proj-42");
    }

    [Fact]
    public void Detach_НесуществующегоОбработчика_ТихийNoOp()
    {
        // Контракт ISessionMessageObserver: «Если handler не зарегистрирован — тихий no-op»
        // (см. ISessionMessageObserver.cs:16). Повторный Detach и Detach несуществующего —
        // не исключение и не эффект.
        var sessions = CreateSessionManager();
        var observer = new SessionMessageObserver(sessions);

        var act = () => observer.Detach((_, _) => Task.CompletedTask);
        act.Should().NotThrow();
    }
}
