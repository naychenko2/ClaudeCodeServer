using System.Text.Json;

namespace ClaudeHomeServer.Services.Llm.Claude;

// Хвостовой ридер главного транскрипта сессии (<flat-cwd>/<sessionId>.jsonl).
// Нужен для учёта завершения фоновых задач: CLI пишет <task-notification> в транскрипт,
// но в stdout при завершённом ходе может не транслировать — единственный надёжный сигнал
// «агент закончил» живёт в файле. Ватчер поллит хвост и отдаёт строковые user-сообщения
// с notification'ами в callback (ClaudeSession.HandleTaskNotification).
//
// Второе назначение — очередь ввода CLI. Уведомление о фоновой задаче и отчёт субагента
// (<agent-message>, SubagentHandback CLI 2.1.299+), пришедшие посреди хода, CLI ставит в
// очередь (queue-operation enqueue). Впитанное ходом снимается операцией remove; не
// впитанное CLI доставит ОТДЕЛЬНЫМ ходом-продолжением сразу после result — и этому ходу
// нужен живой stdin (permission-канал). Учёт таких элементов — HasQueuedDeliveries.
internal sealed class MainTranscriptTailer : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private readonly string _cwd;
    private readonly string _claudeSessionId;
    private readonly Action<string> _onNotification;
    private readonly CancellationTokenSource _cts = new();
    // Скан зовут и цикл поллинга, и поток ридера stdout (синхронный досмотр на result)
    private readonly object _scanGate = new();
    // Доставки основному агенту, поставленные в очередь CLI и ещё не впитанные ходом
    private readonly List<string> _queuedDeliveries = [];
    private string? _path;
    private long _offset;

    public MainTranscriptTailer(string cwd, string claudeSessionId, Action<string> onNotification)
    {
        _cwd = cwd;
        _claudeSessionId = claudeSessionId;
        _onNotification = onNotification;
    }

    // Для тестов: путь транскрипта известен заранее, поиск по корням не нужен
    internal MainTranscriptTailer(string transcriptPath, Action<string> onNotification)
        : this("", "", onNotification)
    {
        _path = transcriptPath;
    }

    // Есть ли в очереди CLI доставка основному агенту, которую он ещё не впитал. Досматривает
    // хвост файла синхронно: enqueue мог лечь за мгновение до result, поллинг его ещё не видел
    public bool HasQueuedDeliveries()
    {
        try { ScanAndDispatch(); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[TranscriptTailer] Досмотр очереди не удался: {ex.Message}");
        }
        lock (_scanGate) return _queuedDeliveries.Count > 0;
    }

    // Ход-продолжение начался (или CLI принял наш ход) — поставленное до него доставлено
    public void ClearQueuedDeliveries()
    {
        lock (_scanGate) _queuedDeliveries.Clear();
    }

    public void Start()
    {
        // Существующее содержимое — прошлые ходы, их notifications уже неактуальны:
        // начинаем с конца файла
        try
        {
            lock (_scanGate)
            {
                _path ??= ResolvePath();
                if (_path is not null) _offset = new FileInfo(_path).Length;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[TranscriptTailer] Инициализация не удалась: {ex.Message}");
        }

        _ = Task.Run(async () =>
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    // Транзиентная ошибка ФС (файл занят CLI/антивирусом) не должна убивать
                    // цикл насовсем: без tailer'а завершения фоновых задач перестают замечаться
                    // и прогон висит зомби до потолка BgLingerTimeout
                    try { ScanAndDispatch(); }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"[TranscriptTailer] Скан не удался, пропускаем тик: {ex.Message}");
                    }
                    await Task.Delay(PollInterval, _cts.Token);
                }
            }
            catch (OperationCanceledException) { /* штатная остановка */ }
            catch (ObjectDisposedException) { /* Dispose между Scan и Delay — штатная остановка */ }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[TranscriptTailer] Цикл поллинга упал: {ex.Message}");
            }
        });
    }

    // Скан под локом, колбэки — вне его: HandleTaskNotification берёт локи сессии
    private void ScanAndDispatch()
    {
        List<string> notifications;
        lock (_scanGate) notifications = Scan();
        foreach (var text in notifications) _onNotification(text);
    }

    private List<string> Scan()
    {
        List<string> notifications = [];
        _path ??= ResolvePath();
        if (_path is null) return notifications;

        long length;
        try { length = new FileInfo(_path).Length; }
        catch { return notifications; }
        if (length <= _offset) return notifications;

        using var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        fs.Seek(_offset, SeekOrigin.Begin);
        using var reader = new StreamReader(fs);
        var chunk = reader.ReadToEnd();

        // Продвигаемся только по целым строкам — хвост без \n дописывается CLI прямо сейчас
        var lastNewline = chunk.LastIndexOf('\n');
        if (lastNewline < 0) return notifications;
        _offset += System.Text.Encoding.UTF8.GetByteCount(chunk[..(lastNewline + 1)]);

        foreach (var line in chunk[..lastNewline].Split('\n'))
        {
            var queueOp = line.Contains("\"queue-operation\"", StringComparison.Ordinal);
            if (!queueOp && !line.Contains("task-notification", StringComparison.Ordinal)) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (!root.TryGetProperty("type", out var t)) continue;
                if (t.GetString() == "queue-operation")
                {
                    ApplyQueueOperation(_queuedDeliveries, root);
                    continue;
                }
                if (t.GetString() != "user") continue;
                if (!root.TryGetProperty("message", out var msg)
                    || !msg.TryGetProperty("content", out var content)
                    || content.ValueKind != JsonValueKind.String) continue;
                if (content.GetString() is { } text) notifications.Add(text);
            }
            catch (JsonException) { /* битая строка — норма для дописываемого файла */ }
        }
        return notifications;
    }

    // enqueue доставки — в учёт, remove — впитана ходом (вложением между вызовами инструментов).
    // dequeue не учитываем: он несёт пустой content, и CLI снимает им из очереди сразу пачку —
    // ровно в момент старта хода-продолжения, который и есть доставка (учёт чистит ClaudeSession).
    // Наши собственные сообщения тоже идут через очередь — их не считаем.
    internal static void ApplyQueueOperation(List<string> queue, JsonElement root)
    {
        if (!root.TryGetProperty("operation", out var op) || op.ValueKind != JsonValueKind.String) return;
        if (!root.TryGetProperty("content", out var c) || c.ValueKind != JsonValueKind.String) return;
        var content = c.GetString()!;
        if (!IsDeliveryToMainAgent(content)) return;
        switch (op.GetString())
        {
            case "enqueue": queue.Add(content); break;
            case "remove": queue.Remove(content); break;
        }
    }

    internal static bool IsDeliveryToMainAgent(string content) =>
        content.StartsWith("<task-notification>", StringComparison.Ordinal)
        || content.StartsWith("<agent-message", StringComparison.Ordinal);

    // Путь главного транскрипта — единый поиск с TranscriptProbe (те же корни и уплощение cwd)
    private string? ResolvePath() => TranscriptProbe.FindMainTranscript(_cwd, _claudeSessionId);

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
