using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace ClaudeHomeServer.Telemetry;

/// <summary>
/// Регистрация OTel SDK с two-mode конфигурацией (dev → Aspire / production → SigNoz).
///
/// Архитектурные решения:
/// - <b>C3-UPD</b>: сэмплер <see cref="ParentBasedSampler"/> поверх <see cref="TraceIdRatioBasedSampler"/>
///   — дочерние спаны следуют за корневым решением. Dev=0.10, Production=0.05.
/// - <b>C6</b>: HttpClient instrumentation редауитует заголовок Authorization.
/// - <b>AD3</b>: источник правды — <c>Backends.{Dev,Production}.Enabled</c> флаги в конфиге.
///   <c>Mode</c> — человеческий preset, который раскрывает Enabled в appsettings-слоях.
/// - <b>AD4</b>: Aspire = OTLP/gRPC (:4317), SigNoz = OTLP/HTTP (:4318).
/// - <b>T15</b>: <see cref="PiiSanitizingProcessor"/> сидит ПЕРВЫМ в pipeline — оба
///   backend'а получают очищенные данные (paths hashed, persona/user_id/prompt dropped).
/// - <b>M3</b>: стандартные OTel resource attributes.
/// - <b>H4</b>: <see cref="TelemetryHeartbeatService"/> регистрируется ВСЕГДА,
///   даже если все backend'ы выключены — иначе observability-of-observability не работает.
/// </summary>
public static class ObservabilityExtensions
{
    private const string ServiceName = "ClaudeHomeServer";

    public static IServiceCollection AddObservability(this IServiceCollection services, IConfiguration config)
    {
        var section = config.GetSection("Telemetry");
        var mode = section.GetValue<string>("Mode") ?? "dev";

        var devEnabled = section.GetValue<bool>("Backends:Dev:Enabled");
        var prodEnabled = section.GetValue<bool>("Backends:Production:Enabled");

        // Сэмплер: ParentBased → дочерние спаны следуют за корнем (C3-UPD)
        var ratioKey = mode == "production" ? "TraceSampleRatio:Production" : "TraceSampleRatio:Dev";
        var ratio = section.GetValue<double?>(ratioKey) ?? (mode == "production" ? 0.05 : 0.10);
        // Защита от мусора в конфиге
        if (ratio is <= 0 or > 1) ratio = mode == "production" ? 0.05 : 0.10;

        var otelBuilder = services.AddOpenTelemetry();

        // Resource attributes (M3)
        otelBuilder.ConfigureResource(r => r
            .AddService(serviceName: ServiceName,
                        serviceVersion: typeof(ObservabilityExtensions).Assembly.GetName().Version?.ToString() ?? "unknown")
            .AddAttributes(new Dictionary<string, object>
            {
                ["deployment.environment"] = mode,
                ["service.instance.id"] = Environment.MachineName,
                ["host.name"] = Environment.MachineName,
            }));

        // Tracing
        otelBuilder.WithTracing(t =>
        {
            t.SetSampler(new ParentBasedSampler(new TraceIdRatioBasedSampler(ratio)));

            // Стандартные instrumentation'ы
            t.AddAspNetCoreInstrumentation();
            t.AddHttpClientInstrumentation(o =>
            {
                // C6: редауитовать Authorization — иначе OAuth/API-key токены утечут в trace attributes
                o.EnrichWithHttpRequestMessage = (activity, msg) =>
                    activity.SetTag("http.request.header.authorization", "<redacted>");
                o.RecordException = true;
            });

            // Наш кастомный ActivitySource (T3)
            t.AddSource(ServerActivitySource.Name);

            // PII-санитайзер ПЕРВЫМ в pipeline (T15) — оба backend'а получают очищенные данные
            t.AddProcessor(new PiiSanitizingProcessor());
        });

        // Metrics
        otelBuilder.WithMetrics(m =>
        {
            m.AddMeter(ServerMetrics.MeterName);
            // ASP.NET Core / HttpClient metric instrumentation имеют другой API в OTel 1.17.0
            // (через experimental packages). Для MVP оставляем только наш custom Meter —
            // operational метрики (latency, errors) уже покрывают основные сценарии.
        });

        // OTLP exporter — один активный backend (AD3, AD4).
        // Multi-exporter fan-out (both) пока не реализован — нужен named options pattern.
        // Приоритет: production > dev. Если оба Enabled — берём production.
        if (prodEnabled)
        {
            var endpoint = section.GetValue<string>("Backends:Production:OtlpEndpoint")
                           ?? "http://localhost:4318";
            otelBuilder.UseOtlpExporter(OtlpExportProtocol.HttpProtobuf, new Uri(endpoint));
        }
        else if (devEnabled)
        {
            var endpoint = section.GetValue<string>("Backends:Dev:OtlpEndpoint")
                           ?? "http://localhost:4317";
            otelBuilder.UseOtlpExporter(OtlpExportProtocol.Grpc, new Uri(endpoint));
        }

        // Heartbeat — ВСЕГДА регистрируется (H4). Даже без backend'ов тики полезны
        // для отладки pipeline: если счётчик остановился, проблема в телеметрии.
        services.AddHostedService<TelemetryHeartbeatService>();

        return services;
    }

    /// <summary>Имена named OTLP exporters для fan-out (оба backend'а одновременно).</summary>
    public static class OtlpExporterNames
    {
        public const string Dev = "otlp/dev";
        public const string Production = "otlp/prod";
    }
}
