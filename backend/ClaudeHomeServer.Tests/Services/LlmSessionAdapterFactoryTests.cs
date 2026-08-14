using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Llm;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Tests.Services;

// Склейка источников оценки контекста (ADR-007 §4.4, задача «Реестр ёмкости не наполняется»):
// живое значение (usage текущего хода) приоритет; при его отсутствии — фолбэк на историю чата;
// нет нигде — 0 (фильтр fail-open, наблюдение не записывается). Контракт адаптера (Func<int>) не
// меняется, источник («живая»/«из истории»/«нет») идёт параллельным Func<string> для диагностики.
// ComposeContext вынесен в чистую функцию, чтобы тестировать композицию без подъёма фабрики.
public class LlmSessionAdapterFactoryTests
{
    [Fact]
    public void ComposeContext_ЖиваяПриоритет_ИсториюНеЗатирает()
    {
        // Живая оценка (от usage текущего хода) точнее истории (та — от ПРЕДЫДУЩЕГО хода): при
        // наличии живой история не нужна. Это инвариант «значение из истории не затирает более
        // свежее живое».
        var (tokens, source) = LlmSessionAdapterFactory.ComposeContext(live: 100_000, fromHistory: 200_000);

        tokens.Should().Be(100_000);
        source.Should().Be("живая");
    }

    [Fact]
    public void ComposeContext_ЖивойНет_БерётсяИзИстории()
    {
        // Живое значение 0 — ход упал до assistant-сообщения / рестарт / холодный старт чата:
        // оценка берётся из истории (последний StoredResultMessage.ContextTokens>0).
        var (tokens, source) = LlmSessionAdapterFactory.ComposeContext(live: 0, fromHistory: 80_000);

        tokens.Should().Be(80_000);
        source.Should().Be("из истории");
    }

    [Fact]
    public void ComposeContext_НетНигде_НольНейтральный()
    {
        // Оценки нет совсем (новый чат, нет истории) — 0: WouldFit уходит в fail-open, RecordOverflow
        // отсекается guard'ом contextTokens<=0. Источник «нет» — для честной диагностики в логе.
        var (tokens, source) = LlmSessionAdapterFactory.ComposeContext(live: 0, fromHistory: null);

        tokens.Should().Be(0);
        source.Should().Be("нет");
    }

    [Fact]
    public void ComposeContext_НулеваяИстория_КакБезНеё()
    {
        // fromHistory = 0 (последний result с ContextTokens=0/null и предыдущих нет) — нет оценки:
        // нулевая история не лучше её отсутствия, фолбэк на неё бессмысленен.
        var (tokens, source) = LlmSessionAdapterFactory.ComposeContext(live: 0, fromHistory: 0);

        tokens.Should().Be(0);
        source.Should().Be("нет");
    }

    // Guard согласованности пары «модель × провайдер» (инцидент 14.08.2026, прод, чат c20746b9):
    // смена модели до первого хода оставила в sessions.json пару (Claude-модель opus[1m] × ключ
    // glm) — CLI стартовал в профиле glm с моделью Anthropic → мгновенный 401 «OAuth session
    // expired». Несочетаемая пара не должна доезжать до запуска CLI: фабрика чинит её по модели
    // (модель — единственный источник правды), заодно леча старые записи sessions.json.
    [Fact]
    public void Create_РассогласованнаяПара_ЧинитсяПоМодели()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ccs_factory_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["DataPath"] = Path.Combine(tempDir, "projects.json"),
                    ["LlmProviders:glm:ApiKey"] = "sk-test",
                    ["LlmProviders:glm:AnthropicBaseUrl"] = "https://glm.example.com",
                    ["LlmProviders:glm:Models:0:Id"] = "glm-5.2",
                }).Build();
            var providers = new LlmProviderRegistry(config);
            var pool = new ClaudeSubscriptionPool(config);
            var factory = new LlmSessionAdapterFactory(config, new SkillsService(),
                new WorkspaceKnowledgeStore(config), providers, pool);
            var session = new Session { Model = "opus[1m]", Provider = "glm" };
            var context = new LlmSessionContext(tempDir, _ => Task.CompletedTask,
                RawSystemPrompt: null, PermissionRules: null, TasksMcp: null);

            var adapter = factory.Create(session, context);

            adapter.Should().NotBeNull();
            session.Provider.Should().Be("claude",
                "пара (Claude-модель × ключ glm) чинится по модели — пустой пул → PrimaryKey");
        }
        finally { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); }
    }

    // Тот же guard в обратную сторону не ломает согласованные пары: сторонняя модель со своим
    // ключом доезжает до CLI как есть (guard молчит, Provider не переписывается).
    [Fact]
    public void Create_СогласованнаяСторонняяПара_НеТрогается()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ccs_factory_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["DataPath"] = Path.Combine(tempDir, "projects.json"),
                    ["LlmProviders:glm:ApiKey"] = "sk-test",
                    ["LlmProviders:glm:AnthropicBaseUrl"] = "https://glm.example.com",
                    ["LlmProviders:glm:Models:0:Id"] = "glm-5.2",
                }).Build();
            var providers = new LlmProviderRegistry(config);
            var pool = new ClaudeSubscriptionPool(config);
            var factory = new LlmSessionAdapterFactory(config, new SkillsService(),
                new WorkspaceKnowledgeStore(config), providers, pool);
            var session = new Session { Model = "glm-5.2", Provider = "glm" };
            var context = new LlmSessionContext(tempDir, _ => Task.CompletedTask,
                RawSystemPrompt: null, PermissionRules: null, TasksMcp: null);

            factory.Create(session, context);

            session.Provider.Should().Be("glm", "согласованная пара — guard молчит");
        }
        finally { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); }
    }
}
