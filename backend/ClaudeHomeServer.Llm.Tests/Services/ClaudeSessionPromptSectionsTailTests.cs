using System.Collections.Concurrent;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Prompts;
using ClaudeHomeServer.Services.Turn;
using ClaudeHomeServer.Services.Llm;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace ClaudeHomeServer.Tests.Services;

// Часть (b) резки ClaudeSessionPromptSectionsOrderTests: хвост хода и состояния редакторов
// (InTurnTail, ToolDescriptions, TurnContext, Audio, Video).
public class ClaudeSessionPromptSectionsTailTests : ClaudeSessionPromptSectionsTestBase
{

    // Сторож хвоста хода (ADR-018 §10.4, риск 1 плана v2): секция InTurnTail (состояние редактора
    // картинки) уезжает хвостом хода ПРИ ЛЮБОЙ настройке RecallInTurnText и в системный блок не
    // попадает никогда. Иначе у провайдера без ручки она обнуляла бы prefix cache всей истории:
    // системный блок чата картинки обязан быть одинаков на ходах с разным состоянием.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InTurnTail_ВсегдаХвостомХода_СистемныйБлокНеЗависитОтСостояния(bool recallInTurnText)
    {
        var first = await RunTailTurnAsync(recallInTurnText, "МАРКЕР_СОСТОЯНИЯ промпт «лампа»");
        var second = await RunTailTurnAsync(recallInTurnText, "МАРКЕР_СОСТОЯНИЯ промпт «окно», 3 варианта");

        first.SystemPrompt.Should().NotContain("МАРКЕР_СОСТОЯНИЯ",
            "блок состояния редактора не должен попадать в системный блок ни при какой настройке провайдера");
        first.SystemPrompt.Should().Be(second.SystemPrompt,
            "системный блок чата картинки одинаков на ходах с разным состоянием редактора — prefix cache жив");

        var tail = first.Sections.Should().ContainSingle(s => s.Key == "image-editor-state").Subject;
        tail.Kind.Should().Be("turn", "секция едет вклейкой в текст хода");
        tail.Title.Should().Be("Состояние редактора");
        tail.Text.Should().Contain("«лампа»");
    }

    // /compact и пользовательские слэш-команды: хвост перед ними ломает распознавание в CLI.
    // Хвост не клеится и в снимок «что ушло модели» не попадает; проза рядом — по-прежнему с хвостом.
    [Theory]
    [InlineData("/compact")]
    [InlineData("  /compact фокус на тестах")]
    public async Task СлэшКоманда_ХвостНеКлеится(string userText)
    {
        var slash = await RunTailTurnAsync(true, "МАРКЕР_СОСТОЯНИЯ", userText: userText);
        slash.Sections.Should().NotContain(s => s.Key == "image-editor-state",
            "хвост перед слэш-командой превращает её в обычную реплику");

        var prose = await RunTailTurnAsync(true, "МАРКЕР_СОСТОЯНИЯ", userText: "привет");
        prose.Sections.Should().Contain(s => s.Key == "image-editor-state");
    }

    // Хэш прошлого хвоста обновляется только когда хвост реально ушёл: слэш-команда его не трогает.
    // В ОДНОЙ сессии: проза(A) → смена хвоста на B → /compact → проза(B) — B обязан уехать,
    // иначе хэш, записанный на слэш-ходе, подавил бы его как «уже отправленный».
    [Fact]
    public async Task СлэшКоманда_ХэшХвостаНеОбновляет_СледующаяПрозаНесётНовыйХвост()
    {
        var state = "МАРКЕР_А";
        var snapshots = new ConcurrentQueue<IReadOnlyList<PromptSectionDto>>();
        var got = new SemaphoreSlim(0);
        var bus = new TurnEventBus();
        bus.OnFilter<PromptAssembling>(100, (e, next) =>
        {
            e.Sections.Add(new ClaudeHomeServer.Services.Turn.PromptSection(
                "image-editor-state", state, "Состояние редактора", InTurnTail: true));
            return next();
        }, "Test.ImageEditorState");
        bus.OnNotification<PromptAssembled>(e =>
        {
            if (e.Snapshot?.Draft is { } draft) { snapshots.Enqueue(draft.Sections); got.Release(); }
            return Task.CompletedTask;
        }, "Test.Snapshot");

        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["LlmProviders:test-local:DisplayName"] = "Тестовый локальный",
                ["LlmProviders:test-local:AnthropicBaseUrl"] = "http://127.0.0.1:65535",
                ["LlmProviders:test-local:IsLocal"] = "true",
                ["LlmProviders:test-local:RecallInTurnText"] = "true",
                ["LlmProviders:test-local:Models:0:Id"] = "qwen-test-27b",
                ["LlmProviders:test-local:Models:0:DisplayName"] = "Qwen Test",
            }).AddInMemoryCollection(IsolatedProfiles()).Build();
        var context = new LlmSessionContext(
            RootPath: _root, OnMessage: _ => Task.CompletedTask,
            RawSystemPrompt: null, BuiltInSystemPrompt: ClaudeHomeServer.Services.ProjectManager.BuiltInSystemPrompt,
            PermissionRules: null, TasksMcp: null,
            Launcher: new CapturingLauncher(_clis, _argsCaptured),
            SystemPromptViaFile: false, Events: bus);

        await using var session = new ClaudeHomeServer.Services.Llm.Claude.ClaudeSession(
            new Session { Model = "qwen-test-27b" }, context, providers: new LlmProviderRegistry(config));

        async Task<bool> TurnAsync(string text)
        {
            await session.SendMessageAsync(text);
            (await got.WaitAsync(TimeSpan.FromSeconds(15))).Should().BeTrue("снимок промпта хода обязан быть опубликован");
            snapshots.TryDequeue(out var s).Should().BeTrue();
            return s!.Any(x => x.Key == "image-editor-state");
        }

        (await TurnAsync("привет")).Should().BeTrue("первая проза несёт хвост");
        state = "МАРКЕР_Б";
        (await TurnAsync("/compact")).Should().BeFalse("слэш-команду хвост не трогает");
        (await TurnAsync("ещё привет")).Should().BeTrue("хвост Б ещё не уходил — обязан уехать со следующей прозой");
    }

    // Манифест recall отражает фактически ушедшее: слэш-команда при хвосте хода хвост не несёт —
    // манифест не шлём; проза с тем же набором манифест получает
    [Theory]
    [InlineData("/compact", false)]
    [InlineData("привет", true)]
    public async Task МанифестRecall_СлэшКомандаПриХвосте_НеУходит(string userText, bool expectManifest)
    {
        var sent = new ConcurrentQueue<ServerMessage>();
        static void Manifest(TurnEventBus bus) => bus.OnFilter<PromptAssembling>(200, (e, next) =>
        {
            e.ManifestItems.Add(new ClaudeHomeServer.Services.Llm.RecallItem("note", "n1", "Заметка", null));
            return next();
        }, "Test.Manifest");

        await RunTailTurnAsync(true, "МАРКЕР_СОСТОЯНИЯ", Manifest, userText: userText,
            tweak: c => c with { OnMessage = m => { sent.Enqueue(m); return Task.CompletedTask; } });

        sent.OfType<RecallManifestMessage>().Any().Should().Be(expectManifest);
    }

    [Theory]
    [InlineData("/compact", true)]
    [InlineData("  \n/compact", true)]
    [InlineData("привет /compact", false)]
    [InlineData("", false)]
    public void IsSlashCommand_ВедущийСлэшПослеTrimStart(string text, bool expected) =>
        ClaudeHomeServer.Services.Llm.Claude.ClaudeSession.IsSlashCommand(text).Should().Be(expected);

    // Секция tool-descriptions (живая русская подпись к инструменту): стабильная, в системном
    // блоке, до voice-mode и слоя персоны; системный блок от хода к ходу не меняется.
    [Fact]
    public async Task ToolDescriptions_ВСистемномБлоке_ДоГолосаИПерсоны_Стабильна()
    {
        static void PersonaLayer(TurnEventBus bus) => bus.OnFilter<PromptAssembling>(900, async (e, next) =>
        {
            e.Sections.Add(new ClaudeHomeServer.Services.Turn.PromptSection(
                "persona-layer", "МАРКЕР_ПЕРСОНЫ Ты — Тестовая Персона."));
            await next();
        }, "Test.PersonaLayer");

        var first = await RunTailTurnAsync(false, "МАРКЕР_СОСТОЯНИЯ первый", PersonaLayer);
        var second = await RunTailTurnAsync(false, "МАРКЕР_СОСТОЯНИЯ второй", PersonaLayer);

        var prompt = first.SystemPrompt;
        var toolIdx = prompt.IndexOf(ToolDescriptionPrompts.SectionText, StringComparison.Ordinal);
        var voiceIdx = prompt.IndexOf(VoicePrompts.LongAnswerSectionText, StringComparison.Ordinal);
        var personaIdx = prompt.IndexOf("МАРКЕР_ПЕРСОНЫ", StringComparison.Ordinal);

        toolIdx.Should().BeGreaterThanOrEqualTo(0, "правило про description обязано уехать модели в системном блоке");
        voiceIdx.Should().BeGreaterThan(toolIdx, "tool-descriptions стоит до voice-mode");
        personaIdx.Should().BeGreaterThan(toolIdx, "tool-descriptions стоит до слоя персоны");

        var section = first.Sections.Should().ContainSingle(s => s.Key == "tool-descriptions").Subject;
        section.Kind.Should().NotBe("turn", "секция стабильная и живёт в системном блоке, а не хвостом хода");
        first.SystemPrompt.Should().Be(second.SystemPrompt, "стабильная секция не рвёт prefix cache между ходами");
    }

    // Хвост «Контекст хода» (ADR-023 §3.1, 2б-2): секция turn-context едет только хвостом при любой настройке
    // RecallInTurnText, а системный блок не зависит от того, что в контексте чата
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TurnContext_ВсегдаХвостомХода_СистемныйБлокНеЗависитОтКонтекста(bool recallInTurnText)
    {
        var first = await RunTurnContextTurnAsync(recallInTurnText, "МАРКЕР_КОНТЕКСТА_ОДИН.md");
        var second = await RunTurnContextTurnAsync(recallInTurnText, "МАРКЕР_КОНТЕКСТА_ДВА.md");

        first.SystemPrompt.Should().NotContain("МАРКЕР_КОНТЕКСТА").And.NotContain("## Контекст хода",
            "контекст хода меняется от хода к ходу и в системном блоке обнулял бы prefix cache");
        first.SystemPrompt.Should().Be(second.SystemPrompt);

        var tail = first.Sections.Should().ContainSingle(s => s.Key == "turn-context").Subject;
        tail.Kind.Should().Be("turn", "секция едет вклейкой в текст хода");
        tail.Title.Should().Be("Контекст хода");
        tail.Text.Should().Contain("МАРКЕР_КОНТЕКСТА_ОДИН.md");
    }

    private Task<(string SystemPrompt, IReadOnlyList<PromptSectionDto> Sections)> RunTurnContextTurnAsync(
        bool recallInTurnText, string label)
    {
        var item = new ClaudeHomeServer.Services.ChatContext.ContextItem("ci_1", "project-file",
            new System.Text.Json.Nodes.JsonObject { ["path"] = label }, null,
            ClaudeHomeServer.Services.ChatContext.ContextActor.Human, DateTime.UtcNow);
        var store = new Moq.Mock<ClaudeHomeServer.Services.ChatContext.IChatContextStore>();
        store.Setup(x => x.Get(Moq.It.IsAny<string>(), Moq.It.IsAny<string>()))
            .Returns(new ClaudeHomeServer.Services.ChatContext.ChatContextState(1, item, []));
        var contributor = new TurnContextContributor(
            new ServiceCollection()
                .AddSingleton(store.Object).BuildServiceProvider(),
            new ClaudeHomeServer.Services.ChatContext.ContextKindRegistry(
                [new ClaudeHomeServer.Services.ChatContext.ProjectFileContextKind()]),
            new Moq.Mock<ClaudeHomeServer.Services.IProjectManager>().Object);
        return RunTailTurnAsync(recallInTurnText, "МАРКЕР_СОСТОЯНИЯ",
            bus => PromptSectionContributorsRegistration.RegisterAll(bus, [contributor]),
            new Session { Model = "qwen-test-27b", OwnerId = "u1" });
    }

    // Блок «Звук в этом чате» (ADR-021 §5): признак HasAudioEditorMcp ClaudeSession собирает сам из
    // доставки сервера audio-editor в конфиг хода; секция едет хвостом, в системный блок не попадает
    private static readonly AudioEditorMcpContext AudioEditorMcp = new("http://127.0.0.1:5000", () => "tok", UseHttp: true);

    private async Task<(string SystemPrompt, PromptSectionDto? Section)> AudioEditorStateSectionAsync(
        bool recallInTurnText, Func<LlmSessionContext, LlmSessionContext> tweak,
        Dictionary<string, string?>? providerConfig = null)
    {
        var contributor = new ClaudeHomeServer.Services.AudioEditor.Chats.AudioEditorStateContributor();
        var (systemPrompt, sections) = await RunTailTurnAsync(recallInTurnText, "МАРКЕР_СОСТОЯНИЯ",
            bus => PromptSectionContributorsRegistration.RegisterAll(bus, [contributor]),
            ProjectChat(), tweak, providerConfig);
        return (systemPrompt, sections.SingleOrDefault(s => s.Key == "audio-editor-state"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AudioEditorState_СерверДоставлен_ХвостомХода(bool recallInTurnText)
    {
        var (systemPrompt, section) = await AudioEditorStateSectionAsync(recallInTurnText,
            c => c with { AudioEditorMcp = AudioEditorMcp });

        section.Should().NotBeNull("сервер audio-editor доехал до хода — блок «Звук» обязан прийти");
        section!.Kind.Should().Be("turn", "блок едет вклейкой в текст хода");
        section.Text.Should().Contain(ClaudeHomeServer.Services.AudioEditor.Chats.AudioEditorStateContributor.PriorityRuleFor(personal: false));
        systemPrompt.Should().NotContain("## Звук в этом чате",
            "блок меняется от хода к ходу и в системный блок не попадает ни при какой настройке провайдера");
    }

    [Theory]
    [InlineData("модуль звука выключен")]
    [InlineData("TrimMcpServers без audio-editor")]
    public async Task AudioEditorState_СервераНетУХода_БлокаНет(string why)
    {
        var (_, section) = why == "модуль звука выключен"
            ? await AudioEditorStateSectionAsync(false, c => c)
            : await AudioEditorStateSectionAsync(false, c => c with { AudioEditorMcp = AudioEditorMcp }, TrimToTasks);

        section.Should().BeNull($"{why}: audio_* у хода нет («No such tool available»)");
    }

    // Блок «Видео в этом чате» (ADR-022 §5): признак HasVideoEditorMcp ClaudeSession собирает сам из доставки сервера
    // video-editor в конфиг хода; секция едет хвостом, в системный блок не попадает
    private static readonly VideoEditorMcpContext VideoEditorMcp = new("http://127.0.0.1:5000", () => "tok", UseHttp: true);

    private async Task<(string SystemPrompt, PromptSectionDto? Section)> VideoEditorStateSectionAsync(
        bool recallInTurnText, Func<LlmSessionContext, LlmSessionContext> tweak,
        Dictionary<string, string?>? providerConfig = null)
    {
        var contributor = new ClaudeHomeServer.Services.VideoEditor.Chats.VideoEditorStateContributor();
        var (systemPrompt, sections) = await RunTailTurnAsync(recallInTurnText, "МАРКЕР_СОСТОЯНИЯ",
            bus => PromptSectionContributorsRegistration.RegisterAll(bus, [contributor]),
            ProjectChat(), tweak, providerConfig);
        return (systemPrompt, sections.SingleOrDefault(s => s.Key == "video-editor-state"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VideoEditorState_СерверДоставлен_ХвостомХода(bool recallInTurnText)
    {
        var (systemPrompt, section) = await VideoEditorStateSectionAsync(recallInTurnText,
            c => c with { VideoEditorMcp = VideoEditorMcp });

        section.Should().NotBeNull("сервер video-editor доехал до хода — блок «Видео» обязан прийти");
        section!.Kind.Should().Be("turn", "блок едет вклейкой в текст хода");
        section.Text.Should().Contain(ClaudeHomeServer.Services.VideoEditor.Chats.VideoEditorStateContributor.PriorityRule)
            .And.Contain(ClaudeHomeServer.Services.VideoEditor.Chats.VideoEditorStateContributor.PaceRule);
        systemPrompt.Should().NotContain("## Видео в этом чате",
            "блок меняется от хода к ходу и в системный блок не попадает ни при какой настройке провайдера");
    }

    [Theory]
    [InlineData("модуль видео выключен")]
    [InlineData("TrimMcpServers без video-editor")]
    public async Task VideoEditorState_СервераНетУХода_БлокаНет(string why)
    {
        var (_, section) = why == "модуль видео выключен"
            ? await VideoEditorStateSectionAsync(false, c => c)
            : await VideoEditorStateSectionAsync(false, c => c with { VideoEditorMcp = VideoEditorMcp }, TrimToTasks);

        section.Should().BeNull($"{why}: video_* у хода нет («No such tool available»)");
    }
}
