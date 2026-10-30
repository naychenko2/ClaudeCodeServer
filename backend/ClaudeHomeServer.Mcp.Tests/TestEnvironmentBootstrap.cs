using System.Runtime.CompilerServices;
using ClaudeHomeServer.Tests.Helpers;

namespace ClaudeHomeServer.Tests;

/// <summary>
/// Включает TestKit-гарант среды до первого теста ЭТОЙ сборки.
///
/// Зачем: TestEnvironmentGuard — [ModuleInitializer] сборки TestKit, а модульный инициализатор
/// CLR исполняется не при загрузке сборки как зависимости: он исполняется, когда впервые
/// запускается код ИЗ этой сборки. Тесты этой сборки строят хосты через TestWebApplicationFactory
/// (сборка TestKit.Host) и могут дожить до первого обращения к коду TestKit: среда тогда
/// остаётся средой процесса — в CI ASPNETCORE_ENVIRONMENT не ставится вовсе → Production →
/// AddGatedHostedFrom регистрирует HiggsfieldSnapshotWarmer как hosted, и ассерт
/// «hosted в Testing не регистрируется» краснеет. В полном прогоне этой сборки «спаситель»
/// обычно находится (DevToolset/LocalMedia/Tests тулсет-тесты зовут TestLauncherFactory из
/// TestKit — ClaudeHomeServer.Mcp.Tests.csproj:65-67), но шард меняет состав testhost'а —
/// и в rest 3/3 (CI run 41028084876) его не было. Правка не в asserts класса, а в порядке:
/// гарант до первого теста.
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
