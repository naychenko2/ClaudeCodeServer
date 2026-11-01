using System.Runtime.CompilerServices;
using ClaudeHomeServer.Tests.Helpers;

namespace ClaudeHomeServer.Tests;

/// <summary>
/// Включает TestKit-гарант среды до первого теста ЭТОЙ сборки.
///
/// Зачем: TestEnvironmentGuard — [ModuleInitializer] сборки TestKit, а модульный инициализатор
/// CLR исполняется не при загрузке сборки как зависимости: он исполняется, когда впервые
/// запускается код ИЗ этой сборки. Класс, который поднимает живой Kestrel-хост сам, а не через
/// TestServer фабрики, может дожить до первого обращения к коду TestKit: LocalApi.Build берёт
/// среду из окружения процесса (backend/ClaudeHomeServer.DeviceAgent/Composition/LocalApi.cs),
/// в CI ASPNETCORE_ENVIRONMENT не ставится вовсе → среда Production → из выходного каталога
/// тестового проекта поднимается appsettings.Production.json с Kestrel:Endpoints:Https —
/// которого у localhost-API агента быть не может, и хост падает на StartAsync в
/// HttpsConfigurationService («Call UseKestrelHttpsConfiguration()»).
///
/// Почему это было зелено и стало красным с шардированием: в одном testhost'е прогона монолита
/// какой-то другой тест раньше трогал код TestKit и среда уже была "Testing". Шард меняет
/// состав и порядок testhost'а — «спаситель» может не найтись, и класс остаётся один на один
/// с окружением процесса. Правка не в asserts класса, а в порядке: гарант до первого теста.
///
/// Почему инициализатор здесь, а не в TestKit: он guaranteed для сборки, чьи тесты строят
/// хосты, — любой её код идёт после него. Здесь вызов, а не копия строк: единственный источник
/// истины по Testing-среде остаётся в TestEnvironmentGuard; тот же вызов инициализирует модуль
/// TestKit, поэтому рядом поднимается и TestTelemetryGuard.
/// </summary>
internal static class TestEnvironmentBootstrap
{
    [ModuleInitializer]
    internal static void EnableTestGuards() => TestEnvironmentGuard.SetTestingEnvironment();
}
