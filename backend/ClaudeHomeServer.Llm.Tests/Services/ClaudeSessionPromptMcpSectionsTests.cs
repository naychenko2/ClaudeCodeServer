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

/// <summary>
/// Секции промпта про MCP-инструменты обязаны следовать за ФАКТИЧЕСКИМ составом серверов
/// ЭТОГО хода, а не за наличием контекста у сессии. До правки (задача be00ff12) урезание
/// TrimMcpServers/KeepMcpServers уносило сервер из конфига хода, а руководство к нему
/// оставалось в промпте: замер 2026-09-07 на local-qwen — 7 751 символ (~2 580 токенов,
/// 15% промпта) на инструкции к инструментам, которых у модели нет. Это не только трата
/// окна, но и дезинформация: секции звали search_unified, notes_create, watch_start.
///
/// Тесты сквозные: ход собирается настоящим BuildArgs, секции снимаются из снимка промпта
/// (шина PromptAssembled) — текстовая проверка исходника ничего не доказала бы, гейт мог бы
/// молча разъехаться с составом серверов.
/// </summary>
public class ClaudeSessionPromptMcpSectionsTests : IDisposable
{
    private readonly ConcurrentDictionary<int, Process> _clis = new();
    private readonly List<string> _tempDirs = [];
    private readonly List<string> _tempConfigs = [];

    // Серверный корень с картой проекта для BareMode (--system-prompt-file)
    private readonly string _serverRoot =
        Path.Combine(Path.GetTempPath(), "ccs-mcp-sections-server-" + Guid.NewGuid().ToString("N")[..8]);

    private const string LocalModelId = "qwen3.8-27b";

    public ClaudeSessionPromptMcpSectionsTests()
    {
        Directory.CreateDirectory(Path.Combine(_serverRoot, "SystemPrompts"));
        File.WriteAllText(Path.Combine(_serverRoot, "SystemPrompts", "CLAUDE-local.md"), "# карта проекта");
    }

    public void Dispose()
    {
        foreach (var p in _clis.Values)
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { /* уже мёртв */ }
            ProcessTestCleanup.SafeDispose(p); // Process.Close гонится с уборкой сессии
        }
        foreach (var cfg in _tempConfigs)
            try { File.Delete(cfg); } catch { /* уборка best-effort */ }
        foreach (var dir in _tempDirs)
            try { Directory.Delete(dir, recursive: true); } catch { /* уборка best-effort */ }
        try { Directory.Delete(_serverRoot, recursive: true); } catch { /* уборка best-effort */ }
    }

    private string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ccs-mcp-sections-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    // Лаунчер-заглушка: настоящий CLI не запускаем, процесс подменяем сном. Args ловим,
    // чтобы забрать путь temp-конфига хода и убрать файл за собой.
    private sealed class FakeLauncher(ConcurrentDictionary<int, Process> clis) : IProcessLauncher
    {
        public IReadOnlyList<string>? CapturedArgs { get; private set; }
        public bool IsSandboxed => false;
        public bool TargetIsWindows => OperatingSystem.IsWindows();
        public IPathMapper Paths => IdentityPathMapper.Instance;
        public string ClaudeCliCommand => "fake-claude";
        public string HostTempDir => Path.GetTempPath();
        public string? McpApiUrlOverride => null;

        public Process Start(ProcessSpec spec)
        {
            CapturedArgs = spec.Args?.ToArray();
            var fake = new ProcessSpec
            {
                FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
                Args = OperatingSystem.IsWindows() ? ["/c", "ping -n 30 127.0.0.1 >nul"] : ["-c", "sleep 30"],
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

    // Реестр провайдеров: local-qwen с заданными урезанием/BareMode + облачный сосед
    // (на нём урезания нет — регрессия «правка не задевает чужого провайдера»).
    private LlmProviderRegistry Providers(bool trimMcp, bool bareMode, params string[] keep) =>
        Providers(trimMcp, bareMode, keepTools: null, keep);

    // keepTools — белый список инструментов внутри оставленных серверов (KeepMcpTools)
    private LlmProviderRegistry Providers(bool trimMcp, bool bareMode,
        Dictionary<string, string[]>? keepTools, params string[] keep)
    {
        var cfg = new Dictionary<string, string?>
        {
            ["LlmProviders:local-qwen:AnthropicBaseUrl"] = "http://127.0.0.1:18020",
            ["LlmProviders:local-qwen:IsLocal"] = "true",
            ["LlmProviders:local-qwen:TrimMcpServers"] = trimMcp ? "true" : "false",
            ["LlmProviders:local-qwen:BareMode"] = bareMode ? "true" : "false",
            ["LlmProviders:local-qwen:SystemPromptFile"] = "SystemPrompts/CLAUDE-local.md",
            ["LlmProviders:local-qwen:Models:0:Id"] = LocalModelId,
        };
        for (var i = 0; i < keep.Length; i++)
            cfg[$"LlmProviders:local-qwen:KeepMcpServers:{i}"] = keep[i];
        foreach (var (server, tools) in keepTools ?? [])
            for (var i = 0; i < tools.Length; i++)
                cfg[$"LlmProviders:local-qwen:KeepMcpTools:{server}:{i}"] = tools[i];
        return new LlmProviderRegistry(Helpers.TestConfig.Build(cfg));
    }

    // Полный набор продуктовых MCP-контекстов на http-ветке: без урезания все они доедут
    // до конфига хода, с урезанием — только те, что в белом списке. UseHttp=true снимает
    // зависимость теста от наличия node-файлов серверов в дереве репозитория.
    private LlmSessionContext FullMcpContext(IProcessLauncher launcher, ITurnEventBus bus) => new(
        RootPath: NewTempDir(),
        OnMessage: _ => Task.CompletedTask,
        RawSystemPrompt: null, BuiltInSystemPrompt: ClaudeHomeServer.Services.ProjectManager.BuiltInSystemPrompt,
        PermissionRules: null,
        TasksMcp: new TasksMcpContext("http://localhost:5999", () => "tok", ProjectId: "proj1",
            UseHttp: true),
        NotesMcp: new NotesMcpContext("http://localhost:5999", () => "tok", ProjectId: "proj1",
            AnnotationsEnabled: false, UseHttp: true),
        MemoryMcp: new MemoryMcpContext("http://localhost:5999", () => "tok", PersonaId: "p1",
            ProjectId: "proj1", UseHttp: true),
        // MentionsHint задан — иначе секции persona-mentions нет и на полном наборе
        PersonasMcp: new PersonasMcpContext("http://localhost:5999", () => "tok", ProjectId: "proj1",
            MentionsHint: "- @gleb — Код-ревьюер (Глеб)", UseHttp: true),
        WorkspaceMcp: new WorkspaceMcpContext("http://localhost:5999", () => "tok", "proj1",
            Sections: ["projects", "files"], UseHttp: true),
        WidgetsMcp: new WidgetsMcpContext("http://localhost:5999", () => "tok", UseHttp: true),
        WatchMcp: new WatchMcpContext("http://localhost:5999", () => "tok", UseHttp: true),
        // Файловые сабагенты: только имена (pmem-серверов не заводим) — от них зависит
        // секция workflow-subagents
        PersonaAgentsProvider: () => new PersonaAgentsContext([], [], ["gleb", "denis"], []),
        Launcher: launcher,
        Events: bus,
        ContentRootPath: _serverRoot);

    // Прогон одного хода: возвращает ключи секций промпта из снимка.
    private async Task<IReadOnlyList<string>> SectionKeysAsync(LlmProviderRegistry providers) =>
        [.. (await SectionsAsync(providers)).Select(s => s.Key)];

    private async Task<IReadOnlyList<PromptSectionDto>> SectionsAsync(LlmProviderRegistry providers)
    {
        var bus = new TurnEventBus();
        var captured = new TaskCompletionSource<IReadOnlyList<PromptSectionDto>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        bus.OnNotification<PromptAssembled>(async e =>
        {
            if (e.Snapshot?.Draft is { } draft && draft.Sections.Count > 0)
                captured.TrySetResult(draft.Sections);
            await Task.CompletedTask;
        });

        var launcher = new FakeLauncher(_clis);
        var session = new ClaudeSession(new Session { Model = LocalModelId, ProjectId = "proj1" },
            FullMcpContext(launcher, bus), providers: providers);
        await using var _ = session;

        await session.SendMessageAsync("тестовый ход");
        var done = await Task.WhenAny(captured.Task, Task.Delay(TimeSpan.FromSeconds(15)));
        done.Should().Be(captured.Task, "снимок промпта публикуется шиной до старта CLI");

        // Temp-конфиг хода за собой убираем: каждый ход пишет новый файл в %TEMP%
        if (launcher.CapturedArgs is { } args)
        {
            var idx = args.ToList().IndexOf("--mcp-config");
            if (idx >= 0 && idx + 1 < args.Count) _tempConfigs.Add(args[idx + 1]);
        }
        return await captured.Task;
    }

    /// <summary>
    /// KeepMcpServers=["tasks"]: секция про задачи остаётся, руководства к негодным
    /// серверам исчезают. Это и есть замеренные 2 580 токенов задачи be00ff12 — мутация
    /// любого гейта обратно на «контекст сервера есть» красит этот тест.
    /// </summary>
    [Fact]
    public async Task УрезанныйНабор_СекцийНедоступныхСерверовНет()
    {
        var keys = await SectionKeysAsync(Providers(trimMcp: true, bareMode: false, "tasks"));

        keys.Should().Contain("mcp-tasks", "tasks в белом списке — инструмент у хода есть, подсказка нужна");
        foreach (var key in new[] { "mcp-notes", "mcp-workspace", "mcp-watch", "mcp-personas",
                                    "mcp-widgets", "mcp-memory" })
            keys.Should().NotContain(key,
                $"сервер секции «{key}» не доехал до хода — подсказка к нему учит несуществующему инструменту");
        keys.Should().NotContain("persona-mentions",
            "persona_ask живёт в сервере personas, а он урезан — звать его некуда");
    }

    /// <summary>
    /// Обратная совместимость: провайдер без урезания получает ВСЕ секции, как раньше.
    /// Без этого теста гейт мог бы «сэкономить» на родном Claude и облачных провайдерах.
    /// </summary>
    [Fact]
    public async Task БезУрезания_ВсеСекцииНаМесте()
    {
        var keys = await SectionKeysAsync(Providers(trimMcp: false, bareMode: false));

        foreach (var key in new[] { "mcp-tasks", "mcp-notes", "mcp-workspace", "mcp-watch",
                                    "mcp-personas", "mcp-widgets", "mcp-memory",
                                    "persona-mentions", "workflow-subagents" })
            keys.Should().Contain(key,
                $"состав не урезан — секция «{key}» описывает реально доступный инструмент");
    }

    /// <summary>
    /// BareMode боевого local-qwen (--bare + KeepMcpServers=["tasks"]): ни persona-mentions,
    /// ни workflow-subagents. Под --bare CLI отдаёт только Bash/Edit/PowerShell/Read —
    /// встроенного Task нет, сабагентов позвать нечем; persona_ask уносит урезание набора.
    /// </summary>
    [Fact]
    public async Task BareMode_НетПодсказокПроСабагентов()
    {
        var keys = await SectionKeysAsync(Providers(trimMcp: true, bareMode: true, "tasks"));

        keys.Should().NotContain("workflow-subagents",
            "под --bare встроенного Task нет — Task(agentType=…) вызвать нечем");
        keys.Should().NotContain("persona-mentions",
            "под --bare нет Task, а personas урезан — оба канала консультаций мертвы");
        keys.Should().Contain("mcp-tasks", "tasks остаётся — локальный исполнитель закрывает задачу им");
    }

    /// <summary>
    /// Две оси гейта разведены: --bare убивает встроенный Task (workflow-subagents уходит),
    /// но НЕ трогает доехавший сервер personas — persona_ask остаётся настоящим инструментом,
    /// и подсказка про @упоминания остаётся вместе с ним (текст умеет быть чисто
    /// persona_ask'овым — SessionManager.BuildMentionsHint, ветка viaAsk).
    /// </summary>
    [Fact]
    public async Task BareModeБезУрезания_TaskСекцияУходит_PersonaAskОстаётся()
    {
        var keys = await SectionKeysAsync(Providers(trimMcp: false, bareMode: true));

        keys.Should().NotContain("workflow-subagents", "Task под --bare недоступен");
        keys.Should().Contain("persona-mentions",
            "сервер personas доехал — persona_ask работает, подсказку про него не отнимаем");
    }

    /// <summary>
    /// СТОРОЖ: при полностью погашенном наборе (TrimMcpServers=true, пустой KeepMcpServers)
    /// в промпте не должно остаться НИ ОДНОЙ секции с ключом «mcp-*». Ловит новую секцию,
    /// заведённую без гейта доставки: она появится здесь и покрасит тест — иначе очередное
    /// руководство к отсутствующему инструменту уехало бы в окно локальной модели молча.
    /// </summary>
    [Fact]
    public async Task ПустойНабор_НиОднойСекцииПроMcp()
    {
        var keys = await SectionKeysAsync(Providers(trimMcp: true, bareMode: false));

        keys.Where(k => k.StartsWith("mcp-", StringComparison.Ordinal)).Should().BeEmpty(
            "ни один продуктовый сервер до хода не доехал — руководств к ним в промпте быть не может");
        keys.Should().NotContain("persona-mentions", "сервер personas погашен вместе со всеми");
    }

    // Белый список облегчённого профиля, как у боевого local-qwen на 2026-11-20
    private static readonly Dictionary<string, string[]> LightKeepTools = new()
    {
        ["tasks"] = ["tasks_get", "tasks_update", "tasks_complete", "tasks_add_subtask",
                     "tasks_toggle_subtask", "tasks_create", "tasks_search", "tasks_list"],
        ["memory"] = ["memory_recall", "memory_search", "memory_remember", "team_memory_search"],
    };

    /// <summary>
    /// Секции называют только ВЫДАННЫЕ инструменты. Снятый белым списком инструмент модель
    /// зовёт вслепую, Strata отдаёт такой вызов текстом — ход обрывается молча (117 случаев
    /// за 17–20.11.2026: team_memory_remember, memory_rethink, tasks_* сверх списка).
    /// </summary>
    [Fact]
    public async Task БелыйСписокИнструментов_СекцииНеНазываютСнятых()
    {
        var sections = await SectionsAsync(Providers(trimMcp: true, bareMode: false, LightKeepTools,
            "tasks", "memory"));

        var tasks = sections.Single(s => s.Key == "mcp-tasks").Text;
        tasks.Should().Contain("tasks_search").And.Contain("tasks_list").And.Contain("tasks_complete");
        foreach (var removed in new[] { "tasks_delete", "tasks_board_columns", "tasks_run_executor", "tasks_list_projects" })
            tasks.Should().NotContain(removed, $"{removed} снят белым списком — вызов оборвал бы ход");

        var memory = sections.Single(s => s.Key == "mcp-memory").Text;
        memory.Should().Contain("memory_remember").And.Contain("team_memory_search");
        foreach (var removed in new[] { "memory_rethink", "memory_forget", "memory_list",
                                        "team_memory_remember", "team_memory_update", "team_memory_forget" })
            memory.Should().NotContain(removed, $"{removed} снят белым списком — вызов оборвал бы ход");
    }

    /// <summary>
    /// Без белого списка инструментов секции прежние: полный перечень, правка и командная запись.
    /// </summary>
    [Fact]
    public async Task БезБелогоСпискаИнструментов_ПолныйПеречень()
    {
        var sections = await SectionsAsync(Providers(trimMcp: false, bareMode: false));

        var tasks = sections.Single(s => s.Key == "mcp-tasks").Text;
        tasks.Should().Contain("tasks_delete").And.Contain("tasks_board_columns").And.Contain("tasks_run_executor");
        var memory = sections.Single(s => s.Key == "mcp-memory").Text;
        memory.Should().Contain("memory_rethink").And.Contain("team_memory_remember");
    }
}
