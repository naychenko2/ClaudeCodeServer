using ClaudeHomeServer.Models;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

// Гейт план-фазы штаба (TeamStateService.EnterPlanPhaseMode): решение о возможностях модели
// принимает реестр провайдеров, ядро сессий по ним не деградирует (ADR-014 §5 — llm
// неотключаема, своей запасной таблицы возможностей в TeamStateService больше нет: был
// `?? LlmCapabilitiesCatalog.Claude`, теперь только реестр).
//
// Чего этим тестом сказать нельзя: «у провайдера без «План» режим плана не включается».
// Такого провайдера реестр сейчас не умеет — CapabilitiesOf строит возможности из
// LlmCapabilitiesCatalog.Claude и SupportsPlanMode нигде не переопределяет
// (LlmProviderRegistry.cs:448), то есть отказ план-режима возможен только если сам реестр
// скажет «нет». Проверяемая половина инварианта ниже: ни своя модель, ни модель вне
// реестра план-режим не выключают.
//
// Мутация бьёт в производителя (TeamStateService.SupportsPlanMode), а не в тест:
// CapabilitiesFor(model) → CapabilitiesOf(ResolveByModel(model)!) — убрать фолбэк самого
// реестра (LlmProviderRegistry.cs:443). Для нерезолвленной модели это NullReferenceException,
// тест краснеет: путь «провайдера нет → возможности родного Claude» идёт через реестр,
// а не через запасной вариант в вертикали.
public class SessionManagerTeamPlanPhaseGateTests : SessionManagerTestBase
{
    [Theory]
    [InlineData(null)]                            // модель не выбрана
    [InlineData("glm-5.2")]                      // провайдер из конфига тест-базы (LlmProviders:glm)
    [InlineData("model-unknown-to-registry")]    // модели нет ни у одного провайдера
    public async Task ПланФаза_НиОднаМодельПланНеВыключает_РешаетРеестр(string? model)
    {
        var (session, _, _) = await MakeTeamStabAsync("ti-plangate-" + (model ?? "none"));
        _sut.GetById(session.Id)!.Model = model;

        _sut.EnterPlanPhaseMode(session.Id);

        var after = _sut.GetById(session.Id)!;
        after.Mode.Should().Be(ClaudeMode.Plan,
            "возможности модели отдаёт реестр: своей модели — провайдера, чужой — его фолбэк "
            + "родного Claude (LlmProviderRegistry.cs:443); вертикаль поверх этого ничего не решает");
        after.TeamImplement!.SavedMode.Should().Be(ClaudeMode.Auto,
            "навязывание плана не затирает выбор человека — он вернётся после согласования");
    }
}
