using ClaudeHomeServer.Services.Llm;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Кулдаун недоступности провайдера (ADR-007 §4.3): TTL по причине, монотонное продление,
// снятие успехом. Проверяем дедлайны НАПРЯМУЮ (UnavailableUntil), а не только IsUnavailable:
// иначе регрессия «схлопнуть QuotaCooldownMinutes обратно в CooldownMinutes» или «выкинуть
// max() из MarkUntil» осталась бы зелёной — IsUnavailable true в обоих случаях, разница в
// минутах невидна. TimeProvider не нужен: истечение — тривиальное сравнение с UtcNow.
public class ProviderHealthRegistryTests
{
    // Ошибка доставки → короткий TTL (CooldownMinutes), исчерпанная квота → длинный (Quota).
    // Допуск ±2 минуты: тест меряет на быстроте, без тайминговых гонок.
    [Fact]
    public void MarkUnavailable_КороткийTtl_MarkQuotaExhausted_ДлинныйTtl()
    {
        var sut = new ProviderHealthRegistry();
        var now = DateTimeOffset.UtcNow;

        sut.MarkUnavailable("p", FallbackErrorClass.Unreachable);
        sut.UnavailableUntil("p").Should().BeCloseTo(
            now.AddMinutes(ProviderHealthRegistry.CooldownMinutes), TimeSpan.FromMinutes(2),
            "ошибка доставки — короткий кулдаун");

        sut.MarkQuotaExhausted("q");
        sut.UnavailableUntil("q").Should().BeCloseTo(
            now.AddMinutes(ProviderHealthRegistry.QuotaCooldownMinutes), TimeSpan.FromMinutes(2),
            "исчерпанная квота — часовой кулдаун, не 5 минут");
    }

    // max(): короткий кулдаун не должен обрезать уже стоящий длинный. Иначе один поздний
    // обрыв связи на уже исчерпанном провайдере сократил бы часовую отметку до 5 минут.
    [Fact]
    public void MarkUnavailable_ПослеИсчерпанияКвоты_НеУкорачиваетОтметку()
    {
        var sut = new ProviderHealthRegistry();
        sut.MarkQuotaExhausted("p");                            // до +60 мин
        var afterQuota = sut.UnavailableUntil("p");

        sut.MarkUnavailable("p", FallbackErrorClass.Unreachable);  // попытка сократить до +5 мин

        sut.UnavailableUntil("p").Should().Be(afterQuota,
            "отметку только продлеваем — короткий кулдаун не режет длинный");
    }

    // Причина кулдауна хранится и отдаётся wire-именем — стартовая подмена показывает точную
    // формулировку («Исчерпан лимит» / «Эндпоинт недоступен»), а не универсальную.
    [Fact]
    public void UnavailableReason_ОтдаётКлассОшибки()
    {
        var sut = new ProviderHealthRegistry();

        sut.MarkQuotaExhausted("p");
        sut.UnavailableReason("p").Should().Be("usage_limit");

        sut.MarkUnavailable("u", FallbackErrorClass.Unreachable);
        sut.UnavailableReason("u").Should().Be("unreachable");

        sut.MarkUnavailable("e", FallbackErrorClass.ProviderError);
        sut.UnavailableReason("e").Should().Be("provider_error");
    }

    // Clear снимает кулдаун (ветка успеха адаптера). Записи нет → null, не выбрасывает.
    [Fact]
    public void Clear_СнимаетКулдаун()
    {
        var sut = new ProviderHealthRegistry();
        sut.MarkQuotaExhausted("p");
        sut.IsUnavailable("p").Should().BeTrue();

        sut.Clear("p");

        sut.IsUnavailable("p").Should().BeFalse("успешный ход снимает кулдаун");
        sut.UnavailableUntil("p").Should().BeNull();
        sut.Clear("отсутствует");   // no-op, не выбрасывает
    }

    [Fact]
    public void IsUnavailable_ПустойКлюч_False()
    {
        var sut = new ProviderHealthRegistry();
        sut.IsUnavailable(null).Should().BeFalse();
        sut.IsUnavailable("").Should().BeFalse();
        sut.IsUnavailable("   ").Should().BeFalse();
    }
}
