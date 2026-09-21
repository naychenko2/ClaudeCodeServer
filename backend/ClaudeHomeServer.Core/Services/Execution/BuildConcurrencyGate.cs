using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Services.Execution;

// Ограничитель одновременных ТЯЖЁЛЫХ запусков local-среды (инцидент 2026-09-21: systemd-oomd
// третий раз за три дня убил прод, ccs-agents.slice держал 18,1 GB в четырёх параллельных
// прогонах). Изоляция в scope (IsolationOptions) лечит, КУДА бьёт oomd, — пределы там заданы
// per-scope, а число одновременных scope не ограничено ничем: три заведённые подряд задачи с
// worktree = три `dotnet build -m:4` разом.
//
// ЕДИНСТВЕННЫЙ счётчик на процесс (Instance): второй семафор где-то ещё — вторая точка правды,
// и общий потолок перестаёт быть потолком. Статика, а не DI, — симметрично IsolationOptions и
// LocalProcessRunner: они тоже процесс-глобальны, а WorktreeBuildWarmup создаётся руками.
//
// ЧТО ПОД ГЕЙТОМ. Только spec с явной меткой Heavy (см. ProcessSpec.Heavy). Лёгкие запуски —
// git status/diff, которых сотни, проба MCP, one-shot claude — проходят насквозь БЕЗ ожидания:
// очередь из них парализовала бы git-бар.
//
// ЧЕГО ГЕЙТ НЕ ВИДИТ (осознанно). Сборку, которую агент запускает ВНУТРИ хода своим Bash, —
// она не отдельный spec, а потомок процесса claude CLI. Ход слот не занимает и не ждёт его
// никогда, поэтому дедлок «ход ждёт слот, занятый прогревом того же дерева» невозможен по
// конструкции, а не по договорённости.
public sealed class BuildConcurrencyGate
{
    // Потолок по умолчанию: два тяжёлых прогона рядом с ходами агентов машина переживает,
    // четыре (замер инцидента) — нет. Откат без пересборки — явный 0 в конфиге, как у
    // BuildNodeReuse.
    public const int DefaultLimit = 2;

    public static BuildConcurrencyGate Instance { get; private set; } = new(DefaultLimit);

    // Установка потолка со старта хоста. Не сеттер: гейт — объект СО СОСТОЯНИЕМ (занятые слоты),
    // и подмена на ходу отдала бы новому гейту полный потолок поверх уже идущих сборок, а
    // освобождение старых слотов ушло бы в никуда. В бою хост один, но WebApplicationFactory
    // поднимает его десятками за тестовый прогон — прежний потолок с тем же значением сохраняем.
    public static void Configure(int limit)
    {
        if (Instance.Limit == (limit > 0 ? limit : 0)) return;
        Instance = new BuildConcurrencyGate(limit);
    }

    // 0 — без ограничения (сквозной проход); отрицательное значение из конфига читается так же
    private readonly SemaphoreSlim? _slots;

    public BuildConcurrencyGate(int limit)
    {
        Limit = limit > 0 ? limit : 0;
        _slots = Limit > 0 ? new SemaphoreSlim(Limit, Limit) : null;
    }

    public int Limit { get; }

    // Свободных слотов; без ограничения — int.MaxValue (для диагностики и тестов)
    public int Available => _slots?.CurrentCount ?? int.MaxValue;

    // Признак «тяжёлого» запуска — ОДНА формула на весь продукт. Читает явную метку spec, а не
    // угадывает по имени команды: `dotnet` — это и `build`, и `dotnet --version`, и запуск
    // dev-сервера проекта, и эвристика по имени ловила бы их все.
    public static bool IsHeavy(ProcessSpec spec) => spec.Heavy;

    // Слот под запуск spec. Лёгкий spec и выключенный потолок возвращаются мгновенно
    // завершённой задачей — вызывающий не платит ни переключением контекста, ни ожиданием.
    // Возвращённый токен обязан быть освобождён по выходу процесса (using/finally), иначе слот
    // утекает до рестарта.
    public Task<IDisposable> AcquireAsync(ProcessSpec spec, CancellationToken ct = default) =>
        _slots is null || !IsHeavy(spec) ? Task.FromResult(NoSlot) : AcquireSlotAsync(ct);

    // Слот без ожидания: взят — токен, занято — null. Нужен вызывающему, который обязан решить
    // «стартовать сейчас или уйти ждать в фон», не заблокировав свой поток (WorktreeBuildWarmup).
    public IDisposable? TryAcquire(ProcessSpec spec)
    {
        if (_slots is null || !IsHeavy(spec)) return NoSlot;
        return _slots.Wait(0) ? new Slot(_slots) : null;
    }

    private async Task<IDisposable> AcquireSlotAsync(CancellationToken ct)
    {
        await _slots!.WaitAsync(ct);
        return new Slot(_slots);
    }

    private static readonly IDisposable NoSlot = new NoOpSlot();

    // Освобождение ровно один раз: повторный Dispose (двойной using на одном токене) иначе
    // поднял бы счётчик выше потолка — тихо и навсегда.
    private sealed class Slot(SemaphoreSlim slots) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) slots.Release();
        }
    }

    private sealed class NoOpSlot : IDisposable
    {
        public void Dispose() { }
    }

    // Потолок читается НЕЗАВИСИМО от Isolation:Enabled: он про то, сколько сборок идёт разом,
    // а не про то, в каком cgroup они живут, и нужен даже там, где обёртки systemd-run нет
    // (Windows, dev-контейнер). Снимается только своим ключом — 0.
    public static int LimitFromConfig(IConfiguration config) =>
        config.GetValue("Execution:Isolation:MaxConcurrentBuilds", DefaultLimit);

    public static BuildConcurrencyGate FromConfig(IConfiguration config) => new(LimitFromConfig(config));
}
