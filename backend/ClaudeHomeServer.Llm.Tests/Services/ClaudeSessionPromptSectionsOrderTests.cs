using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Llm.Claude;
using ClaudeHomeServer.Services.Prompts;
using ClaudeHomeServer.Services.Turn;
using ClaudeHomeServer.Services.Llm;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Tests.Services;

// Сторож-тест порядка секций системного промпта (план «Секции промптов» этап 3, контракт):
// … → recall-memory (без досье) → prompt-sections → dossier-recall → persona-bindings →
// code-graph → слой персоны (voice-mode ПОСЛЕДНИМ). Сборка секций — приватный код глубоко
// внутри RunTurnAsync (реального шва для юнита нет), поэтому гоняем настоящий ClaudeSession
// с fake-CLI launcher (паттерн ClaudeSessionDiedEmptyRetryTests) и читаем фактический аргумент
// --append-system-prompt, ушедший бы модели. «voice-mode последний» на уровне текста самой
// оговорки уже сторожит PersonaPromptBuilderTests (prompt.Should().EndWith(PersonaOverride)) —
// здесь эмулируем персону с voiceMode=true (оговорка в хвосте PersonaPromptProvider, как
// реально строит SessionManager.BuildPersonaLayer) и проверяем, что НИЧЕГО из новых секций
// не оказалось после неё — Combine клеит слой персоны последним.
public class ClaudeSessionPromptSectionsOrderTests : ClaudeSessionPromptSectionsTestBase
{

    [Fact]
    public async Task RecallПромптСекцииДосьеПривязкиГраф_ВПорядкеКонтрактаПлана_ГолосПоследним()
    {
        var messages = new List<ServerMessage>();
        var info = new Session { VoiceMode = true };

        // Этап 2: 6 провайдеров + DossierTrailerHint заменены реестром IPromptSectionContributor
        // через Filter-событие prompt/assembling. Тест эмулирует контрибьюторов на шине:
        // каждый подписчик добавляет свою секцию (или две — recall-memory + dossier-recall).
        var bus = new TurnEventBus();
        bus.OnFilter<PromptAssembling>(100, (e, next) =>
        {
            e.Sections.Add(new ClaudeHomeServer.Services.Turn.PromptSection(
                "dossier-trailer", "МАРКЕР_DOSSIER_TRAILER"));
            return next();
        }, "Test.DossierTrailer");
        bus.OnFilter<PromptAssembling>(200, async (e, next) =>
        {
            // Эмулируем NotesRecallContributor: гейт notesMcp != null живёт в IsEnabled,
            // здесь он true (см. BuildBaseContext: notes подключён).
            e.Sections.Add(new ClaudeHomeServer.Services.Turn.PromptSection(
                "recall-notes", "МАРКЕР_RECALL_NOTES"));
            await next();
        }, "Test.NotesRecall");
        bus.OnFilter<PromptAssembling>(300, async (e, next) =>
        {
            // PersonaRecallContributor эмитит recall-memory + (при splitDossier) dossier-recall
            e.Sections.Add(new ClaudeHomeServer.Services.Turn.PromptSection(
                "recall-memory", "МАРКЕР_RECALL_MEMORY текст памяти"));
            e.Sections.Add(new ClaudeHomeServer.Services.Turn.PromptSection(
                "dossier-recall", "МАРКЕР_DOSSIER_RECALL текст досье"));
            await next();
        }, "Test.PersonaRecall");
        bus.OnFilter<PromptAssembling>(400, async (e, next) =>
        {
            e.Sections.Add(new ClaudeHomeServer.Services.Turn.PromptSection(
                "prompt-sections", "МАРКЕР_PROMPT_SECTIONS текст секций"));
            await next();
        }, "Test.PromptSections");
        bus.OnFilter<PromptAssembling>(500, async (e, next) =>
        {
            e.Sections.Add(new ClaudeHomeServer.Services.Turn.PromptSection(
                "persona-bindings", "МАРКЕР_PERSONA_BINDINGS текст привязок"));
            await next();
        }, "Test.PersonaBindings");
        bus.OnFilter<PromptAssembling>(600, async (e, next) =>
        {
            e.Sections.Add(new ClaudeHomeServer.Services.Turn.PromptSection(
                "code-graph", "МАРКЕР_CODE_GRAPH текст графа"));
            await next();
        }, "Test.CodeGraph");
        bus.OnFilter<PromptAssembling>(900, async (e, next) =>
        {
            // Эмулируем PersonaLayerContributor: эмуляция PersonaPromptBuilder.Build,
            // дописывающего оговорку voice-mode в конец слоя персоны при voiceMode=true.
            e.Sections.Add(new ClaudeHomeServer.Services.Turn.PromptSection(
                "persona-layer",
                "МАРКЕР_ПЕРСОНЫ Ты — Тестовая Персона.\n\n" + VoicePrompts.PersonaOverride));
            await next();
        }, "Test.PersonaLayer");

        // Изолированный профиль (п.3 разреза 0d1f4bde): ход идёт родным путём Claude —
        // пустой реестр не резолвит провайдера по родной модели (ResolveByModel → null),
        // но DataPath и пользовательский ~/.claude смотрят в папку теста, а не в общие.
        var config = new ConfigurationBuilder().AddInMemoryCollection(IsolatedProfiles()).Build();

        var context = new LlmSessionContext(
            RootPath: _root,
            OnMessage: m => { lock (messages) messages.Add(m); return Task.CompletedTask; },
            RawSystemPrompt: null, BuiltInSystemPrompt: ClaudeHomeServer.Services.ProjectManager.BuiltInSystemPrompt,
            PermissionRules: null,
            TasksMcp: null,
            MemoryMcp: new MemoryMcpContext("http://memory.invalid", () => "tok", "persona-1"),
            Launcher: new CapturingLauncher(_clis, _argsCaptured),
            SystemPromptViaFile: false,
            Events: bus);

        var session = new ClaudeSession(info, context, providers: new LlmProviderRegistry(config));
        await using var _ = session;

        await session.SendMessageAsync("привет");

        var args = await WhenAnyAsync(_argsCaptured.Task, TimeSpan.FromSeconds(15));
        var idx = args.ToList().IndexOf("--append-system-prompt");
        idx.Should().BeGreaterThanOrEqualTo(0, "все провайдеры непустые — аргумент обязан присутствовать");
        var prompt = args[idx + 1];

        int Pos(string marker)
        {
            var p = prompt.IndexOf(marker, StringComparison.Ordinal);
            p.Should().BeGreaterThanOrEqualTo(0, $"маркер «{marker}» обязан попасть в промпт хода");
            return p;
        }

        var recallIdx = Pos("МАРКЕР_RECALL_MEMORY");
        var sectionsIdx = Pos("МАРКЕР_PROMPT_SECTIONS");
        var dossierIdx = Pos("МАРКЕР_DOSSIER_RECALL");
        var bindingsIdx = Pos("МАРКЕР_PERSONA_BINDINGS");
        var codeGraphIdx = Pos("МАРКЕР_CODE_GRAPH");
        var personaIdx = Pos("МАРКЕР_ПЕРСОНЫ");
        var voiceOverrideIdx = prompt.IndexOf(VoicePrompts.PersonaOverride, StringComparison.Ordinal);
        voiceOverrideIdx.Should().BeGreaterThanOrEqualTo(0, "оговорка voice-mode обязана попасть в слой персоны");

        recallIdx.Should().BeLessThan(sectionsIdx, "контракт плана: recall-memory → prompt-sections");
        sectionsIdx.Should().BeLessThan(dossierIdx, "контракт плана: prompt-sections → dossier-recall");
        dossierIdx.Should().BeLessThan(bindingsIdx, "контракт плана: dossier-recall → persona-bindings");
        bindingsIdx.Should().BeLessThan(codeGraphIdx, "контракт плана: persona-bindings → code-graph");
        codeGraphIdx.Should().BeLessThan(personaIdx, "code-graph → слой персоны (Combine клеит его последним)");
        personaIdx.Should().BeLessThan(voiceOverrideIdx, "оговорка — хвост слоя персоны, а не его начало");

        // voice-mode ПОСЛЕДНИМ: после оговорки в промпте не должно остаться ничего значимого
        var tail = prompt[(voiceOverrideIdx + VoicePrompts.PersonaOverride.Length)..];
        tail.Trim().Should().BeEmpty(
            "оговорка голосового режима обязана быть последним текстом всего промпта хода");
    }

    // Сторож RecallInTurnText (2026-09-16): у провайдера с включённой ручкой нестабильные
    // секции обязаны ИСЧЕЗНУТЬ из системного блока — он должен быть побайтово одинаков
    // между ходами, иначе prefix cache движка рвёт кэш всей истории. Стабильные секции
    // при этом остаются на месте: ручка не про «меньше промпта», а про место доставки.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RecallInTurnText_УбираетНестабильныеСекцииИзСистемногоБлока(bool enabled)
    {
        var messages = new List<ServerMessage>();
        var info = new Session { Model = "qwen-test-27b" };

        var bus = new TurnEventBus();
        bus.OnFilter<PromptAssembling>(100, (e, next) =>
        {
            // Стабильная секция — остаётся в системном блоке при любой настройке
            e.Sections.Add(new ClaudeHomeServer.Services.Turn.PromptSection(
                "dossier-trailer", "МАРКЕР_DOSSIER_TRAILER"));
            return next();
        }, "Test.DossierTrailer");
        bus.OnFilter<PromptAssembling>(150, (e, next) =>
        {
            // Устав сферы: стабильная секция — остаётся в системном блоке и при RecallInTurnText
            e.Sections.Add(new ClaudeHomeServer.Services.Turn.PromptSection(
                "sphere-charter", "МАРКЕР_УСТАВ_СФЕРЫ"));
            return next();
        }, "Test.SphereCharter");
        bus.OnFilter<PromptAssembling>(200, async (e, next) =>
        {
            e.Sections.Add(new ClaudeHomeServer.Services.Turn.PromptSection(
                "recall-notes", "МАРКЕР_RECALL_NOTES"));
            e.Sections.Add(new ClaudeHomeServer.Services.Turn.PromptSection(
                "recall-memory", "МАРКЕР_RECALL_MEMORY\n## Память сферы «Сфера»\nМАРКЕР_ПАМЯТЬ_СФЕРЫ"));
            e.Sections.Add(new ClaudeHomeServer.Services.Turn.PromptSection(
                "dossier-recall", "МАРКЕР_DOSSIER_RECALL"));
            e.Sections.Add(new ClaudeHomeServer.Services.Turn.PromptSection(
                "persona-bindings", "МАРКЕР_PERSONA_BINDINGS"));
            e.Sections.Add(new ClaudeHomeServer.Services.Turn.PromptSection(
                "code-graph", "МАРКЕР_CODE_GRAPH"));
            await next();
        }, "Test.Unstable");

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["LlmProviders:test-local:DisplayName"] = "Тестовый локальный",
            ["LlmProviders:test-local:AnthropicBaseUrl"] = "http://127.0.0.1:65535",
            ["LlmProviders:test-local:IsLocal"] = "true",
            ["LlmProviders:test-local:RecallInTurnText"] = enabled ? "true" : "false",
            ["LlmProviders:test-local:Models:0:Id"] = "qwen-test-27b",
            ["LlmProviders:test-local:Models:0:DisplayName"] = "Qwen Test",
        }).AddInMemoryCollection(IsolatedProfiles()).Build();

        var context = new LlmSessionContext(
            RootPath: _root,
            OnMessage: m => { lock (messages) messages.Add(m); return Task.CompletedTask; },
            RawSystemPrompt: null, BuiltInSystemPrompt: ClaudeHomeServer.Services.ProjectManager.BuiltInSystemPrompt,
            PermissionRules: null,
            TasksMcp: null,
            MemoryMcp: new MemoryMcpContext("http://memory.invalid", () => "tok", "persona-1"),
            Launcher: new CapturingLauncher(_clis, _argsCaptured),
            SystemPromptViaFile: false,
            Events: bus);

        var session = new ClaudeSession(info, context, providers: new LlmProviderRegistry(config));
        await using var _ = session;

        await session.SendMessageAsync("привет");

        var args = await WhenAnyAsync(_argsCaptured.Task, TimeSpan.FromSeconds(15));
        var idx = args.ToList().IndexOf("--append-system-prompt");
        idx.Should().BeGreaterThanOrEqualTo(0, "стабильные секции непустые — аргумент обязан присутствовать");
        var prompt = args[idx + 1];

        prompt.Should().Contain("МАРКЕР_DOSSIER_TRAILER",
            "стабильная секция остаётся в системном блоке при любой настройке ручки");
        prompt.Should().Contain("МАРКЕР_УСТАВ_СФЕРЫ",
            "устав сферы стабилен: при RecallInTurnText он остаётся в системном блоке, а не уезжает хвостом");

        foreach (var marker in new[]
                 { "МАРКЕР_RECALL_NOTES", "МАРКЕР_RECALL_MEMORY", "МАРКЕР_ПАМЯТЬ_СФЕРЫ", "МАРКЕР_DOSSIER_RECALL",
                   "МАРКЕР_PERSONA_BINDINGS", "МАРКЕР_CODE_GRAPH" })
        {
            if (enabled)
                prompt.Should().NotContain(marker,
                    "при RecallInTurnText нестабильная секция уезжает хвостом хода, а не в системный блок");
            else
                prompt.Should().Contain(marker, "с выключенной ручкой поведение прежнее");
        }
    }

    // Сторож 2026-10-09: ручка RecallInTurnText провайдера действует и у персоны с ВЫКЛЮЧЕННЫМ
    // облегчённым контекстом (LightProfileFor → null). Иначе рабочий фокус и recall памяти
    // персоны (они меняются между чатами) стояли в середине системного блока и рвали prefix
    // cache движка: первый вход чата персоны на local-qwen 50 с вместо 1 с при прогретом кэше.
    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public async Task RecallInTurnText_ДействуетУПерсоныБезОблегчённогоКонтекста(bool? lightContext)
    {
        var messages = new List<ServerMessage>();
        var info = new Session { Model = "qwen-test-27b", PersonaId = "persona-1" };

        var bus = new TurnEventBus();
        bus.OnFilter<PromptAssembling>(100, (e, next) =>
        {
            e.Sections.Add(new ClaudeHomeServer.Services.Turn.PromptSection(
                "dossier-trailer", "МАРКЕР_DOSSIER_TRAILER"));
            e.Sections.Add(new ClaudeHomeServer.Services.Turn.PromptSection(
                "recall-notes", "МАРКЕР_RECALL_NOTES"));
            e.Sections.Add(new ClaudeHomeServer.Services.Turn.PromptSection(
                "recall-memory", "Твоё текущее дело (рабочая память): МАРКЕР_ФОКУС\nМАРКЕР_RECALL_MEMORY"));
            e.Sections.Add(new ClaudeHomeServer.Services.Turn.PromptSection(
                "persona-bindings", "МАРКЕР_PERSONA_BINDINGS"));
            return next();
        }, "Test.Sections");

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["LlmProviders:test-local:DisplayName"] = "Тестовый локальный",
            ["LlmProviders:test-local:AnthropicBaseUrl"] = "http://127.0.0.1:65535",
            ["LlmProviders:test-local:IsLocal"] = "true",
            ["LlmProviders:test-local:RecallInTurnText"] = "true",
            ["LlmProviders:test-local:Models:0:Id"] = "qwen-test-27b",
            ["LlmProviders:test-local:Models:0:DisplayName"] = "Qwen Test",
        }).AddInMemoryCollection(IsolatedProfiles()).Build();

        var context = new LlmSessionContext(
            RootPath: _root,
            OnMessage: m => { lock (messages) messages.Add(m); return Task.CompletedTask; },
            RawSystemPrompt: null, BuiltInSystemPrompt: ClaudeHomeServer.Services.ProjectManager.BuiltInSystemPrompt,
            PermissionRules: null,
            TasksMcp: null,
            MemoryMcp: new MemoryMcpContext("http://memory.invalid", () => "tok", "persona-1"),
            Launcher: new CapturingLauncher(_clis, _argsCaptured),
            SystemPromptViaFile: false,
            PersonaProvider: () => new Persona { Id = "persona-1", Name = "Тестовая Персона", LightContext = lightContext },
            Events: bus);

        var session = new ClaudeSession(info, context, providers: new LlmProviderRegistry(config));
        await using var _ = session;

        await session.SendMessageAsync("привет");

        var args = await WhenAnyAsync(_argsCaptured.Task, TimeSpan.FromSeconds(15));
        var idx = args.ToList().IndexOf("--append-system-prompt");
        idx.Should().BeGreaterThanOrEqualTo(0, "стабильная секция непустая — аргумент обязан присутствовать");
        var prompt = args[idx + 1];

        prompt.Should().Contain("МАРКЕР_DOSSIER_TRAILER", "стабильная секция остаётся в системном блоке");
        foreach (var marker in new[] { "МАРКЕР_RECALL_NOTES", "МАРКЕР_ФОКУС", "МАРКЕР_RECALL_MEMORY", "МАРКЕР_PERSONA_BINDINGS" })
            prompt.Should().NotContain(marker,
                "ручка провайдера уводит нестабильную секцию хвостом хода и без облегчённого контекста персоны");
    }
}
