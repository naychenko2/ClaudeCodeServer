using System.Collections.Concurrent;
using System.Diagnostics;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Execution;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Llm.Claude;
using ClaudeHomeServer.Services.Prompts;
using FluentAssertions;
using Xunit;

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
public class ClaudeSessionPromptSectionsOrderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ccs-prompt-order-" + Guid.NewGuid().ToString("N"));
    private readonly ConcurrentDictionary<int, Process> _clis = new();
    private readonly TaskCompletionSource<IReadOnlyList<string>> _argsCaptured =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ClaudeSessionPromptSectionsOrderTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        foreach (var p in _clis.Values)
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { /* уже мёртв */ }
            p.Dispose();
        }
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    // Захватывает args первого старта процесса, дальше держит фейковый CLI живым (молчуном) —
    // ход сам по себе тесту не нужен, только сигнатура запуска
    private sealed class CapturingLauncher(
        ConcurrentDictionary<int, Process> clis,
        TaskCompletionSource<IReadOnlyList<string>> argsCaptured) : IProcessLauncher
    {
        public bool IsSandboxed => false;
        public bool TargetIsWindows => OperatingSystem.IsWindows();
        public IPathMapper Paths => IdentityPathMapper.Instance;
        public string ClaudeCliCommand => "fake-claude";
        public string HostTempDir => Path.GetTempPath();
        public string? McpApiUrlOverride => null;

        public Process Start(ProcessSpec spec)
        {
            argsCaptured.TrySetResult(spec.Args);
            var fake = new ProcessSpec
            {
                FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
                Args = OperatingSystem.IsWindows()
                    ? ["/c", "ping -n 120 127.0.0.1 >nul"]
                    : ["-c", "sleep 120"],
                WorkingDirectory = spec.WorkingDirectory,
                ClearEnv = spec.ClearEnv,
                StdioEncoding = spec.StdioEncoding,
                EnableRaisingEvents = spec.EnableRaisingEvents,
                RedirectStdin = spec.RedirectStdin,
                Track = false, // тестовый процесс: в реестр боевых PID его не пишем
            };
            var process = LocalProcessRunner.Instance.Start(fake);
            clis[clis.Count + 1] = process;
            return process;
        }

        public void Kill(Process process, string? turnId = null)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* уже мёртв */ }
        }
    }

    [Fact]
    public async Task RecallПромптСекцииДосьеПривязкиГраф_ВПорядкеКонтрактаПлана_ГолосПоследним()
    {
        var messages = new List<ServerMessage>();
        var info = new Session { VoiceMode = true };

        var context = new LlmSessionContext(
            RootPath: _root,
            OnMessage: m => { lock (messages) messages.Add(m); return Task.CompletedTask; },
            RawSystemPrompt: null,
            PermissionRules: null,
            TasksMcp: null,
            // Эмулирует SessionManager.BuildPersonaLayer: PersonaPromptBuilder дописывает
            // оговорку voice-mode последним куском текста персоны при voiceMode=true
            PersonaPromptProvider: () => "МАРКЕР_ПЕРСОНЫ Ты — Тестовая Персона.\n\n" + VoicePrompts.PersonaOverride,
            MemoryMcp: new MemoryMcpContext("http://memory.invalid", "tok", "persona-1"),
            PersonaRecallProvider: _ => Task.FromResult<RecallBlock?>(
                new RecallBlock("МАРКЕР_RECALL_MEMORY текст памяти", [], "МАРКЕР_DOSSIER_RECALL текст досье")),
            BindingsProvider: _ => Task.FromResult<string?>("МАРКЕР_PERSONA_BINDINGS текст привязок"),
            CodeGraphProvider: _ => Task.FromResult<string?>("МАРКЕР_CODE_GRAPH текст графа"),
            PromptSectionsProvider: _ => Task.FromResult<string?>("МАРКЕР_PROMPT_SECTIONS текст секций"),
            Launcher: new CapturingLauncher(_clis, _argsCaptured));

        var session = new ClaudeSession(info, context);
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

    private static async Task<IReadOnlyList<string>> WhenAnyAsync(
        Task<IReadOnlyList<string>> task, TimeSpan timeout)
    {
        var done = await Task.WhenAny(task, Task.Delay(timeout));
        done.Should().Be(task, "не дождались старта fake-CLI процесса с захватом args хода");
        return await task;
    }
}
