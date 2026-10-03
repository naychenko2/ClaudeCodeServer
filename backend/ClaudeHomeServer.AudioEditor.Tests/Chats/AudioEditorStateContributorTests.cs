using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.AudioEditor;
using ClaudeHomeServer.Services.AudioEditor.Chats;
using ClaudeHomeServer.Services.AudioEditor.Mcp;
using ClaudeHomeServer.Services.AudioEditor.Prefs;
using ClaudeHomeServer.Services.AudioEditor.Threads;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Turn;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.AudioEditor.Tests.Chats;

// Блок «Звук в этом чате» (ADR-021 §5, вопрос 1): есть только при доставленном сервере audio-editor,
// едет хвостом хода, ставит audio_* выше прямых local_* и коротко показывает фокус и выбор в полосе
public sealed class AudioEditorStateContributorTests : IDisposable
{
    private const string Owner = "owner-1";
    private const string Chat = "chat-1";
    private const string ProjectId = "p-1";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "audio-state-" + Guid.NewGuid().ToString("N"));
    private readonly AudioThreadStore _store;
    private readonly AudioPrefsService _prefs;

    public AudioEditorStateContributorTests()
    {
        _store = new AudioThreadStore(Path.Combine(_dir, AudioThreadStore.DirName));
        _prefs = new AudioPrefsService(new AudioPrefsStore(Path.Combine(_dir, AudioPrefsStore.DirName)));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private sealed class Flags(params string[] on) : IFeatureFlagGate
    {
        public bool IsEnabled(string userId, string key) => on.Contains(key);
    }

    private AudioEditorStateContributor Contributor(bool flag = true, bool agentLaunch = true, bool contextRow = false) =>
        new(flag ? new Flags(contextRow ? [FeatureFlagKeys.AudioEditor, FeatureFlagKeys.ComposerContextRow] : [FeatureFlagKeys.AudioEditor])
                : new Flags(), _store, _prefs,
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                [AudioEditorToolset.AgentLaunchKey] = agentLaunch ? "true" : "false",
            }).Build());

    private static PromptSessionContext Context(bool hasAudioEditorMcp = true, bool personal = false) =>
        new(new Session { Id = Chat, OwnerId = Owner, ProjectId = personal ? null : ProjectId }, Owner, null, "/root",
            HasAudioEditorMcp: hasAudioEditorMcp);

    private async Task<PromptSection> SectionAsync(PromptSessionContext? context = null, bool agentLaunch = true,
        bool contextRow = false) =>
        (await Contributor(agentLaunch: agentLaunch, contextRow: contextRow).BuildAsync(context ?? Context(), "озвучь заставку"))!
        .Sections.Should().ContainSingle().Subject;

    [Fact]
    public void Сервер_доставлен_и_флаг_включён_блок_есть() =>
        Contributor().IsEnabled(Context()).Should().BeTrue();

    // TrimMcpServers без audio-editor или выключенный модуль: audio_* у хода нет, звать их нельзя
    [Fact]
    public void Сервер_не_доставлен_блока_нет() =>
        Contributor().IsEnabled(Context(hasAudioEditorMcp: false)).Should().BeFalse();

    [Fact]
    public void Флаг_выключен_блока_нет() =>
        Contributor(flag: false).IsEnabled(Context()).Should().BeFalse();

    [Fact]
    public async Task Едет_хвостом_хода_с_правилом_приоритета()
    {
        var section = await SectionAsync();

        section.Key.Should().Be(AudioEditorStateContributor.SectionKey);
        section.Title.Should().Be("Звук в этом чате");
        section.InTurnTail.Should().BeTrue("фокус меняется от хода к ходу — в системном блоке он обнулил бы prefix cache");
        section.Text.Should().StartWith("## Звук в этом чате\n")
            .And.Contain("В работе: ничего не выбрано")
            .And.Contain("Выбор человека в полосе «Звук»: по умолчанию")
            .And.Contain(AudioEditorStateContributor.PriorityRule);
        AudioEditorStateContributor.PriorityRule.Should().Contain("audio_new → audio_generate")
            .And.Contain("audio_generate с provider local")
            .And.Contain("local_speech")
            .And.Contain("только если человек явно попросил сделать напрямую, мимо редактора");
    }

    [Fact]
    public async Task Фокус_показан_файлом_и_версией()
    {
        var thread = _store.Open(Owner, Chat, "voice/intro.mp3", null, null).Thread!;
        _store.AddEditVersion(Owner, Chat, thread.Id, [new AudioVersionFile(AudioFileRoles.Main, "work/v1.mp3")], null);

        var text = (await SectionAsync()).Text;

        text.Should().Contain($"В работе: звук {thread.Id} — файл voice/intro.mp3 · версия 1 "
            + $"({AudioEditorStateContributor.FocusIsNotBindingText})");
    }

    [Fact]
    public async Task Выбор_поставщика_в_полосе_по_режимам()
    {
        _prefs.Save(Owner, AudioEditScope.Of(new Session { ProjectId = ProjectId }), AudioModes.Voice,
            new AudioModePrefs(null, "yandex", null, null, null));

        var text = (await SectionAsync()).Text;

        text.Should().Contain("Выбор человека в полосе «Звук»: голос — поставщик yandex, модель по умолчанию")
            .And.NotContain("музыка —");
    }

    // В личной области локального звука нет — правило не шлёт ни к provider local, ни к local_*
    [Fact]
    public async Task Личный_чат_без_локальных_моделей()
    {
        var text = (await SectionAsync(Context(personal: true))).Text;

        text.Should().Contain(AudioEditorStateContributor.PersonalPriorityRule)
            .And.NotContain("provider local").And.NotContain("local_speech");
    }

    [Fact]
    public async Task Без_запуска_агентом_правила_нет()
    {
        var text = (await SectionAsync(agentLaunch: false)).Text;

        text.Should().NotContain("audio_generate", "без AudioEditor:AgentLaunch инструмента audio_generate у агента нет");
    }

    // ── Строка контекста (ADR-023 §3.1, 2б-2): блок худеет, без флага — прежний текст байт-в-байт ──

    [Fact]
    public async Task Без_флага_строки_контекста_текст_блока_прежний_байт_в_байт()
    {
        var thread = _store.Open(Owner, Chat, "voice/intro.mp3", null, null).Thread!;

        var text = (await SectionAsync()).Text;

        text.Should().Be(
            "## Звук в этом чате\n"
            + $"В работе: звук {thread.Id} — файл voice/intro.mp3 · исходник (остался с прошлых сообщений и не обязывает его продолжать)\n"
            + "Выбор человека в полосе «Звук»: по умолчанию\n"
            + AudioEditorStateContributor.PriorityRule);
    }

    [Fact]
    public async Task При_строке_контекста_уходят_В_работе_и_Выбор_человека_правило_приоритета_остаётся()
    {
        _store.Open(Owner, Chat, "voice/intro.mp3", null, null);

        var text = (await SectionAsync(contextRow: true)).Text;

        text.Should().StartWith("## Звук в этом чате\n")
            .And.NotContain("В работе:").And.NotContain("Выбор человека в полосе").And.NotContain("полосе «Звук»");
        text.Should().Contain("audio_new → audio_generate").And.Contain("уважают выбор человека в строке контекста")
            .And.Contain("только если человек явно попросил сделать напрямую, мимо редактора");
        text.Should().Be("## Звук в этом чате\n" + AudioEditorStateContributor.PriorityRuleFor(personal: false, contextRow: true));
    }

    [Fact]
    public async Task При_строке_контекста_без_запуска_агентом_блок_пуст_и_секции_нет()
    {
        var contribution = await Contributor(agentLaunch: false, contextRow: true).BuildAsync(Context(), "озвучь");

        contribution.Should().BeNull("кроме правила приоритета в блоке ничего не осталось, а правила без audio_generate нет");
    }

    [Fact]
    public void Стоит_между_блоком_картинок_и_правилом_локальной_модели() =>
        Contributor().Order.Should().BeInRange(701, 709);
}
