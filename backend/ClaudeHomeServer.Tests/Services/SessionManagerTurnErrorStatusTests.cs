using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Характеризационный тест конца хода (S3-6, docs/research/session-manager-split-2026-11.md §4
// «Фолбэк хода»): ядро OnMessageQueuedAsync маппит ErrorMessage без следующего ResultMessage
// (ExpectResultFollows=false) в SessionStatus.Error, а не в Finished; последующий ExitedMessage
// умирающего процесса не должен «доразрешать» ошибочный ход до Finished — переход
// ExitedMessage→Finished работает только из Active (result уже завершила ход), Error терминален.
// Тест написан ДО переноса ядра в TurnCompletion и обязан остаться зелёным после него:
// вызывает вход фасада OnMessageAsync, а не класс роли.
public class SessionManagerTurnErrorStatusTests : SessionManagerTestBase
{
    [Fact]
    public async Task ErrorMessage_БезОжидаемогоResulta_СтатусErrorANeFinished()
    {
        var session = await MkBusySessionAsync("err-status", SessionStatus.Working);
        var entry = GetEntry(session.Id);

        // ExpectResultFollows=false (по умолчанию): ход завершился ошибкой, терминальное
        // сообщение — сам этот ErrorMessage, отдельного Result{subtype:error} за ним нет.
        await InvokeOnMessageAsync(session.Id, GetAccumulator(entry),
            new ErrorMessage("подписка недоступна"), TestRunId);

        _sut.GetById(session.Id)!.Status.Should().Be(SessionStatus.Error);
    }

    [Fact]
    public async Task ExitedMessage_ПослеError_СтатусНеМеняется()
    {
        var session = await MkBusySessionAsync("err-exit", SessionStatus.Working);
        var entry = GetEntry(session.Id);

        await InvokeOnMessageAsync(session.Id, GetAccumulator(entry),
            new ErrorMessage("подписка недоступна"), TestRunId);
        _sut.GetById(session.Id)!.Status.Should().Be(SessionStatus.Error);

        // Процесс умер после ошибки: маппинг ExitedMessage даёт переход только из
        // Working/Waiting/Active; из Error — null, статус остаётся Error.
        await InvokeOnMessageAsync(session.Id, GetAccumulator(entry), new ExitedMessage(), TestRunId);

        _sut.GetById(session.Id)!.Status.Should().Be(SessionStatus.Error);
    }
}
