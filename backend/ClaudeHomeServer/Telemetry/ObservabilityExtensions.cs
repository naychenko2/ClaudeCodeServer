using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
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
        // Аварийный выключатель ЭКСПОРТА поверх любой конфигурации. Глушит только отправку
        // наружу: регистрация SDK, heartbeat (H4) и гейджи остаются на месте — иначе
        // ломается инвариант «observability-of-observability работает без backend'ов».
        //
        // Нужен там, где конфигом не обойтись: appsettings.Local.json подключается
        // в Program.cs ПОСЛЕ источников, которые подставляет WebApplicationFactory,
        // поэтому тестовый хост не может переопределить Telemetry:Backends:*:Enabled —
        // файл разработчика сильнее. Без этого весь прогон тестов экспортировал метрики
        // в боевой SigNoz: Meter статический на процесс, и данные чистых юнит-тестов
        // уезжали через экспортёр, поднятый любым из Controllers-тестов
        // (ряды provider=test-<guid>, tool_name=tool_x). См. TestTelemetryGuard в тестах.
        // В проде — быстрый способ вырубить отправку, не трогая конфиг.
        var exportDisabled = Environment.GetEnvironmentVariable("CCS_TELEMETRY_DISABLED") == "1";

        var section = config.GetSection("Telemetry");
        var mode = section.GetValue<string>("Mode") ?? "dev";

        var devEnabled = section.GetValue<bool>("Backends:Dev:Enabled");
        var prodEnabled = section.GetValue<bool>("Backends:Production:Enabled");

        // Сэмплер: ParentBased → дочерние спаны следуют за корнем (C3-UPD)
        var ratioKey = mode == "production" ? "TraceSampleRatio:Production" : "TraceSampleRatio:Dev";
        var sampler = ResolveSampler(section.GetValue<double?>(ratioKey));

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
            t.SetSampler(sampler);

            // Стандартные instrumentation'ы
            t.AddAspNetCoreInstrumentation();
            t.AddHttpClientInstrumentation(o =>
            {
                // RecordException НЕ включаем: он создаёт ActivityEvent с exception.message
                // и exception.stacktrace, а события — неизменяемая коллекция, до которой
                // PiiSanitizingProcessor не дотянется (он чистит только теги). В сообщении
                // исключения приезжает URL с query-строкой (там бывают API-ключи Dify и
                // OpenRouter) и абсолютные пути сборки. Факт и категория ошибки остаются
                // в статусе спана и теге error.type — для диагностики этого достаточно.
                o.RecordException = false;

                // Прежде здесь стояла «редакция» заголовка Authorization через
                // EnrichWithHttpRequestMessage. Она не защищала ни от чего: инструментация
                // HttpClient заголовки запроса не пишет вовсе, то есть редактировать было
                // нечего — тег с текстом "<redacted>" ДОБАВЛЯЛСЯ на каждый исходящий спан
                // и тут же дропался санитайзером. Реальный канал утечки токена — url.full
                // с query-строкой, он закрыт в PiiSanitizingProcessor (не входит в KeepTags).
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

            // Явные границы бакетов для длительности хода LLM. Без View дефолтные границы
            // OTel заканчиваются на 10 000 мс, и все ходы падают в последний бакет —
            // p95/p99 упираются в потолок и не различают 30 секунд и 10 минут.
            // Обоснование шкалы — в ServerMetrics.LlmDurationBoundaries.
            m.AddView(
                ServerMetrics.LlmDuration.Name,
                new ExplicitBucketHistogramConfiguration { Boundaries = ServerMetrics.LlmDurationBoundaries });

            // Встроенные метры .NET 10 + OTel 1.17.0 (GA, не experimental).
            // Раньше был комментарий «experimental packages, оставляем custom Meter для MVP» —
            // он устарел: AspNetCore/Http 1.17.0 дают GA-метрики, а метры ниже нативны в .NET 10.
            //
            // PII: метрики идут в pipeline БЕЗ PiiSanitizingProcessor (он только в WithTracing).
            // Все перечисленные метры вычитаны на PII-аудите (см. docs/observability/dashboards.md):
            // ни один не несёт user_id/session_id/path/prompt — только method/route/status/host.
            m.AddMeter("Microsoft.AspNetCore.Hosting");     // http.server.request.duration
            m.AddMeter("Microsoft.AspNetCore.Server.Kestrel"); // kestrel.active_connections, tls handshakes
            m.AddMeter("Microsoft.AspNetCore.Routing");     // http.route matched/unmatched
            m.AddMeter("Microsoft.AspNetCore.RateLimiting"); // rate-limit policy hits (для Auth:PingRateLimit)
            m.AddMeter("System.Net.Http");                  // http.client.request.duration, server_address (host LLM)
            m.AddMeter("System.Net.NameResolution");        // dns.lookup_duration
            m.AddMeter("System.Runtime");                   // GC, ThreadPool, process.memory (нужен Runtime pkg)
        });

        // Logging — IncludeFormattedMessage для читаемости в SigNoz ListView.
        // Без этого SigNoz показывает голый template с {placeholder} вместо значений.
        otelBuilder.WithLogging(l =>
        {
            l.ConfigureServices(s =>
            {
                s.Configure<OpenTelemetryLoggerOptions>(o =>
                {
                    o.IncludeFormattedMessage = true;
                    o.ParseStateValues = true;
                });
            });

            // PII-санитайзер логов (T15, парный к трейсовому). ОБЯЗАТЕЛЕН при включённых
            // IncludeFormattedMessage/ParseStateValues: без него в SigNoz уезжают готовые
            // строки со значениями — имена чатов, идентификаторы пользователей, пути
            // к файлам секретов, имена персон. Процессор возвращает тело к шаблону
            // и фильтрует атрибуты теми же правилами, что и спаны.
            l.AddProcessor(new PiiSanitizingLogProcessor());
        });

        // OTLP exporter — один активный backend (AD3, AD4).
        // Multi-exporter fan-out (both) пока не реализован — нужен named options pattern.
        // Приоритет: production > dev. Если оба Enabled — берём production.
        if (prodEnabled && !exportDisabled)
        {
            var endpoint = ParseEndpoint(
                section.GetValue<string>("Backends:Production:OtlpEndpoint"), "http://localhost:4318");
            if (endpoint is not null)
            {
                otelBuilder.UseOtlpExporter(OtlpExportProtocol.HttpProtobuf, endpoint);
                QuietDownExportLogging(services);
            }
        }
        else if (devEnabled && !exportDisabled)
        {
            var endpoint = ParseEndpoint(
                section.GetValue<string>("Backends:Dev:OtlpEndpoint"), "http://localhost:4317");
            if (endpoint is not null)
            {
                otelBuilder.UseOtlpExporter(OtlpExportProtocol.Grpc, endpoint);
                QuietDownExportLogging(services);
            }
        }

        // Heartbeat — ВСЕГДА регистрируется (H4). Даже без backend'ов тики полезны
        // для отладки pipeline: если счётчик остановился, проблема в телеметрии.
        services.AddHostedService<TelemetryHeartbeatService>();

        // T9: Gauges registrar — отложенная регистрация ObservableGauges после построения
        // DI-контейнера (когда SessionManager/ConnectionDiagnostics доступны).
        services.AddHostedService<GaugesRegistrarService>();

        AddAlerts(services, config);

        return services;
    }

    /// <summary>
    /// Приглушение логов неудачного экспорта телеметрии.
    ///
    /// Экспортёры берут HttpClient из <c>IHttpClientFactory</c> под этими именами, а дефолтное
    /// логирование <c>Microsoft.Extensions.Http</c> печатает каждый провалившийся запрос как
    /// Error со стектрейсом. Коллектор поднят не всегда (на деве — почти никогда), экспорт идёт
    /// по расписанию, и консоль превращается в ленту красных портянок, в которой не видно
    /// настоящих ошибок. Меняем дефолтные логгеры на <see cref="OtlpExportHttpLogger"/>:
    /// Warning, одна строка, не чаще раза в пять минут.
    ///
    /// Вызывается только когда экспорт реально включён — иначе именованные клиенты никем
    /// не создаются и настраивать нечего.
    /// </summary>
    private static void QuietDownExportLogging(IServiceCollection services)
    {
        services.AddSingleton<OtlpExportHttpLogger>();

        // Синглтон один на все три клиента: троттлинг общий, один сбой коллектора = одна строка.
        foreach (var client in new[] { "OtlpTraceExporter", "OtlpMetricExporter", "OtlpLogExporter" })
            services.AddHttpClient(client)
                .RemoveAllLoggers()
                .AddLogger(sp => sp.GetRequiredService<OtlpExportHttpLogger>());
    }

    /// <summary>
    /// Доставка алертов SigNoz в уведомления CCS (секция <c>Telemetry:Alerts</c>).
    ///
    /// Регистрируется НЕЗАВИСИМО от экспортёров: опрос читает SigNoz, а не пишет в него,
    /// и осмыслен даже там, где экспорт выключен. Выключено или без ключа — служба
    /// не поднимается вовсе, как и провайдер LLM с пустым ApiKey.
    /// </summary>
    private static void AddAlerts(IServiceCollection services, IConfiguration config)
    {
        var options = Alerts.AlertsOptions.FromConfig(config);
        if (!options.IsUsable) return;

        services.AddSingleton(options);
        services.AddSingleton<Alerts.AlertStateStore>();
        services.AddSingleton<Alerts.SignozAlertsClient>();
        services.AddHttpClient("signoz-alerts", c => c.Timeout = TimeSpan.FromSeconds(15));
        services.AddHostedService<Alerts.AlertPollingService>();
    }

    /// <summary>
    /// Сэмплер трейсов по значению <c>Telemetry:TraceSampleRatio:{Dev|Production}</c>.
    ///
    /// Дефолт — <b>1.0</b> (пишем все трейсы). Прежние 0.05/0.10 пришли из практики
    /// нагруженных сервисов и здесь работали против цели: инсталляция однопользовательская,
    /// ходов единицы в минуту, и при 5% нужного трейса в 19 случаях из 20 просто нет —
    /// «трейсинг включён, но разобрать по нему нечего». Экономить не на чем: такой поток
    /// 15-дневный retention переваривает не замечая, а метрики сэмплинга вообще не касаются.
    ///
    /// Значения: <c>0</c> — осознанное «трейсы не нужны» (раньше молча превращалось в дефолт,
    /// то есть выключить трейсинг конфигом было НЕЛЬЗЯ); <c>(0;1]</c> — доля корневых трейсов;
    /// вне диапазона — мусор в конфиге, откатываемся к дефолту с жалобой в stderr.
    ///
    /// ParentBased везде, кроме краёв: дочерние спаны обязаны следовать решению корня,
    /// иначе трейс приезжает дырявым (C3-UPD).
    /// </summary>
    internal static Sampler ResolveSampler(double? configured)
    {
        if (configured is null) return new AlwaysOnSampler();

        var ratio = configured.Value;
        if (ratio == 0) return new AlwaysOffSampler();
        if (ratio >= 1) return new AlwaysOnSampler();

        if (ratio is < 0 or > 1)
        {
            Console.Error.WriteLine(
                $"[Telemetry] TraceSampleRatio={ratio} вне диапазона [0;1] — беру 1.0 (все трейсы)");
            return new AlwaysOnSampler();
        }

        return new ParentBasedSampler(new TraceIdRatioBasedSampler(ratio));
    }

    /// <summary>
    /// Адрес OTLP-коллектора. Раньше строка из конфига шла в <c>new Uri(...)</c> без проверки,
    /// и опечатка в <c>OtlpEndpoint</c> роняла приложение на старте — единственное место,
    /// где observability убивала продукт. Теперь кривой адрес просто выключает экспорт:
    /// сервер поднимается, в stderr — причина.
    /// </summary>
    internal static Uri? ParseEndpoint(string? configured, string fallback)
    {
        var raw = string.IsNullOrWhiteSpace(configured) ? fallback : configured;

        if (Uri.TryCreate(raw, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            return uri;

        Console.Error.WriteLine(
            $"[Telemetry] OtlpEndpoint='{raw}' — не абсолютный http(s)-адрес, экспорт телеметрии выключен");
        return null;
    }

    /// <summary>Имена named OTLP exporters для fan-out (оба backend'а одновременно).</summary>
    public static class OtlpExporterNames
    {
        public const string Dev = "otlp/dev";
        public const string Production = "otlp/prod";
    }
}
