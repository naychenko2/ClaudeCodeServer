namespace AiHomeDesktop.Core.Policies;

/// <summary>
/// Адрес сервера, к которому подключается клиент. Правило транспорта — зеркало серверного
/// DeviceChannelGuard: HTTPS годится всегда, открытый канал — только на петле, где
/// подслушивать нечего. Иначе подменный сервер сам сочиняет текст тоста и командует
/// машиной, не нарушив ни одного правила (ADR-008, «Аутентификация и транспорт»).
///
/// Проверка стоит и на клиенте, и на сервере: серверная — граница, клиентская — внятный
/// отказ до отправки кода сопряжения.
/// </summary>
public static class ServerAddress
{
    /// <summary>Разобрать адрес, введённый человеком («home.local:5000» — тоже адрес).</summary>
    public static bool TryParse(string? raw, out Uri? uri, out string? error)
    {
        uri = null;
        error = null;

        var text = (raw ?? "").Trim();
        if (text.Length == 0)
        {
            error = "Укажите адрес сервера AI Home";
            return false;
        }

        // Схему не угадываем в пользу http: без явного http:// считаем адрес защищённым,
        // иначе опечатка молча уводила бы сопряжение в открытый канал.
        if (!text.Contains("://", StringComparison.Ordinal)) text = "https://" + text;

        if (!Uri.TryCreate(text, UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        {
            error = "Адрес должен быть вида https://host:port";
            return false;
        }

        if (!IsSecureEnough(parsed))
        {
            error = "По открытому каналу сопряжение недоступно: нужен https " +
                    "(либо адрес на этой же машине — localhost)";
            return false;
        }

        // Хвостовой слэш режем один раз здесь: дальше пути клеятся конкатенацией.
        uri = new Uri(parsed.GetLeftPart(UriPartial.Authority));
        return true;
    }

    /// <summary>HTTPS — всегда; http — только на петле.</summary>
    public static bool IsSecureEnough(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps || uri.IsLoopback;
}
