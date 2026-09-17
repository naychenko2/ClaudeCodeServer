using System.Collections.Concurrent;
using System.Diagnostics;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Execution;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Llm.Claude;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Превью карточки чата (Session.LastMessage) — НЕ дело адаптера: сюда ход приходит уже с
// обвязкой (BuildCliTurnText: системные директивы, протокол цикла «до готово», пометка об
// обрыве сабагента), а при фолбэке этот же вызов повторяется на каждой попытке. Раньше
// адаптер писал превью безусловно, и сырая директива уезжала в список чатов.
// Паттерн фикстуры — как у ClaudeSessionWatchPromptTests: настоящий ClaudeSession с
// fake-CLI launcher, реальный claude.exe не запускается.
public class ClaudeSessionChatPreviewTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "ccs-chat-preview-" + Guid.NewGuid().ToString("N"));
    private readonly ConcurrentDictionary<int, Process> _clis = new();

    public ClaudeSessionChatPreviewTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        foreach (var p in _clis.Values)
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { /* уже мёртв */ }
            p.Dispose();
        }
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    // Держит фейковый CLI живым молчуном: ход уходит в stdin и там остаётся
    private sealed class SilentLauncher(ConcurrentDictionary<int, Process> clis) : IProcessLauncher
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
                Args = OperatingSystem.IsWindows()
                    ? ["/c", "ping -n 120 127.0.0.1 >nul"]
                    : ["-c", "sleep 120"],
                WorkingDirectory = spec.WorkingDirectory,
                RedirectStdin = spec.RedirectStdin,
                Track = false,
            };
            var process = LocalProcessRunner.Instance.Start(fake);
            clis[clis.Count + 1] = process;
            return process;
        }

        public void Kill(Process process, string? turnId = null)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* уже мёртв */ }
        }

        // Оценка длины командной строки — чистая функция интерфейса IProcessLauncher,
        // делегируем штатной реализации (как и Start выше)
        public int EstimateCommandLineLength(ProcessSpec spec) =>
            LocalProcessRunner.Instance.EstimateCommandLineLength(spec);
    }

    private ClaudeSession NewSession(Session info) => new(info, new LlmSessionContext(
        RootPath: _root,
        OnMessage: _ => Task.CompletedTask,
        RawSystemPrompt: null, BuiltInSystemPrompt: ClaudeHomeServer.Services.ProjectManager.BuiltInSystemPrompt,
        PermissionRules: null,
        TasksMcp: null,
        Launcher: new SilentLauncher(_clis)));

    [Fact]
    public async Task SendMessage_ПревьюЧатаНеТрогает()
    {
        var info = new Session { LastMessage = "последнее сообщение человека" };
        var session = NewSession(info);
        await using var _ = session;

        await session.SendMessageAsync(
            "[СИСТЕМНАЯ ДИРЕКТИВА — ОБРЫВ САБАГЕНТА 1/2] Сабагент «kostya» замолчал\n\nпродолжай");

        info.LastMessage.Should().Be("последнее сообщение человека",
            "превью ставит SessionManager исходным текстом — адаптеру ход приходит с обвязкой");
    }

    [Fact]
    public async Task SendMessage_СчётчикХодовРастётИНаДирективе()
    {
        // MessageCount считает ходы ПРОЦЕССА (признак «ход запускался» в SessionLiveness и
        // ревизия синка чата), а не реплики человека: директива реально уходит в CLI и
        // меняет ленту — инкремент осознан
        var info = new Session();
        var session = NewSession(info);
        await using var _ = session;

        await session.SendMessageAsync("[СИСТЕМНАЯ ДИРЕКТИВА — ОБРЫВ САБАГЕНТА 1/2] продолжай");

        info.MessageCount.Should().Be(1);
        info.UpdatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
    }
}
