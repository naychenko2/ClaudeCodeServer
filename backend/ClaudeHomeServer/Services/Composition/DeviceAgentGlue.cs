namespace ClaudeHomeServer.Services.Composition;

// Клей Main↔Devices (S6-1): регистрация каталога релизов агента устройства
// (`AgentReleaseCatalog`, agent-distribution Р3, Р5) перенесена из точечного гейта
// в Program.cs. Его читают и ack хаба, и анонимная раздача AgentDownloadsController.
//
// Ключ тумблера — легасийный `desktop` (не ключ IAppSubsystem: Devices не оформлена
// подсистемой, ключ держится ради совместимости с конфигами; см. SubsystemDisableMatrixTests —
// «desktop» вне матрицы). Поэтому это не «Register своей подсистемы», а клей: точка
// регистрации ушла из корня, гейт остался тем же. Нет регистрации — каталог null,
// раздача отвечает 503 с причиной, ack не называет версий. Полная выноска регистраций
// Devices — S6-2.
public static class DeviceAgentGlue
{
    public static IServiceCollection AddAgentReleaseCatalog(
        this IServiceCollection services, IConfiguration config)
    {
        if (SubsystemGate.IsEnabled(config, "desktop"))
            services.AddSingleton<ClaudeHomeServer.Services.Devices.AgentReleaseCatalog>();
        return services;
    }
}
