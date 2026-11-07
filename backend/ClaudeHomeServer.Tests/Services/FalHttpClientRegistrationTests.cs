using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Services;

// Сторож регистрации HTTP-клиента "fal" (хвосты ревью R-S-SPEND-4, п.2): у клиента задан
// свой таймаут (15 с), а не дефолтный HttpClient 100 с. Слот троттлинг-гейта FalCostService
// держится на всё время запроса: один зависший опрос биллинга стопорил бы весь бэкфилл
// цен на 100 с. Длинные вызовы этим не ограничены: FalImageService/FalVideoEngine/
// FalAudioEngine ставят свой больший таймаут на экземпляре клиента до первого запроса
// (180 с / 120 с) — регистрационное значение закрывает коротких потребителей (опрос
// цен, аккаунт). Поднимает полный стенд: проверка идёт по реальному DI-графу Program.
[Trait("Category", "Host")]
public class FalHttpClientRegistrationTests
{
    [Fact]
    public void КлиентФалИмеетСвойТаймаут_АНеДефолтные100с() {
        using var factory = new TestWebApplicationFactory();
        var client = factory.Services.GetRequiredService<IHttpClientFactory>().CreateClient("fal");

        client.Timeout.Should().Be(TimeSpan.FromSeconds(15),
            "дефолтные 100 с держат слот гейта опроса полтора минуты на зависшем запросе; "
            + "15 с — конвенция проекта для внешних API");
    }
}
