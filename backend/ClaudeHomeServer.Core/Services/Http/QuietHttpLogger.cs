using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http.Logging;

namespace ClaudeHomeServer.Services.Http;

/// <summary>
/// Профиль тихого HTTP-клиента: как называть зависимость в логе и чем грозит её отсутствие.
/// </summary>
/// <param name="Category">
/// Категория логгера — по ней сообщение видно в консоли и её можно приглушить отдельно
/// в секции <c>Logging:LogLevel</c>.
/// </param>
/// <param name="Subject">
/// Название зависимости в ТВОРИТЕЛЬНОМ падеже — подставляется в «Нет связи с …»
/// («OTLP-коллектором», «локальной моделью Ollama»).
/// </param>
/// <param name="Consequence">
/// Что это значит для продукта: «Телеметрия не уходит.», «Фоновые действия уйдут облачной модели.»
/// Без этой фразы читатель лога не понимает, надо ли ему вообще что-то делать.
/// </param>
public sealed record QuietHttpClientProfile(string Category, string Subject, string Consequence);

/// <summary>
/// Логгер HTTP-клиента для зависимости, которой в норме может не быть на месте.
///
/// Дефолтное логирование <c>Microsoft.Extensions.Http</c> печатает КАЖДЫЙ провалившийся запрос
/// как <b>Error</b> с полным стектрейсом <see cref="HttpRequestException"/>. Для зависимостей,
/// которые опрашиваются по расписанию или дёргаются часто (OTLP-экспорт — раз в 5 секунд,
/// локальная модель — на каждое фоновое действие), это превращает консоль в ленту красных
/// портянок, в которой тонут настоящие ошибки приложения. При этом сами вызывающие уже
/// умеют жить без зависимости: телеметрия просто не уходит, Ollama уступает облачной модели.
///
/// Поэтому здесь: <b>Warning</b>, одна строка, без стектрейса (он всегда одинаков — путь через
/// <c>HttpConnectionPool</c>) и не чаще раза в <see cref="ReportInterval"/>. Успешный запрос
/// сбрасывает троттлинг, чтобы следующий сбой был виден сразу, а не после пяти минут тишины.
///
/// Один экземпляр на профиль (см. <see cref="QuietHttpClientExtensions.AddQuietHttpClient"/>):
/// три клиента OTLP-экспорта делят общий интервал, и один мёртвый коллектор даёт одну строку,
/// а не три.
/// </summary>
public sealed class QuietHttpLogger : IHttpClientLogger
{
    /// <summary>Не чаще одной жалобы за этот интервал (на весь профиль сразу).</summary>
    internal static readonly TimeSpan ReportInterval = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Потолок длины тела 4xx в логе. Типичный JSON-ответ с диагнозом укладывается в 200–300
    /// символов; 500 хватает с запасом для многословных сообщений от локальных моделей.
    /// </summary>
    internal const int MaxBodyChars = 500;

    private readonly ILogger _log;
    private readonly QuietHttpClientProfile _profile;
    private readonly Func<DateTimeOffset> _now;
    private readonly object _gate = new();
    private DateTimeOffset? _lastReport;
    // Тело последнего 4xx в нормализованном виде — повтор того же тела в течение сессии
    // не повторяем (диагноз уже сказан), но другая 4xx (другой статус/тело) логируется всегда:
    // иначе новый вид отказа теряется за тишиной интервала и приходится ловить его глазами.
    private string? _lastClientErrorBody;

    public QuietHttpLogger(ILoggerFactory factory, QuietHttpClientProfile profile, Func<DateTimeOffset>? now = null)
    {
        _profile = profile;
        _log = factory.CreateLogger(profile.Category);
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public object? LogRequestStart(HttpRequestMessage request) => null;

    public void LogRequestStop(
        object? context, HttpRequestMessage request, HttpResponseMessage response, TimeSpan elapsed)
    {
        if (response.IsSuccessStatusCode)
        {
            // Зависимость снова отвечает — снимаем троттлинг.
            lock (_gate)
            {
                _lastReport = null;
                _lastClientErrorBody = null;
            }
            return;
        }

        var statusCode = (int)response.StatusCode;
        var host = Endpoint(request);

        if (statusCode is >= 400 and < 500)
        {
            // 4xx — сервер жив и отверг именно наш запрос. Это наш баг: устаревший формат,
            // слишком длинный промпт, протухший токен. 5xx-формулировка здесь не подходит:
            // «на стороне зависимости» намекает на её поломку, а зависимость работает штатно.
            // Тело почти всегда содержит готовый диагноз — печатаем усечённо.
            var body = TryReadBody(response);
            lock (_gate)
            {
                if (_lastClientErrorBody == body) return;
                _lastClientErrorBody = body;
            }
            _log.LogWarning(
                "Запрос отвергнут {Subject} ({Host}): HTTP {Status}. Тело: {Body}. {Consequence}",
                _profile.Subject, host, statusCode, body, _profile.Consequence);
            return;
        }

        // 5xx и прочие не-2xx — хост жив, но отказал (503 при перезапуске SigNoz, 500 от
        // модели). Тело обычно бесполезно (HTML/пусто), а вычитывать его на каждом сбое
        // лежащего сервиса накладно; обходимся статусом.
        if (ShouldReport())
            _log.LogWarning("Ошибка на стороне {Subject} ({Host}): HTTP {Status}. {Consequence}",
                _profile.Subject, host, statusCode, _profile.Consequence);
    }

    public void LogRequestFailed(
        object? context, HttpRequestMessage request, HttpResponseMessage? response,
        Exception exception, TimeSpan elapsed)
    {
        if (!ShouldReport()) return;

        _log.LogWarning("Нет связи с {Subject} ({Host}): {Reason}. {Consequence}",
            _profile.Subject, Endpoint(request), Reason(exception), _profile.Consequence);
    }

    // Точку в конце срезаем: сообщения сетевых исключений заканчиваются ею сами,
    // а шаблон ставит свою — иначе в логе «отверг запрос на подключение.. Телеметрия…».
    private static string Reason(Exception exception) =>
        exception.GetBaseException().Message.TrimEnd(' ', '.');

    /// <summary>
    /// Схема+хост+порт вызываемого сервиса. Имя параметра — {Host}, а НЕ {Endpoint}:
    /// под {Endpoint} в PushService логируется адрес push-подписки, то есть
    /// идентификатор устройства пользователя. Одно имя на две сущности означало бы, что
    /// правило приватности для них тоже одно — и разрешив адрес SigNoz, мы бы заодно
    /// выпустили наружу токены устройств.
    /// </summary>
    private static string Endpoint(HttpRequestMessage request) =>
        request.RequestUri?.GetLeftPart(UriPartial.Authority) ?? "адрес неизвестен";

    private bool ShouldReport()
    {
        var now = _now();
        lock (_gate)
        {
            if (_lastReport is { } last && now - last < ReportInterval) return false;
            _lastReport = now;
            return true;
        }
    }

    /// <summary>
    /// Дочитывает тело 4xx в строку, пригодную для лога. Делает это так, чтобы вызывающий
    /// (например, <c>ChatTurnAsync</c> с <c>HttpCompletionOption.ResponseHeadersRead</c> и
    /// потоковым SSE) мог прочитать тело сам: сначала <see cref="HttpContent.LoadIntoBufferAsync"/>
    /// — внутри HttpContent он перекладывает поток в <c>MemoryStream</c> и оставляет
    /// <see cref="HttpContent"/> читаемым; для уже буферизованного <c>ResponseContentRead</c>
    /// это повторная (безвредная) копия. Без этого шага <see cref="HttpContent.ReadAsStringAsync"/>
    /// съел бы сетевой поток, и стрим вызывающего получил бы EOF.
    /// </summary>
    private static string TryReadBody(HttpResponseMessage response)
    {
        try
        {
            response.Content.LoadIntoBufferAsync().GetAwaiter().GetResult();
            var text = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            return NormalizeBody(text);
        }
        catch
        {
            return "<тело не прочиталось>";
        }
    }

    private static string NormalizeBody(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "<пусто>";
        // Схлопываем переводы строк: иначе многострочный JSON рвёт строку лога, и читать
        // приходится склейкой через голову. Соседние пробелы тоже давим, чтобы усечённый
        // хвост не превратился в «                       …».
        var flat = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();
        return flat.Length <= MaxBodyChars ? flat : flat[..MaxBodyChars] + "…";
    }
}

public static class QuietHttpClientExtensions
{
    /// <summary>
    /// Именованный HttpClient, который на недоступность зависимости отвечает одной строкой
    /// Warning вместо ленты Error со стектрейсами (см. <see cref="QuietHttpLogger"/>).
    ///
    /// Клиенты с одинаковым профилем делят один логгер, а значит и интервал жалоб: keyed-синглтон
    /// по <see cref="QuietHttpClientProfile.Category"/>.
    /// </summary>
    public static IHttpClientBuilder AddQuietHttpClient(
        this IServiceCollection services, string clientName, QuietHttpClientProfile profile)
    {
        services.TryAddKeyedSingleton(profile.Category, (IServiceProvider sp, object _) =>
            new QuietHttpLogger(sp.GetRequiredService<ILoggerFactory>(), profile));

        return services.AddHttpClient(clientName)
            .RemoveAllLoggers()
            .AddLogger(sp => sp.GetRequiredKeyedService<QuietHttpLogger>(profile.Category));
    }

    /// <summary>
    /// Отключает системный прокси для клиента. Нужно всем, кто ходит к НАШИМ ЖЕ сервисам
    /// (Dify, Forgejo, OnlyOffice, телеметрия, dev-серверы): переменные HTTP_PROXY/HTTPS_PROXY
    /// задают egress в интернет, и запрос к соседнему хосту уходил бы в этот прокси — тот
    /// локальные адреса не обслуживает и отвечает 503.
    ///
    /// Почему не NO_PROXY: переменная наследуется от окружения запуска, а не читается заново.
    /// Процесс, стартовавший из окна, открытого до её правки, молча получает старое значение —
    /// поломка невидима и воспроизводится только на конкретной машине. Адреса наших сервисов
    /// приходят из конфига, так что проксировать их не нужно никогда.
    /// </summary>
    public static IHttpClientBuilder WithoutEgressProxy(this IHttpClientBuilder builder) =>
        builder.ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { UseProxy = false });
}
