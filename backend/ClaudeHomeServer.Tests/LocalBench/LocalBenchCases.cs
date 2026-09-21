using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClaudeHomeServer.Tests.LocalBench;

/// <summary>
/// Банк кейсов места: лежит ВНЕ кода теста, в <c>LocalBench/cases/{место}.json</c> рядом
/// с харнессом. Файл читается из исходников по корню репозитория, а не из каталога
/// сборки: кейсы пополняются пачками и без пересборки — ровно ради этого банк и вынесен.
///
/// Каталог можно перенаправить переменной окружения LOCALBENCH_CASES_DIR.
/// </summary>
public static class LocalBenchCases
{
    private const string CasesDirEnv = "LOCALBENCH_CASES_DIR";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Загрузить банк кейсов места. Файла нет — понятная ошибка с путём поиска.</summary>
    public static LocalBenchCaseBank Load(string place)
    {
        var dir = CasesDirectory();
        var path = Path.Combine(dir, $"{place}.json");
        if (!File.Exists(path))
            throw new FileNotFoundException($"Банк кейсов места «{place}» не найден: {path}", path);

        var bank = JsonSerializer.Deserialize<LocalBenchCaseBank>(File.ReadAllText(path), Json)
            ?? throw new InvalidOperationException($"Банк кейсов «{place}» пуст или нечитаем: {path}");
        if (bank.Cases.Count == 0)
            throw new InvalidOperationException($"В банке кейсов «{place}» нет ни одного кейса: {path}");
        var duplicate = bank.Cases.GroupBy(c => c.Id, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
            throw new InvalidOperationException(
                $"В банке кейсов «{place}» повторяется id «{duplicate.Key}»: {path}");
        return bank;
    }

    /// <summary>Каталог банков кейсов.</summary>
    public static string CasesDirectory()
    {
        var overridden = Environment.GetEnvironmentVariable(CasesDirEnv);
        if (!string.IsNullOrWhiteSpace(overridden)) return overridden;
        return Path.Combine(RepoRoot(), "backend", "ClaudeHomeServer.Tests", "LocalBench", "cases");
    }

    // Корень репозитория от каталога сборки — тот же приём, что в PromptBenchTests.
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, ".git"))
                || File.Exists(Path.Combine(dir.FullName, ".git")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException(
            $"Корень репозитория не найден от {AppContext.BaseDirectory}; "
            + $"задайте каталог кейсов переменной {CasesDirEnv}");
    }
}

/// <summary>Банк кейсов одного места каталога.</summary>
public sealed record LocalBenchCaseBank(
    [property: JsonPropertyName("place")] string Place,
    [property: JsonPropertyName("cases")] IReadOnlyList<LocalBenchCase> Cases);

/// <summary>
/// Один кейс: подставной вход места и (необязательно) эталон для метрики совпадения.
/// <see cref="Expect"/> — сырой JSON: его смысл знает оракул конкретного места, поэтому
/// новое место подключается своим банком, не трогая харнесс.
/// </summary>
public sealed record LocalBenchCase(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("input")] string Input,
    [property: JsonPropertyName("expect")] JsonElement? Expect = null);
