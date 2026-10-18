using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Execution;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Llm.Claude;
using ClaudeHomeServer.Services.Prompts;
using ClaudeHomeServer.Services.Turn;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Services;

// СТОРОЖ слоя персоны у проектной сессии. У проектных сессий Session.OwnerId по контракту
// null (владелец берётся через проект), а ClaudeSession кладёт в PromptSessionContext именно
// Info.OwnerId. Контрибьюторы слоя персоны гасятся при OwnerId == null — и у исполнителя
// задачи (Origin=Task), и у знакомства с проектом (OnboardingKind=project) молча пропадает
// persona-layer вместе с характером персоны.
//
// Тест сквозной: боевая DI и боевые контрибьюторы, подменён только адаптер (перехват
// LlmSessionContext) и лаунчер (чтобы не стартовал настоящий CLI).
public class ProjectSessionPersonaLayerGuardTests : IDisposable
{
    private const string PersonaPrompt = "МАРКЕР_ХАРАКТЕР_ПРОЕКТНОЙ_ПЕРСОНЫ";

    private sealed class CapturingFactory : ILlmSessionAdapterFactory
    {
        public ConcurrentDictionary<string, LlmSessionContext> Contexts { get; } = new();
        private readonly FakeLlmSessionAdapterFactory _inner = new();

        public ILlmSessionAdapter Create(Session session, LlmSessionContext context)
        {
            Contexts[session.Id] = context;
            return _inner.Create(session, context);
        }
    }

    private readonly CapturingFactory _adapters = new();
    private readonly TestWebApplicationFactory _factory;
    private readonly ConcurrentDictionary<int, Process> _clis = new();

    public ProjectSessionPersonaLayerGuardTests()
    {
        _factory = new TestWebApplicationFactory
        {
            ExtraServices = services => services.AddSingleton<ILlmSessionAdapterFactory>(_adapters),
        };
    }

    public void Dispose()
    {
        foreach (var p in _clis.Values)
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { /* уже мёртв */ }
            p.Dispose();
        }
        _factory.Dispose();
    }

    // Держит фейковый процесс живым, чтобы ход не ушёл в боевой claude.
    private sealed class CapturingLauncher(ConcurrentDictionary<int, Process> clis) : IProcessLauncher
    {
        public bool IsSandboxed => false;
        public bool TargetIsWindows => OperatingSystem.IsWindows();
        public IPathMapper Paths => IdentityPathMapper.Instance;
        public string ClaudeCliCommand => "fake-claude";
        public string HostTempDir => Path.GetTempPath();
        public string? McpApiUrlOverride => null;

        public Process Start(ProcessSpec spec)
        {
            var fake = new ProcessSpec
            {
                FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
                Args = OperatingSystem.IsWindows() ? ["/c", "ping -n 10 127.0.0.1 >nul"] : ["-c", "sleep 10"],
                WorkingDirectory = spec.WorkingDirectory,
                ClearEnv = spec.ClearEnv,
                StdioEncoding = spec.StdioEncoding,
                EnableRaisingEvents = spec.EnableRaisingEvents,
                RedirectStdin = spec.RedirectStdin,
                Track = false,
            };
            var process = LocalProcessRunner.Instance.Start(fake);
            clis[clis.Count + 1] = process;
            return process;
        }

        public int EstimateCommandLineLength(ProcessSpec spec)
        {
            var total = (spec.FileName ?? string.Empty).Length;
            foreach (var a in spec.Args) total += TurnPromptAssembler.ArgCost(a);
            return total;
        }

        public void Kill(Process process, string? turnId = null)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* уже мёртв */ }
        }
    }

    // Владелец, его проект и проектная персона с характером; через SessionManager заводится
    // сессия проекта (OwnerId у неё null), затем из перехваченного контекста собирается
    // настоящий ClaudeSession и гоняется один ход. Возвращает секции снимка промпта.
    private async Task<IReadOnlyList<PromptSectionDto>> RunProjectTurnAsync(
        bool asTask, string? onboardingKind)
    {
        var client = _factory.CreateAuthenticatedClient();
        var created = await client.PostAsJsonAsync("/api/projects", new { name = $"pl-{Guid.NewGuid():N}" });
        created.EnsureSuccessStatusCode();
        var projectId = JsonSerializer.Deserialize<JsonElement>(
            await created.Content.ReadAsStringAsync()).GetProperty("id").GetString()!;

        var projects = _factory.Services.GetRequiredService<ProjectManager>();
        var project = projects.GetById(projectId)!;

        var persona = _factory.Services.GetRequiredService<PersonaManager>().Create(
            project.OwnerId!, "Проектная Персона", role: null, description: null,
            systemPrompt: PersonaPrompt, model: null, effort: null,
            scope: PersonaScope.Project, projectId: projectId,
            color: null, greeting: null, memoryEnabled: false);

        var sessions = _factory.Services.GetRequiredService<SessionManager>();
        var info = await sessions.CreateAsync(projectId, ClaudeMode.AcceptEdits,
            personaId: persona.Id,
            taskExecution: asTask,
            taskId: asTask ? $"task-{Guid.NewGuid():N}" : null,
            onboardingKind: onboardingKind);

        info.OwnerId.Should().BeNull("у проектных сессий владелец берётся через проект — это контракт");
        var captured = _adapters.Contexts[info.Id];

        var bus = _factory.Services.GetRequiredService<ITurnEventBus>();
        var sections = new TaskCompletionSource<IReadOnlyList<PromptSectionDto>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        bus.OnNotification<PromptAssembled>(e =>
        {
            if (e.Turn.SessionId == info.Id && e.Snapshot?.Draft is { } draft && draft.Sections.Count > 0)
                sections.TrySetResult(draft.Sections);
            return Task.CompletedTask;
        });

        var context = captured with { Launcher = new CapturingLauncher(_clis), Events = bus };
        await using var session = new ClaudeSession(info, context);
        await session.SendMessageAsync("привет");

        var done = await Task.WhenAny(sections.Task, Task.Delay(TimeSpan.FromSeconds(15)));
        done.Should().Be(sections.Task, "снимок промпта должен публиковаться из шины до старта CLI");
        return await sections.Task;
    }

    [Fact]
    public async Task ИсполнительЗадачи_ВПромптеЕстьСлойПерсоны()
    {
        var sections = await RunProjectTurnAsync(asTask: true, onboardingKind: null);

        sections.Should().Contain(s => s.Key == "persona-layer",
            "у исполнителя задачи с PersonaId слой персоны обязан доехать до модели, "
            + "даже если Session.OwnerId == null (владелец — через проект)");
        sections.Single(s => s.Key == "persona-layer").Text.Should().Contain(PersonaPrompt);
    }

    [Fact]
    public async Task ЗнакомствоСПроектом_ВСлоеПерсоныЕстьОверлейНаставника()
    {
        var sections = await RunProjectTurnAsync(asTask: false, onboardingKind: OnboardingKinds.Project);

        sections.Should().Contain(s => s.Key == "persona-layer",
            "у знакомства с проектом слой персоны обязан доехать до модели при OwnerId == null");
        var layer = sections.Single(s => s.Key == "persona-layer").Text;
        layer.Should().Contain("## Знакомство с проектом «",
            "надстройка наставника — часть слоя персоны проектного знакомства");
        layer.Should().Contain(PersonaPrompt);
    }
}
