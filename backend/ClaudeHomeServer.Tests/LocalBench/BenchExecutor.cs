using Xunit.Abstractions;

namespace ClaudeHomeServer.Tests.LocalBench;

/// <summary>
/// Кто исполняет ходы замера — ПАРАМЕТР ПРОГОНА, а не копия теста.
///
/// Локальный прогон (дефолт):
///   dotnet test --filter "Category=LocalBench"
/// Облачный прогон тем же банком и тем же продуктовым промптом:
///   LOCALBENCH_RUNNER=cloud dotnet test --filter "Category=LocalBench"
///
/// Категорию облачный прогон НЕ меняет по той же причине, что и локальный: оба ходят в
/// живую модель, и в CI им делать нечего. Исполнитель недоступен (движок не поднят, нет
/// claude CLI) — замер выходит без падения: пустая таблица честнее красной сборки на
/// чужой машине.
/// </summary>
public static class BenchExecutor
{
    public const string RunnerEnv = "LOCALBENCH_RUNNER";

    /// <summary>Выбранный прогоном исполнитель: local (дефолт) либо cloud.</summary>
    public static bool Cloud =>
        string.Equals(Environment.GetEnvironmentVariable(RunnerEnv)?.Trim(), "cloud",
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Создать исполнителя прогона. null — исполнителя нет (стенд не поднят / нет CLI);
    /// причина уже напечатана в вывод теста.
    /// </summary>
    public static async Task<BenchRunner?> CreateAsync(ITestOutputHelper output)
    {
        if (Cloud)
        {
            if (!LiveCloudRunner.Available())
            {
                output.WriteLine("Облачный прогон запрошен, но claude CLI не найден — замер пропущен");
                return null;
            }
            var cloud = new LiveCloudRunner();
            output.WriteLine($"Исполнитель: {cloud.Describe}");
            return cloud;
        }

        var stand = new LocalBenchStand();
        if (!await stand.AliveAsync())
        {
            output.WriteLine($"Локальный стенд {stand.BaseUrl} не поднят — замер пропущен");
            return null;
        }
        var local = new LiveLocalRunner(stand);
        output.WriteLine($"Исполнитель: {local.Describe}");
        return local;
    }
}
