using Microsoft.Extensions.Primitives;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Forwarder;
using Yarp.ReverseProxy.Transforms;

namespace ClaudeHomeServer.Services.Modules;

/// <summary>
/// Добавочный провайдер конфигурации YARP из реестра модулей (ТЗ R2).
/// Регистрируется РЯДОМ с LoadFromConfig (YARP объединяет несколько IProxyConfigProvider) —
/// существующие маршруты OnlyOffice/drawio/forgejo не затрагиваются.
/// На модуль: маршрут module-{id} ({routePrefix}/** → PathRemovePrefix) + кластер с active
/// health-check на healthPath и activity-таймаутом 300 с (§3, §3.1). Стриминг/SSE YARP
/// не буферизует по умолчанию — отдельной настройки не требуется.
/// Состав модулей фиксирован до рестарта (hot-plug вне scope v1) — конфиг статичен.
/// Параметры health-check читаются из конфига (секция Modules:HealthCheck) — чтобы тесты
/// могли задать минимальные задержки и не ждать 15 с до первой пробы.
/// </summary>
public sealed class ModuleProxyConfigProvider(ModuleRegistry registry, IConfiguration config) : IProxyConfigProvider
{
    // Дефолты health-check: период/таймаут опроса healthPath; после первой же
    // неудачи destination выключается (политика YARP ConsecutiveFailures).
    private readonly TimeSpan _healthInterval =
        TimeSpan.FromSeconds(config.GetValue("Modules:HealthCheck:IntervalSeconds", 15));
    private readonly TimeSpan _healthTimeout =
        TimeSpan.FromSeconds(config.GetValue("Modules:HealthCheck:TimeoutSeconds", 2));
    private readonly string _healthThreshold =
        config.GetValue("Modules:HealthCheck:Threshold", "1");
    // §3.1: proxy activity timeout — 300 с бездействия, не суммарный
    public static readonly TimeSpan ActivityTimeout = TimeSpan.FromSeconds(300);

    private sealed class StaticProxyConfig(IReadOnlyList<RouteConfig> routes, IReadOnlyList<ClusterConfig> clusters) : IProxyConfig
    {
        public IReadOnlyList<RouteConfig> Routes => routes;
        public IReadOnlyList<ClusterConfig> Clusters => clusters;
        public IChangeToken ChangeToken { get; } = new CancellationChangeToken(CancellationToken.None);
    }

    public IProxyConfig GetConfig()
    {
        var routes = new List<RouteConfig>();
        var clusters = new List<ClusterConfig>();
        foreach (var module in registry.All)
        {
            var backend = module.Manifest.Backend!;
            var clusterId = $"module-{module.Id}";
            routes.Add(new RouteConfig
            {
                RouteId = clusterId,
                ClusterId = clusterId,
                Match = new RouteMatch { Path = $"{backend.RoutePrefix}/{{**catch-all}}" },
            }.WithTransformPathRemovePrefix(backend.RoutePrefix));

            clusters.Add(new ClusterConfig
            {
                ClusterId = clusterId,
                Destinations = new Dictionary<string, DestinationConfig>
                {
                    ["default"] = new() { Address = backend.BaseUrl },
                },
                HttpRequest = new ForwarderRequestConfig { ActivityTimeout = ActivityTimeout },
                HealthCheck = new HealthCheckConfig
                {
                    Active = new ActiveHealthCheckConfig
                    {
                        Enabled = true,
                        Interval = _healthInterval,
                        Timeout = _healthTimeout,
                        Policy = "ConsecutiveFailures",
                        Path = backend.HealthPath,
                    },
                },
                Metadata = new Dictionary<string, string>
                {
                    ["ConsecutiveFailuresHealthPolicy.Threshold"] = _healthThreshold,
                },
            });
        }
        return new StaticProxyConfig(routes, clusters);
    }
}
