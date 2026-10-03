using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Images.LocalMedia;
using ClaudeHomeServer.Services.Turn;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Tests.Services;

// Шаг 3 «локальная модель по умолчанию»: условия показа хвостовой секции и её тексты
public class LocalMediaDefaultContributorTests
{
    private sealed class Flags(params string[] on) : IFeatureFlagGate
    {
        public bool IsEnabled(string userId, string key) => on.Contains(key);
    }

    private static readonly string[] AllFlags =
        [FeatureFlagKeys.LocalMediaDefault, FeatureFlagKeys.ImageEditor, FeatureFlagKeys.AudioEditor];

    private static LocalMediaDefaultContributor Contributor(
        string[]? flags = null, bool localMedia = true, bool images = true, bool agentLaunch = true,
        bool audioAgentLaunch = true) =>
        new(new Flags(flags ?? AllFlags), new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["LocalMedia:Enabled"] = localMedia ? "true" : "false",
            ["Subsystems:images:Enabled"] = images ? "true" : "false",
            ["ImageEditor:AgentLaunch"] = agentLaunch ? "true" : "false",
            ["AudioEditor:AgentLaunch"] = audioAgentLaunch ? "true" : "false",
        }).Build());

    private static PromptSessionContext Project(bool hasLocalMediaMcp = true, bool unattended = false,
        Session? session = null, bool hasAudioEditorMcp = false) =>
        new(session ?? new Session { ProjectId = "p1", OwnerId = "u1" }, "u1", null, "/root",
            HasLocalMediaMcp: hasLocalMediaMcp, HasAudioEditorMcp: hasAudioEditorMcp, Unattended: unattended);

    private static PromptSessionContext Personal(bool unattended = false, bool hasImageEditorMcp = true,
        bool hasAudioEditorMcp = false) =>
        new(new Session { OwnerId = "u1" }, "u1", null, null,
            HasImageEditorMcp: hasImageEditorMcp, HasAudioEditorMcp: hasAudioEditorMcp, Unattended: unattended);

    [Fact]
    public void Проект_с_local_media_и_флагом_видит_правило() =>
        Contributor().IsEnabled(Project()).Should().BeTrue();

    [Fact]
    public void Личный_чат_с_флагом_редактора_видит_правило() =>
        Contributor().IsEnabled(Personal()).Should().BeTrue();

    [Fact]
    public void Флаг_выключен_правила_нет()
    {
        var c = Contributor(flags: [FeatureFlagKeys.ImageEditor]);
        c.IsEnabled(Project()).Should().BeFalse();
        c.IsEnabled(Personal()).Should().BeFalse();
    }

    // ReadOnly-персона и локальный проект ADR-016 не получают local-media (SessionManager
    // .BuildLocalMediaContext) — до секции это доходит как HasLocalMediaMcp = false
    [Theory]
    [InlineData("ReadOnly-персона")]
    [InlineData("локальный проект ADR-016")]
    [InlineData("LocalMediaHttp выключен")]
    public void Проект_без_local_media_правила_нет(string why) =>
        Contributor().IsEnabled(Project(hasLocalMediaMcp: false)).Should().BeFalse(why);

    [Fact]
    public void Личный_чат_без_флага_редактора_правила_нет() =>
        Contributor(flags: [FeatureFlagKeys.LocalMediaDefault]).IsEnabled(Personal()).Should().BeFalse();

    [Fact]
    public void Личный_чат_без_запуска_агентом_правила_нет() =>
        Contributor(agentLaunch: false).IsEnabled(Personal()).Should().BeFalse();

    // Находка (B) финального ревью: TrimMcpServers без image-editor (local-qwen) или выключенный
    // модуль — инструментов image_new/image_generate у хода нет, звать их правилом нельзя
    [Fact]
    public void Личный_чат_без_доставленного_редактора_правила_нет() =>
        Contributor().IsEnabled(Personal(hasImageEditorMcp: false)).Should().BeFalse();

    [Fact]
    public void Проекту_флаг_редактора_не_нужен() =>
        Contributor(flags: [FeatureFlagKeys.LocalMediaDefault]).IsEnabled(Project()).Should().BeTrue();

    // Ходы без человека: признак считает TurnAudience.IsUnattended, здесь — его три источника
    [Theory]
    [InlineData("TaskExecution")]
    [InlineData("AgentDepth=1")]
    [InlineData("AutomationRuleId")]
    public void Ход_без_человека_правила_не_получает(string source)
    {
        var session = source switch
        {
            "TaskExecution" => new Session { ProjectId = "p1", OwnerId = "u1", TaskExecution = true },
            "AutomationRuleId" => new Session { ProjectId = "p1", OwnerId = "u1", AutomationRuleId = "r1" },
            _ => new Session { ProjectId = "p1", OwnerId = "u1" },
        };
        var unattended = TurnAudience.IsUnattended(session, agentDepth: source == "AgentDepth=1" ? 1 : 0);

        Contributor().IsEnabled(Project(unattended: unattended, session: session)).Should().BeFalse(source);
        Contributor().IsEnabled(Personal(unattended: unattended)).Should().BeFalse(source);
    }

    [Fact]
    public void Локальные_модели_не_зарегистрированы_правила_нет()
    {
        Contributor(localMedia: false).IsEnabled(Project()).Should().BeFalse();
        Contributor(localMedia: false).IsEnabled(Personal()).Should().BeFalse();
    }

    [Fact]
    public void Подсистема_images_выключена_правила_нет()
    {
        Contributor(images: false).IsEnabled(Project()).Should().BeFalse();
        Contributor(images: false).IsEnabled(Personal()).Should().BeFalse();
    }

    [Fact]
    public void Без_владельца_правила_нет() =>
        Contributor().IsEnabled(new PromptSessionContext(new Session { ProjectId = "p1" }, null, null, "/root",
            HasLocalMediaMcp: true)).Should().BeFalse();

    [Fact]
    public async Task Проектный_вариант_хвостом_с_пометкой_и_локальным_видео()
    {
        var section = (await Contributor().BuildAsync(Project(), "нарисуй кота"))!.Sections.Should().ContainSingle().Subject;

        section.Key.Should().Be("local-media-default");
        section.InTurnTail.Should().BeTrue("правило едет хвостом хода и в системный блок не попадает");
        section.Text.Should().Be(LocalMediaDefaultContributor.ProjectRule);
        section.Text.Should().Contain("рисую локально (бесплатно), ≈N с; нужно облако — скажи")
            .And.Contain("local_text_to_video").And.Contain("local_generate_image")
            .And.Contain("если стоит «по умолчанию» — image_generate передавай с provider local")
            .And.NotContain(LocalMediaDefaultContributor.PersonalNoVideoRule);
    }

    [Fact]
    public async Task Личный_вариант_без_локального_видео_и_local_media()
    {
        var section = (await Contributor().BuildAsync(Personal(), "нарисуй кота"))!.Sections.Should().ContainSingle().Subject;

        section.InTurnTail.Should().BeTrue();
        section.Text.Should().Be(LocalMediaDefaultContributor.PersonalRule);
        section.Text.Should().Contain(LocalMediaDefaultContributor.PersonalNoVideoRule)
            .And.Contain("рисую локально (бесплатно), ≈N с; нужно облако — скажи")
            .And.Contain("если стоит «по умолчанию» или блока нет — image_generate передавай с provider local")
            .And.NotContain("local_text_to_video").And.NotContain("local_generate_image");
    }

    // Правки по ревью b15cbe58: (б) выбор в полосе и исключение из ChoiceRule, (в) N не выдумывать,
    // (г) согласие на облако не переносится, (д) сервис текущей просьбы, а не прошлых вызовов
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Тексты_согласованы_с_блоком_картинок_и_не_прилипают(bool personal)
    {
        var text = personal ? LocalMediaDefaultContributor.PersonalRule : LocalMediaDefaultContributor.ProjectRule;

        text.Should().Contain("если в блоке «Картинки в этом чате» указан поставщик — используй его (не подменяй)", "(б)")
            .And.Contain("(это исключение из правила «не передавай provider»)", "(б)")
            .And.Contain("если числа нет — не называй его, скажи просто «рисую локально (бесплатно)»", "(в)")
            .And.NotContain("с/мин", "(в)")
            .And.Contain("согласие на одно облачное действие не распространяется на следующие просьбы", "(г)")
            .And.Contain("1. Сервис, названный в ТЕКУЩЕЙ просьбе", "(д)")
            .And.Contain("даже если предыдущие картинки в этом чате рисовались локально; прошлые вызовы правилом не считаются", "(д)");
    }

    // Внутри варианта текст не зависит от хода: иначе хвост гонял бы разный текст
    // ── Строка контекста (ADR-023 §3.1, 2б-2): выбор человека виден в «Чем» контекста хода, полос больше нет ──

    private static readonly string[] AllFlagsWithRow = [.. AllFlags, FeatureFlagKeys.ComposerContextRow];

    [Fact]
    public async Task Без_флага_строки_контекста_тексты_всех_вариантов_прежние_байт_в_байт()
    {
        (await Contributor().BuildAsync(Project(), "x"))!.Sections[0].Text.Should().Be(LocalMediaDefaultContributor.ProjectRule);
        (await Contributor().BuildAsync(Personal(), "x"))!.Sections[0].Text.Should().Be(LocalMediaDefaultContributor.PersonalRule);
        (await Contributor().BuildAsync(Project(hasAudioEditorMcp: true), "x"))!.Sections[0].Text
            .Should().Be(LocalMediaDefaultContributor.ProjectRuleWithAudioEditor);
        (await Contributor().BuildAsync(Personal(hasAudioEditorMcp: true), "x"))!.Sections[0].Text
            .Should().Be(LocalMediaDefaultContributor.PersonalRuleWithAudioEditor);
        LocalMediaDefaultContributor.ProjectRule.Should().Contain("полосе «Картинки»");
    }

    [Fact]
    public async Task При_строке_контекста_ни_один_вариант_не_ссылается_на_полосы()
    {
        var contributor = Contributor(flags: AllFlagsWithRow);
        var texts = new[]
        {
            await contributor.BuildAsync(Project(), "x"),
            await contributor.BuildAsync(Personal(), "x"),
            await contributor.BuildAsync(Project(hasAudioEditorMcp: true), "x"),
            await contributor.BuildAsync(Personal(hasAudioEditorMcp: true), "x"),
        }.Select(c => c!.Sections[0].Text).ToList();

        foreach (var text in texts)
        {
            text.Should().NotContain("полосе «Картинки»").And.NotContain("полосе «Звук»")
                .And.NotContain("виден в блоке «Картинки в этом чате»")
                .And.NotContain("в блоке «Картинки в этом чате» указан поставщик");
            text.Should().Contain("«Чем» контекста хода");
        }
        texts[0].Should().Contain("Выбор человека в строке контекста, если в «Чем» контекста хода указан исполнитель")
            .And.Contain(LocalMediaDefaultContributor.BandProviderRuleContextRow);
        texts[2].Should().Contain("если в «Чем» контекста хода указан поставщик — используй его (не подменяй)");
        texts[0].Should().Contain("Картинки: если в ходе есть блок «Картинки в этом чате»", "сам блок под флагом остаётся");
    }

    [Fact]
    public async Task Текст_варианта_не_зависит_от_хода()
    {
        var a = await Contributor().BuildAsync(Project(), "нарисуй кота");
        var b = await Contributor().BuildAsync(Project(session: new Session { ProjectId = "p2", OwnerId = "u1" }), null);
        a!.Sections[0].Text.Should().Be(b!.Sections[0].Text);
    }

    [Fact]
    public void Стоит_сразу_после_блока_картинок() =>
        Contributor().Order.Should().Be(
            new ClaudeHomeServer.Services.ImageEditor.Chats.ImageEditorStateContributor(new Flags()).Order + 10);

    // Устаревшая фраза «локальных моделей для них нет» — уже ложь: у local-media есть 9 аудио-инструментов,
    // из-за неё модель уводила звук в облако. Проектный вариант их перечисляет, личный — честно говорит, что нет.
    [Fact]
    public void Аудио_инструменты_в_правилах_вместо_устаревшей_фразы()
    {
        var project = LocalMediaDefaultContributor.ProjectRule;
        var personal = LocalMediaDefaultContributor.PersonalRule;

        project.Should().NotContain("локальных моделей для них нет");
        personal.Should().NotContain("локальных моделей для них нет");

        project.Should().Contain("local_speech").And.Contain("local_music_generate");
        personal.Should().Contain("локальных моделей в этом чате нет");
    }

    // ADR-021 §5, вопрос 1: модуль «Звук» доехал до хода — звук идёт через audio_* с provider local,
    // прямые local_* — только по прямой просьбе; картинки и видео те же
    [Fact]
    public async Task Проект_с_модулем_звука_звук_через_audio_generate()
    {
        var text = (await Contributor().BuildAsync(Project(hasAudioEditorMcp: true), "озвучь"))!.Sections[0].Text;

        text.Should().Be(LocalMediaDefaultContributor.ProjectRuleWithAudioEditor)
            .And.Contain("audio_generate передавай с provider local")
            .And.Contain("Прямые local_* для звука — только если человек явно попросил сделать напрямую")
            .And.NotContain("озвучка — local_speech");
        text.Should().StartWith(LocalMediaDefaultContributor.ProjectRule[..LocalMediaDefaultContributor.ProjectRule.IndexOf("Звук и музыка", StringComparison.Ordinal)],
            "картинки и видео модуль звука не трогает");
    }

    [Fact]
    public async Task Личный_чат_с_модулем_звука_облако_через_audio_generate()
    {
        var text = (await Contributor().BuildAsync(Personal(hasAudioEditorMcp: true), "озвучь"))!.Sections[0].Text;

        text.Should().Be(LocalMediaDefaultContributor.PersonalRuleWithAudioEditor)
            .And.Contain("локальных моделей в этом чате нет").And.Contain("audio_generate")
            .And.NotContain("Прямые local_*", "в личной области локального звука нет вовсе");
    }

    // Без доставленного сервера, без флага модуля или без audio_generate — прежние варианты
    [Theory]
    [InlineData("сервер не доставлен")]
    [InlineData("флаг audio-editor выключен")]
    [InlineData("AudioEditor:AgentLaunch=false")]
    public async Task Без_модуля_звука_прежние_тексты(string why)
    {
        var contributor = why switch
        {
            "флаг audio-editor выключен" => Contributor(flags: [FeatureFlagKeys.LocalMediaDefault, FeatureFlagKeys.ImageEditor]),
            "AudioEditor:AgentLaunch=false" => Contributor(audioAgentLaunch: false),
            _ => Contributor(),
        };
        var audio = why != "сервер не доставлен";

        (await contributor.BuildAsync(Project(hasAudioEditorMcp: audio), null))!.Sections[0].Text
            .Should().Be(LocalMediaDefaultContributor.ProjectRule, why);
        (await contributor.BuildAsync(Personal(hasAudioEditorMcp: audio), null))!.Sections[0].Text
            .Should().Be(LocalMediaDefaultContributor.PersonalRule, why);
    }
}
