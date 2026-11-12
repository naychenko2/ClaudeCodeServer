using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Services;

// Сторож регистрации HTTP-клиента "fal" и таймаута опроса биллинга (хвосты ревью R-S-SPEND-4, п.2;
// S-SPEND-TAIL-2): у клиента задан свой таймаут (15 с), а не дефолтный HttpClient 100 с, и опрос
// биллинга получает именно его. Слот троттлинг-гейта FalCostService держится на всё время запроса:
// один зависший опрос биллинга стопорил бы весь бэкфилл цен на 100 с. Длинные вызовы этим не
// ограничены: потребители берут тот же именованный клиент и перезаписывают таймаут на своём
// экземпляре (FalImageService 180 с, FalVideoEngine/FalAudioEngine 120 с) — перезапись живёт на
// экземпляре и не переносится на экземпляры других потребителей. Второй тест проверяет ровно это:
// после перезаписи экземпляра генерации инстанс, который берёт опрос цен, остаётся 15 с.
// Поднимает полный стенд: проверка идёт по реальному DI-графу Program.
[Trait("Category", "Host")]
public class FalHttpClientRegistrationTests : IClassFixture<FalHttpClientRegistrationTests.Hosts>
{
    // Хост на КЛАСС (храповик HostPerClassRatchet): оба теста только читают DI-граф и создают
    // клиентов из фабрики, состояния между тестами не делят
    public sealed class Hosts : TestWebApplicationFactory;

    private readonly Hosts _factory;

    public FalHttpClientRegistrationTests(Hosts factory) => _factory = factory;

    [Fact]
    public void КлиентФалИмеетСвойТаймаут_АНеДефолтные100с() {
        var client = _factory.Services.GetRequiredService<IHttpClientFactory>().CreateClient("fal");

        client.Timeout.Should().Be(TimeSpan.FromSeconds(15),
            "дефолтные 100 с держат слот гейта опроса полтора минуты на зависшем запросе; "
            + "15 с — конвенция проекта для внешних API");
    }

    [Fact]
    public void ПослеПерезаписиТаймаутаГенерации_ИнстансОпросаБиллингаОстаётся15с() {
        var http = _factory.Services.GetRequiredService<IHttpClientFactory>();

        // Повторяем поведение FalImageService.cs:87-88: инстанс длинной генерации
        // перезаписывается на 180 с.
        var gen = http.CreateClient("fal");
        gen.Timeout = TimeSpan.FromSeconds(180);

        // Инстанс опроса биллинга — тот же вызов фабрики, что FetchCostAsync (FalCostService.cs:336).
        var poll = http.CreateClient("fal");
        poll.Timeout.Should().Be(TimeSpan.FromSeconds(15),
            "слот гейта троттлинга держится на всё время запроса: если 180 с генерации легли бы "
            + "на тот же инстанс, зависший опрос биллинга держал бы бэкфилл цен три минуты");
    }
}
