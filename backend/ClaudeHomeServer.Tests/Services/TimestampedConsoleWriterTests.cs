using ClaudeHomeServer.Services;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Коллекция ProcessGlobalState: тест подменяет Console.Out/Error на весь свой прогон —
// параллельный класс, делающий то же самое, получил бы в finally чужой поток.
[Collection(TestCollections.ProcessGlobalState)]
public class TimestampedConsoleWriterTests
{
    // Формат-литерал вместо настоящего шаблона времени: в выводе получается предсказуемый
    // маркер «[TS] », по числу которых и видно, сколько слоёв обёртки навесилось.
    private const string MarkerFormat = "'TS'";

    /// <summary>
    /// Повторный Enable не добавляет второго слоя обёртки. Прежняя проверка
    /// (`Console.Out is not TimestampWriter`) не срабатывала никогда — SetOut заворачивает
    /// writer в TextWriter.Synchronized, — и каждый старт хоста наращивал луковицу
    /// TimestampWriter×SyncTextWriter до дедлока всего прогона.
    /// </summary>
    [Fact]
    public void Enable_ПовторныйВызов_НеОборачиваетВтороеРаз()
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        var outSink = new StringWriter();
        var errSink = new StringWriter();
        var marker = "ts-probe-" + Guid.NewGuid().ToString("N");
        try
        {
            // Сброс: Enable уже звал каждый поднятый в этом процессе хост (Program.cs)
            TimestampedConsoleWriter.ResetForTests();
            Console.SetOut(outSink);
            Console.SetError(errSink);

            TimestampedConsoleWriter.Enable(MarkerFormat);
            TimestampedConsoleWriter.Enable(MarkerFormat);

            Console.WriteLine(marker);
            Console.Error.WriteLine(marker);
            Console.Out.Flush();
            Console.Error.Flush();
        }
        finally
        {
            // Флаг НЕ сбрасываем: пусть хосты, стартующие следом, видят «уже включено» и
            // не вешают слой на восстановленный Console.Out.
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }

        // Своя строка ищется по уникальному маркеру: в sink параллельно капает чужой вывод
        LineWith(outSink, marker).Should().Be("[TS] " + marker, "stdout обёрнут ровно один раз");
        LineWith(errSink, marker).Should().Be("[TS] " + marker, "stderr обёрнут ровно один раз");
    }

    private static string LineWith(StringWriter sink, string marker) =>
        sink.ToString()
            .Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .Single(l => l.Contains(marker));
}
