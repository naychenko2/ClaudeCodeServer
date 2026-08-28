using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Execution;

namespace ClaudeHomeServer.Services.Mcp;

/// <summary>Итог разовой пробы сервера. Status — нормализованный (см. McpServerStatuses).</summary>
public sealed record McpProbeResult(
    bool Ok, string Status, string? ServerName, int ToolCount,
    IReadOnlyList<string> ToolNames, string? Error);

/// <summary>
/// Разовая проба MCP-сервера «по кнопке»: поднимаем его так же, как это сделал бы ход,
/// проходим рукопожатие и спрашиваем список инструментов. Фонового поллинга у фичи нет —
/// статус берётся из system/init ходов (<see cref="McpStatusStore"/>), а проба нужна, чтобы
/// проверить свежую запись, не начиная чат.
///
/// stdio поднимается через <see cref="ILauncherFactory.ForOwner"/> — у container-пользователя
/// процесс должен родиться в песочнице, иначе проба проверяла бы не ту среду.
/// </summary>
public class McpProbeService(
    McpSecretStore secrets,
    ILauncherFactory launchers,
    McpStatusStore statusStore,
    IHttpClientFactory httpFactory,
    IConfiguration config,
    ILogger<McpProbeService> log,
    // Опционально (в тестах не передаётся): обновление истекающего токена OAuth перед пробой
    McpOAuthService? oauth = null)
{
    /// <summary>Имя тихого HTTP-клиента (чужой сервер лежит штатно — не засыпаем консоль).</summary>
    public const string HttpClientName = "mcp-probe";

    private readonly TimeSpan _timeout = TimeSpan.FromSeconds(
        Math.Clamp(config.GetValue("Mcp:ProbeTimeoutSeconds", 10), 1, 120));

    // Потолок одновременных проб: «Проверить все» на два десятка stdio-серверов иначе
    // форкнет два десятка процессов разом
    private readonly SemaphoreSlim _global = new(
        Math.Clamp(config.GetValue("Mcp:MaxParallelProbes", 4), 1, 16));

    // Одна проба на (владелец, сервер): повторный клик по кнопке не должен плодить процессы
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _perServer = new();

    // Незаполненная шаблонная переменная в адресе ({COMPANY} из декларации реестра)
    private static readonly Regex UrlTemplatePattern = new(@"\{[A-Za-z0-9_.-]+\}", RegexOptions.Compiled);

    /// <summary>
    /// Пробует сервер и записывает наблюдение в стор. Исключений не бросает: любая беда —
    /// это результат с Ok=false и текстом для человека.
    /// </summary>
    public async Task<McpProbeResult> ProbeAsync(string ownerId, McpServerRecord record,
        CancellationToken ct = default)
    {
        var gate = _perServer.GetOrAdd(ownerId + "\n" + record.Key, _ => new SemaphoreSlim(1, 1));
        // Отказ ДО пробы наблюдением не считается: писать в стор «не работает» из-за того,
        // что человек кликнул дважды, значит соврать про сервер
        if (!await gate.WaitAsync(0, ct))
            return Busy("Проверка этого сервера уже идёт");
        try
        {
            // Ждём слот недолго: очередь из проб человеку бесполезна — лучше честное «занято»
            if (!await _global.WaitAsync(_timeout, ct))
                return Busy("Слишком много одновременных проверок — повтори позже");
            try
            {
                var result = record.Transport == McpTransport.Stdio
                    ? await ProbeStdioAsync(ownerId, record, ct)
                    : await ProbeHttpAsync(ownerId, record, ct);
                statusStore.RecordProbe(ownerId, record.Key, result.Status, result.Error);
                return result;
            }
            finally { _global.Release(); }
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Проба MCP-сервера «{Key}» сорвалась", record.Key);
            statusStore.RecordProbe(ownerId, record.Key, McpServerStatuses.Failed, ex.Message);
            return Failed(ex.Message);
        }
        finally { gate.Release(); }
    }

    // ── stdio ────────────────────────────────────────────────────────────────────────

    private async Task<McpProbeResult> ProbeStdioAsync(string ownerId, McpServerRecord record,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(record.Command))
            return Failed("У записи не задана команда запуска");

        var launcher = launchers.ForOwner(ownerId);
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        string command;
        List<string> args;
        try
        {
            foreach (var (name, value) in record.Env ?? [])
                env[name] = AdaptValue(launcher, secrets.Resolve(ownerId, value) ?? "");
            command = MapPath(launcher, record.Command!);
            args = (record.Args ?? []).Select(a => MapPath(launcher, a)).ToList();
        }
        catch (InvalidOperationException ex) { return Failed(ex.Message); }

        var turnId = "probe-" + Guid.NewGuid().ToString("N")[..8];
        Process process;
        try
        {
            process = launcher.Start(new ProcessSpec
            {
                FileName = command,
                Args = args,
                Env = env,
                RedirectStdin = true,
                StdioEncoding = new UTF8Encoding(false),
                // Проба живёт секунды и убивается сама — в реестре долгоживущих процессов
                // ей делать нечего (иначе KillAll чужого хоста расстреливал бы её на лету)
                Track = false,
                TurnId = turnId,
            });
        }
        catch (Exception ex) { return Failed("Не удалось запустить сервер: " + ex.Message); }

        // stderr вычитываем сразу и целиком: иначе его буфер заполнится и процесс встанет,
        // а нам этот текст нужен — он единственный объясняет падение сервера
        var stderrTask = process.StandardError.ReadToEndAsync();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_timeout);
        try
        {
            var stdin = process.StandardInput;
            await stdin.WriteLineAsync(McpProbeProtocol.InitializeRequest());
            await stdin.FlushAsync(cts.Token);

            var (initResult, initError) = await ReadFrameAsync(
                process, stderrTask, McpProbeProtocol.InitializeId, cts.Token);
            if (initError is not null) return Failed(initError);

            await stdin.WriteLineAsync(McpProbeProtocol.InitializedNotification());
            await stdin.WriteLineAsync(McpProbeProtocol.ToolsListRequest());
            await stdin.FlushAsync(cts.Token);

            var (toolsResult, toolsError) = await ReadFrameAsync(
                process, stderrTask, McpProbeProtocol.ToolsListId, cts.Token);
            if (toolsError is not null) return Failed(toolsError);

            var names = McpProbeProtocol.ToolNamesFrom(toolsResult);
            return new McpProbeResult(true, McpServerStatuses.Connected,
                McpProbeProtocol.ServerNameFrom(initResult), names.Count, names, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Failed($"Сервер не ответил за {_timeout.TotalSeconds:0} с");
        }
        catch (Exception ex) { return Failed(ex.Message); }
        finally
        {
            // Процесс пробы живёт ровно до этой строки — ни при успехе, ни при таймауте
            // он не имеет права остаться висеть (в песочнице добиваем по TurnId)
            try { launcher.Kill(process, turnId); } catch { /* уже умер */ }
            process.Dispose();
        }
    }

    // Ответ с нужным id из stdout. Чужие строки (логи сервера, уведомления) пропускаем;
    // закрытый поток означает, что процесс умер — объясняем это концом его stderr.
    private static async Task<(JsonElement? Result, string? Error)> ReadFrameAsync(
        Process process, Task<string> stderrTask, int expectedId, CancellationToken ct)
    {
        while (true)
        {
            var line = await process.StandardOutput.ReadLineAsync(ct);
            if (line is null)
            {
                var stderr = await StderrTailAsync(stderrTask);
                return (null, stderr.Length > 0
                    ? "Сервер завершился: " + stderr
                    : "Сервер закрыл поток, не ответив на рукопожатие");
            }
            if (!McpProbeProtocol.TryParseFrame(line, expectedId, out var frame)) continue;
            return (frame.Result, frame.Error);
        }
    }

    // Хвост stderr упавшего процесса. Ждём недолго: поток мог остаться открытым у потомка
    private static async Task<string> StderrTailAsync(Task<string> stderrTask)
    {
        try
        {
            var text = (await stderrTask.WaitAsync(TimeSpan.FromSeconds(2))).Trim();
            return text.Length > 400 ? text[^400..] : text;
        }
        catch { return ""; }
    }

    // Хостовый путь → путь целевой среды (в песочнице). Правило то же, что у сборки конфига
    // хода (ClaudeSession.AdaptServerForRuntime): голое имя команды («node», «npx») отдаём
    // среде как есть, абсолютный Windows-путь переводим, непереводимый — ошибка пробы.
    private static string MapPath(IProcessLauncher launcher, string value)
    {
        if (!launcher.IsSandboxed) return value;
        if (value is not { Length: > 2 } || !char.IsLetter(value[0]) || value[1] != ':') return value;
        try { return launcher.Paths.ToRuntime(value); }
        catch (InvalidOperationException)
        {
            throw new InvalidOperationException($"Путь {value} недоступен в песочнице");
        }
    }

    // loopback хоста в значении env недостижим из песочницы — тот же перевод, что в конфиге хода
    private static string AdaptValue(IProcessLauncher launcher, string value) =>
        !launcher.IsSandboxed
            ? value
            : value.Replace("://localhost", "://host.docker.internal", StringComparison.OrdinalIgnoreCase)
                .Replace("://127.0.0.1", "://host.docker.internal", StringComparison.OrdinalIgnoreCase);

    // ── http / sse ───────────────────────────────────────────────────────────────────

    private async Task<McpProbeResult> ProbeHttpAsync(string ownerId, McpServerRecord original,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(original.Url))
            return Failed("У записи не задан адрес сервера");

        // SSRF-гейт для каталожной записи (адрес объявил реестр, а не человек; ручные записи
        // не трогаем — http://localhost:3000/mcp для них штатный сценарий). Снимается, когда
        // Url разошёлся с импортированным: адрес тогда правил человек. Фильтр от объявленного
        // приватного адреса, а не защита от активного обхода (резолв гейта и соединение через
        // egress-прокси видят разные адреса) — митигация живёт в выключенности записи
        if (original.CatalogRef is { } catalogRef
            && string.Equals(original.Url, catalogRef.Url, StringComparison.Ordinal))
        {
            if (UrlTemplatePattern.IsMatch(original.Url))
                return Failed("В адресе не заполнены переменные — подставьте значения в настройках сервера");
            if (Uri.TryCreate(original.Url, UriKind.Absolute, out var imported)
                && await SsrfGuard.CheckAsync(imported, ct) != SsrfGuard.AddressCheck.Public)
                return Failed("Адрес сервера из каталога указывает на частную сеть — подключить нельзя");
        }

        // Тот же шаг, что и перед ходом: истекающий токен OAuth обновляем, провал — «нужен вход».
        // Иначе проба говорила бы «сервер не пускает» про запись, которую ход бы починил сам
        var record = original.Auth.Kind == McpAuthKind.OAuth2 && oauth is not null
            ? await oauth.EnsureFreshAsync(ownerId, original, ct)
            : original;
        if (record is null)
            return new McpProbeResult(false, McpServerStatuses.NeedsAuth, null, 0, [],
                "Нужен вход: токен истёк и не обновился");

        var headers = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in record.Headers ?? [])
            headers[name] = secrets.Resolve(ownerId, value) ?? "";
        // Секрет потерян — это «нужен вход», а не поломка сервера: чинить надо запись
        if (!McpAuthHeaders.TryApply(record, headers, r => secrets.Resolve(ownerId, r)))
            return new McpProbeResult(false, McpServerStatuses.NeedsAuth, null, 0, [],
                "Не найдено значение ключа или токена — задай его в настройках сервера");

        var client = httpFactory.CreateClient(HttpClientName);
        client.Timeout = _timeout;

        var init = await SendAsync(client, record.Url!, headers, sessionId: null,
            McpProbeProtocol.InitializeRequest(), McpProbeProtocol.InitializeId, ct);
        if (init.Error is not null)
            return new McpProbeResult(false, init.Status, null, 0, [], init.Error);

        // Уведомление о готовности ответа не требует — сбой этого шага не приговор
        await SendAsync(client, record.Url!, headers, init.SessionId,
            McpProbeProtocol.InitializedNotification(), expectedId: null, ct);

        var tools = await SendAsync(client, record.Url!, headers, init.SessionId,
            McpProbeProtocol.ToolsListRequest(), McpProbeProtocol.ToolsListId, ct);
        if (tools.Error is not null)
            return new McpProbeResult(false, tools.Status, null, 0, [], tools.Error);

        var names = McpProbeProtocol.ToolNamesFrom(tools.Result);
        return new McpProbeResult(true, McpServerStatuses.Connected,
            McpProbeProtocol.ServerNameFrom(init.Result), names.Count, names, null);
    }

    private sealed record HttpStep(JsonElement? Result, string? SessionId, string? Error, string Status);

    // Один запрос JSON-RPC. expectedId=null — уведомление (тела не разбираем).
    private async Task<HttpStep> SendAsync(
        HttpClient client, string url, Dictionary<string, string> headers, string? sessionId,
        string payload, int? expectedId, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
        foreach (var (name, value) in headers)
            request.Headers.TryAddWithoutValidation(name, value);
        // Streamable HTTP держит сессию заголовком — без него сервер отвергнет tools/list
        if (sessionId is { Length: > 0 }) request.Headers.TryAddWithoutValidation("Mcp-Session-Id", sessionId);

        HttpResponseMessage response;
        try { response = await client.SendAsync(request, ct); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return new HttpStep(null, sessionId,
                $"Сервер не ответил за {_timeout.TotalSeconds:0} с", McpServerStatuses.Failed);
        }
        catch (HttpRequestException ex)
        {
            return new HttpStep(null, sessionId,
                "Не удалось соединиться: " + ex.Message, McpServerStatuses.Failed);
        }

        using (response)
        {
            var httpStatus = McpProbeProtocol.StatusFromHttp((int)response.StatusCode);
            var newSessionId = response.Headers.TryGetValues("Mcp-Session-Id", out var ids)
                ? ids.FirstOrDefault() ?? sessionId : sessionId;
            if (!response.IsSuccessStatusCode)
                return new HttpStep(null, newSessionId,
                    httpStatus == McpServerStatuses.NeedsAuth
                        ? "Сервер требует авторизации"
                        : $"Сервер ответил {(int)response.StatusCode}",
                    httpStatus);

            if (expectedId is null) return new HttpStep(null, newSessionId, null, httpStatus);

            var body = await response.Content.ReadAsStringAsync(ct);
            foreach (var frame in McpProbeProtocol.Frames(body))
            {
                if (!McpProbeProtocol.TryParseFrame(frame, expectedId.Value, out var parsed)) continue;
                return parsed.Error is { } rpcError
                    ? new HttpStep(null, newSessionId, rpcError, McpServerStatuses.Failed)
                    : new HttpStep(parsed.Result, newSessionId, null, McpServerStatuses.Connected);
            }
            return new HttpStep(null, newSessionId, "Ответ сервера не разобран", McpServerStatuses.Failed);
        }
    }

    private static McpProbeResult Failed(string error) =>
        new(false, McpServerStatuses.Failed, null, 0, [], error);

    // Отказ не от сервера, а от нас самих: статус сервера при этом неизвестен
    private static McpProbeResult Busy(string error) =>
        new(false, McpServerStatuses.Unknown, null, 0, [], error);
}
