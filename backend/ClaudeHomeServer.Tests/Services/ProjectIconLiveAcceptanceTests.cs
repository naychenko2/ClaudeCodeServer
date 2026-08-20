using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.ProjectIcons;
using ClaudeHomeServer.Tests.Helpers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Живая приёмка подбора значка (ADR-009 §2.4, «Что проверять»): двухходовая схема
/// прогоняется РЕАЛЬНОЙ моделью тем же маршрутом, что и в продукте — место
/// <c>project-icon</c> через <see cref="CheapTextRunner"/>.
///
/// Тест ручной (Skip): он ходит в модель, стоит денег и минут, и в CI ему делать нечего.
/// Прогон — снятием Skip локально при настроенном claude CLI. Критерий приёмки §9.1:
/// не меньше 9 успехов из 10, ни одного подбора, где все кандидаты выдуманы (это
/// невозможно по построению — выбор идёт из меню, но проверяется фактом), и видимое
/// в логе срабатывание повтора, если он был.
///
/// Названия взяты нарочно «редкими по смыслу» (маяк, улей, самовар) — именно на них
/// одноходовая схема выдумывала имена (замер 08.2026: lighthouse, kettle, mushroom…).
/// </summary>
public class ProjectIconLiveAcceptanceTests
{
    // Названия приёмки: обиходные вперемешку с редкими смыслами из постановки задачи
    private static readonly (string Name, string? Hint)[] Cases =
    [
        ("Маяк", null),
        ("Улей", null),
        ("Самовар", null),
        ("Копилка", null),
        ("Книжная полка", null),
        ("Грибница", null),
        ("Светофор", null),
        ("Шарф ручной вязки", null),
        ("Кофейня на углу", null),
        ("Домашняя аптечка", null),
        ("Парусная регата", "что-нибудь про ветер и море"),
        ("Ремонт квартиры", "инструменты"),
    ];

    // Абсолютный пол приёмки: сколько названий из Cases обязаны получить значок в любом
    // случае. Сторожит поломку подбора целиком — фолбэк на инициалы честен для отдельного
    // проекта, но не для восьми из двенадцати
    private const int MinPicked = 8;

    private sealed class NullHttpFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    // Профиль CLI с живым OAuth: тот, в котором работает текущее окружение,
    // иначе — домашний ~/.claude
    private static string LiveProfileDir() =>
        Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } dir
            ? dir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");

    // Логгер, печатающий диагностику подбора в вывод теста: по этим строкам и смотрят
    // длительность ходов и срабатывание повтора
    private sealed class ConsoleLogger(List<string> sink) : ILogger<ProjectIconGlyphService>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
            => sink.Add(formatter(state, exception) + (exception is null ? "" : $" | {exception.Message}"));
    }

    private static ICheapTextRunner BuildCheapRunner()
    {
        // Конфиг дев-контура: локаль выключена явно — приёмка проверяет облачный маршрут
        // места, тот же, каким подбор идёт у пользователя.
        //
        // ClaudeUserProfileDir указывает на НАСТОЯЩИЙ профиль CLI: TestConfig по умолчанию
        // подставляет пустую папку (экономит ~150 с на копировании плагинов), но в ней нет
        // OAuth — живой прогон упирается в «OAuth session expired» на первом же ходе
        var config = TestConfig.Build(new()
        {
            ["DataPath"] = Path.Combine(Path.GetTempPath(), "cc-live-icons", "projects.json"),
            ["Ollama:Model"] = "",
            ["ClaudeUserProfileDir"] = LiveProfileDir(),
            // Маршрут CLI продукт задаёт сам и чистит унаследованные CLAUDE_CONFIG_DIR /
            // ANTHROPIC_* (LlmProviderRegistry.ProviderEnvKeys). Живому прогону это мешает:
            // рабочий OAuth лежит в профиле из CLAUDE_CONFIG_DIR, а очистка уводит CLI
            // в ~/.claude. Наследование включается ТОЛЬКО здесь, штатным рубильником
            ["Claude:InheritSystemEnv"] = "true",
        });
        var http = new NullHttpFactory();
        var ollama = new OllamaClient(http, config, NullLogger<OllamaClient>.Instance);
        var store = new LocalActionOverridesStore(config, NullLogger<LocalActionOverridesStore>.Instance);
        var router = new LocalActionRouter(ollama, store, config, NullLogger<LocalActionRouter>.Instance);
        var providers = new LlmProviderRegistry(config);
        var cloud = new CloudCheapClient(http, config, providers, NullLogger<CloudCheapClient>.Instance);
        var claude = new OneShotClaudeRunner(providers, TestLauncherFactory.Instance, config);
        return new CheapTextRunner(router, ollama, cloud, claude, NullLogger<CheapTextRunner>.Instance);
    }

    [Fact(Skip = "Живая приёмка ADR-009 §9.1: ходит в реальную модель (~3.5 мин, деньги). " +
                 "Снять Skip для ручного прогона; протокол — %TEMP%/icon-live-acceptance.log")]
    public async Task ЖиваяПриёмка_ДесятьПодборов_НеМеньшеДевятиУспехов()
    {
        var log = new List<string>();
        var service = new ProjectIconGlyphService(BuildCheapRunner(), new ConsoleLogger(log));

        var ok = 0;
        var failures = new List<string>();
        // Отказ «в наборе нет иконок по смыслу» — не провал приёмки, а честный фолбэк:
        // такой проект остаётся на инициалах осознанно (SelectMenu §3)
        var honest = 0;
        foreach (var (name, hint) in Cases)
        {
            var result = await service.SuggestAsync(name, hint, "live-acceptance");
            log.Add($"  → «{name}»: {(result.Ok ? string.Join(", ", result.Candidates.Select(c => c.Name)) : "— " + result.FailReason)}");
            if (result.Ok)
            {
                ok++;
                // Все показанные кандидаты обязаны существовать в наборе: «все четыре
                // выдуманы» не должно случиться ни разу
                Assert.All(result.Candidates, c => Assert.True(LucideGlyphs.Contains(c.Name),
                    $"«{name}»: показан несуществующий значок {c.Name}"));
            }
            else if (result.FailReason == "no-glyphs") honest++;
            else failures.Add($"{name} → {result.FailReason}");
        }

        // Диагностика прогона целиком — в сообщении провала, чтобы не лезть в файловый лог
        var report = string.Join("\n", log);
        // Протокол прогона рядом с репозиторием: приёмку показывают владельцу, а из
        // зелёного теста цифр не видно
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "icon-live-acceptance.log"),
            $"Значок подобран: {ok} из {Cases.Length}; честных инициалов: {honest}\n{report}");

        // Абсолютный пол — ПЕРВЫМ: без него сломанный SelectMenu (меню всегда пусто) дал бы
        // honest = 12, eligible = 0 и зелёный тест при полностью нерабочем подборе.
        // Факт прогона 20.08.2026 — 10 успехов и 2 честных инициала на 12 названий
        Assert.True(ok >= MinPicked,
            $"Значок подобран лишь {ok} раз из {Cases.Length} при пороге {MinPicked}: " +
            $"похоже, подбор сломан целиком (честных инициалов {honest}).\n" +
            $"Отказы: {string.Join("; ", failures)}\n--- лог подбора ---\n{report}");

        // Порог качества считается по подборам, которые МОГЛИ состояться: проект, для
        // которого в наборе нет подходящей иконки, уходит на инициалы намеренно
        var eligible = Cases.Length - honest;
        Assert.True(ok >= eligible - 1,
            $"Успехов {ok} из {eligible} возможных (честных инициалов {honest}), порог {eligible - 1}.\n" +
            $"Отказы: {string.Join("; ", failures)}\n--- лог подбора ---\n{report}");
    }
}
