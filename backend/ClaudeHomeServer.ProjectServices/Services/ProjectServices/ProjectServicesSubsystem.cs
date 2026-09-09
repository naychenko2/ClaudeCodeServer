using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Http;

namespace ClaudeHomeServer.Services.ProjectServices;

/// <summary>
/// Подсистема раздела «Сервисы проекта»: обнаружение запускаемых сервисов в проекте
/// (манифесты <c>package.json</c>/<c>launchSettings.json</c>/<c>docker-compose</c>/<c>Procfile</c>/<c>Makefile</c>
/// плюс сохранённые <c>.claude/launch.json</c>), запуск процессов дев-серверов,
/// прокси <c>/preview/**</c> и внешний доступ к дев-серверу по отдельному поддомену.
/// Подпись раздела переименована в «Сервисы», но маршрут и ключи <c>preview</c> сохранены.
///
/// Сознательная граница: <c>OutputRingBuffer</c> живёт в корне
/// <c>ClaudeHomeServer.Services</c> — общий примитив реплея вывода с дев-серверов и
/// терминалов, чтобы не плодить дубль и не заводить ребро «вертикаль → вертикаль».
/// Внешний CDN-прокси <c>/api/proxy</c> НЕ здесь — он сидит на отдельном клиенте
/// <c>media-proxy</c>, потому что ходит через системный egress.
/// </summary>
public sealed class ProjectServicesSubsystem : IAppSubsystem
{
    public string Key => "project-services";

    public string Title => "Сервисы проекта";

    public void Register(IServiceCollection services, IConfiguration config)
    {
        // Последний известный порт сервиса: без него живой дев-сервер после перезапуска
        // продукта выглядел бы остановленным, и запуск падал бы на занятом порту.
        services.AddSingleton<DevServerPortMemory>();
        services.AddSingleton<DevServerService>();
        services.AddSingleton<LaunchConfigService>();
        services.AddSingleton<ProjectServiceDiscovery>();
        // Внешний доступ к дев-серверу проекта по отдельному поддомену. По умолчанию ВЫКЛЮЧЕН —
        // см. ExternalPreviewOptions: код уезжает всем, у кого свой инстанс, поэтому защита
        // обязана быть конфигурацией, а не отсутствием кода.
        services.Configure<ExternalPreviewOptions>(config.GetSection(ExternalPreviewOptions.Section));
        services.AddSingleton<ExternalPreviewStore>();
        services.AddSingleton<ExternalPreviewRouter>();
        // "proxy" ходит только к нашим же сервисам: dev-серверы проектов и скачивание
        // готового документа у OnlyOffice в office-callback. Egress-прокси им не нужен —
        // см. WithoutEgressProxy. Медиа-прокси /api/proxy на этом клиенте НЕ сидит —
        // он живёт на отдельном "media-proxy" ниже: прямой канал наружу душится DPI,
        // поэтому внешние CDN обязаны идти через системный egress.
        services.AddHttpClient("proxy").WithoutEgressProxy();
    }
}