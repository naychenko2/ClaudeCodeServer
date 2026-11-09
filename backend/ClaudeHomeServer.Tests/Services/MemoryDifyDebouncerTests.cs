using ClaudeHomeServer.Services.Memory;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Уборка таймеров дебаунсера (находка QA 23.08): Dispose гасит уже запланированные
// вызовы, а Schedule после Dispose — no-op. Таймер, переживший остановку сервиса,
// срабатывал бы после неё и запускал фоновую работу (git/Dify) по мёртвому приложению.
public class MemoryDifyDebouncerTests
{
    [Fact]
    public async Task Dispose_ГаситЗапланированноеИОтклоняетНовыеРасписания()
    {
        var debounce = TimeSpan.FromMilliseconds(50);
        var fired = new TaskCompletionSource();
        using var debouncer = new MemoryDifyDebouncer(debounce);

        // Расписание до Dispose: таймер заведён, но остановлен раньше срабатывания
        debouncer.Schedule("before", () => fired.TrySetResult());
        debouncer.Dispose();

        // Расписание после Dispose — no-op, новый таймер не заводится
        var late = new TaskCompletionSource();
        debouncer.Schedule("after", () => late.TrySetResult());

        // Негативное условие меряется сигналом, а не паузой: живой «часовой» дебаунсер того же
        // класса с интервалом в 3× проверяемого. Его срабатывание значит, что окно дебаунса
        // прошло и механизм таймеров работает; живой таймер «before» (50 мс) сработал бы раньше
        // него — поэтому проверка после сигнала не «слишком рано», а после срока обеих записей.
        // Таймаут сигнала — красный, как у любого ожидания.
        var window = new TaskCompletionSource();
        using var ticker = new MemoryDifyDebouncer(debounce * 3);
        ticker.Schedule("clock", () => window.TrySetResult());
        await window.Task.WaitAsync(TimeSpan.FromSeconds(10));

        fired.Task.IsCompleted.Should().BeFalse("Dispose освободил запланированный таймер");
        late.Task.IsCompleted.Should().BeFalse("Schedule после Dispose не заводит новых таймеров");
    }
}
