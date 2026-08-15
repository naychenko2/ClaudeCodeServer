using ClaudeHomeServer.Services.Diagnostics;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

// Файловый лог инстанса (data/logs/server-YYYYMMDD.log): stderr-зеркало — единственный
// диагностический канал боевого инстанса при обрывах ходов (инцидент 15.08.2026 — логов
// прод-бэкенда не оказалось вовсе). Проверяются фактическая запись и удержание старых
// дневных файлов; Attach(IConfiguration) не тестируется — живой Console.SetError в
// параллельных тестах затронул бы весь рантайм. Конструкторы internal (InternalsVisibleTo).
public class FileLogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ccs-filelog-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* временная — уберёт ОС */ }
    }

    private string LogDir => Path.Combine(_dir, "logs");

    // Зеркальная запись через обёртку stderr: строка попадает в файл с UTC-таймстемпом,
    // нижележащий writer получает её как есть (консоль не искажается)
    [Fact]
    public async Task MirrorWriter_ПишетСтрокуВФайлСКлеймомИПропускаетВКонсоль()
    {
        using var log = new FileLog(LogDir, 14);
        var forwarded = new List<string>();
        using var mirror = new FileLog.MirrorTextWriter(new CollectingWriter(forwarded), log);

        mirror.WriteLine("[ClaudeSession] Прогон умер при активном ходе");

        var lines = await ReadTodayLinesAsync();
        lines.Should().ContainSingle(l => l.EndsWith("[ClaudeSession] Прогон умер при активном ходе"));
        lines[0].Should().MatchRegex(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}Z ",
            "каждая строка файла несёт UTC-таймстемп");
        forwarded.Should().Contain("[ClaudeSession] Прогон умер при активном ходе");
    }

    // Удержание: дневные файлы старше RetainDays чистятся при ротации (открытии нового файла),
    // свежие остаются
    [Fact]
    public async Task Ротация_УбираетФайлыСтаршеRetainDays()
    {
        using var log = new FileLog(LogDir, 14);
        Directory.CreateDirectory(LogDir);
        var stale = Path.Combine(LogDir, $"server-{DateTime.UtcNow.AddDays(-20):yyyyMMdd}.log");
        var fresh = Path.Combine(LogDir, $"server-{DateTime.UtcNow.AddDays(-2):yyyyMMdd}.log");
        await File.WriteAllTextAsync(stale, "old");
        await File.WriteAllTextAsync(fresh, "recent");

        // Первая запись открывает сегодняшний файл и запускает чистку
        using (var mirror = new FileLog.MirrorTextWriter(new CollectingWriter([]), log))
        {
            mirror.WriteLine("запись-триггер");
            (await ReadTodayLinesAsync()).Should().NotBeEmpty("триггер открыл сегодняшний файл");
        }

        File.Exists(stale).Should().BeFalse("файл старше RetainDays удалён при ротации");
        File.Exists(fresh).Should().BeTrue("файл внутри RetainDays остался");
    }

    // Фоновый писатель асинхронный — ждём появления файла и строк в нём, а не паузу.
    // Чтение — с FileShare.ReadWrite: файл держит открытый писатель лога
    private async Task<string[]> ReadTodayLinesAsync()
    {
        var file = Path.Combine(LogDir, $"server-{DateTime.UtcNow:yyyyMMdd}.log");
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            var lines = await TryReadAsync(file);
            if (lines is { Length: > 0 }) return lines;
            await Task.Delay(25);
        }
        return await TryReadAsync(file) ?? [];
    }

    private static async Task<string[]?> TryReadAsync(string file)
    {
        if (!File.Exists(file)) return null;
        try
        {
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs);
            var text = await sr.ReadToEndAsync();
            return text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        }
        catch (IOException) { return null; }
    }

    private sealed class CollectingWriter(List<string> lines) : TextWriter
    {
        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;
        public override void WriteLine(string? value) => lines.Add(value ?? "");
    }
}
