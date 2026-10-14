using System.Diagnostics;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.ChatSubjects;
using ClaudeHomeServer.Services.Llm;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Живой прогон разбора чатов по темам на ЛОКАЛЬНОЙ модели (критерий шага 3 плана «AI-группировка
/// чатов»): пачка максимального размера по планировщику, реплики на лимите длины, реестр из 12
/// тем — 5 прогонов из 5 должны дать валидный JSON с ответом на каждый алиас. Вызов идёт тем же
/// <see cref="LlamaServerClient.ChatJsonAsync"/> с профилем Text, каким ходит
/// <see cref="CheapTextRunner"/> на локальном шаге.
///
/// В CI не запускается: Skip (метка Category CI не отсекает — фильтр там только Category!=Node).
/// Прогон: снять Skip вручную, dotnet test --filter "FullyQualifiedName~ChatSubjectsLiveLocal".
/// Числа (модель, пачка, время) печатаются в вывод теста и переносятся в отчёт задачи.
/// </summary>
public class ChatSubjectsLiveLocalTests(ITestOutputHelper output)
{
    private sealed class RealHttpFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private const string BaseUrl = "http://127.0.0.1:18020";
    private const string Model = "qwen3.8-27b";

    [Fact(Skip = "Живой прогон: ходит в локальную модель (llama-server на 127.0.0.1:18020). Снять Skip вручную: dotnet test --filter \"FullyQualifiedName~ChatSubjectsLiveLocal\".")]
    public async Task ПачкаМаксимальногоРазмера_5из5_ВалидныйJsonНаКаждыйАлиас()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["LocalLlm:Provider"] = "llama-server",
            ["LocalLlm:BaseUrl"] = BaseUrl,
            ["LocalLlm:Model"] = Model,
            ["LocalLlm:TextModel"] = Model,
            ["LocalLlm:DisableThinking"] = "true",
        }).Build();
        var client = new LlamaServerClient(new RealHttpFactory(), config, NullLogger<LlamaServerClient>.Instance);

        var spec = LocalActionCatalog.ProfileDefaults[CheapProfile.Text];
        var plan = SubjectBatchPlanner.Plan(spec, ChatSubjectRules.MaxSubjectsPerProject,
            ChatSubjectSweeper.DefaultBatchCeiling);
        var names = new[] { "Деплой", "Интерфейс", "Тесты", "Персоны", "Заметки", "Знания", "Задачи",
            "Картинки", "Звук", "Видео", "Безопасность", "Телеметрия" };
        var registry = names.Select((n, i) => new SubjectRegistryEntry("t" + (i + 1), "id" + i, n)).ToList();
        var chats = Enumerable.Range(1, plan.BatchSize).Select(i => new SubjectChatInput("c" + i,
            $"Чат номер {i}: разбор проблемы с {names[i % names.Length].ToLowerInvariant()}",
            [Long($"Нужно разобраться с {names[i % names.Length]}"), Long("Продолжение разговора"),
             Long("Итог обсуждения")])).ToList();
        var prompt = SubjectPrompt.Build(registry, chats, plan.SnippetChars, ChatSubjectRules.MaxNewPerPass);
        var aliases = chats.Select(c => c.Alias).ToHashSet();
        var topics = registry.Select(r => r.Alias).ToHashSet();

        output.WriteLine($"Модель {Model}, пачка {plan.BatchSize}, реплики {plan.SnippetChars} симв., " +
            $"промпт {prompt.Length} симв.");
        for (var run = 1; run <= 5; run++)
        {
            var sw = Stopwatch.StartNew();
            var answer = await client.ChatJsonAsync("", prompt, "json", model: Model,
                timeoutMs: spec.TimeoutMs, numPredict: spec.NumPredict, numCtx: spec.NumCtx,
                label: LocalActionCatalog.ChatSubjects);
            sw.Stop();
            var parsed = SubjectPrompt.Parse(answer, aliases, topics);
            output.WriteLine($"Прогон {run}: {sw.ElapsedMilliseconds} мс, разобран={parsed.Parsed}, " +
                $"ответов {parsed.Answers.Count}/{aliases.Count}");
            Assert.True(parsed.Parsed, $"прогон {run}: ответ не разобран: {answer}");
            Assert.Equal(aliases.Count, parsed.Answers.Select(a => a.ChatAlias).Distinct().Count());
        }
    }

    private static string Long(string seed)
    {
        var s = seed;
        while (s.Length < ChatSubjectRules.SnippetMaxChars) s += " " + seed;
        return s[..ChatSubjectRules.SnippetMaxChars];
    }
}
