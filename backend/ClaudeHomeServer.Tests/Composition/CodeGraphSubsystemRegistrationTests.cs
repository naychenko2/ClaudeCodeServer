using System.Collections.Concurrent;
using System.Reflection;
using ClaudeHomeServer.Services.CodeGraph;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Composition;

// Сторож подсистемы «CodeGraph» (волна 2): проверяет, что CodeGraphSubsystem.Register
// подключает ВСЕ типы, которые раньше жили в Program.cs блоком 158-170 (GraphPersistence
// + CodeGraphService + CodeGraphPromptProvider + CodeGraphQueryService). Если кто-то
// добавит регистрацию в Program.cs вместо подсистемы — или вынесет регистрацию из
// Register — тест упадёт, и ревью это поймает.
//
// Первая подсистема с пост-билд фазой (`IAppPhaseSubsystem.ConfigureApp`): в фазу
// `ConfigureApp` переехал блок регистрации языковых провайдеров `.cs`/`.ts`/`.tsx`
// (бывший блок 926-946 в Program.cs, под `if (!inspectionMode)`). Гейт инспекции
// сохраняется через `app.Configuration.GetValue<bool>("InspectionMode")`.
public class CodeGraphSubsystemRegistrationTests
{
    private static IConfiguration BuildConfig() =>
        new ConfigurationBuilder().Build();

    [Fact]
    public void Register_SubsystemHasStableKeyAndTitle()
    {
        var subsystem = new CodeGraphSubsystem();

        subsystem.Key.Should().Be("code-graph");
        subsystem.Title.Should().Be("Граф кода");
    }

    [Fact]
    public void Register_RegistersAllCodeGraphServices()
    {
        var services = new ServiceCollection();
        var config = BuildConfig();

        services.AddSubsystems(config, new CodeGraphSubsystem());

        // GraphPersistence — ленивый factory; тип регистрируется как singleton-factory,
        // поэтому проверяем именно ServiceType, а не ImplementationType.
        services.Should().Contain(s => s.ServiceType == typeof(GraphPersistence));
        services.Should().Contain(s => s.ServiceType == typeof(CodeGraphService));
        services.Should().Contain(s => s.ServiceType == typeof(CodeGraphPromptProvider));
        services.Should().Contain(s => s.ServiceType == typeof(CodeGraphQueryService));
    }

    // Сторож факта подключения в Program.cs: поднимает полный стенд через
    // `TestWebApplicationFactory<Program>` и резолвит типы CodeGraph из РЕАЛЬНОГО
    // DI-графа (а не из изолированного ServiceCollection, как предыдущие тесты).
    // Если кто-то уберёт `new CodeGraphSubsystem()` из `AddSubsystems(...)` в
    // Program.cs — резолв упадёт с InvalidOperationException, и тест поймает регрессию.
    [Fact]
    public void Program_RegistersCodeGraphSubsystem_ServicesResolvable()
    {
        using var factory = new TestWebApplicationFactory();
        var sp = factory.Services;

        sp.GetRequiredService<GraphPersistence>();
        sp.GetRequiredService<CodeGraphService>();
        sp.GetRequiredService<CodeGraphPromptProvider>();
        sp.GetRequiredService<CodeGraphQueryService>();
    }

    // Сторож пост-билд фазы: после `app.UseSubsystems()` в Program.cs провайдеры
    // `.cs`/`.ts`/`.tsx` должны быть зарегистрированы в `CodeGraphService._providers`.
    // Публичного API проверки состава провайдеров у `CodeGraphService` нет (записи
    // доступны только через `RegisterProvider`), поэтому используем рефлексию по
    // приватному полю `_providers` — это сознательный обходной путь, чтобы не
    // расширять публичный контракт сервиса ради теста.
    [Fact]
    public void Program_ConfigureApp_RegistersLanguageProviders()
    {
        using var factory = new TestWebApplicationFactory();
        var service = factory.Services.GetRequiredService<CodeGraphService>();

        var providers = GetProviders(service);

        providers.Should().ContainKey(".cs",
            "ConfigureApp должен зарегистрировать провайдер для .cs (CSharpGraphProvider)");
        providers.Should().ContainKey(".ts",
            "ConfigureApp должен зарегистрировать провайдер для .ts (TypeScriptGraphProvider)");
        providers.Should().ContainKey(".tsx",
            "ConfigureApp должен зарегистрировать провайдер для .tsx (TypeScriptGraphProvider)");
    }

    // Извлечь приватный `_providers` из CodeGraphService. ConcurrentDictionary<string, ICodeGraphProvider>
    // в CodeGraphService.cs:20. Тестовая обвязка — единственное место, где мы «лезем»
    // в приватное состояние, и она намеренно изолирована от продакшн-кода.
    private static ConcurrentDictionary<string, ICodeGraphProvider> GetProviders(CodeGraphService service)
    {
        var field = typeof(CodeGraphService).GetField("_providers", BindingFlags.NonPublic | BindingFlags.Instance);
        field.Should().NotBeNull("CodeGraphService должен иметь приватное поле _providers");

        var value = field!.GetValue(service);
        value.Should().BeOfType<ConcurrentDictionary<string, ICodeGraphProvider>>();

        return (ConcurrentDictionary<string, ICodeGraphProvider>)value!;
    }
}