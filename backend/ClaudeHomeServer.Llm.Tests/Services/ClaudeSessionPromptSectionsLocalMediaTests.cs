using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Turn;
using ClaudeHomeServer.Services.Llm;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Tests.Services;

// Часть (c) резки ClaudeSessionPromptSectionsOrderTests: правило local-media-default
// и его условия доставки (проектный/личный чат, TrimMcpServers, ход без человека).
public class ClaudeSessionPromptSectionsLocalMediaTests : ClaudeSessionPromptSectionsTestBase
{

    // Порядок хвоста (шаг 3 «локальная по умолчанию»): правило local-media-default едет хвостом
    // сразу после блока «Картинки в этом чате» и ссылается на него; в системный блок не попадает
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LocalMediaDefault_ХвостомПослеБлокаКартинок(bool recallInTurnText)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["LocalMedia:Enabled"] = "true",
        }).Build();
        var contributor = new ClaudeHomeServer.Services.Images.LocalMedia.LocalMediaDefaultContributor(config);

        var (systemPrompt, sections) = await RunTailTurnAsync(recallInTurnText, "МАРКЕР_СОСТОЯНИЯ",
            bus => PromptSectionContributorsRegistration.RegisterAll(bus, [contributor]),
            new Session { Model = "qwen-test-27b", OwnerId = "u1" },
            context => context with { ImageEditorMcp = ImageEditorMcp });

        // Название секции статично упомянуто в BuiltInSystemPrompt (ссылка на правило хвоста), поэтому
        // ищем заголовок и тело самой секции
        systemPrompt.Should().NotContain("## Картинки и видео: локальная модель по умолчанию",
                "правило едет хвостом хода и в системный блок не попадает ни при какой настройке провайдера")
            .And.NotContain(ClaudeHomeServer.Services.Images.LocalMedia.LocalMediaDefaultContributor.NoticeRule);
        sections.Where(s => s.Kind == "turn").Select(s => s.Key).Should().ContainInOrder(
            "image-editor-state", "local-media-default");
        sections.Single(s => s.Key == "local-media-default").Text
            .Should().Be(ClaudeHomeServer.Services.Images.LocalMedia.LocalMediaDefaultContributor.PersonalRule);
    }

    // Проводка контекста секции (находки A и B финального ревью 33571541): поля HasLocalMediaMcp,
    // HasImageEditorMcp и Unattended собирает сам ClaudeSession из факта доставки MCP в конфиг хода и
    // признаков хода — тест руками их не задаёт. Без этих ходов замена проводки на false оставалась
    // зелёной во всех наборах
    private static readonly LocalMediaMcpContext LocalMediaMcp = new("http://127.0.0.1:5000", () => "tok", UseHttp: true);
    private static readonly ImageEditorMcpContext ImageEditorMcp = new("http://127.0.0.1:5000", () => "tok", UseHttp: true);

    private async Task<PromptSectionDto?> LocalMediaDefaultSectionAsync(Session info,
        Func<LlmSessionContext, LlmSessionContext> tweak, Dictionary<string, string?>? providerConfig = null,
        int agentDepth = 0)
    {
        var contributor = new ClaudeHomeServer.Services.Images.LocalMedia.LocalMediaDefaultContributor(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LocalMedia:Enabled"] = "true",
            }).Build());
        var (_, sections) = await RunTailTurnAsync(false, "МАРКЕР_СОСТОЯНИЯ",
            bus => PromptSectionContributorsRegistration.RegisterAll(bus, [contributor]),
            info, tweak, providerConfig, agentDepth);
        return sections.SingleOrDefault(s => s.Key == "local-media-default");
    }
    private static Session PersonalChat() => new() { Model = "qwen-test-27b", OwnerId = "u1" };

    [Fact]
    public async Task LocalMediaDefault_ПроектныйЧат_LocalMediaДоставлен_ПравилоПроекта()
    {
        var section = await LocalMediaDefaultSectionAsync(ProjectChat(), c => c with { LocalMediaMcp = LocalMediaMcp });

        section.Should().NotBeNull("local-media доехал до хода — проектный вариант правила обязан прийти хвостом");
        section!.Text.Should().Be(ClaudeHomeServer.Services.Images.LocalMedia.LocalMediaDefaultContributor.ProjectRule);
    }

    [Fact]
    public async Task LocalMediaDefault_ПровайдерОтрезалLocalMedia_ПравилаНет()
    {
        var section = await LocalMediaDefaultSectionAsync(ProjectChat(), c => c with { LocalMediaMcp = LocalMediaMcp },
            TrimToTasks);

        section.Should().BeNull("TrimMcpServers отрезал local-media — правило звало бы инструменты, которых у хода нет");
    }

    [Theory]
    [InlineData("TaskExecution")]
    [InlineData("AutomationRuleId")]
    [InlineData("AgentDepth=1")]
    public async Task LocalMediaDefault_ХодБезЧеловека_ПравилаНет(string source)
    {
        var info = ProjectChat();
        if (source == "TaskExecution") info.TaskExecution = true;
        if (source == "AutomationRuleId") info.AutomationRuleId = "r1";

        var section = await LocalMediaDefaultSectionAsync(info, c => c with { LocalMediaMcp = LocalMediaMcp },
            agentDepth: source == "AgentDepth=1" ? 1 : 0);

        section.Should().BeNull($"{source}: ход без человека не должен занимать общую GPU по умолчанию");
    }

    [Fact]
    public async Task LocalMediaDefault_ЛичныйЧат_РедакторДоставлен_ЛичноеПравило()
    {
        var section = await LocalMediaDefaultSectionAsync(PersonalChat(), c => c with { ImageEditorMcp = ImageEditorMcp });

        section.Should().NotBeNull("сервер редактора доехал до хода — личный вариант правила обязан прийти");
        section!.Text.Should().Be(ClaudeHomeServer.Services.Images.LocalMedia.LocalMediaDefaultContributor.PersonalRule);
    }

    [Theory]
    [InlineData("TrimMcpServers без image-editor")]
    [InlineData("модуль редактора выключен")]
    public async Task LocalMediaDefault_ЛичныйЧат_РедактораНетУХода_ПравилаНет(string why)
    {
        var section = why == "модуль редактора выключен"
            ? await LocalMediaDefaultSectionAsync(PersonalChat(), c => c)
            : await LocalMediaDefaultSectionAsync(PersonalChat(), c => c with { ImageEditorMcp = ImageEditorMcp }, TrimToTasks);

        section.Should().BeNull($"{why}: image_new/image_generate у хода нет («No such tool available»)");
    }
}
