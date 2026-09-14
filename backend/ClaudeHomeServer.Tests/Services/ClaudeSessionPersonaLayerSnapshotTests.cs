using System.Collections.Concurrent;
using System.Diagnostics;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Execution;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Llm.Claude;
using ClaudeHomeServer.Services.Turn;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// СТОРОЖ H1: persona-layer (до MaxContractChars у персоны) ОБЯЗАН быть в Draft.Sections
// снимка промпта, не только в --append-system-prompt. Шторка «что ушло модели» строится
// из Draft.Sections; без persona-layer самая жирная часть промпта не видна и стоимость
// персоны считается без неё. Слой персоны идёт в модель через Combine(sections, agentPrompt),
// но в список секций снимка его отдельным шагом кладёт ClaudeSession.cs:3353-3354 после
// срезки — иначе он пропадёт вместе с TruncationOrder и шторка соврёт.
//
// Мутация удаления этого шага в ClaudeSession.cs должна ронять тест ниже: persona-layer
// уходит в модель (через Combine), но в Draft.Sections его нет → RED на
// `snapshotSections.Should().Contain("persona-layer")`.
//
// Параллельный golden в ClaudeSessionPromptSectionsOrderTests проверяет текст
// --append-system-prompt — это другая поверхность, не список секций снимка. Поэтому
// отдельный сторож.
public class ClaudeSessionPersonaLayerSnapshotTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "ccs-persona-snapshot-" + Guid.NewGuid().ToString("N"));
    private readonly ConcurrentDictionary<int, Process> _clis = new();
    private readonly TaskCompletionSource<IReadOnlyList<PromptSectionDto>> _snapshotSections =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ClaudeSessionPersonaLayerSnapshotTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        foreach (var p in _clis.Values)
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { /* уже мёртв */ }
            p.Dispose();
        }
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    // Fake-CLI launcher: держит процесс живым, чтобы ход не ушёл в боевой claude.exe.
    // Оценка cmdline — FileName+args через ArgCost (контракт LocalProcessRunner; точные цифры
    // раннера — отдельный сторож в DockerProcessRunnerCmdlineEstimationTests, нам здесь
    // достаточно не падать).
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

    [Fact]
    public async Task Снимок_СодержитСекциюPersonaLayer()
    {
        var info = new Session();
        var bus = new TurnEventBus();

        // PersonaLayerContributor: кладёт секцию Key="persona-layer". Этот слой ClaudeSession
        // клеит через PersonaSeparator в Combine и — отдельным шагом после ApplyBudget —
        // добавляет в Draft.Sections (ClaudeSession.cs:3353-3354).
        bus.OnFilter<PromptAssembling>(900, async (e, next) =>
        {
            e.Sections.Add(new PromptSection("persona-layer", "МАРКЕР_PERSONA_LAYER"));
            await next();
        }, "Test.PersonaLayer");

        // Подписчик на PromptAssembled: перехватываем снимок промпта и достаём Sections.
        // Первая публикация — Draft. Подписчик приносит сам список секций через TaskCompletionSource.
        bus.OnNotification<PromptAssembled>(async e =>
        {
            if (e.Snapshot?.Draft is { } draft && draft.Sections.Count > 0)
                _snapshotSections.TrySetResult(draft.Sections);
            await Task.CompletedTask;
        });

        var context = new LlmSessionContext(
            RootPath: _root,
            OnMessage: _ => Task.CompletedTask,
            RawSystemPrompt: null, BuiltInSystemPrompt: ClaudeHomeServer.Services.ProjectManager.BuiltInSystemPrompt,
            PermissionRules: null,
            TasksMcp: null,
            MemoryMcp: new MemoryMcpContext("http://memory.invalid", () => "tok", "persona-1"),
            PersonaProvider: () => new Persona { Id = "persona-1", Name = "Тестовая Персона" },
            Launcher: new CapturingLauncher(_clis),
            Events: bus);

        var session = new ClaudeSession(info, context);
        await using var _ = session;

        await session.SendMessageAsync("привет");

        // Ждём ТОЛЬКО снимок. Прежняя редакция жонглировала _argsCaptured в WhenAny, и на
        // нагруженной машине / CI ubuntu-latest (ThreadPool голодает) первым приходил именно
        // _argsCaptured — тест падал, хотя снимок приходил мгновением позже. PublishAsync
        // fire-and-forget (ClaudeSession.cs:4101) публикацию не ждёт, поэтому гонка была
        // неизбежной; _argsCaptured из гонки убран — он тут ничего не даёт. 15 с, как у
        // братьев по паттерну: longRunningTestSeconds=10 в xunit.runner.json оставляет 10 с
        // на грани — на CI ThreadPool иногда не укладывается.
        var done = await Task.WhenAny(_snapshotSections.Task, Task.Delay(TimeSpan.FromSeconds(15)));
        done.Should().Be(_snapshotSections.Task,
            "снимок промпта должен публиковаться из шины до старта CLI; если этого не происходит — "
            + "ClaudeSession изменился и тест надо обновить под новый контракт");

        var sections = await _snapshotSections.Task;
        sections.Should().Contain(s => s.Key == "persona-layer",
            "слой персоны ОТДЕЛЬНЫМ шагом после ApplyBudget ложится в sections; "
            + "иначе самая крупная секция промпта (до MaxContractChars) не видна в шторке");
        sections.Single(s => s.Key == "persona-layer").Group.Should().Be("persona",
            "persona-layer должен ехать с Group=persona, чтобы фронт сразу метил её иконкой "
            + "«Часть персоны» и считал вес персоны в общем зачёте");
    }
}
