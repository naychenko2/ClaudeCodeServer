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

    private static readonly string[] AllFlags = [FeatureFlagKeys.LocalMediaDefault, FeatureFlagKeys.ImageEditor];

    private static LocalMediaDefaultContributor Contributor(
        string[]? flags = null, bool localMedia = true, bool images = true, bool agentLaunch = true) =>
        new(new Flags(flags ?? AllFlags), new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["LocalMedia:Enabled"] = localMedia ? "true" : "false",
            ["Subsystems:images:Enabled"] = images ? "true" : "false",
            ["ImageEditor:AgentLaunch"] = agentLaunch ? "true" : "false",
        }).Build());

    private static PromptSessionContext Project(bool hasLocalMediaMcp = true, bool unattended = false,
        Session? session = null) =>
        new(session ?? new Session { ProjectId = "p1", OwnerId = "u1" }, "u1", null, "/root",
            HasLocalMediaMcp: hasLocalMediaMcp, Unattended: unattended);

    private static PromptSessionContext Personal(bool unattended = false) =>
        new(new Session { OwnerId = "u1" }, "u1", null, null, Unattended: unattended);

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
        section.Text.Should().Contain("рисую локально (бесплатно), ≈N с/мин; нужно облако — скажи")
            .And.Contain("local_text_to_video").And.Contain("local_generate_image")
            .And.Contain("Сам в облако не запускай")
            .And.NotContain(LocalMediaDefaultContributor.PersonalNoVideoRule);
    }

    [Fact]
    public async Task Личный_вариант_без_локального_видео_и_local_media()
    {
        var section = (await Contributor().BuildAsync(Personal(), "нарисуй кота"))!.Sections.Should().ContainSingle().Subject;

        section.InTurnTail.Should().BeTrue();
        section.Text.Should().Be(LocalMediaDefaultContributor.PersonalRule);
        section.Text.Should().Contain(LocalMediaDefaultContributor.PersonalNoVideoRule)
            .And.Contain("рисую локально (бесплатно), ≈N с/мин; нужно облако — скажи")
            .And.Contain("image_generate с provider local")
            .And.NotContain("local_text_to_video").And.NotContain("local_generate_image");
    }

    // Внутри варианта текст не зависит от хода: иначе хвост гонял бы разный текст
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
}
