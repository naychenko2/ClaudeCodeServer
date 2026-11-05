using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Execution;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Llm.Claude;
using ClaudeHomeServer.Services.Turn;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

// Доработка S2a-4 по ревью R-S2a-4 (Major 2): дыра same-process закрыта рамкой в сигнатуре
// запуска (BuildLaunchSignature). Системный блок пишется ОДИН РАЗ НА ПРОЦЕСС и живёт до
// IdleTimeout; рамка — свойство проекта, а проект меняется на лету (ProjectMembersController
// → ProjectMembershipService → ProjectManager.SetMembers). Сценарий этой проверки:
//   1. владелец в личном проекте (Wide) поднял процесс: у провайдера без ручки
//      RecallInTurnText личные recall-маркеры стоят в СИСТЕМНОМ блоке (P1);
//   2. «владелец добавляет участника» (здесь — переворот ITurnFrameSource на Narrow);
//   3. ход участника: сигнатура с рамкой НЕ совпадает → живой процесс не принимет его,
//      стартует НОВЫЙ процесс (P2), чей системный блок собран по Narrow — без маркеров.
// Мутация (убрать рамку из BuildLaunchSignature): сигнатуры совпадают → ход уходит
// same-process submit'ом в P1 → P2 не стартует → тест красный.
//
// Инвариант McpToolsetStabilityTests не задет: рамка не зависит от свойств хода, два хода
// одного чата дают один отпечаток (сторож РукиВключены_ДваХода_ОднаСигнатура в Guards).
public class ClaudeSessionFrameSignatureTests : IDisposable
{
    private const string Marker = "МАРКЕР_ЛИЧНОЙ_ПОДБОРКИ";

    private static readonly FieldInfo RunField =
        typeof(ClaudeSession).GetField("_run", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly FieldInfo TurnDoneField =
        typeof(ClaudeSession).GetNestedType("CliRun", BindingFlags.NonPublic)!
            .GetField("TurnDone", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!;

    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "ccs-frame-signature-" + Guid.NewGuid().ToString("N"));
    private readonly ConcurrentDictionary<int, Process> _clis = new();
    // Номер старта → args процесса (замер системного блока каждого поднятого процесса)
    private readonly ConcurrentDictionary<int, IReadOnlyList<string>> _argsByStart = new();
    private readonly TaskCompletionSource _p1Started = NewTcs();
    private readonly TaskCompletionSource _p2Started = NewTcs();
    private string? _firstCliScript;

    public ClaudeSessionFrameSignatureTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        foreach (var p in _clis.Values)
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { /* уже мёртв */ }
            p.Dispose();
        }
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    private static TaskCompletionSource NewTcs() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    // «CLI» первого хода (паттерн ClaudeSessionDiedEmptyRetryTests): эхо user-сообщения,
    // task_started (фоновая задача держит прогон живым), result (завершает ход), затем
    // молчание до убийства. Второй и далее — молчуны: их старт — сам замер теста.
    private string WriteFirstCliScript()
    {
        var lines = new[]
        {
            """{"type":"system","subtype":"task_started","task_id":"bg-1","tool_use_id":"toolu-bg-1","description":"bg","subagent_type":"general-purpose","task_type":"local_agent","prompt":"p"}""",
            """{"type":"result","subtype":"success","duration_ms":1,"num_turns":1,"result":"ok"}""",
        };
        string script;
        if (OperatingSystem.IsWindows())
        {
            var text = "@echo off\r\nsetlocal enabledelayedexpansion\r\n"
                + "set /p L=\r\n"
                + "set \"L=!L:\"=#!\"\r\n"
                + "for /f \"tokens=8 delims=#\" %%U in (\"!L!\") do set \"U=%%U\"\r\n"
                + "echo {\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":\"x\"},\"uuid\":\"!U!\",\"isReplay\":true}\r\n"
                + string.Join("\r\n", lines.Select(l => $"echo {l}"))
                + "\r\nping -n 120 127.0.0.1 >nul\r\n";
            script = Path.Combine(_root, "fake-cli.cmd");
            File.WriteAllText(script, text, System.Text.Encoding.ASCII);
        }
        else
        {
            var text = "#!/bin/sh\n"
                + "read -r L\n"
                + "U=$(printf '%s' \"$L\" | sed 's/.*\"uuid\":\"\\([^\"]*\\)\".*/\\1/')\n"
                + "echo \"{\\\"type\\\":\\\"user\\\",\\\"message\\\":{\\\"role\\\":\\\"user\\\",\\\"content\\\":\\\"x\\\"},\\\"uuid\\\":\\\"$U\\\",\\\"isReplay\\\":true}\"\n"
                + string.Join("\n", lines.Select(l => $"echo '{l}'"))
                + "\nsleep 120\n";
            script = Path.Combine(_root, "fake-cli.sh");
            File.WriteAllText(script, text, System.Text.Encoding.ASCII);
        }
        return script;
    }

    private sealed class FrameCliLauncher(
        string firstCliScript,
        ConcurrentDictionary<int, Process> clis,
        ConcurrentDictionary<int, IReadOnlyList<string>> argsByStart,
        TaskCompletionSource p1Started, TaskCompletionSource p2Started) : IProcessLauncher
    {
        private int _starts;

        public bool IsSandboxed => false;
        public bool TargetIsWindows => OperatingSystem.IsWindows();
        public IPathMapper Paths => IdentityPathMapper.Instance;
        public string ClaudeCliCommand => "fake-claude";
        public string HostTempDir => Path.GetTempPath();
        public string? McpApiUrlOverride => null;

        public Process Start(ProcessSpec spec)
        {
            var n = Interlocked.Increment(ref _starts);
            argsByStart[n] = spec.Args;
            var fake = new ProcessSpec
            {
                FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
                Args = n == 1
                    ? (OperatingSystem.IsWindows() ? ["/c", firstCliScript] : [firstCliScript])
                    : (OperatingSystem.IsWindows()
                        ? ["/c", "ping -n 120 127.0.0.1 >nul"]
                        : ["-c", "sleep 120"]),
                WorkingDirectory = spec.WorkingDirectory,
                ClearEnv = spec.ClearEnv,
                StdioEncoding = spec.StdioEncoding,
                EnableRaisingEvents = spec.EnableRaisingEvents,
                RedirectStdin = spec.RedirectStdin,
                Track = false,
            };
            var process = LocalProcessRunner.Instance.Start(fake);
            clis[n] = process;
            if (n == 1) p1Started.TrySetResult(); else p2Started.TrySetResult();
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

    // Рамка как свойство проекта: «участников нет» → Wide; «добавили участника» → Narrow
    // (сам резолв по составу участников — TurnFrameTests; здесь — только смена вердикта).
    private sealed class ProjectFrameSource : ClaudeHomeServer.Services.Access.ITurnFrameSource
    {
        private volatile bool _shared;
        public void AddMember() => _shared = true;
        public ClaudeHomeServer.Services.Access.TurnFrameKind Frame(string? sessionId, string ownerId) =>
            _shared ? ClaudeHomeServer.Services.Access.TurnFrameKind.Narrow
                    : ClaudeHomeServer.Services.Access.TurnFrameKind.Wide;
    }

    [Fact]
    public async Task ПереходЛичныйВОбщий_ЖивойПроцессПересоздан_СистемныйБлокБезЛичныхМаркеров()
    {
        var frameSource = new ProjectFrameSource();
        var context = BuildContext(frameSource);

        var session = new ClaudeSession(
            // Чат личного проекта с владельцем: рамку решает ITurnFrameSource (состав проекта),
            // не ProjectId сам по себе.
            new Session { Model = "qwen-test-27b", OwnerId = "u1", ProjectId = "p1" },
            context,
            providers: new LlmProviderRegistry(BuildContextConfig()));

        await using var _ = session;

        // Шаг 1: ход владельца в личном проекте (Wide). Провайдер без RecallInTurnText →
        // личная подборка (маркер) стоит в СИСТЕМНОМ блоке P1 — фикстура не вакуумная.
        await session.SendMessageAsync("первый ход владельца");
        await WhenAnyAsync(_p1Started.Task, TimeSpan.FromSeconds(15), "старт P1");
        var prompt1 = SystemPromptOf(_argsByStart[1]);
        prompt1.Should().Contain(Marker,
            "в личном проекте личная подборка стоит в системном блоке поднятого процесса — " +
            "именно она и есть то, что не должно доехать участнику");

        // Окно same-process открыто: ход завершён result'ом, прогон жив с фоновой задачей.
        await UntilAsync(() => RunOf(session) is not null, TimeSpan.FromSeconds(5), "регистрация прогона");
        var run1 = RunOf(session)!;
        await UntilAsync(() => TurnDoneOf(run1), TimeSpan.FromSeconds(30), "завершение первого хода result'ом");

        // Шаг 2: «владелец добавляет участника» — проект стал общим, рамка перевернулась.
        frameSource.AddMember();

        // Шаг 3: ход участника. Рамка в сигнатуре → она НЕ совпадает с P1 → живой процесс
        // хода не принимает: обязан стартовать новый процесс (P2). Без рамки в сигнатуре
        // (мутация) этот шаг красный: P2 не стартует, ход уйдёт same-process submit'ом в P1.
        await session.SendMessageAsync("ход участника");
        await WhenAnyAsync(_p2Started.Task, TimeSpan.FromSeconds(30),
            "старт нового процесса: смена рамки меняет сигнатуру запуска");

        var prompt2 = SystemPromptOf(_argsByStart[2]);
        prompt2.Should().NotContain(Marker,
            "новый процесс общего проекта стартует с узкой подборкой: личных маркеров " +
            "владельца в его системном блоке нет");
    }

    // Чистая функция сигнатуры: рамка входит в отпечаток. Два хода одного чата (одна рамка)
    // — один отпечаток (инвариант стабильности, McpToolsetStabilityTests); смена рамки —
    // другой отпечаток (новый процесс).
    [Fact]
    public void Сигнатура_РамкаВходит_ОдинаковаяСтабильна_СменаРазная()
    {
        var args = new List<string> { "--print", "--model", "opus" };
        string Sig(ClaudeHomeServer.Services.Access.TurnFrameKind frame) =>
            ClaudeSession.BuildLaunchSignature(args, "tasks", "",
                new Dictionary<string, string>(), null, frame);

        Sig(ClaudeHomeServer.Services.Access.TurnFrameKind.Wide)
            .Should().Be(Sig(ClaudeHomeServer.Services.Access.TurnFrameKind.Wide),
                "рамка — свойство проекта, не хода: два хода одного чата дают один отпечаток");
        Sig(ClaudeHomeServer.Services.Access.TurnFrameKind.Wide)
            .Should().NotBe(Sig(ClaudeHomeServer.Services.Access.TurnFrameKind.Narrow),
                "смена рамки (личный→общий) — другой отпечаток → новый процесс");
    }

    // ─── helper'ы ───

    private LlmSessionContext BuildContext(ProjectFrameSource frameSource) => new(
        RootPath: _root,
        OnMessage: _ => Task.CompletedTask,
        RawSystemPrompt: null, BuiltInSystemPrompt: ClaudeHomeServer.Services.ProjectManager.BuiltInSystemPrompt,
        PermissionRules: null,
        TasksMcp: null,
        // Облачный провайдер без ручки RecallInTurnText (LlmProviderRegistry.RecallInTurnTextFor
        // = false): личные нестабильные секции стоят в системном блоке, а не хвостом хода.
        Launcher: new FrameCliLauncher(_firstCliScript = WriteFirstCliScript(),
            _clis, _argsByStart, _p1Started, _p2Started),
        SystemPromptViaFile: false,
        Events: BusWithPersonalRecall(),
        FrameSource: frameSource);

    // Личная подборка в подборке хода — эмуляция recall-контрибьютора (тот же приём, что
    // ClaudeSessionPromptSectionsOrderTests): маркер реально проходит через Add/sections
    // и при Wide+без ручки попадает в системный блок.
    private static TurnEventBus BusWithPersonalRecall()
    {
        var bus = new TurnEventBus();
        bus.OnFilter<PromptAssembling>(200, (e, next) =>
        {
            e.Sections.Add(new ClaudeHomeServer.Services.Turn.PromptSection(
                "recall-notes", Marker));
            return next();
        }, "Test.PersonalRecall");
        return bus;
    }

    private IConfiguration BuildContextConfig() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["LlmProviders:test-local:DisplayName"] = "Тестовый локальный",
            ["LlmProviders:test-local:AnthropicBaseUrl"] = "http://127.0.0.1:65535",
            ["LlmProviders:test-local:IsLocal"] = "true",
            ["LlmProviders:test-local:RecallInTurnText"] = "false",
            ["LlmProviders:test-local:Models:0:Id"] = "qwen-test-27b",
            ["LlmProviders:test-local:Models:0:DisplayName"] = "Qwen Test",
            // Профили CLI — в папке теста (IsolatedProfiles из ClaudeSessionPromptSectionsOrderTests):
            // реальный ~/.claude не сится на каждый тест.
            ["DataPath"] = Path.Combine(_root, "data", "projects.json"),
            ["ClaudeUserProfileDir"] = Path.Combine(_root, "no-user-claude"),
        }).Build();

    private static string SystemPromptOf(IReadOnlyList<string> args)
    {
        var idx = args.ToList().IndexOf("--append-system-prompt");
        idx.Should().BeGreaterThanOrEqualTo(0,
            "стабильная часть промпта (BuiltInSystemPrompt) непустая — аргумент обязан присутствовать");
        return args[idx + 1];
    }

    private static object? RunOf(ClaudeSession session) => RunField.GetValue(session);

    private static bool TurnDoneOf(object run) => (bool)TurnDoneField.GetValue(run)!;

    private static async Task WhenAnyAsync(Task task, TimeSpan timeout, string what)
    {
        var done = await Task.WhenAny(task, Task.Delay(timeout));
        done.Should().Be(task, $"не дождались: {what}");
    }

    private static async Task UntilAsync(Func<bool> condition, TimeSpan timeout, string what)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(25);
        }
        condition().Should().BeTrue($"не дождались: {what}");
    }
}
