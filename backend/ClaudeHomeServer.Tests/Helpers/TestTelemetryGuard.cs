using System.Runtime.CompilerServices;

namespace ClaudeHomeServer.Tests.Helpers;

/// <summary>
/// Глушит экспорт телеметрии на весь тестовый процесс.
///
/// Зачем: <c>WebApplicationFactory</c> поднимает НАСТОЯЩИЙ Program.cs, а тот читает
/// appsettings.Local.json разработчика. Если у него включён SigNoz, тестовый хост
/// создаёт реальный OTLP-экспортёр. <c>Meter</c> в ServerMetrics статический на процесс,
/// поэтому через этот экспортёр уезжают метрики ВСЕХ тестов, включая чистые юнит-тесты:
/// в боевом ClickHouse оседали ряды <c>provider=test-&lt;guid&gt;</c> и <c>tool_name=tool_x</c>,
/// причём каждый прогон плодил НОВЫЕ уникальные ряды — медленный взрыв кардинальности.
///
/// Почему переменная окружения, а не конфиг: Program.cs подключает appsettings.Local.json
/// ПОСЛЕ источников, которые подставляет WebApplicationFactory, поэтому переопределить
/// <c>Telemetry:Backends:*:Enabled</c> из теста невозможно — файл разработчика сильнее.
/// Переменная читается в <c>AddObservability</c> напрямую, в обход конфигурации.
///
/// Почему ModuleInitializer: срабатывает при загрузке сборки — раньше любого теста
/// и любого тестового хоста. Fixture или конструктор фабрики для этого уже поздно.
/// </summary>
internal static class TestTelemetryGuard
{
    [ModuleInitializer]
    internal static void DisableTelemetryExport() =>
        Environment.SetEnvironmentVariable("CCS_TELEMETRY_DISABLED", "1");
}
