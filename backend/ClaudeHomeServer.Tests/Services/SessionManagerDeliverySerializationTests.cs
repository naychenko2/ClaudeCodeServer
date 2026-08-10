using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Llm;
using FluentAssertions;
using Moq;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

// Ф1 «сериализация отложенной доставки на смерти» (инцидент 2026-08-10, контрольная регрессия):
// старт нового хода ожидает подтверждённой смерти прогона предыдущего, иначе гоняется с ним
// (same-process reuse Ч1, лок миграции Ч2, interrupt свежего прогона). Ядро решения — признак
// «адаптер ещё занят previous-ходом» (SessionManager.Busy): жив прогон CLI (HasLiveTurn) ИЛИ
// активна фолбэк-оркестрация (OrchestrationActive, между SettleAsync и finally). Здесь покрыты
// все ветки чистой функции; само ожидание + Interrupt по потолку — асинхронная обвязка вокруг неё.
public class SessionManagerDeliverySerializationTests
{
    private static ILlmSessionAdapter Fake(bool hasLiveTurn, bool orchestrationActive)
    {
        var a = new Mock<ILlmSessionAdapter>();
        a.SetupGet(x => x.HasLiveTurn).Returns(hasLiveTurn);
        a.SetupGet(x => x.OrchestrationActive).Returns(orchestrationActive);
        return a.Object;
    }

    [Fact]
    public void Busy_ЖивойПрогон_True() =>
        SessionManager.Busy(Fake(hasLiveTurn: true, orchestrationActive: false))
            .Should().BeTrue("живой прогон держит _turnLock и транзрипт — старт нового хода гоняется с ним");

    [Fact]
    public void Busy_АктивнаяОркестрация_True() =>
        // Прогон мог уже умер (HasLiveTurn=false), но фолбэк-адаптер ещё в окне между SettleAsync
        // и finally: _turn не сброшен, второй ход уйдёт в EnqueueBypass-цикл — тоже «занято».
        SessionManager.Busy(Fake(hasLiveTurn: false, orchestrationActive: true))
            .Should().BeTrue("оркестрация не снята — гонка на окне Settle→finally");

    [Fact]
    public void Busy_Свободен_False() =>
        // И процесс мёртв, И оркестрация снята → _turnLock свободен, транзрипт закрыт → стартовать.
        SessionManager.Busy(Fake(hasLiveTurn: false, orchestrationActive: false))
            .Should().BeFalse("предыдущий ход полностью завершён — можно стартовать новый");

    [Fact]
    public void Busy_Null_False() =>
        // Адаптера нет (сессия без хода) — не занято.
        SessionManager.Busy(null).Should().BeFalse();
}
