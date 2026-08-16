using System.Reflection;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Llm.Claude;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Инцидент 16.08.2026: реанимация зависшего чата (ReviveStuckSession → DisposeAsync в фоне)
// диспозила _turnLock/_stdinLock/_cts под живым запаркованным ходом — ход ловил
// «Cannot access a disposed object: SemaphoreSlim» в ленту чата (тише — в необработанное
// исключение fire-and-forget). Фикс: DisposeAsync НЕ диспозит примитивы синхронизации —
// SemaphoreSlim без AvailableWaitHandle не держит неуправляемых ресурсов, объект уходит
// мусорщику целиком. Тест фиксирует контракт: после DisposeAsync семафоры остаются рабочими,
// а новое сообщение тихо отменяется по CTS, не падая ObjectDisposedException.
public class ClaudeSessionDisposeRaceTests
{
    private static readonly FieldInfo TurnLockField =
        typeof(ClaudeSession).GetField("_turnLock", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly FieldInfo StdinLockField =
        typeof(ClaudeSession).GetField("_stdinLock", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static (ClaudeSession Session, List<ServerMessage> Sent) NewClaudeSession()
    {
        var sent = new List<ServerMessage>();
        var context = new LlmSessionContext(
            RootPath: Path.GetTempPath(),
            OnMessage: msg => { lock (sent) sent.Add(msg); return Task.CompletedTask; },
            RawSystemPrompt: null,
            PermissionRules: null,
            TasksMcp: null);
        return (new ClaudeSession(new Session(), context), sent);
    }

    // Ядро фикса: семафоры переживают DisposeAsync. Wait(0) на диспознутом SemaphoreSlim
    // бросает ObjectDisposedException — на живом возвращает true (семафор свободен).
    [Fact]
    public async Task DisposeAsync_НеДиспозитСемафоры()
    {
        var (session, _) = NewClaudeSession();
        await session.DisposeAsync();

        var turnLock = (SemaphoreSlim)TurnLockField.GetValue(session)!;
        var stdinLock = (SemaphoreSlim)StdinLockField.GetValue(session)!;

        var act = () =>
        {
            turnLock.Wait(0).Should().BeTrue("свободный семафор после DisposeAsync обязан выдаваться");
            turnLock.Release();
            stdinLock.Wait(0).Should().BeTrue("свободный семафор после DisposeAsync обязан выдаваться");
            stdinLock.Release();
        };
        act.Should().NotThrow("dispose под живыми ожидателями — гонка by design; до фикса здесь был ObjectDisposedException в ленту чата");
    }

    // Поведение после dispose: новое сообщение тихо отменяется (CTS уже отменён), без
    // исключений наружу и без ErrorMessage «disposed» в ленту.
    [Fact]
    public async Task СообщениеПослеDispose_ТихоОтменяется()
    {
        var (session, sent) = NewClaudeSession();
        await session.DisposeAsync();

        var act = () => session.SendMessageAsync("сообщение в мёртвую сессию");
        await act.Should().NotThrowAsync("гонка с dispose — штатная отмена хода, а не исключение");

        // Ход отменяется по CTS до взятия _turnLock: ни ошибки, ни запуска процесса
        await Task.Delay(300);
        lock (sent) sent.Should().BeEmpty("отменённый ход не должен ронять ErrorMessage в ленту");
    }
}
