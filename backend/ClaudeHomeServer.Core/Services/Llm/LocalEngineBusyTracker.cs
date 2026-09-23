namespace ClaudeHomeServer.Services.Llm;

// Признак «на локальном движке идёт ход исполнителя». Живой прогон 2026-09-23
// (tools/thinking-strip-proxy/live-run-2026-09-23.md, разбор промахов 9a7aeced): фоновые
// one-shot действия (теги, сводки, память) ходили в тот же vLLM параллельно с ходом
// исполнителя — 14 запросов на 213k токенов за 49 минут. Кэшем они не пользуются, а KV-бюджет
// (307 698 токенов) занимают и вытесняют префикс чата: два из шести промахов кэша пришли
// через 100–130 мс после такого запроса.
//
// Счётчик, а не флаг: локальных ходов может идти несколько (разные чаты на одном движке),
// и «свободно» наступает только когда закончился ПОСЛЕДНИЙ.
//
// Статика, а не DI, — симметрично BuildConcurrencyGate: ClaudeSession создаётся руками
// (SessionManager), а не контейнером, и протянуть к нему singleton неоткуда. Instance
// регистрируется в DI как тот же объект — второй трекер где-то ещё сделал бы признак
// неправдой. Тестам объект передаётся через конструктор потребителя, чтобы не делить
// глобальное состояние между прогонами.
public sealed class LocalEngineBusyTracker
{
    public static LocalEngineBusyTracker Instance { get; } = new();

    private int _active;

    // Идёт ли прямо сейчас хотя бы один ход на локальном движке.
    public bool Busy => Volatile.Read(ref _active) > 0;

    // Сколько ходов на локали идёт — для диагностики и тестов.
    public int Active => Volatile.Read(ref _active);

    // Пометка «ход на локали начался». Возвращённый токен обязан быть освобождён по выходу
    // из хода (using на всё тело хода), иначе движок навсегда останется «занятым» и фоновые
    // действия до рестарта уедут в облако.
    public IDisposable Enter()
    {
        Interlocked.Increment(ref _active);
        return new Slot(this);
    }

    // Освобождение ровно один раз: повторный Dispose иначе увёл бы счётчик в минус и
    // «занято» перестало бы наступать при живом ходе.
    private sealed class Slot(LocalEngineBusyTracker owner) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                Interlocked.Decrement(ref owner._active);
        }
    }
}
