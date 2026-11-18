using ClaudeHomeServer.Services.Llm.Gateway;
using ClaudeHomeServer.Tests.Helpers;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Services;

// КИ1a: ILocalPageOcr резолвится в ТОТ ЖЕ экземпляр, что LocalOcrService.
// Второй экземпляр = второй семафор = два слота GPU: две страницы шли бы в vLLM
// одновременно, и семафор сервиса это не держал бы.
[Trait("Category", "Host")]
public class LocalPageOcrDiTests
{
    [Fact]
    public void InterfaceResolvesToTheSameInstanceAsTheService()
    {
        using var factory = new TestWebApplicationFactory();
        var service = factory.Services.GetRequiredService<LocalOcrService>();
        Assert.Same(service, factory.Services.GetRequiredService<ILocalPageOcr>());
    }
}
