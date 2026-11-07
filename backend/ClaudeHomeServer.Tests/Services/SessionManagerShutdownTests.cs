using System.Reflection;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Llm;
using FluentAssertions;
using Moq;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Характеризация окна штатной остановки (S3-7, ДО переноса Stopping в ядро): флаг ставится
/// «Стопом» до гашения адаптеров; автоматические пути (drain) под флагом закрыты, очередь
/// остаётся в памяти; флаг не снимается ни drain'ом, ни приёмом пользовательского сообщения.
/// Тесты зовут метод-владелец и читают поле-флаг через резолвер владельца (фасад или его
/// роль-ядро), поэтому остаются зелёными после переноса: проверяют поведение, а не место.
/// </summary>
public class SessionManagerShutdownTests : SessionManagerTestBase
{
    [Fact]
    public async Task StopWindow_СтопПередDrain_ОчередьОстаётсяХодНеСтартует()
    {
        // Контроль внутри того же теста: без «Стопа» тот же drain доставляет сообщение
        // (модится ровно один раз) — иначе проверка «после Стопа молчит» была бы вакуумной.
        var session = await MkBusySessionAsync("stop-drain", SessionStatus.Active);
        session.Name = "есть имя";
        var entry = GetEntry(session.Id);
        var adapter = StubAdapter(entry);
        SetProcess(entry, adapter.Object);

        EnqueueUser(entry, "первое сообщение");
        await InvokeOwnerAsync("DrainNextPendingAsync", session.Id);
        _sut.GetPending(session.Id).Should().BeEmpty("контроль: без Стопа drain доставляет");
        adapter.Verify(a => a.SendMessageAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(),
            It.IsAny<int>(), It.IsAny<bool>()), Times.Once());

        EnqueueUser(entry, "отложенное сообщение");
        _sut.KillAllProcesses();
        await InvokeOwnerAsync("DrainNextPendingAsync", session.Id);

        _sut.GetPending(session.Id).Should().ContainSingle()
            .Which.Text.Should().Be("отложенное сообщение");
        adapter.Verify(a => a.SendMessageAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(),
            It.IsAny<int>(), It.IsAny<bool>()), Times.Once()); // нового хода нет
    }

    [Fact]
    public async Task StopWindow_ФлагПоднятСтопом_ИНеСнимаетсяНиDrainomНиПриёмомЧеловека()
    {
        // Текущая семантика Stopping (фиксируется ДО переноса в ядро): писатель один —
        // KillAllProcesses; ни drain, ни пользовательский приём флаг не снимают.
        var session = await MkBusySessionAsync("stop-flag", SessionStatus.Active);
        session.Name = "есть имя";
        var entry = GetEntry(session.Id);
        var adapter = StubAdapter(entry);
        SetProcess(entry, adapter.Object);

        StoppingFlag().Should().BeFalse();
        _sut.KillAllProcesses();
        StoppingFlag().Should().BeTrue();

        var queued = await _sut.SendOrEnqueueAsync(session.Id, "сообщение человека");
        queued.Should().BeFalse("флаг не гейтит пользовательский приём: свободный чат принимает ход");
        adapter.Verify(a => a.SendMessageAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(),
            It.IsAny<int>(), It.IsAny<bool>()), Times.Once());
        StoppingFlag().Should().BeTrue("флаг не снимается приёмом: в коде нет писателя, который бы его сбрасывал");
    }

    // Резолвер владельца: метод может жить в фасаде или в роли ядра (перенос идёт) —
    // ищется в обоих, с любой областью видимости (роль реализует шов публично).
    private IEnumerable<object> Owners()
    {
        yield return _sut;
        foreach (var f in typeof(SessionManager).GetFields(BindingFlags.NonPublic | BindingFlags.Instance))
        {
            if (f.GetValue(_sut) is not { } role) continue;
            var ns = role.GetType().Namespace;
            if (ns is not null && ns.StartsWith("ClaudeHomeServer.Services.Sessions"))
                yield return role;
        }
    }

    private async Task InvokeOwnerAsync(string name, params object?[] args)
    {
        foreach (var owner in Owners())
        {
            var m = owner.GetType().GetMethod(name,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (m is null) continue;
            await (Task)m.Invoke(owner, args)!;
            return;
        }
        throw new MissingMethodException(typeof(SessionManager).FullName, name);
    }

    private bool StoppingFlag()
    {
        foreach (var owner in Owners())
        {
            var f = owner.GetType().GetField("Stopping",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (f is not null) return (bool)f.GetValue(owner)!;
        }
        throw new MissingFieldException(typeof(SessionManager).FullName, "Stopping");
    }
}
