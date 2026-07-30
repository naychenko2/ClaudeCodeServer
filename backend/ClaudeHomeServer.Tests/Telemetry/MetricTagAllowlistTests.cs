using System.Reflection;
using System.Text.RegularExpressions;
using ClaudeHomeServer.Telemetry;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Telemetry;

/// <summary>
/// Контрактные тесты кардинальности тегов ServerMetrics.
///
/// Дисциплина (design-time enforcement):
/// 1. Все строковые параметры public static Record* методов = имена тегов (camelCase).
/// 2. Каждый такой параметр, переведённый в snake_case, обязан состоять в AllowedTags.
/// 3. Скалярное значение метрики (duration, count) всегда числовое — НЕ строка.
///
/// Эффект: будущий разработчик не может добавить RecordFoo(string userId) —
/// тест упадёт, потому что user_id не в AllowedTags. Так cardinality bomb и PII
/// ловятся на ревью, а не на проде.
/// </summary>
public class MetricTagAllowlistTests
{
    private static readonly Type ServerMetricsType = typeof(ServerMetrics);

    /// <summary>
    /// Все строковые параметры всех public static Record* методов ServerMetrics
    /// должны иметь имена (snake_case), входящие в AllowedTags.
    /// Это и есть главный кардинальный инвариант.
    /// </summary>
    [Fact]
    public void ServerMetrics_AllTagKeys_AreInAllowlist()
    {
        var recordMethods = ServerMetricsType
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.Name.StartsWith("Record", StringComparison.Ordinal))
            .ToList();

        recordMethods.Should().NotBeEmpty(
            "должен быть хотя бы один Record* метод, иначе фасад бесполезен");

        var violations = new List<string>();

        foreach (var method in recordMethods)
        {
            foreach (var param in method.GetParameters())
            {
                // Только строковые параметры считаются тегами.
                // Числовые (double/long/int) — скалярное значение метрики.
                if (param.ParameterType != typeof(string))
                {
                    continue;
                }

                var snakeName = ToSnakeCase(param.Name!);
                if (!ServerMetrics.AllowedTags.Contains(snakeName))
                {
                    violations.Add(
                        $"{method.Name}({param.Name}: {param.ParameterType.Name}) — " +
                        $"тег '{snakeName}' не в AllowedTags");
                }
            }
        }

        violations.Should().BeEmpty(
            "найдены теги вне allowlist (cardinality/PII риск):\n" +
            string.Join("\n", violations));
    }

    /// <summary>
    /// Запрещённые теги (cardinality bomb или PII) не должны приниматься
    /// ни одним Record* методом — ни под каким именем параметра.
    /// </summary>
    [Theory]
    [InlineData("user_id", "userId")]       // cardinality bomb
    [InlineData("session_id", "sessionId")] // cardinality bomb
    [InlineData("file_path", "filePath")]   // cardinality + PII
    [InlineData("persona_name", "personaName")] // PII
    public void ServerMetrics_RejectsNonAllowlistedTag(string snakeTag, string camelTag)
    {
        var recordMethods = ServerMetricsType
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.Name.StartsWith("Record", StringComparison.Ordinal))
            .ToList();

        var offenders = recordMethods
            .Where(m => m.GetParameters()
                .Any(p => p.Name == camelTag))
            .Select(m => m.Name)
            .ToList();

        offenders.Should().BeEmpty(
            $"тег '{snakeTag}' (param '{camelTag}') запрещён, " +
            $"но найден в методах: {string.Join(", ", offenders)}");
    }

    /// <summary>
    /// C4 — SpendStore (JSONL) — единственный source of truth для токенов.
    /// В OTel-метриках учёт токенов ЗАПРЕЩЁН: это бухгалтерия, не операционка.
    /// </summary>
    [Fact]
    public void ServerMetrics_HasNoTokenMetrics()
    {
        var tokenMethods = ServerMetricsType
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.Name.Contains("Token", StringComparison.Ordinal))
            .Select(m => m.Name)
            .ToList();

        tokenMethods.Should().BeEmpty(
            "ServerMetrics не должен содержать token-метрик " +
            "(SpendStore — source of truth для биллинга). Найдены: " +
            string.Join(", ", tokenMethods));
    }

    /// <summary>
    /// Точный состав AllowedTags — добавление/удаление тега = сознательное
    /// архитектурное решение, отражённое в этом тесте.
    /// </summary>
    [Fact]
    public void AllowedTags_ContainsExactly_ExpectedSet()
    {
        var expected = new[]
        {
            "provider",    // claude, deepseek, glm, ollama, ...
            "model",       // claude-sonnet-4-5, glm-4, ...
            "execution",   // local | docker — среда исполнения хода, ровно два значения
            // direction (input/output/cache_read/cache_creation) убран: он размечает токены,
            // а учёт токенов в OTel запрещён (C4) — см. ServerMetrics_HasNoTokenMetrics ниже
            "tool_name",   // идентификатор MCP-инструмента (≤80-90 значений)
            "outcome",     // success, error, timeout
            "error_type",  // rate_limit, network, auth, ...
            "reason",      // ошибки Dify-синхронизации: 401, 404, 429, timeout, other
        };

        ServerMetrics.AllowedTags.Should().BeEquivalentTo(expected);
    }

    /// <summary>
    /// camelCase → snake_case. Используется только в тесте для сопоставления
    /// имён параметров (camelCase) с ключами тегов (snake_case).
    /// </summary>
    private static string ToSnakeCase(string camel)
    {
        // Вставляем '_' перед каждой заглавной буквой, затем lowercase.
        // Примеры: provider → provider, errorType → error_type, toolName → tool_name.
        var snake = Regex.Replace(camel, "(?<!^)([A-Z])", "_$1");
        return snake.ToLowerInvariant();
    }
}
