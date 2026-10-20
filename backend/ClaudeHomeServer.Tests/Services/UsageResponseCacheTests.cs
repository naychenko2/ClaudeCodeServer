using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Отпечаток кэша /api/usage (Перф-3 B10, п.5): блок providerSnaps лёгкого ответа зависит
// от состава LLM-провайдеров. Смена состава (админ добавил/убрал/переименовал провайдера)
// меняет отпечаток → залипший лёгкий ответ не переживает смену.
public class UsageResponseCacheTests
{
    [Fact]
    public void ProviderFingerprint_ПустойСостав_Ноль()
    {
        UsageResponseCache.ProviderFingerprint(null).Should().Be(0);
        UsageResponseCache.ProviderFingerprint(Array.Empty<LlmProviderConfig>()).Should().Be(0);
    }

    [Fact]
    public void ProviderFingerprint_СменаСостава_МеняетОтпечаток()
    {
        var a = new LlmProviderConfig { Key = "deepseek" };
        var b = new LlmProviderConfig { Key = "glm" };

        var withA = UsageResponseCache.ProviderFingerprint(new[] { a });
        var withAB = UsageResponseCache.ProviderFingerprint(new[] { a, b });
        var withBA = UsageResponseCache.ProviderFingerprint(new[] { b, a });
        var withOther = UsageResponseCache.ProviderFingerprint(new[] { new LlmProviderConfig { Key = "other" } });

        withA.Should().NotBe(0);
        // Добавление провайдера меняет отпечаток
        withA.Should().NotBe(withAB);
        // Порядок ключей не важен (сортировка)
        withAB.Should().Be(withBA);
        // Переименование (другой ключ) тоже меняет отпечаток
        withA.Should().NotBe(withOther);
    }

    // Fingerprint-запись: два отпечатка с разным составом провайдеров неравны → кэш промах
    [Fact]
    public void Fingerprint_РазныйСостав_КэшПромах()
    {
        var cache = new UsageResponseCache();
        var response = new UsageResponse([], null, null, null, null, null, null);
        var fpA = new UsageResponseCache.Fingerprint(1, 1, 1, 1,
            UsageResponseCache.ProviderFingerprint(new[] { new LlmProviderConfig { Key = "deepseek" } }));
        var fpB = new UsageResponseCache.Fingerprint(1, 1, 1, 1,
            UsageResponseCache.ProviderFingerprint(new[] { new LlmProviderConfig { Key = "glm" } }));

        cache.Set("limits", admin: true, fpA, response);
        cache.TryGet("limits", admin: true, fpA, out _).Should().BeTrue("тот же состав — попадание");
        cache.TryGet("limits", admin: true, fpB, out _).Should().BeFalse("состав провайдеров сменился — промах");
    }
}
