using System.Threading.Channels;

namespace ClaudeHomeServer.Services.Diagnostics;

// Файловый лог инстанса: data/logs/server-YYYYMMDD.log, дневная ротация, удержание N дней.
// Боевой инстанс historically слепой вне консоли трея: полный stdout живёт в его server.log
// (шум SignalR dbug, десятки тысяч строк в день), а целевой диагностический канал — stderr —
// туда свален без структуры и ротации. Здесь stderr дублируется в отдельный файл рядом с
// connections.log: туда попадают все Warning/Error событий ILogger (консольный провайдер
// печатает их в stderr) и голые Console.Error.WriteLine — маркеры смерти процессов CLI из
// ClaudeSession («Процесс CLI завершился …», «Прогон умер при активном ходе» и т.п.).
//
// Секреты/PII: новых источников нет — пишется ровно тот поток, что уже идёт в консоль
// (и лог трея); код продукта секреты в stderr сознательно не печатает.
//
// Конфигурация (appsettings*.json):
//   Logging:File:Enabled    — true/false; не задан = авто: включён вне Development/Testing
//   Logging:File:RetainDays — сколько дневных файлов держать (дефолт 14)
//
// Уровень отдельных категорий настраивается штатной секцией Logging:LogLevel:* — она же
// управляет консолью, файл получает отфильтрованный ею stderr-поток как есть.
public sealed class FileLog : IDisposable
{
    private readonly string _dir;
    private readonly int _retainDays;
    private readonly Channel<string> _queue = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = true });
    private readonly Task _writerTask;
    private StreamWriter? _file;
    private string _fileDate = "";

    // internal для тестов (InternalsVisibleTo): reflection с primary-конструкторами
    // вложенного класса не умеет приводить аргументы, честная ссылка проще
    internal FileLog(string dir, int retainDays)
    {
        _dir = dir;
        _retainDays = Math.Max(1, retainDays);
        _writerTask = Task.Run(WriteLoopAsync);
    }

    /// <summary>
    /// Включить файловый лог по конфигу и окружению. Вызывать в Program.cs сразу после
    /// CreateBuilder — чем раньше, тем меньше ранних stderr-строк пройдёт мимо файла.
    /// </summary>
    public static void Attach(IConfiguration config, IHostEnvironment env)
    {
        var enabled = config.GetValue<bool?>("Logging:File:Enabled")
            ?? (!env.IsDevelopment() && !env.IsEnvironment("Testing"));
        if (!enabled) return;

        var dataDir = Path.GetDirectoryName(
            config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json"))
            ?? Path.Combine(AppContext.BaseDirectory, "data");
        var log = new FileLog(
            Path.Combine(dataDir, "logs"),
            config.GetValue("Logging:File:RetainDays", 14));

        // Зеркало stderr поверх текущего Console.Error: строка уходит и в консоль как есть,
        // и в файл с UTC-таймстемпом. Обёртка таймстемпов консоли (TimestampedConsoleWriter)
        // остаётся ниже — свой префикс в файл добавляем здесь, двойного таймстемпа нет.
        Console.SetError(new MirrorTextWriter(Console.Error, log));
    }

    private void Enqueue(string line)
    {
        var stamped = $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}Z {line}";
        _queue.Writer.TryWrite(stamped);
    }

    // Один фоновый писатель: лог-вызовы не блокируются на IO, пачки строк (краш CLI сыплет
    // stderr) пишутся подряд без межстрочных разрывов файла
    private async Task WriteLoopAsync()
    {
        await foreach (var line in _queue.Reader.ReadAllAsync())
        {
            try
            {
                await EnsureFileAsync();
                await _file!.WriteLineAsync(line);
            }
            catch (Exception ex)
            {
                // Файл лога не имеет права утекать в stderr — это и есть его источник,
                // получилась бы рекурсия зеркало→файл→ошибка→зеркало
                System.Diagnostics.Debug.WriteLine($"[FileLog] запись не удалась: {ex.Message}");
            }
        }
        if (_file is not null) await _file.DisposeAsync();
        _file = null;
    }

    private async Task EnsureFileAsync()
    {
        var today = DateTime.UtcNow.ToString("yyyyMMdd");
        if (_file is not null && today == _fileDate) return;

        if (_file is not null) await _file.DisposeAsync();
        Directory.CreateDirectory(_dir);
        _file = new StreamWriter(new FileStream(
            Path.Combine(_dir, $"server-{today}.log"),
            FileMode.Append, FileAccess.Write, FileShare.ReadWrite),
            new System.Text.UTF8Encoding(false)) { AutoFlush = true };
        _fileDate = today;
        DeleteStaleFiles();
    }

    // Удержание: дневные файлы старше RetainDays уходят. Дату берём из имени, а не из mtime —
    // копирование/восстановление каталога ломает mtime, а имя стабильно.
    private void DeleteStaleFiles()
    {
        try
        {
            foreach (var path in Directory.EnumerateFiles(_dir, "server-*.log"))
            {
                var stamp = Path.GetFileName(path)["server-".Length..][..^".log".Length];
                if (!DateTime.TryParseExact(stamp, "yyyyMMdd", null,
                        System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                        out var fileDate))
                    continue;
                if ((DateTime.UtcNow - fileDate).TotalDays > _retainDays)
                {
                    try { File.Delete(path); }
                    catch { /* занят/нет прав — останется до следующей чистки */ }
                }
            }
        }
        catch { /* чистка вспомогательная */ }
    }

    public void Dispose()
    {
        _queue.Writer.TryComplete();
        try { _writerTask.Wait(TimeSpan.FromSeconds(3)); } catch { /* не дождались — не блокируем остановку */ }
    }

    // Декоратор stderr: писать в консоль как есть (включая нижележащие обёртки), дублируя
    // строку в файловый лог. ВсеTextWriter-методы, которыми пользуются ILogger и голый
    // Console.Error.WriteLine, сводятся к Write(char[], int, int)/WriteLine(string) —
    // покрываем оба, остальные через базовую кодировку TextWriter не идут в практике.
    internal sealed class MirrorTextWriter(TextWriter inner, FileLog sink) : TextWriter
    {
        public override System.Text.Encoding Encoding => inner.Encoding;

        public override void Write(char[] buffer, int index, int count)
        {
            inner.Write(buffer, index, count);
            FlushToSink(new string(buffer, index, count));
        }

        public override void WriteLine(string? value)
        {
            inner.WriteLine(value);
            FlushToSink((value ?? "").TrimEnd('\n', '\r'));
        }

        private void FlushToSink(string line)
        {
            if (line.Length > 0) sink.Enqueue(line);
        }
    }
}
