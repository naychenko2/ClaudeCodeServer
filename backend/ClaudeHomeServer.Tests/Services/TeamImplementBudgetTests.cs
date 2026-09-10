using ClaudeHomeServer.Models;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Фикс-волна 4 team-blocker-honest (M1): дефект c156193b — формула Max + max(0, plan - left)
// жила в двух местах (TeamStateService.FillAfterBudgetAsync и TeamDecisionService) и
// тихо разъезжалась. Свели в один метод TeamImplementBudget.PlanShortfall. Этот набор
// покрывает её бэкенд-тестом с ненулевым расходом: фронтовые кейсы кормятся руками
// написанными пропсами и эту формулу не сторожат.
public class TeamImplementBudgetTests
{
    [Fact]
    public void PlanShortfall_НулевойРасходВозвращаетРазницуПланМинусОстаток()
    {
        // Свежий бюджет, WavesUsed=0, план шире потолка — дельта строго равна дефициту.
        // Это и есть «план сверх остатка», который раньше в TeamDecisionService считали
        // от plan напрямую, а в TeamStateService — от remainingWaves (Math.Max(0,
        // MaxWaves - WavesUsed)); на ненулевом расходе числа разъезжались.
        TeamImplementBudget.PlanShortfall(max: 2, used: 0, plan: 5)
            .Should().Be(3, "план шире потолка на 3 — дельта = 3");
    }

    [Fact]
    public void PlanShortfall_НенулевойРасходУчитывается()
    {
        // Главный сценарий дефекта c156193b: MaxWaves=2, WavesUsed=1, план на 2 волны.
        // До фикса TeamStateService считала MaxWavesAfter = 2 + max(0, 2 - 1) = 3, а
        // TeamDecisionService — deltaWaves = max(0, 2 - (2-1)) = 1, потолки не сдвигались
        // (Глеб: «MaxWaves=2, WavesUsed=1, план на 2 волны — потолки не сдвинулись, после
        // волны 1 — карточка»). Единая формула должна дать 1, и плашка, и расширение.
        TeamImplementBudget.PlanShortfall(max: 2, used: 1, plan: 2)
            .Should().Be(1, "остаток 1, план 2 — дефицит 1 (находка дефекта c156193b)");
    }

    [Fact]
    public void PlanShortfall_ПланУжеУкладываетсяВозвращаетНоль()
    {
        // План короче остатка — расширять нечего. Защита от обратного хода (плашка бы
        // показала отрицательную дельту без max(0, …)).
        TeamImplementBudget.PlanShortfall(max: 10, used: 5, plan: 3)
            .Should().Be(0, "план уже укладывается в остаток 5 — расширение 0");
    }

    [Fact]
    public void PlanShortfall_ОстатокРовноРавенПлануВозвращаетНоль()
    {
        // Граница: остаток и план совпали — добавлять нечего, но и не уменьшать.
        TeamImplementBudget.PlanShortfall(max: 4, used: 1, plan: 3)
            .Should().Be(0, "остаток 3 = план 3 — дельта 0");
    }

    [Fact]
    public void PlanShortfall_ОтрицательныйОстатокЗащищаетсяНижнимНулем()
    {
        // Легаси-сессия без реинициализации счётчиков: used > max (счётчик «израсходовано»
        // мог перегнать потолок при ручных правках). Без max(0, max - used) ушла бы
        // отрицательная дельта, а Max += delta уехала бы в минус и тихо сломала
        // арифметику.
        TeamImplementBudget.PlanShortfall(max: 2, used: 5, plan: 10)
            .Should().Be(10, "остаток защищён нулём, дельта = весь план");
    }

    [Fact]
    public void PlanShortfall_НулевойПланВозвращаетНоль()
    {
        // Плана нет (плашка бюджета скрывается, потолки не двигаются). Защита от
        // отрицательной дельты при пустом плане.
        TeamImplementBudget.PlanShortfall(max: 4, used: 0, plan: 0)
            .Should().Be(0);
        TeamImplementBudget.PlanShortfall(max: 4, used: 2, plan: 0)
            .Should().Be(0);
    }
}
