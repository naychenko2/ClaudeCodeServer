using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Tests.Helpers;

namespace ClaudeHomeServer.Tests.LocalBench;

/// <summary>
/// Исполнитель замера, ходящий в ОБЛАКО (пара сравнения к <see cref="LiveLocalRunner"/>).
///
/// Зачем он есть: валидность отвечает на вопрос «соблюдён ли формат», но не на вопрос
/// «разумен ли выбор». Ответ на второй даёт только эталон, и дешевле всего его берёт сам
/// харнесс — тот же банк кейсов, тот же продуктовый промпт, другой исполнитель. Копии
/// теста под облако нет и быть не должно: разойдись банк или промпт — сравнение
/// превратилось бы в сравнение двух разных замеров.
///
/// Ходит продуктовым <see cref="OneShotClaudeRunner"/> (claude CLI) — тем же транспортом,
/// которым облачный шаг делает боевой <c>CheapTextRunner</c>. Модель ОДНА на весь прогон
/// (LOCALBENCH_CLOUD_MODEL, дефолт haiku), а не «как у места»: места зовут раннер с
/// разными fallbackModel, и сводка по семи местам на разных моделях была бы несравнима
/// сама с собой.
///
/// Причины остановки облако этим путём не отдаёт: <c>FinishReason</c> всегда null, и
/// колонка обрывов у облачного прогона пуста по построению, а не потому что обрывов нет.
/// </summary>
public sealed class LiveCloudRunner : BenchRunner
{
    public const string DefaultModel = "haiku";

    private readonly OneShotClaudeRunner _claude;

    public string Model { get; }

    public LiveCloudRunner(string? model = null)
    {
        Model = model ?? Environment.GetEnvironmentVariable("LOCALBENCH_CLOUD_MODEL") ?? DefaultModel;
        var config = TestConfig.Build(new()
        {
            // Профиль CLI с живым OAuth: пустой профиль тестов упёрся бы в «OAuth session
            // expired» на первом же ходе (тот же приём, что в живой приёмке значка).
            ["ClaudeUserProfileDir"] = LiveProfileDir(),
            // Маршрут CLI продукт задаёт сам и чистит унаследованные CLAUDE_CONFIG_DIR /
            // ANTHROPIC_*. Живому прогону это мешает: рабочий OAuth лежит в профиле из
            // CLAUDE_CONFIG_DIR. Наследование включается штатным рубильником.
            ["Claude:InheritSystemEnv"] = "true",
        });
        _claude = new OneShotClaudeRunner(new LlmProviderRegistry(config),
            TestLauncherFactory.Instance, config);
    }

    public override string Describe => $"облако {Model} (claude CLI)";

    protected override bool Local => false;

    /// <summary>
    /// Прогревать облако нечем и незачем: весов, которые грузятся в память, там нет, а
    /// холостой ход стоил бы денег. Прогревочный проход места (он же первый кейс банка)
    /// цикл делает всё равно — на нём и осядет разовая цена старта CLI.
    /// </summary>
    public override Task WarmUpAsync(CancellationToken ct = default) => Task.CompletedTask;

    protected override async Task<BenchAnswer> CallModelAsync(string actionKey, string prompt,
        object? jsonFormat, CheapProfileSpec profile, int numPredict, int timeoutMs, CancellationToken ct)
    {
        // Облачный таймаут места, а не локальный: у профилей это разные величины, и
        // мерить облако локальным потолком значило бы записать ему чужие обрывы.
        var timeout = TimeSpan.FromMilliseconds(profile.CloudTimeoutMs);
        var run = await _claude.RunDetailedAsync(prompt, Model, timeout, ct, label: actionKey);
        return new BenchAnswer(run.Text, FinishReason: null, StatusCode: 0,
            PromptTokens: (int)(run.Usage?.InputTokens ?? 0),
            CompletionTokens: (int)(run.Usage?.OutputTokens ?? 0));
    }

    /// <summary>Есть ли чем ходить в облако: без claude CLI прогон выходит без падения.</summary>
    public static bool Available() => ClaudeCliFound();

    private static bool ClaudeCliFound()
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        var names = OperatingSystem.IsWindows() ? new[] { "claude.exe", "claude.cmd" } : ["claude"];
        return path.Split(Path.PathSeparator)
            .Where(dir => !string.IsNullOrWhiteSpace(dir))
            .Any(dir => names.Any(name => File.Exists(Path.Combine(dir, name))));
    }

    // Профиль CLI с живым OAuth: тот, в котором работает текущее окружение,
    // иначе — домашний ~/.claude
    private static string LiveProfileDir() =>
        Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } dir
            ? dir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
}
