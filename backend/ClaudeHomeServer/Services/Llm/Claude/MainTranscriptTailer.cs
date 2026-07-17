using System.Text.Json;

namespace ClaudeHomeServer.Services.Llm.Claude;

// Хвостовой ридер главного транскрипта сессии (<flat-cwd>/<sessionId>.jsonl).
// Нужен для учёта завершения фоновых задач: CLI пишет <task-notification> в транскрипт,
// но в stdout при завершённом ходе может не транслировать — единственный надёжный сигнал
// «агент закончил» живёт в файле. Ватчер поллит хвост и отдаёт строковые user-сообщения
// с notification'ами в callback (ClaudeSession.HandleTaskNotification).
internal sealed class MainTranscriptTailer : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private readonly string _cwd;
    private readonly string _claudeSessionId;
    private readonly Action<string> _onNotification;
    private readonly CancellationTokenSource _cts = new();
    private string? _path;
    private long _offset;

    public MainTranscriptTailer(string cwd, string claudeSessionId, Action<string> onNotification)
    {
        _cwd = cwd;
        _claudeSessionId = claudeSessionId;
        _onNotification = onNotification;
    }

    public void Start()
    {
        // Существующее содержимое — прошлые ходы, их notifications уже неактуальны:
        // начинаем с конца файла
        try
        {
            _path = ResolvePath();
            if (_path is not null) _offset = new FileInfo(_path).Length;
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
                    Scan();
                    await Task.Delay(PollInterval, _cts.Token);
                }
            }
            catch (OperationCanceledException) { /* штатная остановка */ }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[TranscriptTailer] Цикл поллинга упал: {ex.Message}");
            }
        });
    }

    private void Scan()
    {
        _path ??= ResolvePath();
        if (_path is null) return;

        long length;
        try { length = new FileInfo(_path).Length; }
        catch { return; }
        if (length <= _offset) return;

        using var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        fs.Seek(_offset, SeekOrigin.Begin);
        using var reader = new StreamReader(fs);
        var chunk = reader.ReadToEnd();

        // Продвигаемся только по целым строкам — хвост без \n дописывается CLI прямо сейчас
        var lastNewline = chunk.LastIndexOf('\n');
        if (lastNewline < 0) return;
        _offset += System.Text.Encoding.UTF8.GetByteCount(chunk[..(lastNewline + 1)]);

        foreach (var line in chunk[..lastNewline].Split('\n'))
        {
            if (!line.Contains("task-notification", StringComparison.Ordinal)) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (!root.TryGetProperty("type", out var t) || t.GetString() != "user") continue;
                if (!root.TryGetProperty("message", out var msg)
                    || !msg.TryGetProperty("content", out var content)
                    || content.ValueKind != JsonValueKind.String) continue;
                if (content.GetString() is { } text) _onNotification(text);
            }
            catch (JsonException) { /* битая строка — норма для дописываемого файла */ }
        }
    }

    // Путь главного транскрипта — те же корни и уплощение cwd, что у сабагент-ватчера
    private string? ResolvePath()
    {
        var flat = string.Concat(_cwd.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-'));
        foreach (var root in WorkflowAgentParser.AllowedRoots)
        {
            if (!Directory.Exists(root)) continue;

            var byConvention = Path.Combine(root, flat, _claudeSessionId + ".jsonl");
            if (File.Exists(byConvention)) return byConvention;

            foreach (var projDir in Directory.GetDirectories(root))
            {
                var candidate = Path.Combine(projDir, _claudeSessionId + ".jsonl");
                if (File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
