using System.Diagnostics;
using ClaudeHomeServer.Services.Execution;
using ClaudeHomeServer.Services.Llm.Claude;
using ClaudeHomeServer.Services.Turn;
using ClaudeHomeServer.Services.Llm;

namespace ClaudeHomeServer.Tests.Services;

// Часть (d) резки ClaudeSessionPromptSectionsOrderTests: подсказки run_tests/build
// и хук потребителей.
public class ClaudeSessionPromptSectionsHintsTests : ClaudeSessionPromptSectionsTestBase
{

    // Подсказка run_tests: HasTestsMcp собирает сам ClaudeSession из доставки узла tests; секция
    // постоянная и едет СИСТЕМНЫМ блоком (prefix cache стабилен), а не хвостом хода
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TestsHint_СерверДоставлен_СистемнымБлоком(bool recallInTurnText)
    {
        var (systemPrompt, sections) = await RunTailTurnAsync(recallInTurnText, "МАРКЕР_СОСТОЯНИЯ",
            bus => PromptSectionContributorsRegistration.RegisterAll(bus,
                [new ClaudeHomeServer.Services.TestRuns.TestRunsHintContributor()]),
            ProjectChat(), c => c with { TestsMcp = TestsMcp });

        systemPrompt.Should().Contain(ClaudeHomeServer.Services.TestRuns.TestRunsHintContributor.Text);
        sections.Should().NotContain(s => s.Key == "mcp-tests" && s.Kind == "turn");
    }

    [Theory]
    [InlineData("нет узла")]
    [InlineData("TrimMcpServers без tests")]
    public async Task TestsHint_СервераНетУХода_ПодсказкиНет(string why)
    {
        var (systemPrompt, _) = await RunTailTurnAsync(false, "МАРКЕР_СОСТОЯНИЯ",
            bus => PromptSectionContributorsRegistration.RegisterAll(bus,
                [new ClaudeHomeServer.Services.TestRuns.TestRunsHintContributor()]),
            ProjectChat(), why == "нет узла" ? c => c : c => c with { TestsMcp = TestsMcp },
            why == "нет узла" ? null : TrimToTasks);

        systemPrompt.Should().NotContain(ClaudeHomeServer.Services.TestRuns.TestRunsHintContributor.Text,
            $"{why}: подсказка звала бы инструмент, которого у хода нет");
    }

    // Подсказка build (сервер dev): HasDevMcp собирает ClaudeSession из доставки узла dev; секция
    // постоянная и едет СИСТЕМНЫМ блоком
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BuildHint_СерверДоставлен_СистемнымБлоком(bool recallInTurnText)
    {
        var (systemPrompt, sections) = await RunTailTurnAsync(recallInTurnText, "МАРКЕР_СОСТОЯНИЯ",
            bus => PromptSectionContributorsRegistration.RegisterAll(bus,
                [new ClaudeHomeServer.Services.TestRuns.BuildHintContributor()]),
            ProjectChat(), c => c with { TestsMcp = TestsMcp });

        systemPrompt.Should().Contain(ClaudeHomeServer.Services.TestRuns.BuildHintContributor.Text);
        sections.Should().NotContain(s => s.Key == "mcp-dev" && s.Kind == "turn");
    }

    [Theory]
    [InlineData("нет узла")]
    [InlineData("TrimMcpServers без dev")]
    public async Task BuildHint_СервераНетУХода_ПодсказкиНет(string why)
    {
        var (systemPrompt, _) = await RunTailTurnAsync(false, "МАРКЕР_СОСТОЯНИЯ",
            bus => PromptSectionContributorsRegistration.RegisterAll(bus,
                [new ClaudeHomeServer.Services.TestRuns.BuildHintContributor()]),
            ProjectChat(), why == "нет узла" ? c => c : c => c with { TestsMcp = TestsMcp },
            why == "нет узла" ? null : TrimToTasks);

        systemPrompt.Should().NotContain(ClaudeHomeServer.Services.TestRuns.BuildHintContributor.Text,
            $"{why}: подсказка звала бы инструмент, которого у хода нет");
    }

    private static readonly TestsMcpContext TestsMcp = new("http://127.0.0.1:5000", () => "tok", UseHttp: true);

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task ПризнакХукаПотребителей_ХостБезПесочницыСHttpГрафом_ТогдаИТолькоТогдаTrue(bool httpGraph, bool expected)
    {
        if (OperatingSystem.IsWindows()) return;
        bool? seen = null;
        await RunTailTurnAsync(false, "x", bus => bus.OnFilter<PromptAssembling>(200, (e, next) =>
        {
            seen = e.Session.ConsumersHookActive;
            return next();
        }, "Test.HookFlag"), tweak: httpGraph
            ? c => c with { CodeGraphMcp = new CodeGraphMcpContext("http://localhost:5999", () => "tok", "proj1") }
            : null);

        seen.Should().Be(expected, "хук доезжает до CLI только на хосте без песочницы и при http-адресе графа");
    }

    [Fact]
    public void ХукПотребителей_ВПесочницеНеЗапускается()
    {
        ClaudeRuntimeSettings.ConsumersHookRuns(new SandboxedLauncher()).Should().BeFalse(
            "у container-владельца --settings не передаётся — потребителей несёт хвост первого хода");
    }

    private sealed class SandboxedLauncher : IProcessLauncher
    {
        public bool IsSandboxed => true;
        public bool TargetIsWindows => false;
        public IPathMapper Paths => IdentityPathMapper.Instance;
        public string ClaudeCliCommand => "fake-claude";
        public string HostTempDir => Path.GetTempPath();
        public string? McpApiUrlOverride => null;
        public Process Start(ProcessSpec spec) => throw new NotSupportedException();
        public void Kill(Process process, string? turnId = null) { }
        public int EstimateCommandLineLength(ProcessSpec spec) => 0;
    }
}
