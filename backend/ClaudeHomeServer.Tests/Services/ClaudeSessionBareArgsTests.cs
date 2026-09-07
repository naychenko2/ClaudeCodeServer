using System.Diagnostics;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Execution;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Llm.Claude;
using FluentAssertions;
using Microsoft.Extensions.Logging;

namespace ClaudeHomeServer.Tests.Services;

// Аргументы BareMode (--bare + --system-prompt-file + опц. --tools) и СНИМОК промпта —
// критичные сигнатуры запуска CLI и наблюдаемого состояния. Проверяем через сквозной
// BuildArgs (ArgsCapturingLauncher по образцу ClaudeSessionEffortArgsTests): гейт
// `bareProvider is { BareMode: true }` (ClaudeSession.cs:2455) и снимок
// `_lastBareModeApplied` (ClaudeSession.cs:3948) должны краснеть на своих мутациях,
// иначе «облачные не задеты» и «снимок не врёт» — фикция.
//
// Класс перехватывает stderr (Console.SetError) — а он процесс-глобален: параллельный
// сосед со своим SetError/восстановлением в finally отдал бы нам чужой поток посреди
// ассерта. Лечится КОЛЛЕКЦИЕЙ (TestCollections.ProcessGlobalState), а не глобальным
// parallelizeTestCollections=false: сериализуются только классы, трогающие
// процесс-глобальное состояние, остальной набор идёт параллельно (ревью 2026-09-06, M-3).
//
// Чистая функция BuildBareModeArgs покрывает резолв пути и снятие обоих флагов
// (SafeJoin, отсутствие файла, bareTools=null/[]/заполненный); её контракт
// проверяется отдельно в нижней части файла.
[Collection(TestCollections.ProcessGlobalState)]
public class ClaudeSessionBareArgsTests : IDisposable
{
    private readonly List<Process> _processes = [];
    // Временные каталоги, созданные ЭТИМ тестом (корни ходов и проектов). Чистятся в
    // Dispose — иначе копятся в %TEMP% десятками за прогон (ревью 2026-09-06, L).
    private readonly List<string> _tempDirs = [];

    // Создать временный каталог и поставить его на учёт для уборки в Dispose.
    private string NewTempDir(string prefix)
    {
        var dir = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        lock (_tempDirs) _tempDirs.Add(dir);
        return dir;
    }

    public void Dispose()
    {
        foreach (var p in _processes)
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { /* уже мёртв */ }
            p.Dispose();
        }
        // Временные каталоги тестов чистим при выходе — иначе накапливаются в %TEMP%
        // (см. ревью 2026-09-05, L-7). ServerRoot общий для всех тестов класса
        // (static readonly), удаляется здесь; корни ходов и проектов — из _tempDirs.
        lock (_tempDirs)
        {
            foreach (var dir in _tempDirs)
            {
                try { Directory.Delete(dir, recursive: true); } catch { /* уже удалён/занят */ }
            }
            _tempDirs.Clear();
        }
        try { Directory.Delete(ServerRoot, recursive: true); } catch { /* уже удалён */ }
    }

    // Локальный серверный корень: в нём лежит SystemPrompts/CLAUDE-local.md для BareMode.
    private static readonly string ServerRoot =
        Path.Combine(Path.GetTempPath(), "bare_server_" + Guid.NewGuid().ToString("N"));

    private static readonly IPathMapper Paths = IdentityPathMapper.Instance;

    public ClaudeSessionBareArgsTests()
    {
        Directory.CreateDirectory(ServerRoot);
        Directory.CreateDirectory(Path.Combine(ServerRoot, "SystemPrompts"));
        File.WriteAllText(Path.Combine(ServerRoot, "SystemPrompts", "CLAUDE-local.md"), "# local map");
        File.WriteAllText(Path.Combine(ServerRoot, "SystemPrompts", "ok.md"), "# ok map");
    }

    // ---------- Сторожа через сквозной BuildArgs (мутационная проверка) ----------

    private const string LocalModelId = "qwen3.8-27b";
    private const string CloudModelId = "deepseek-v4-pro";

    // Лаунчер по образцу ClaudeSessionEffortArgsTests: захватывает args хода и подменяет
    // процесс сном, чтобы не запускать настоящий CLI. IsSandboxed=false: IdentityPathMapper,
    // никакой ToRuntime-перевод, SystemPrompts берётся прямо с диска.
    private sealed class ArgsCapturingLauncher(List<Process> processes, TaskCompletionSource started) : IProcessLauncher
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
                Args = OperatingSystem.IsWindows()
                    ? ["/c", "ping -n 120 127.0.0.1 >nul"]
                    : ["-c", "sleep 120"],
                WorkingDirectory = spec.WorkingDirectory,
                StdioEncoding = spec.StdioEncoding,
                EnableRaisingEvents = spec.EnableRaisingEvents,
                RedirectStdin = spec.RedirectStdin,
                Track = false,
            };
            var process = LocalProcessRunner.Instance.Start(fake);
            lock (processes) processes.Add(process);
            started.TrySetResult();
            return process;
        }

                public int EstimateCommandLineLength(ProcessSpec spec)
        {
            // Заглушка для фейков: тесты, которые гоняют ClaudeSession.ApplyBudget,
            // нуждаются в числовом ответе, но не в точной семантике раннера (её
            // проверяет DockerProcessRunnerCmdlineEstimationTests на реальном раннере).
            // Считаем FileName + args через TurnPromptAssembler.ArgCost — та же формула,
            // что в LocalProcessRunner.EstimateCommandLineLength, без RawArguments.
            var total = (spec.FileName ?? string.Empty).Length;
            foreach (var a in spec.Args) total += TurnPromptAssembler.ArgCost(a);
            return total;
        }
        public void Kill(Process process, string? turnId = null)
        {
            try { process.Kill(entireProcessTree: true); }
            catch { /* уже мёртв */ }
        }
    }

    // Провайдер с локальной моделью local-qwen и облачным deepseek для проверки изоляции
    // BareMode между провайдерами (мутация `is { BareMode: true }` → `is not null` сломает
    // тесты для cloud BareMode=false).
    private static LlmProviderRegistry LocalProviders(
        bool localBareMode, string? localSystemPromptFile,
        string[]? localBareTools = null)
    {
        var cfg = Helpers.TestConfig.Build(new Dictionary<string, string?>
        {
            ["LlmProviders:local-qwen:AnthropicBaseUrl"] = "http://127.0.0.1:18020",
            ["LlmProviders:local-qwen:IsLocal"] = "true",
            ["LlmProviders:local-qwen:BareMode"] = localBareMode.ToString().ToLowerInvariant(),
            ["LlmProviders:local-qwen:Models:0:Id"] = LocalModelId,
            ["LlmProviders:deepseek:AnthropicBaseUrl"] = "https://api.deepseek.com/anthropic",
            ["LlmProviders:deepseek:ApiKey"] = "sk-test",
            ["LlmProviders:deepseek:Models:0:Id"] = CloudModelId,
            // У облачного провайдера BareMode НЕ включён, но карта задана и файл существует
            // (ok.md кладёт конструктор класса). Без этой строки сторож изоляции инертен:
            // под мутацией гейта `is { BareMode: true }` → `is not null` BuildBareModeArgs
            // получил бы пустой SystemPromptFile, сам снял бы оба флага (ранний возврат по
            // string.IsNullOrWhiteSpace) и тест остался бы ЗЕЛЁНЫМ — мутация прошла бы молча
            // (ревью 2026-09-06, H-1). С существующей картой мутация даёт --bare → честный RED.
            ["LlmProviders:deepseek:SystemPromptFile"] = "SystemPrompts/ok.md",
        });
        if (localSystemPromptFile is not null)
            cfg["LlmProviders:local-qwen:SystemPromptFile"] = localSystemPromptFile;
        if (localBareTools is not null)
        {
            for (var i = 0; i < localBareTools.Length; i++)
                cfg[$"LlmProviders:local-qwen:BareTools:{i}"] = localBareTools[i];
        }
        return new LlmProviderRegistry(cfg);
    }

    // Запуск хода с подменой процесса на сон; возвращает (args, сессия для проверки
    // LastBareModeApplied/снимка после хода).
    private async Task<(IReadOnlyList<string>? Args, ClaudeSession Session)> RunTurnAsync(
        string model, LlmProviderRegistry providers, string? effort = null)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var root = NewTempDir("ccs-bare-args-tests-");

        var launcher = new ArgsCapturingLauncher(_processes, started);
        var context = new LlmSessionContext(
            RootPath: root,
            OnMessage: _ => Task.CompletedTask,
            RawSystemPrompt: null,
            PermissionRules: null,
            TasksMcp: null,
            WidgetsMcp: new WidgetsMcpContext("http://localhost:5999", () => "tok", UseHttp: false),
            PersonaAgentsProvider: null,
            HttpMcpActive: false,
            HttpMcpEnabledProvider: null,
            Launcher: launcher,
            ContentRootPath: ServerRoot);
        var session = new ClaudeSession(new Session { Model = model, Effort = effort }, context,
            providers: providers);

        await session.SendMessageAsync("тестовый ход");
        var done = await Task.WhenAny(started.Task, Task.Delay(TimeSpan.FromSeconds(15)));
        done.Should().Be(started.Task, "процесс хода обязан стартовать");
        launcher.CapturedArgs.Should().NotBeNull("args хода собираются до запуска процесса");

        Process cli;
        lock (_processes) cli = _processes[^1];
        cli.Kill();
        await Task.WhenAny(Task.Delay(TimeSpan.FromSeconds(2)));
        return (launcher.CapturedArgs, session);
    }

    /// <summary>
    /// СТОРОЖ ГЕЙТА: при BareMode=true у локального провайдера CLI получает --bare,
    /// _lastBareModeApplied=true. Мутация `is { BareMode: true }` → `is not null` оставит
    /// ход в обычном режиме (облачный провайдер не подключит BuildBareModeArgs), но
    /// этот тест облачные модели не запускает — он зеркальный к тесту облачного.
    /// </summary>
    [Fact]
    public async Task ХодНаЛокальном_BareMode_СтавитBareИПоследнийПризнакTrue()
    {
        var providers = LocalProviders(
            localBareMode: true,
            localSystemPromptFile: "SystemPrompts/CLAUDE-local.md");
        var (args, session) = await RunTurnAsync(LocalModelId, providers);

        args!.Should().Contain("--bare", "BareMode=true обязан пройти в CLI");
        args.Should().Contain("--system-prompt-file");
        session.LastBareModeApplied.Should().BeTrue();
    }

    /// <summary>
    /// СТОРОЖ ГЕЙТА: при BareMode=false у локального провайдера CLI НЕ получает --bare,
    /// _lastBareModeApplied=false. Мутация `is { BareMode: true }` → `is not null` сломает
    /// ход на deepseek-v4-pro (облачный провайдер, без BareMode вовсе): BareArgs подключатся
    /// → args будут содержать --bare → RED. Сторож ДЕРЖИТСЯ на строке фикстуры
    /// `LlmProviders:deepseek:SystemPromptFile` (существующий ok.md): без неё
    /// BuildBareModeArgs снял бы оба флага сам, по пустому пути карты, и мутация прошла
    /// бы молча — так и было до круга 8.
    /// </summary>
    [Fact]
    public async Task ХодНаОблачномПровайдере_BareModeОтсутствует_ФлагаBareНет()
    {
        var providers = LocalProviders(localBareMode: false, localSystemPromptFile: null);
        var (args, session) = await RunTurnAsync(CloudModelId, providers);

        args!.Should().NotContain("--bare", "облачный провайдер без BareMode не должен подключать BuildBareModeArgs");
        args.Should().NotContain("--system-prompt-file");
        session.LastBareModeApplied.Should().BeFalse();
    }

    /// <summary>
    /// СТОРОЖ CATCH: при пути ЗА корень SafeJoin бросает UnauthorizedAccessException,
    /// catch в BuildArgs ловит его И ПИШЕТ ДИАГНОСТИКУ В STDERR («за пределами корня»),
    /// плюс сбрасывает _lastBareModeApplied=false. Мутация удаления catch приведёт к
    /// (а) вылету исключения из SendMessageAsync → CapturedArgs останется null → RED
    /// или (б) при no-op catch — пропадёт stderr-диагностика → RED. Тест проверяет ОБА
    /// канала: args без --bare, признак false, И в stderr есть сообщение о выходе за корень.
    /// </summary>
    [Fact]
    public async Task ХодНаЛокальном_ПутьЗаКорень_CatchСбрасываетBareMode()
    {
        var providers = LocalProviders(
            localBareMode: true,
            localSystemPromptFile: "../etc/passwd");

        var originalErr = Console.Error;
        var errCapture = new System.IO.StringWriter();
        Console.SetError(errCapture);
        try
        {
            var (args, session) = await RunTurnAsync(LocalModelId, providers);

            args!.Should().NotContain("--bare", "UnauthorizedAccessException из SafeJoin должен быть пойман, BareMode снят");
            args.Should().NotContain("--system-prompt-file");
            session.LastBareModeApplied.Should().BeFalse();
            errCapture.ToString().Should().Contain("за пределами корня",
                "catch обязан писать диагностику в stderr; no-op catch ломает это");
        }
        finally
        {
            Console.SetError(originalErr);
        }
    }

    /// <summary>
    /// СТОРОЖ ВТОРОГО CATCH: ToRuntime у DockerPathMapper бросает InvalidOperationException,
    /// когда путь не входит ни в одно правило маппинга (файл есть на хосте, но bind-mount в
    /// контейнер его не показывает). Первый catch ловит UnauthorizedAccessException из SafeJoin
    /// (путь за пределы корня), второй — InvalidOperationException из ToRuntime (путь вне
    /// монтирований). Без второго catch — вылет из SendMessageAsync, CapturedArgs остаётся
    /// null → RED. Сторож: подсунуть IPathMapper, чей ToRuntime кидает InvalidOperationException,
    /// и убедиться, что args без --bare, признак false, И в stderr есть сообщение о недоступности
    /// в песочнице (мутация удаления catch → RED по любому из трёх каналов).
    /// </summary>
    [Fact]
    public async Task ХодНаЛокальном_ToRuntimeБросает_CatchСбрасываетBareMode()
    {
        var providers = LocalProviders(
            localBareMode: true,
            localSystemPromptFile: "SystemPrompts/CLAUDE-local.md");
        var throwingLauncher = new ThrowingRuntimeLauncher(_processes);

        var originalErr = Console.Error;
        var errCapture = new System.IO.StringWriter();
        Console.SetError(errCapture);
        try
        {
            var (args, session) = await RunTurnAsyncWithLauncher(LocalModelId, providers, throwingLauncher);

            args!.Should().NotContain("--bare",
                "InvalidOperationException из ToRuntime должен быть пойман вторым catch, BareMode снят");
            args.Should().NotContain("--system-prompt-file");
            session.LastBareModeApplied.Should().BeFalse();
            errCapture.ToString().Should().Contain("недоступен в песочнице",
                "второй catch обязан писать диагностику в stderr; no-op ломает это");
        }
        finally
        {
            Console.SetError(originalErr);
        }
    }

    private async Task<(IReadOnlyList<string>? Args, ClaudeSession Session)> RunTurnAsyncWithLauncher(
        string model, LlmProviderRegistry providers, IProcessLauncher launcher)
    {
        var root = NewTempDir("ccs-bare-throwing-");

        var context = new LlmSessionContext(
            RootPath: root,
            OnMessage: _ => Task.CompletedTask,
            RawSystemPrompt: null,
            PermissionRules: null,
            TasksMcp: null,
            WidgetsMcp: new WidgetsMcpContext("http://localhost:5999", () => "tok", UseHttp: false),
            PersonaAgentsProvider: null,
            HttpMcpActive: false,
            HttpMcpEnabledProvider: null,
            Launcher: launcher,
            ContentRootPath: ServerRoot);
        var session = new ClaudeSession(new Session { Model = model }, context, providers: providers);

        await session.SendMessageAsync("тестовый ход");
        // Args собираются BuildArgs ДО запуска процесса (в try до Start), поэтому ждать
        // started не нужно: ToRuntime бросает ПОСЛЕ сборки args, а Catch ловит — args уже
        // у лаунчера. Даём короткий таймаут на отработку исключения в фоне.
        await Task.WhenAny(Task.Delay(TimeSpan.FromSeconds(3)));
        // CapturedArgs читается через рефлексию, чтобы не зависеть от конкретного типа лаунчера.
        var capturedArgs = GetCapturedArgs(launcher);
        capturedArgs.Should().NotBeNull("args хода собираются до запуска процесса");

        Process cli;
        lock (_processes)
        {
            if (_processes.Count > 0)
            {
                cli = _processes[^1];
                try { cli.Kill(); } catch { /* может быть уже мёртв */ }
            }
        }
        await Task.WhenAny(Task.Delay(TimeSpan.FromSeconds(2)));
        return (capturedArgs, session);
    }

    private static IReadOnlyList<string>? GetCapturedArgs(IProcessLauncher launcher)
    {
        var prop = launcher.GetType().GetProperty("CapturedArgs");
        return prop?.GetValue(launcher) as IReadOnlyList<string>;
    }

    // Лаунчер с маппером, чей ToRuntime кидает InvalidOperationException — как DockerPathMapper
    // вне своих правил. CapturedArgs заполняется до вызова ToRuntime (args собираются раньше
    // запуска процесса), поэтому тест успевает их увидеть до того, как исключение долетит.
    private sealed class ThrowingRuntimeLauncher : IProcessLauncher
    {
        private readonly List<Process> _processes;
        public IReadOnlyList<string>? CapturedArgs { get; private set; }
        public bool IsSandboxed => true;
        public bool TargetIsWindows => OperatingSystem.IsWindows();
        public IPathMapper Paths => ThrowingMapper.Instance;
        public string ClaudeCliCommand => "fake-claude";
        public string HostTempDir => Path.GetTempPath();
        public string? McpApiUrlOverride => null;

        public ThrowingRuntimeLauncher(List<Process> processes) => _processes = processes;

        public Process Start(ProcessSpec spec)
        {
            CapturedArgs = spec.Args?.ToArray();
            var fake = new ProcessSpec
            {
                FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
                Args = OperatingSystem.IsWindows()
                    ? ["/c", "ping -n 60 127.0.0.1 >nul"]
                    : ["-c", "sleep 60"],
                WorkingDirectory = spec.WorkingDirectory,
                StdioEncoding = spec.StdioEncoding,
                EnableRaisingEvents = spec.EnableRaisingEvents,
                RedirectStdin = spec.RedirectStdin,
                Track = false,
            };
            var process = LocalProcessRunner.Instance.Start(fake);
            lock (_processes) _processes.Add(process);
            return process;
        }

                public int EstimateCommandLineLength(ProcessSpec spec) => throw new NotSupportedException();
        public void Kill(Process process, string? turnId = null)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* уже мёртв */ }
        }
    }

    // Маппер, который на любой ToRuntime бросает InvalidOperationException — точно как
    // DockerPathMapper, когда путь вне его правил. ToHost и CanMap не нужны — тест
    // проверяет только путь до BuildArgs → ToRuntime.
    private sealed class ThrowingMapper : IPathMapper
    {
        public static readonly ThrowingMapper Instance = new();
        public string ToRuntime(string hostPath) =>
            throw new InvalidOperationException($"Путь недоступен в песочнице (test): {hostPath}");
        public string ToHost(string runtimePath) => runtimePath;
        public bool CanMap(string hostPath) => true;
    }

    /// <summary>
    /// СТОРОЖ СНИМКА: при BareMode=true и существующем файле карты снимок содержит
    /// секцию "(bare)". Мутация инверсии `if (_lastBareModeApplied)` →
    /// `if (!_lastBareModeApplied)` сломает оба теста сразу: первый покажет "(bare)"
    /// только в обратном случае, второй — нет.
    /// </summary>
    [Fact]
    public async Task СнимокПромпта_BareModeПрименён_СодержитСекциюBare()
    {
        var providers = LocalProviders(
            localBareMode: true,
            localSystemPromptFile: "SystemPrompts/CLAUDE-local.md");
        var (_, session) = await RunTurnAsync(LocalModelId, providers);

        var files = session.BuildCliLayerFilesForTest().Files;
        files.Should().Contain(f => f.Key == "(bare)",
            "_lastBareModeApplied=true обязан показать bare-режим в снимке");
    }

    /// <summary>
    /// СТОРОЖ СНИМКА: при BareMode=true и ОТСУТСТВУЮЩЕМ файле карты BuildBareModeArgs
    /// снимает оба флага (_lastBareModeApplied=false), снимок НЕ содержит секцию "(bare)".
    /// Мутация инверсии гейта приведёт к показу "(bare)" в обычном режиме → RED.
    /// </summary>
    [Fact]
    public async Task СнимокПромпта_BareModeСнятИззаПроваФайла_НетСекцииBare()
    {
        var providers = LocalProviders(
            localBareMode: true,
            localSystemPromptFile: "SystemPrompts/missing.md");
        var (_, session) = await RunTurnAsync(LocalModelId, providers);

        session.LastBareModeApplied.Should().BeFalse("файл не найден — BuildBareModeArgs снимает оба флага");
        var files = session.BuildCliLayerFilesForTest().Files;
        files.Should().NotContain(f => f.Key == "(bare)",
            "_lastBareModeApplied=false → снимок показывает обычный режим");
    }

    /// <summary>
    /// СТОРОЖ ШВА «ClaudeSession → runner» (M1): safeEstimate обязан ДЕЛАТЬ вызов
    /// _launcher.EstimateCommandLineLength и при его исключении ПИСАТЬ warning в stderr.
    /// Без этого шов выглядит рабочим, но декоративен: docker-владелец без обвязки в оценке
    /// снова ловит Win32 206 уже после старта (та же дыра dc641949, что гейт волны 3 не
    /// покрывал — он смотрел только на DockerProcessRunner, а не на связку).
    /// Мутация «заменить тело safeEstimate на локальную формулу без обращения к _launcher»
    /// или «поменять catch (Exception) на catch (NotSupportedException)» — RED по stderr.
    /// </summary>
    [Fact]
    public async Task safeEstimate_EstimateБросает_ВstderrУходитWarning()
    {
        var providers = LocalProviders(localBareMode: false, localSystemPromptFile: null);
        var throwingLauncher = new ThrowingRuntimeLauncher(_processes);

        var originalErr = Console.Error;
        var errCapture = new System.IO.StringWriter();
        Console.SetError(errCapture);
        try
        {
            await RunTurnAsyncWithLauncher(LocalModelId, providers, throwingLauncher);

            var err = errCapture.ToString();
            err.Should().Contain("[ClaudeSession] safeEstimate: EstimateCommandLineLength упал",
                "catch обязан диагностировать — иначе шов ClaudeSession → раннер выглядит "
                + "рабочим, но не работает (тот же класс ошибки, что и блокер dc641949)");
            err.Should().Contain("NotSupportedException",
                "диагностика обязана нести тип исключения: реальный сбой docker-владельца — "
                + "NotSupportedException из ThrowingMapper.ToRuntime или IOException из EnsureProfile, "
                + "и без типа причины в stderr отличить их нельзя");
        }
        finally
        {
            Console.SetError(originalErr);
        }
    }

    // ---------- Контракт BuildBareModeArgs (резолв пути + снятие флагов) ----------

    private static (string PromptPath, string PromptRel) CreateServerPromptFile(string rel = "prompts/local.md")
    {
        var full = Path.Combine(ServerRoot, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, "# local map");
        return (full, rel);
    }

    private static IReadOnlyList<string> Build(
        string promptRel,
        string[]? bareTools,
        string? serverRoot,
        out string? warning,
        out bool effective)
        => ClaudeSession.BuildBareModeArgs(
            Path.Combine(Path.GetTempPath(), "ccs-bare-args-tests-project-" + Guid.NewGuid().ToString("N")),
            serverRoot ?? ServerRoot,
            promptRel,
            bareTools,
            Paths,
            out warning,
            out effective);

    [Fact]
    public void BareMode_ФайлСуществуетОтносительноСервера_ОбаФлагаДобавлены()
    {
        var (full, rel) = CreateServerPromptFile();

        var args = Build(rel, bareTools: null, serverRoot: ServerRoot,
            out var warning, out var effective);

        args.Should().Equal("--bare", "--system-prompt-file", full);
        warning.Should().BeNull();
        effective.Should().BeTrue();
    }

    [Fact]
    public void BareMode_ФайлНеСуществует_НикакихФлаговИПредупреждение()
    {
        var args = Build("SystemPrompts/nope.md", bareTools: null,
            serverRoot: ServerRoot, out var warning, out var effective);

        args.Should().BeEmpty();
        warning.Should().NotBeNullOrEmpty();
        warning.Should().Contain("не найден");
        effective.Should().BeFalse();
    }

    [Fact]
    public void BareMode_СерверныйКореньНеЗадан_ПутьАбсолютныйРаботает()
    {
        var (full, _) = CreateServerPromptFile(rel: "abs.md");

        var args = Build(full, bareTools: null, serverRoot: null,
            out var warning, out var effective);

        args.Should().Equal("--bare", "--system-prompt-file", full);
        warning.Should().BeNull();
        effective.Should().BeTrue();
    }

    [Fact]
    public void BareMode_ПутьЗаКореньСервера_SafeJoinБросает()
    {
        // Контракт SafeJoin: путь за пределы корня → UnauthorizedAccessException.
        // Обработка в BuildArgs проверяется сквозным тестом ХодНаЛокальном_ПутьЗаКорень_CatchСбрасываетBareMode.
        Action act = () => ClaudeSession.BuildBareModeArgs(
            Path.Combine(Path.GetTempPath(), "ccs-bare-args-tests-project-" + Guid.NewGuid().ToString("N")),
            ServerRoot,
            "../etc/passwd",
            null,
            Paths,
            out _,
            out _);

        act.Should().Throw<UnauthorizedAccessException>();
    }

    [Fact]
    public void BareMode_SystemPromptFileПустой_СнимаетBareИWarning()
    {
        // SystemPromptFile пустой: --bare без --system-prompt-file оставил бы модель
        // без контекста, т.к. --bare отключает автозагрузку CLAUDE.md и CLI ничего
        // своего не подтянет. Асимметрия с веткой «файл не найден» неоправданна —
        // теперь единое поведение: пустой SystemPromptFile = BareMode снят с warning,
        // ход в обычном режиме с полной CLAUDE.md.
        var args = Build("", bareTools: null, serverRoot: ServerRoot,
            out var warning, out var effective);

        args.Should().BeEmpty();
        warning.Should().NotBeNullOrEmpty();
        warning.Should().Contain("SystemPromptFile не задан");
        effective.Should().BeFalse();
    }

    [Fact]
    public void BareMode_BareToolsЗаданы_АргументToolsПрисутствует()
    {
        // Контракт --tools: имя сужает набор, расширять нельзя (замер 2026-09-05).
        // CLI получает один аргумент "--tools" со списком через пробел.
        var (full, rel) = CreateServerPromptFile();
        var tools = new[] { "Bash", "Edit", "Read", "PowerShell" };

        var args = Build(rel, bareTools: tools, serverRoot: ServerRoot,
            out var warning, out var effective);

        args.Should().Equal(
            "--bare",
            "--tools", string.Join(' ', tools),
            "--system-prompt-file", full);
        warning.Should().BeNull();
        effective.Should().BeTrue();
    }

    [Fact]
    public void BareMode_BareToolsПустой_ФлагаToolsНет()
    {
        var (full, rel) = CreateServerPromptFile();

        var args = Build(rel, bareTools: [], serverRoot: ServerRoot,
            out var warning, out var effective);

        args.Should().Equal("--bare", "--system-prompt-file", full);
        warning.Should().BeNull();
        effective.Should().BeTrue();
        args.Should().NotContain("--tools");
    }

    [Fact]
    public void BareMode_BareToolsNull_ФлагаToolsНет()
    {
        var (full, rel) = CreateServerPromptFile();

        var args = Build(rel, bareTools: null, serverRoot: ServerRoot,
            out var warning, out var effective);

        args.Should().Equal("--bare", "--system-prompt-file", full);
        warning.Should().BeNull();
        effective.Should().BeTrue();
        args.Should().NotContain("--tools");
    }

    [Fact]
    public void BareMode_ФайлНеНайден_ФлагаToolsТожеНет()
    {
        // --tools без --bare бесполезен (CLI всё равно урежет тулсет). При снятии
        // обоих флагов из-за пропажи файла карты --tools снимается тем же ходом.
        var args = Build("SystemPrompts/nope.md",
            bareTools: new[] { "Write", "Glob" },
            serverRoot: ServerRoot, out var warning, out var effective);

        args.Should().BeEmpty();
        warning.Should().NotBeNullOrEmpty();
        effective.Should().BeFalse();
        args.Should().NotContain("--tools");
    }

    [Fact]
    public void BareMode_SystemPromptFileПустой_BareToolsЗаданы_ТожеСнимает()
    {
        // Пустой SystemPromptFile + любой bareTools: BareMode всё равно снимается
        // с warning. --bare без карты оставил бы модель без контекста — --tools тут
        // не спасает.
        var tools = new[] { "Bash", "Edit", "Read", "PowerShell" };

        var args = Build("", bareTools: tools, serverRoot: ServerRoot,
            out var warning, out var effective);

        args.Should().BeEmpty();
        warning.Should().NotBeNullOrEmpty();
        warning.Should().Contain("SystemPromptFile не задан");
        effective.Should().BeFalse();
        args.Should().NotContain("--bare");
        args.Should().NotContain("--tools");
    }

    [Fact]
    public void BareMode_ВПроектеЕстьDocsCLAUDELocal_ПеребиваетСерверныйДефолт()
    {
        // Per-project lookup: docs/CLAUDE-local.md в корне проекта чата перебивает серверный
        // SystemPrompts/CLAUDE-local.md. Сторож: мутация приоритета (убрать File.Exists
        // проверку) → тест зелёный НЕ пройдёт (вернётся серверный путь, текст другой).
        var projectRoot = NewTempDir("ccs-bare-perproject-");
        Directory.CreateDirectory(Path.Combine(projectRoot, "docs"));
        var projectLocal = Path.Combine(projectRoot, "docs", "CLAUDE-local.md");
        File.WriteAllText(projectLocal, "# per-project map");
        // Серверный путь существует (из конструктора), но НЕ должен быть выбран.
        var serverLocal = Path.Combine(ServerRoot, "SystemPrompts", "CLAUDE-local.md");

        var args = ClaudeSession.BuildBareModeArgs(
            projectRoot, ServerRoot,
            "SystemPrompts/CLAUDE-local.md", null, Paths,
            out var warning, out var effective);

        args.Should().Equal("--bare", "--system-prompt-file", projectLocal);
        warning.Should().BeNull();
        effective.Should().BeTrue();
        // Защита от регрессии: серверный путь НЕ должен попасть в args.
        args.Should().NotContain(serverLocal);
    }

    [Fact]
    public void BareMode_ВПроектеНетDocsCLAUDELocal_БерётсяСерверныйДефолт()
    {
        // Контракт fallback: проектного файла нет → серверный SystemPrompts/CLAUDE-local.md.
        var projectRoot = NewTempDir("ccs-bare-noproj-");

        var args = ClaudeSession.BuildBareModeArgs(
            projectRoot, ServerRoot,
            "SystemPrompts/CLAUDE-local.md", null, Paths,
            out var warning, out var effective);

        var expectedPath = Path.Combine(ServerRoot, "SystemPrompts", "CLAUDE-local.md");
        args.Should().Equal("--bare", "--system-prompt-file", expectedPath);
        warning.Should().BeNull();
        effective.Should().BeTrue();
    }

    // СТОРОЖ потолка: проектная карта больше 16 КБ — отступаем к серверному дефолту,
    // warning в stderr. Мутация удаления проверки `size > ProjectMapSizeLimit` → RED:
    // вернётся проектный путь, ассерт на серверный провалится, warning останется null.
    [Fact]
    public void BareMode_ПроектнаяКартаБольшеПотолка_ОтступКСерверной()
    {
        var projectRoot = NewTempDir("ccs-bare-bigproj-");
        Directory.CreateDirectory(Path.Combine(projectRoot, "docs"));
        var projectLocal = Path.Combine(projectRoot, "docs", "CLAUDE-local.md");
        // Пишем файл явно > 16 КБ
        File.WriteAllText(projectLocal, new string('x', 20 * 1024));

        var args = ClaudeSession.BuildBareModeArgs(
            projectRoot, ServerRoot,
            "SystemPrompts/CLAUDE-local.md", null, Paths,
            out var warning, out var effective);

        var expectedPath = Path.Combine(ServerRoot, "SystemPrompts", "CLAUDE-local.md");
        args.Should().Equal("--bare", "--system-prompt-file", expectedPath);
        warning.Should().NotBeNullOrEmpty("oversized-флаг обязан сопровождаться warning");
        warning.Should().Contain("превышает");
        effective.Should().BeTrue();
    }

    // СТОРОЖ потолка: проектная карта ровно 16 КБ — НЕ отступаем (граничное значение).
    [Fact]
    public void BareMode_ПроектнаяКартаРовноПотолок_БерётсяПроектная()
    {
        var projectRoot = NewTempDir("ccs-bare-edgeproj-");
        Directory.CreateDirectory(Path.Combine(projectRoot, "docs"));
        var projectLocal = Path.Combine(projectRoot, "docs", "CLAUDE-local.md");
        File.WriteAllText(projectLocal, new string('x', 16 * 1024));

        var args = ClaudeSession.BuildBareModeArgs(
            projectRoot, ServerRoot,
            "SystemPrompts/CLAUDE-local.md", null, Paths,
            out var warning, out var effective);

        args.Should().Equal("--bare", "--system-prompt-file", projectLocal);
        warning.Should().BeNull();
        effective.Should().BeTrue();
    }

    // СТОРОЖ лога размера: при наличии logger в info-сообщении указан источник
    // ("проектная"/"серверная") и байтовый размер карты. Мутация удаления LogInformation
    // → тест красный: либо вызовов нет, либо нет ожидаемой подстроки.
    [Fact]
    public void BareMode_ЛогРазмера_СодержитИсточникИБайты()
    {
        var projectRoot = NewTempDir("ccs-bare-log-");
        Directory.CreateDirectory(Path.Combine(projectRoot, "docs"));
        var projectLocal = Path.Combine(projectRoot, "docs", "CLAUDE-local.md");
        File.WriteAllText(projectLocal, "# per-project\nmap body");

        var captured = new List<(string Category, string Message)>();
        var logger = new ListLogger(captured);

        var args = ClaudeSession.BuildBareModeArgs(
            projectRoot, ServerRoot,
            "SystemPrompts/CLAUDE-local.md", null, Paths,
            logger, out _, out _);

        args.Should().Contain("--system-prompt-file");
        captured.Should().ContainSingle()
            .Which.Should().Be(("info", $"BareMode: взята проектная карта {projectLocal} (22 байт)"));
    }

    // СТОРОЖ УНИКАЛЬНОСТИ: флаг --system-prompt-file обязан идти в аргументах CLI
    // ровно один раз. Мутация: добавить второй args.Add("--system-prompt-file") в
    // BuildBareModeArgs → count = 2 → RED.
    [Fact]
    public void BareMode_ФлагSystemPromptFile_ИдётРовноОдинРаз()
    {
        var (full, rel) = CreateServerPromptFile();

        var args = Build(rel, bareTools: null, serverRoot: ServerRoot,
            out var warning, out var effective);

        // Флаг --system-prompt-file обязан присутствовать ровно один раз (не дублироваться)
        var count = args.Count(a => a == "--system-prompt-file");
        count.Should().Be(1,
            "--system-prompt-file не должен дублироваться в аргументах CLI (факт: {0})", count);
        // Путь карты также обязан встретиться ровно один раз
        args.Count(a => a == full).Should().Be(1,
            "путь карты не должен дублироваться в аргументах CLI");
    }

    // Тестовый logger, копит сообщения в список — для проверки логирования размера.
    private sealed class ListLogger(List<(string Category, string Message)> sink) : ILogger
    {
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var category = logLevel switch
            {
                LogLevel.Trace => "trace",
                LogLevel.Debug => "debug",
                LogLevel.Information => "info",
                LogLevel.Warning => "warn",
                LogLevel.Error => "error",
                LogLevel.Critical => "crit",
                _ => "other",
            };
            sink.Add((category, formatter(state, exception)));
        }
        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
