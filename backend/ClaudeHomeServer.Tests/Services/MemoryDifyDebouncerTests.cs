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

        // Пауза с запасом больше дебаунса (негативное условие — флако-безопасно):
        // живой таймер успел бы сработать и на самом медленном раннере
        await Task.Delay(TimeSpan.FromSeconds(1));
        fired.Task.IsCompleted.Should().BeFalse("Dispose освободил запланированный таймер");

        // Расписание после Dispose — no-op, новый таймер не заводится
        var late = new TaskCompletionSource();
        debouncer.Schedule("after", () => late.TrySetResult());
        await Task.Delay(TimeSpan.FromSeconds(1));
        late.Task.IsCompleted.Should().BeFalse("Schedule после Dispose не заводит новых таймеров");
    }
}
