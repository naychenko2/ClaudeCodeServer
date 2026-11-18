using ClaudeHomeServer.Services.Llm.Gateway;
using ClaudeHomeServer.Tests.Helpers;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Services;

// КИ1a: ILocalPageOcr резолвится в ТОТ ЖЕ экземпляр, что LocalOcrService.
// Второй экземпляр = второй семафор = два слота GPU: две страницы шли бы в vLLM
// одновременно, и семафор сервиса это не держал бы.
// Хост — на КЛАСС (храповик, тест. «Г3»): поднимается один раз в фикстуре Hosts,
// живёт до конца класса.
[Trait("Category", "Host")]
public class LocalPageOcrDiTests : IClassFixture<LocalPageOcrDiTests.Hosts>
{
    public sealed class Hosts : TestWebApplicationFactory { }

    private readonly Hosts _hosts;

    public LocalPageOcrDiTests(Hosts hosts) => _hosts = hosts;

    [Fact]
    public void InterfaceResolvesToTheSameInstanceAsTheService()
    {
        var service = _hosts.Services.GetRequiredService<LocalOcrService>();
        Assert.Same(service, _hosts.Services.GetRequiredService<ILocalPageOcr>());
    }
}
