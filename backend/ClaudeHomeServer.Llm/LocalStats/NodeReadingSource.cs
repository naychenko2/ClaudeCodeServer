namespace ClaudeHomeServer.Services.Llm;

// Статус прочитанного среза (ADR-029, «Версии и статусы узла»):
// Down — срез не получен или не разобран (сеть, не-2xx, тайм-аут, кривое тело);
// Incompatible — это срез другой версии; Stale — срез принят, но ageMs за порогом свежести.
public enum NodeReadingStatus
{
    Ok,
    Stale,
    Down,
    Incompatible,
}

// Сырой срез узла — общий вход сэмплера (Ш2c): тексты и карты БЕЗ толкования.
// Носитель статуса — сам источник: ошибка прочтения (сеть/диск/разбор) даёт Down,
// исключения наружу не выходят никогда (недоверенный источник, как у Gddr6HelperReader).
public sealed record NodeReading
{
    public NodeReadingStatus Status { get; init; }
    // Файлы /sys и /proc этого узла: DiskFileSource (local) или SliceFileSource (агент).
    public ISysFileSource Files { get; init; } = new SliceFileSource(new Dictionary<string, string>());
    // Карты nvidia-smi узла: local — IGpuSampler (все карты хоста, ADR-029 п.7); агент — ParseCsv(gpuCsv).
    public IReadOnlyList<GpuSample> Gpus { get; init; } = [];
    // Текст /run/ccs-gddr6/temps.json дословно; null — файла нет.
    public string? Gddr6 { get; init; }
    // Возраст среза, мс: у локального источника 0 (снимок свежий по построению);
    // у агента — ageMs из ответа (monotonic-часы агента).
    public long AgeMs { get; init; }

    public static NodeReading Down() => new() { Status = NodeReadingStatus.Down };
}

// Шов «откуда берётся сырой срез узла» (ADR-029, «Узлы фермы»): локальный диск или агент.
// Реализации без состояния: каждый ReadAsync независим (одновременные вызовы не мешают
// друг другу), общее изменяемое состояние — у вызывающего (сэмплер, Ш2c).
public interface INodeReadingSource
{
    Task<NodeReading> ReadAsync(CancellationToken ct = default);
}

// Локальный узел (worker): файлы — реальный диск (DiskFileSource), карты — хостовый
// IGpuSampler, gddr6 — файл помощника по пути из LocalModelStatsOptions. AgeMs = 0:
// локальный снимок собирается здесь и сейчас, порога свежести к нему нет.
public sealed class LocalReadingSource : INodeReadingSource
{
    private readonly ISysFileSource _files;
    private readonly IGpuSampler _gpu;
    private readonly string _gddr6File;

    public LocalReadingSource(IGpuSampler gpu, string gddr6File)
    {
        // Файлы — реальный диск (тот же DiskFileSource, что у сегодняшнего локального
        // снимка); тесты гоняют на фикстуре во временном каталоге — DiskFileSource
        // читает любой абсолютный путь, подмена не нужна.
        _files = new DiskFileSource();
        _gpu = gpu;
        _gddr6File = gddr6File;
    }

    public async Task<NodeReading> ReadAsync(CancellationToken ct = default)
    {
        try
        {
            var gpus = await _gpu.ReadAsync(ct).ConfigureAwait(false);
            return new NodeReading
            {
                Status = NodeReadingStatus.Ok,
                Files = _files,
                Gpus = gpus,
                Gddr6 = ReadGddr6(),
                AgeMs = 0,
            };
        }
        catch
        {
            // Гипотеза: IGpuSampler по контракту не бросает. Любое неожиданное — Down,
            // наружу не выходит (упавший источник не роняет хост).
            return NodeReading.Down();
        }
    }

    // Текст файла помощника дословно; null — файла нет/сбой чтения (дальнеe решит
    // Gddr6HelperReader в сэмплере: свежесть по метке "at", не здесь).
    private string? ReadGddr6()
    {
        try
        {
            return File.Exists(_gddr6File) ? File.ReadAllText(_gddr6File) : null;
        }
        catch
        {
            return null;
        }
    }
}
