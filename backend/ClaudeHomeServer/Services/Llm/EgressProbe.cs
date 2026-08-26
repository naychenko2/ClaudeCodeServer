using System.Net.Sockets;

namespace ClaudeHomeServer.Services.Llm;

/// <summary>
/// Жив ли НАШ выход в сеть — исходящий прокси, через который CLI ходит к любому провайдеру.
///
/// ЗАЧЕМ. Классификатор кладёт «connection refused» в <see cref="FallbackErrorClass.Unreachable"/> —
/// «эндпоинт недоступен». Но у отказа два разных корня, и лечатся они противоположно:
///   • недоступен эндпоинт провайдера — помогает смена пары «модель × подписка»;
///   • недоступен канал, ОБЩИЙ для всех провайдеров, — смена пары бессмысленна: соседняя
///     модель пойдёт через тот же мёртвый прокси, цепочка сгорит впустую, а здоровый сторонний
///     провайдер вдобавок получит кулдаун за чужую вину.
/// Разводит их эта проба: прокси не отвечает — значит дело в канале, а не в вендоре.
/// Разбор суток 25.08.2026: 10 из 14 показанных человеку ошибок — один и тот же ConnectionRefused
/// сразу по трём вендорам (claude, alibabacloud, glm), то есть общий канал, а не эндпоинты.
///
/// Проба намеренно тупая (TCP-коннект, без CONNECT и TLS): нас интересует ровно факт «порт
/// слушает». Отказ прокси в туннеле — это уже ответ прокси, то есть канал жив.
/// </summary>
public interface IEgressProbe
{
    /// <summary>
    /// true — прокси задан и НЕ отвечает (выход в сеть лежит). false — отвечает ЛИБО не задан
    /// вовсе: без прокси проверять нечего, и поведение фолбэка остаётся прежним (fail-open).
    /// </summary>
    Task<bool> IsDownAsync(CancellationToken ct = default);
}

/// <inheritdoc />
public sealed class EgressProbe : IEgressProbe
{
    // Порядок как у самих клиентов (curl/node/undici): https_proxy важнее http_proxy, ALL_PROXY —
    // последний. Регистр обеих форм: на Windows переменные регистронезависимы, но процесс CLI
    // может получить env и от родителя с юникс-стилем.
    private static readonly string[] ProxyVars =
        ["HTTPS_PROXY", "https_proxy", "HTTP_PROXY", "http_proxy", "ALL_PROXY", "all_proxy"];

    // Проба стоит на пути ОШИБКИ хода, поэтому дорогой быть не имеет права: живой прокси в
    // локальной сети отвечает за единицы миллисекунд, мёртвый отдаёт RST мгновенно. Потолок
    // нужен только против «чёрной дыры» (пакеты уходят в никуда).
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMilliseconds(400);

    // За один ход фолбэк может спросить пробу несколько раз подряд (шаги цепочки идут секунда
    // в секунду). Кеш гасит эту пачку, но живёт заведомо меньше паузы перед повтором (5 с) —
    // повтор обязан видеть СВЕЖЕЕ состояние канала, иначе смысл повтора теряется.
    private static readonly TimeSpan DefaultCacheFor = TimeSpan.FromSeconds(2);

    private readonly (string Host, int Port)? _proxy;
    private readonly TimeSpan _timeout;
    private readonly TimeSpan _cacheFor;
    private readonly Lock _lock = new();
    private DateTime _checkedAt;
    private bool _lastDown;

    public EgressProbe() : this(ReadProxyFromEnv()) { }

    internal EgressProbe((string Host, int Port)? proxy, TimeSpan? timeout = null, TimeSpan? cacheFor = null)
    {
        _proxy = proxy;
        _timeout = timeout ?? DefaultTimeout;
        _cacheFor = cacheFor ?? DefaultCacheFor;
    }

    /// <summary>Адрес прокси, который проба сторожит (null — переменных окружения нет).</summary>
    public string? ProxyAddress => _proxy is { } p ? $"{p.Host}:{p.Port}" : null;

    /// <inheritdoc />
    public async Task<bool> IsDownAsync(CancellationToken ct = default)
    {
        if (_proxy is not { } proxy) return false;

        lock (_lock)
            if (_checkedAt != default && DateTime.UtcNow - _checkedAt < _cacheFor) return _lastDown;

        var down = !await CanConnectAsync(proxy.Host, proxy.Port, ct);

        lock (_lock)
        {
            _checkedAt = DateTime.UtcNow;
            _lastDown = down;
        }
        return down;
    }

    // Любой исход, кроме установленного соединения, — «канала нет»: отказ порта, таймаут,
    // неизвестное имя. Разбирать их по отдельности незачем — решение одно на все.
    private async Task<bool> CanConnectAsync(string host, int port, CancellationToken ct)
    {
        try
        {
            using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_timeout);
            await socket.ConnectAsync(host, port, cts.Token);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    internal static (string Host, int Port)? ReadProxyFromEnv()
    {
        foreach (var name in ProxyVars)
            if (TryParseProxy(Environment.GetEnvironmentVariable(name), out var parsed))
                return parsed;
        return null;
    }

    /// <summary>
    /// Разбор значения *_PROXY. Схему допускаем любую (http/https/socks5), а её отсутствие
    /// («192.168.7.208:2080» — тоже законное значение переменной) достраиваем сами.
    /// </summary>
    internal static bool TryParseProxy(string? raw, out (string Host, int Port) proxy)
    {
        proxy = default;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        var text = raw.Trim();
        if (!text.Contains("://", StringComparison.Ordinal)) text = "http://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host))
            return false;

        // Uri знает порт по умолчанию только для своих схем (http/https); у socks5 он отдаёт -1
        var port = uri.Port > 0 ? uri.Port : DefaultPort(uri.Scheme);
        if (port <= 0) return false;

        proxy = (uri.Host, port);
        return true;
    }

    private static int DefaultPort(string scheme) => scheme.ToLowerInvariant() switch
    {
        "http" => 80,
        "https" => 443,
        "socks5" or "socks5h" or "socks4" or "socks" => 1080,
        _ => -1,
    };
}
