using System.Text;

namespace ClaudeHomeServer.Services;

// Обёртка консольного вывода с UTC-таймстемпом в начале каждой строки.
//
// Перехватывает И голые Console.WriteLine/Console.Error.WriteLine (диагностика ClaudeSession и
// др.), И сообщения ILogger (SimpleConsoleFormatter пишет в тот же Console.Out) — единый
// механизм, без дублей: сам форматер таймстемпа не добавляет, обёртка — одна на поток.
//
// Без времени в логе события бэкенда невозможно сопоставить с UTC-таймстемпами транскриптов
// CLI (инцидент P27/I11: корневая причина падения хода не установлена именно из-за этого).
//
// Включение и формат — ключ Diagnostics:ConsoleTimestampFormat в appsettings: значение — шаблон
// DateTime.UtcNow.ToString (напр. «yyyy-MM-ddTHH:mm:ss.fffZ»); пусто или отсутствие — обёртка
// выключена, вывод остаётся как есть. Ключ держим ПОД Diagnostics, не под Logging:Console: прежнее
// имя Logging:Console:TimestampFormat биндилось во фреймворчный ConsoleLoggerOptions.TimestampFormat
// (устаревшее, но живое), и при отсутствии FormatterName провайдер копировал его в SimpleConsoleFormatter —
// тогда ILogger-строки получали ВТОРОЙ таймстемп (с липовым суффиксом Z: форматтер пишет локальное
// время). Обёртка ставит ровно одно время на любую строку (ILogger и Console.WriteLine).
//
// ПОТОКОБЕЗОПАСНОСТЬ. Сам по себе TimestampWriter НЕ потокобезопасен: общий `StringBuilder _line`
// при одновременной записи из двух потоков перемешается посимвольно. В обычном сценарии спасает
// то, что `Console.SetOut`/`SetError` оборачивают переданный writer в `TextWriter.Synchronized`
// и весь вызов `WriteLine(string)` проходит под одной блокировкой. НО это неочевидный внешний
// инвариант: стоит вызывать writer напрямую (мимо Console) или писать через `Console.Write` по
// частям — и строки перемешаются. Поэтому ВСЕ методы записи здесь дополнительно взяты под
// собственный `lock (_lock)`: корректность держится на самом writer'е, а не на поведении Console.
public static class TimestampedConsoleWriter
{
    public static void Enable(string? format)
    {
        if (string.IsNullOrWhiteSpace(format)) return;
        try
        {
            // Повторный вызов НЕ оборачивает уже обёрнутое: тесты поднимают в одном
            // процессе десятки хостов, каждый звал Enable → Console.Out обрастал луковицей
            // вложенных TimestampWriter×SyncTextWriter. Вложенные пары «свой lock + монитор
            // SyncTextWriter» дают lock-ordering inversion между потоками — на CI (stdout =
            // узкий пайп) это приводило к дедлоку всех тестовых слотов (диагноз по full-дампу:
            // цепочка из 8 обёрток, 6 мониторов SyncTextWriter с разными владельцами).
            if (Console.Out is not TimestampWriter)
            {
                Console.SetOut(new TimestampWriter(Console.Out, format));
                Console.SetError(new TimestampWriter(Console.Error, format));
            }
        }
        catch
        {
            // Нет консоли/права (неинтерактивный контекст) — остаётся прямой вывод без времени
        }
    }

    private sealed class TimestampWriter : TextWriter
    {
        private readonly TextWriter _inner;
        private readonly string _format;
        // Накапливаем символы текущей строки; при '\n' — flush с таймстемпом в начале.
        // Доступ — строго под _lock (см. комментарий о потокобезопасности над классом).
        private readonly StringBuilder _line = new();
        private readonly object _lock = new();

        public TimestampWriter(TextWriter inner, string format)
        {
            _inner = inner;
            _format = format;
        }

        public override Encoding Encoding => _inner.Encoding;

        public override void Write(string? value)
        {
            if (string.IsNullOrEmpty(value)) return;
            lock (_lock)
            {
                foreach (var ch in value) Append(ch);
            }
        }

        public override void Write(char value)
        {
            lock (_lock) Append(value);
        }

        public override void Write(char[] buffer, int index, int count)
        {
            lock (_lock)
            {
                for (var i = 0; i < count; i++) Append(buffer[index + i]);
            }
        }

        public override void Write(ReadOnlySpan<char> buffer)
        {
            lock (_lock)
            {
                foreach (var ch in buffer) Append(ch);
            }
        }

        // '\r' пропускаем — он всегда paired с '\n', перевод строки обрабатывается на '\n'.
        // Иначе получили бы две пустые строки с таймстемпом в CRLF-выводе.
        // Вызывается ТОЛЬКО под _lock.
        private void Append(char ch)
        {
            if (ch == '\n')
            {
                FlushLine();
                _inner.Write('\n');
            }
            else if (ch != '\r')
            {
                _line.Append(ch);
            }
        }

        // Таймстемп берётся в момент перевода строки — он ближе к времени записи события.
        // Вызывается ТОЛЬКО под _lock.
        private void FlushLine()
        {
            _inner.Write('[');
            _inner.Write(DateTime.UtcNow.ToString(_format));
            _inner.Write("] ");
            _inner.Write(_line);
            _line.Clear();
        }

        // Незавершённая хвостовая строка (без '\n') — выводим с таймстемпом при Flush/Dispose.
        public override void Flush()
        {
            lock (_lock)
            {
                if (_line.Length > 0) FlushLine();
                _inner.Flush();
            }
        }

        // Dispose (shutdown): дочистить хвост (см. Flush), иначе незавершённая строка теряется —
        // комментарий класса обещает вывод «при Flush/Dispose», и без переопределения TextWriter.
        // Dispose(bool) здесь молча проглатывал бы накопленный _line (3.3).
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                lock (_lock)
                {
                    if (_line.Length > 0) FlushLine();
                    _inner.Dispose();
                }
            }
            base.Dispose(disposing);
        }
    }
}
