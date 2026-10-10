using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;

namespace ClaudeHomeServer.Services.Images.LocalMedia;

// Настоящие шаги идущего прогона ComfyUI: Step из Total у текущей ноды с прогрессом (семплер,
// тайловый декод). Stage — какая это по счёту нода с прогрессом в прогоне: у видео семплеров
// несколько, и счёт шагов у каждого свой
public sealed record ComfyStepProgress(int Step, int Total, int Stage)
{
    // Честный процент текущей ноды; потолок 99 — «готово» говорит только история
    public int Percent => (int)Math.Floor(Math.Min(99.0, Step * 100.0 / Total));
}

// Шаги прогонов по событиям WebSocket ComfyUI (progress / executing / execution_*). Держит
// только наши незавершённые прогоны: события чужих прогонов стенда не копятся. Нет записи —
// значит шагов не знаем (WebSocket не подключён, оборвался или нода ещё не дала прогресса),
// и прогресс карточки берётся из оценки по ETA.
// Сторож: запись, по которой progress молчит дольше порога, не отдаётся — сокет бывает жив, но
// молчит (ComfyUI забыл его без Close, полуоткрытый TCP), и замёрзшие шаги не должны выдаваться
// за точные. Порог адаптивный — не меньше StaleAfter и не меньше трёх последних интервалов
// между progress: шаг видеосемплера идёт 30–100 с, и фиксированный порог гасил бы полосу на
// каждом шаге. Протухшая запись не удаляется (снимают её только конец прогона и Reset): иначе
// следующий progress начал бы счёт этапов заново, и второй семплер выдавался бы за первый
public sealed class ComfyProgressTracker(Func<string, bool> isOurs, TimeProvider? time = null)
{
    public static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(15);

    // Interval — между двумя последними progress одной ноды; на смене ноды переносится с
    // прошлой: темп шагов соседних семплеров один, а первый шаг новой ноды без него протух бы
    private sealed record Entry(ComfyStepProgress Steps, string Node, DateTimeOffset At, TimeSpan Interval)
    {
        public TimeSpan StaleAfter => Interval * 3 > ComfyProgressTracker.StaleAfter ? Interval * 3 : ComfyProgressTracker.StaleAfter;
    }

    private readonly ConcurrentDictionary<string, Entry> _prompts = new(StringComparer.Ordinal);
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public ComfyProgressTracker(LocalMediaJobStore store, TimeProvider? time = null) : this(store.IsActivePrompt, time) { }

    // Сообщение обработано или знание сброшено — тесты ждут это событие, а не спят
    internal event Action? Changed;

    public ComfyStepProgress? Get(string promptId)
    {
        if (!_prompts.TryGetValue(promptId, out var entry)) return null;
        return _time.GetUtcNow() - entry.At <= entry.StaleAfter ? entry.Steps : null;
    }

    // Связь потеряна: что знали — устарело, до нового события честнее оценка
    public void Reset()
    {
        _prompts.Clear();
        Changed?.Invoke();
    }

    public void Handle(string text)
    {
        // Непонятное молча пропускается: формат событий — чужой, и его сюрпризы (дубли ключей,
        // неожиданные типы) не должны рвать сессию сокета вместе со всеми шагами
        try
        {
            Apply(text);
        }
        catch (Exception)
        {
            // сообщение пропущено
        }
        Changed?.Invoke();
    }

    // Одно текстовое сообщение WebSocket
    private void Apply(string text)
    {
        if (JsonNode.Parse(text) is not JsonObject message) return;
        if (message["data"] is not JsonObject data || Str(data["prompt_id"]) is not { Length: > 0 } promptId) return;

        switch (Str(message["type"]))
        {
            case "progress":
                if (Int(data["value"]) is not { } value || Int(data["max"]) is not { } max || max <= 0 || value < 0) return;
                if (!isOurs(promptId)) return;
                var node = Str(data["node"]) ?? "";
                var at = _time.GetUtcNow();
                _prompts.AddOrUpdate(promptId,
                    _ => new Entry(new ComfyStepProgress(Math.Min(value, max), max, 1), node, at, TimeSpan.Zero),
                    (_, prev) => prev.Node == node
                        ? new Entry(new ComfyStepProgress(Math.Min(value, max), max, prev.Steps.Stage), node, at, at - prev.At)
                        : new Entry(new ComfyStepProgress(Math.Min(value, max), max, prev.Steps.Stage + 1), node, at, prev.Interval));
                break;
            // executing с node = null — прогон закончен (так ComfyUI сообщает конец)
            case "executing" when data["node"] is null:
            case "execution_success" or "execution_error" or "execution_interrupted":
                _prompts.TryRemove(promptId, out _);
                break;
        }

        static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

        static int? Int(JsonNode? n) =>
            n is not JsonValue v ? null
            : v.TryGetValue<int>(out var i) ? i
            : v.TryGetValue<double>(out var d) && double.IsFinite(d) ? (int)d
            : null;
    }
}

// Слушатель WebSocket ComfyUI: шаги семплера в ComfyProgressTracker. Опциональная
// зависимость, как и HTTP-клиент ComfyUI: подключается, только пока есть незавершённые наши
// задачи и тумблер LocalMedia:Enabled включён; недоступность и обрывы — Debug в лог и тихий
// повтор с растущей паузой, без Error-спама. Опрос local_jobs_wait от слушателя не зависит:
// без него карточка просто остаётся на оценке по ETA.
// clientId — тот же, с которым ComfyClient ставит граф: события progress ComfyUI шлёт
// только сокету этого клиента. Он свой у каждого процесса, поэтому дев и бой на одном
// ComfyUI сокеты друг друга не вытесняют; замолчавший сокет ловит сторож трекера
public sealed class ComfyProgressListener(
    ComfyProgressTracker tracker,
    LocalMediaJobStore store,
    IConfiguration config,
    ILogger<ComfyProgressListener> log) : BackgroundService
{
    // Подключение к сокету; тесты подменяют его фейковым сервером
    internal Func<Uri, CancellationToken, Task<WebSocket>> Connect { get; set; } = ConnectAsync;

    // Пауза, пока подключаться не к чему (нет задач, тумблер выключен), и проверка, не пора
    // ли закрыть сокет, когда задачи кончились
    internal TimeSpan IdleDelay { get; init; } = TimeSpan.FromSeconds(2);
    internal TimeSpan MinRetryDelay { get; init; } = TimeSpan.FromSeconds(1);
    internal TimeSpan MaxRetryDelay { get; init; } = TimeSpan.FromSeconds(30);

    private const int MaxMessageBytes = 1024 * 1024;

    public static Uri SocketUri(string comfyUrl)
    {
        var builder = new UriBuilder(comfyUrl.TrimEnd('/') + "/ws");
        builder.Scheme = builder.Scheme == Uri.UriSchemeHttps ? "wss" : "ws";
        builder.Query = "clientId=" + Uri.EscapeDataString(ComfyClient.ClientId);
        return builder.Uri;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var retry = MinRetryDelay;
        while (!stoppingToken.IsCancellationRequested)
        {
            var options = LocalMediaOptions.Read(config);
            if (!options.Enabled || !store.HasActive())
            {
                if (!await DelayAsync(IdleDelay, stoppingToken)) return;
                continue;
            }

            var heard = false;
            try
            {
                heard = await ListenOnceAsync(SocketUri(options.ComfyUrl), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Опциональная зависимость: выключенный ComfyUI — штатное состояние, не ошибка
                log.LogDebug("Шаги ComfyUI по WebSocket недоступны: {Error}", ex.Message);
            }
            finally
            {
                tracker.Reset();
            }

            // Связь жила и что-то принесла — пауза снова короткая; иначе растёт до потолка
            if (heard) retry = MinRetryDelay;
            if (!await DelayAsync(retry, stoppingToken)) return;
            if (!heard) retry = TimeSpan.FromTicks(Math.Min(retry.Ticks * 2, MaxRetryDelay.Ticks));
        }
    }

    // Одна сессия сокета: до обрыва, закрытия сервером или конца наших задач.
    // true — пришло хотя бы одно сообщение
    internal async Task<bool> ListenOnceAsync(Uri uri, CancellationToken stoppingToken)
    {
        using var session = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        using var socket = await Connect(uri, session.Token);
        log.LogDebug("Слушаю шаги ComfyUI: {Uri}", uri);

        // Задачи кончились — сокет закрываем: держать связь с GPU-стендом не к чему
        var watch = WatchActiveAsync(session);
        var heard = false;
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        try
        {
            while (!session.IsCancellationRequested)
            {
                message.SetLength(0);
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(buffer, session.Token);
                    if (result.MessageType == WebSocketMessageType.Close) return heard;
                    // Двоичные кадры — превью latent2rgb: не нужны, читаются и выбрасываются
                    if (result.MessageType == WebSocketMessageType.Text && message.Length + result.Count <= MaxMessageBytes)
                        message.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                heard = true;
                if (result.MessageType == WebSocketMessageType.Text && message.Length > 0)
                    tracker.Handle(Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length));
            }
            return heard;
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
            return heard;
        }
        catch (WebSocketException ex)
        {
            // Обрыв посреди сессии (ComfyUI перезапущен, сеть): переподключится цикл выше
            log.LogDebug("Сокет шагов ComfyUI оборвался: {Error}", ex.Message);
            return heard;
        }
        finally
        {
            await session.CancelAsync();
            await watch;
        }
    }

    private async Task WatchActiveAsync(CancellationTokenSource session)
    {
        while (await DelayAsync(IdleDelay, session.Token))
            if (!store.HasActive() || !LocalMediaOptions.IsEnabled(config))
            {
                await session.CancelAsync();
                return;
            }
    }

    private static async Task<WebSocket> ConnectAsync(Uri uri, CancellationToken ct)
    {
        var socket = new ClientWebSocket();
        // ComfyUI — наш сервис на loopback: системный прокси его не обслуживает
        socket.Options.Proxy = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            await socket.ConnectAsync(uri, timeout.Token);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static async Task<bool> DelayAsync(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, ct);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
