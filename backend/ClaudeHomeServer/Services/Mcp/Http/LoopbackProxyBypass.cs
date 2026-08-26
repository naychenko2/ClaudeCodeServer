namespace ClaudeHomeServer.Services.Mcp.Http;

/// <summary>
/// Обход прокси для локальных адресов бэкенда (ADR-012). До HTTP-транспорта в бэкенд ходили
/// node-прокси со своим окружением, теперь по тому же адресу идёт САМ claude CLI: его http-клиент
/// уважает HTTP_PROXY, и запрос к <c>http://localhost:5000/mcp/...</c> уедет в прокси, если адрес
/// не покрыт NO_PROXY. У инструмента это выглядит как 503 CLIENT_HTTP_NOT_IMPLEMENTED — то есть
/// сервер молча исчезает у модели.
///
/// В песочнице NO_PROXY ставится при создании контейнера (SandboxManager.BuildRunArgs), у
/// local-владельцев переменная наследуется от системы, и гарантии нет никакой. Поэтому значение
/// задаём на каждый ход сами — унаследованное не затираем, а дополняем.
/// </summary>
public static class LoopbackProxyBypass
{
    /// <summary>
    /// Базовые адреса, по которым CLI видит бэкенд: loopback хоста и мост из контейнера.
    /// Перечислены все формы: сопоставление в NO_PROXY идёт по ИМЕНИ, и «localhost»
    /// не покрывает «127.0.0.1».
    /// </summary>
    public static readonly string[] Hosts = ["localhost", "127.0.0.1", "::1", "host.docker.internal"];

    /// <summary>
    /// Значение NO_PROXY для хода: унаследованное окружение, базовые локальные адреса и хосты
    /// фактически резолвленных URL (адрес бэкенда может оказаться именем машины, а не loopback).
    /// Унаследованное сохраняется первым и целиком — HTTP_PROXY на машине бывает единственным
    /// маршрутом до провайдеров, затирать его исключения нельзя. Уже перечисленный адрес не
    /// задваивается, порядок фиксирован: значение входит в сигнатуру запуска CLI и обязано
    /// быть детерминированным, иначе процесс перезапускается между ходами.
    /// </summary>
    public static string Merge(string? inherited, params string?[] urls)
    {
        var items = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string value) { if (seen.Add(value)) items.Add(value); }

        foreach (var part in (inherited ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            Add(part);
        foreach (var host in Hosts) Add(host);
        foreach (var url in urls)
        {
            if (string.IsNullOrWhiteSpace(url)) continue;
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Host is { Length: > 0 } host)
                Add(host);
        }
        return string.Join(",", items);
    }
}
