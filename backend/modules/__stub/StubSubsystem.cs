using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ClaudeHomeServer.Services.Composition;

namespace StubModule;

// Заглушка динамического модуля (PoC сценария Б): минимальная IAppSubsystem с одним
// публичным контроллером. Она не ProjectReference-зависимость Main — ядро находит её
// по пути из конфига (ModuleLoader) и зовёт Register. Три факта, которые это доказывает:
//   1) сборка загружена по пути (AssemblyLoadContext.LoadFromAssemblyPath);
//   2) загрузчик нашёл IAppSubsystem и вызвал Register(IServiceCollection, IConfiguration)
//      (тест резолвит саму подсистему по Key="__stub");
//   3) MVC подключил ApplicationPart модуля — контроллер отвечает (GET /api/stub-module/ping).
public sealed class StubSubsystem : IAppSubsystem
{
    public string Key => "__stub";
    public string Title => "Test Stub Module";

    public void Register(IServiceCollection services, IConfiguration config)
    {
        // Подсистему регистрируем под интерфейсом — так её видит и UseSubsystems,
        // и тест, который проверяет, что Register реально вызван (иначе в DI её бы не было).
        services.AddSingleton<IAppSubsystem>(this);
    }
}
