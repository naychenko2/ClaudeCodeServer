using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.ChildMode;
using ClaudeHomeServer.Services.Execution;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Llm.Claude;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Tests.Services;

// Б10b (И8; ADR-041 ред. 5–6, решения 53/65/67; протокол КТ0 п.2 и наблюдение 7): разрешение
// инструмента детского хода решается по ПУТИ, а не по имени.
//  - путь берётся из input запроса can_use_tool (file_path у Read/Write/Edit, path у Glob/Grep —
//    каталог-основа поиска) и переводится в хостовый; НИ ОДНОМУ пути из input не доверяем как
//    абсолютному: относительный резолвится против cwd хода (его хостовой counterpart — RootPath
//    чата = папка предмета), при симлинке судим по blocked_path — input.file_path при этом
//    остаётся внутри cwd;
//  - вердикт даёт ChildPathPolicy: одна функция путей И8, её же зовут рельса «Файлы» и загрузка;
//    действующий — репетитор: инструментами урока правит закреплённая персона-руководитель чата,
//    «Для репетитора/» ей читается, ребёнку — нет (решение 65);
//  - картинка модели без зрения — deny с путём текстовой копии (решение 67);
//  - неизвестный инструмент или нераспознанный input — deny (fail-closed).
// Мутации (критерий c2): «решать по имени, без пути» — краснеет таблица путей
// ПутьРешаетАНеИмяИнструмента; «без перевода контейнерный → хостовый» — краснеет
// КонтейнерныйПутьСвоегоФайла_ПереводитсяИРазрешён; «без проверки зрения модели» — краснеет
// КартинкаМоделиБезЗрения_ОтказСПодсказкойТекстовойКопии.
public class ChildPermissionDecisionTests : IDisposable
{
    // White-box: решение приватное (его зовёт только HandleControlRequestAsync), тот же приём,
    // что в ChildCliLayerTests (DecidePermissionAsync) и ClaudeSessionPromptCacheTests.
    private static readonly MethodInfo DecideRequest =
        typeof(ClaudeSession).GetMethods(BindingFlags.NonPublic | BindingFlags.Instance)
            .Single(m => m.Name == "ChildPermissionDecision"
                && m.GetParameters() is { Length: 1 } p && p[0].ParameterType == typeof(JsonElement));

    private static readonly Type CliRunType = typeof(ClaudeSession).GetNestedType("CliRun", BindingFlags.NonPublic)!;
    private static readonly MethodInfo ProcessLineAsyncMethod =
        typeof(ClaudeSession).GetMethod("ProcessLineAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static readonly DateTimeOffset ConsentAt = new(2026, 11, 19, 10, 0, 0, TimeSpan.Zero);

    private readonly List<string> _tempDirs = [];
    private readonly List<Process> _processes = [];
    // Папка предмета = RootPath чата урока (решения 48/50): тот корень, относительно которого
    // решаются все пути хода.
    private readonly string _root;
    private readonly string _otherSubject;

    public ChildPermissionDecisionTests()
    {
        var home = Path.Combine(Path.GetTempPath(), "ccs-child-path-" + Guid.NewGuid().ToString("N"));
        _root = Path.Combine(home, "Предметы", "subj-1");
        _otherSubject = Path.Combine(home, "Предметы", "subj-2");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_otherSubject);
        _tempDirs.Add(home);
    }

    public void Dispose()
    {
        foreach (var p in _processes)
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { /* уже мёртв */ }
            ProcessTestCleanup.SafeDispose(p);
        }
        foreach (var d in _tempDirs)
            try { Directory.Delete(d, recursive: true); } catch { /* уже удалён */ }
    }

    private static ChildProfile Kid(string model = "sonnet") => new()
    {
        Enabled = true, ProviderKey = "kids-api", Model = model, TutorPersonaId = "tutor-1",
        RouteRiskAcknowledgedAt = ConsentAt, RouteRiskAcknowledgedBy = "parent-1",
        Subjects =
        [
            new ChildSubject
            {
                Id = "subj-1", Label = "Окружающий мир", Glyph = "Ом", Color = "info",
            },
        ],
    };

    private static ChildPolicySnapshot Child(string model = "sonnet") => new("kid", Kid(model), false);

    private ClaudeSession NewChildSession(IProcessLauncher? launcher = null, LlmProviderRegistry? providers = null,
        string model = "sonnet")
    {
        var context = new LlmSessionContext(
            RootPath: _root,
            OnMessage: _ => Task.CompletedTask,
            RawSystemPrompt: null, BuiltInSystemPrompt: ClaudeHomeServer.Services.ProjectManager.BuiltInSystemPrompt,
            PermissionRules: null, TasksMcp: null, Launcher: launcher,
            ChildPolicyProvider: () => Child(model));
        return new ClaudeSession(
            // Модель чата — закреплённая запуском (Б17c: явной модели предмета нет, EffectiveModel читает её отсюда)
            new Session { OwnerId = "kid", ChildSpace = ChildSpaces.Lessons, ChildSubject = "subj-1", ProjectId = "proj-subj-1", Model = model },
            context, providers: providers);
    }

    /// can_use_tool ровно той формы, что присылает CLI (KT0-логи r1a/r2a/r2c): input с
    /// file_path/path и соседнее с ним поле blocked_path при симлинке.
    private static JsonElement Request(string toolName, object? input = null, string? blockedPath = null)
    {
        var request = new Dictionary<string, object> { ["subtype"] = "can_use_tool", ["tool_name"] = toolName, ["tool_use_id"] = "tool-1" };
        if (input is not null) request["input"] = input;
        if (blockedPath is not null) request["blocked_path"] = blockedPath;
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(request));
        return doc.RootElement.Clone();
    }

    private static object? InputFor(string tool, string? path) => tool switch
    {
        "Read" or "Write" or "Edit" when path is not null => new { file_path = path, content = "текст" },
        "Glob" when path is not null => new { pattern = "**/*", path },
        "Grep" when path is not null => new { pattern = "содержимое", path },
        "Glob" or "Grep" => new { pattern = "**/*" },
        _ => null,
    };

    // Вызов Glob с разным pattern: каталог обхода называет сам pattern, а не только path.
    private static object GlobInput(string pattern, string? path = null) =>
        path is null ? new { pattern } : new { pattern, path };

    private static (string Behavior, string Message) Decide(ClaudeSession session, string toolName,
        object? input = null, string? blockedPath = null)
    {
        var result = (ValueTuple<string, string>)DecideRequest.Invoke(session, [Request(toolName, input, blockedPath)])!;
        return (result.Item1, result.Item2);
    }

    // --- таблица путей (сторож И8 из ADR-041 «Сторожа (редакция 5)»: «таблица путей (внутри,
    // вне, ../, симлинк, Материалы/, .claude/, .mcp.json, CLAUDE.md) против вердикта»).
    // Путь в input относительный — как его пишет модель (КТ0 наблюдение 7): резолвится против
    // cwd хода, т.е. против папки предмета. ---

    [Theory]
    [InlineData("Read", "конспект.md", "allow")]
    [InlineData("Read", "Заметки/черновик.md", "allow")]
    [InlineData("Read", "Материалы/контур.png", "allow")]
    [InlineData("Read", "Для репетитора/ключи.md", "allow")]
    [InlineData("Read", ".ccs-text/скан.txt", "allow")]
    [InlineData("Read", "../чужой/секрет.md", "deny")]
    [InlineData("Read", "Материалы/../../чужой/секрет.md", "deny")]
    [InlineData("Read", ".claude/settings.json", "deny")]
    [InlineData("Read", "CLAUDE.md", "deny")]
    [InlineData("Read", "Заметки/CLAUDE.md", "deny")]
    [InlineData("Write", "Заметки/заметка.md", "allow")]
    [InlineData("Write", "Материалы/конспект.md", "deny")]
    [InlineData("Write", "Для репетитора/ключи.md", "deny")]
    [InlineData("Write", ".claude/settings.json", "deny")]
    [InlineData("Write", ".ccs-trash/2026-11-19/конспект.md", "deny")]
    [InlineData("Edit", "конспект.md", "allow")]
    [InlineData("Edit", ".claude/settings.json", "deny")]
    [InlineData("Edit", "Материалы/конспект.md", "deny")]
    [InlineData("Glob", "Заметки", "allow")]
    [InlineData("Glob", "../чужой", "deny")]
    [InlineData("Grep", "Заметки", "allow")]
    [InlineData("Grep", "../чужой", "deny")]
    public void ПутьРешаетАНеИмяИнструмента(string tool, string path, string expected)
    {
        var session = NewChildSession();

        var (behavior, message) = Decide(session, tool, InputFor(tool, path));

        behavior.Should().Be(expected, $"{tool} «{path}»");
        if (expected == "deny") message.Should().NotBeNullOrWhiteSpace("отказ обязан называть причину модели");
    }

    // Глоб/Grep без path: основа поиска — cwd хода, т.е. сама папка предмета. Это не
    // «нераспознанный input», а обычный поиск у себя в папке (решение 53.1: внутри cwd можно).
    [Theory]
    [InlineData("Glob")]
    [InlineData("Grep")]
    public void ПоискБезPath_ЭтоПоискВСвоейПапке(string tool)
    {
        var session = NewChildSession();

        Decide(session, tool, InputFor(tool, null)).Behavior.Should().Be("allow");
    }

    // --- Glob: каталог обхода называет ещё и pattern (доработка ревью 95a7b7fc, И8) ---
    // Раньше у Glob судился только path: без path решение выходило по корню, т.е. allow, — и
    // «../*/*.md» или «/etc/*» давали ребёнку список файлов чужих папок. Теперь судится основа
    // pattern: сегменты до первого «*?[{»; относительная резолвится против path ?? cwd хода,
    // абсолютная переводится IPathMapper. Инвариант: любой путь, который Glob реально обойдёт,
    // лежит в папке предмета. Мутация «не разбирать pattern» краснеет на строках deny.
    [Theory]
    [InlineData("**/*.md", null, "allow")]
    [InlineData("**/*", null, "allow")]
    [InlineData("*", null, "allow")]
    [InlineData("Заметки/**/*", null, "allow")]
    [InlineData("./Заметки/*", null, "allow")]
    [InlineData("Материалы/*.png", null, "allow")]
    [InlineData("Для репетитора/*.md", null, "allow")]
    [InlineData(".ccs-text/*.txt", null, "allow")]
    [InlineData("../*", null, "deny")]
    [InlineData("../*/*.md", null, "deny")]
    [InlineData("../чужой/*", null, "deny")]
    [InlineData("Материалы/../../x/*", null, "deny")]
    [InlineData("/etc/*", null, "deny")]
    [InlineData(".claude/*", null, "deny")]
    [InlineData("CLAUDE.md", null, "deny")]
    [InlineData("**/*.md", "Заметки", "allow")]
    [InlineData("*.md", "Материалы", "allow")]
    // «..» от path «Заметки» — сама папка предмета: обход остаётся внутри, это allow.
    [InlineData("../*.md", "Заметки", "allow")]
    [InlineData("../../x/*.md", "Заметки", "deny")]
    [InlineData("../*", "Материалы", "allow")]
    public void GlobСудитсяПоОсновеPattern(string pattern, string? path, string expected)
    {
        var session = NewChildSession();

        var (behavior, message) = Decide(session, "Glob", GlobInput(pattern, path));

        behavior.Should().Be(expected, $"Glob pattern «{pattern}» path «{path}»");
        if (expected == "deny") message.Should().NotBeNullOrWhiteSpace("отказ обязан называть причину модели");
    }

    [Fact]
    public void GlobПоАбсолютнойОсновеЧужогоПредмета_Отказ()
    {
        // Тот же случай, что у «чужого предмета» в таблице Read: папка названа абсолютно.
        var session = NewChildSession();

        var (behavior, message) = Decide(session, "Glob", GlobInput(Path.Combine(_otherSubject, "*.md")));

        using var scope = new AssertionScope();
        behavior.Should().Be("deny", "абсолютная основа pattern — папка чужого предмета");
        message.Should().MatchRegex("(?i)вне папки", "текст отказа — про настоящую причину");
    }

    // --- относительный против абсолютного (дельта R-КТ0 164b6603): один и тот же файл ---

    [Fact]
    public void ОдинФайл_ОтносительныйИАбсолютныйВнутриПапки_ОбаAllow()
    {
        var session = NewChildSession();

        var rel = Decide(session, "Read", InputFor("Read", "Заметки/черновик.md"));
        var abs = Decide(session, "Read", new { file_path = Path.Combine(_root, "Заметки", "черновик.md") });

        using var scope = new AssertionScope();
        rel.Behavior.Should().Be("allow", "относительный путь внутри папки");
        abs.Behavior.Should().Be("allow", "тот же файл, названный абсолютно");
    }

    [Fact]
    public void ОтносительныйНаружу_И_АбсолютныйНаружу_ОбаDeny()
    {
        var session = NewChildSession();

        var rel = Decide(session, "Read", InputFor("Read", "../subj-2/секрет.md"));
        var abs = Decide(session, "Read", new { file_path = Path.Combine(_otherSubject, "секрет.md") });

        using var scope = new AssertionScope();
        rel.Behavior.Should().Be("deny", "относительный путь наружу");
        abs.Behavior.Should().Be("deny", "тот же файл, названный абсолютно");
    }

    // --- симлинк: решение по blocked_path (KT0 п.2: input.file_path при этом внутри cwd) ---

    [Theory]
    [InlineData("Read")]
    [InlineData("Write")]
    [InlineData("Edit")]
    [InlineData("Grep")]
    public void СимлинкНаружу_Отказ_ХотяПутьЗапросаВнутриCwd(string tool)
    {
        var session = NewChildSession();
        // Путь ссылки — внутри папки предмета (в cwd), наружный назван blocked_path.
        var linkPath = tool is "Grep" ? "dirlink" : Path.Combine("dirlink", "секрет.md");

        var (behavior, message) = Decide(session, tool, InputFor(tool, linkPath),
            blockedPath: Path.Combine(_otherSubject, "секрет.md"));

        using var scope = new AssertionScope();
        behavior.Should().Be("deny", $"{tool}: судить надо по blocked_path, а не по пути внутри cwd");
        // Текст отказа — причина из ChildPathPolicy (одна точка формулировки), не общая фраза.
        message.Should().MatchRegex("(?i)вне папки", "текст отказа — про настоящую причину");
    }

    // Write/Edit через ссылку в СВОЮ папку — можно, в том числе в файл, которого ещё нет
    // (решение здесь — про путь, а не про существование).
    [Theory]
    [InlineData("Write")]
    [InlineData("Edit")]
    public void СимлинкВнутрь_ПисатьМожно_ВТомЧислеВНовыйФайл(string tool)
    {
        var session = NewChildSession();

        Decide(session, tool, InputFor(tool, Path.Combine("dirlink", "новый-файла-нет.md")),
            blockedPath: Path.Combine(_root, "Заметки", "новый-файла-нет.md"))
            .Behavior.Should().Be("allow");
    }

    // --- перевод контейнерного пути в хостовый (IPathMapper) ---

    private sealed class SandboxLikePaths : IPathMapper
    {
        private readonly string _runtimeRoot;
        private readonly string _hostRoot;
        public SandboxLikePaths(string runtimeRoot, string hostRoot) => (_runtimeRoot, _hostRoot) = (runtimeRoot, hostRoot);
        public string ToRuntime(string hostPath) => hostPath.StartsWith(_hostRoot, StringComparison.Ordinal)
            ? _runtimeRoot + hostPath[_hostRoot.Length..].Replace('\\', '/')
            : throw new InvalidOperationException($"Путь недоступен в песочнице: {hostPath}");
        public string ToHost(string runtimePath) => runtimePath.StartsWith(_runtimeRoot, StringComparison.Ordinal)
            ? _hostRoot + runtimePath[_runtimeRoot.Length..].Replace('/', Path.DirectorySeparatorChar)
            : throw new InvalidOperationException($"Путь песочницы вне известных точек монтирования: {runtimePath}");
    }

    private sealed class SandboxLikeLauncher(string runtimeRoot, string hostRoot) : IProcessLauncher
    {
        public bool IsSandboxed => true;
        public bool TargetIsWindows => false;
        public IPathMapper Paths { get; } = new SandboxLikePaths(runtimeRoot, hostRoot);
        public string ClaudeCliCommand => "fake-claude";
        public string HostTempDir => Path.GetTempPath();
        public string? McpApiUrlOverride => null;
        public Process Start(ProcessSpec spec) => throw new NotSupportedException("прогон здесь не запускается");
        public void Kill(Process process, string? turnId = null) { }
        public int EstimateCommandLineLength(ProcessSpec spec) => 0;
    }

    [Fact]
    public void КонтейнерныйПутьСвоегоФайла_ПереводитсяИРазрешён()
    {
        // Мутация «убрать IPathMapper.ToHost» красит этот тест: без перевода контейнерный
        // путь «/projects/…» не лежит в хостовой папке предмета — свой файл отклонялся бы
        // как «вне папки».
        const string runtimeRoot = "/projects/kid/Предметы/subj-1";
        var session = NewChildSession(new SandboxLikeLauncher(runtimeRoot, _root));

        Decide(session, "Read", new { file_path = runtimeRoot + "/Заметки/черновик.md" }).Behavior.Should().Be("allow");
    }

    [Fact]
    public void КонтейнерныйПутьЧужойПапки_Отказ()
    {
        const string runtimeRoot = "/projects/kid/Предметы/subj-1";
        var session = NewChildSession(new SandboxLikeLauncher(runtimeRoot, _root));

        var (behavior, message) = Decide(session, "Read",
            new { file_path = "/projects/kid/Предметы/subj-2/секрет.md" });

        using var scope = new AssertionScope();
        behavior.Should().Be("deny");
        message.Should().Contain("вне папки");
    }

    [Fact]
    public void GlobКонтейнернаяОсноваСвоейПапки_ПереводитсяИРазрешена()
    {
        // Мутация «убрать ToHost для основы pattern» красит этот тест: без перевода
        // контейнерный путь «/projects/…» не лежит в хостовой папке предмета.
        const string runtimeRoot = "/projects/kid/Предметы/subj-1";
        var session = NewChildSession(new SandboxLikeLauncher(runtimeRoot, _root));

        Decide(session, "Glob", GlobInput(runtimeRoot + "/Заметки/*.md")).Behavior.Should().Be("allow");
    }

    [Fact]
    public void GlobКонтейнернаяОсноваЧужойПапки_Отказ()
    {
        const string runtimeRoot = "/projects/kid/Предметы/subj-1";
        var session = NewChildSession(new SandboxLikeLauncher(runtimeRoot, _root));

        var (behavior, message) = Decide(session, "Glob", GlobInput("/projects/kid/Предметы/subj-2/*.md"));

        using var scope = new AssertionScope();
        behavior.Should().Be("deny", "основа обхода — папка чужого предмета");
        message.Should().Contain("вне папки");
    }

    // --- картинка модели без зрения (решение 67) ---

    private static LlmProviderRegistry BlindProviderRegistry() => new(new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["LlmProviders:blind:AnthropicBaseUrl"] = "https://api.example.invalid/anthropic",
            ["LlmProviders:blind:ApiKey"] = "sk-test",
            ["LlmProviders:blind:SupportsImages"] = "false",
            ["LlmProviders:blind:Models:0:Id"] = "blind-model",
        }).Build());

    // Форма копии — решение 90: расширение оригинала сохраняется, «ChangeExtension» её не строит.
    [Theory]
    [InlineData("скан.png", ".ccs-text/скан.png.txt")]
    [InlineData("Материалы/фото.JPG", ".ccs-text/Материалы/фото.JPG.txt")]
    [InlineData("Материалы/рисунок.jpeg", ".ccs-text/Материалы/рисунок.jpeg.txt")]
    [InlineData("Материалы/схема.webp", ".ccs-text/Материалы/схема.webp.txt")]
    [InlineData("Материалы/график.gif", ".ccs-text/Материалы/график.gif.txt")]
    public void КартинкаМоделиБезЗрения_ОтказСПодсказкойТекстовойКопии(string rel, string expectedCopy)
    {
        var session = NewChildSession(providers: BlindProviderRegistry(), model: "blind-model");

        var (behavior, message) = Decide(session, "Read", InputFor("Read", rel));

        using var scope = new AssertionScope();
        behavior.Should().Be("deny", $"{rel}: модель без зрения не читает картинку");
        message.Should().Contain(".ccs-text/", "модели подсказывается путь текстовой копии");
        message.Should().Contain(expectedCopy, "путь копии — та же относительная путь с сохранённым расширением оригинала (решение 90)");
    }

    [Fact]
    public void ТаЖеКартинкаМОделиСоЗрением_Разрешена()
    {
        // Обратная половина того же правила: отказывает не расширение, а отсутствие зрения.
        var session = NewChildSession();

        Decide(session, "Read", InputFor("Read", "Материалы/контур.png")).Behavior.Should().Be("allow");
    }

    [Fact]
    public void ТекстовыйФайлМОделиБезЗрения_Разрешён()
    {
        var session = NewChildSession(providers: BlindProviderRegistry(), model: "blind-model");

        Decide(session, "Read", InputFor("Read", "Материалы/конспект.txt")).Behavior.Should().Be("allow");
    }

    // --- имена вне пути: MCP-пара, AskUserQuestion, неизвестное, нераспознанный input ---

    [Theory]
    [InlineData("mcp__tutor__search_materials", "allow")]
    [InlineData("mcp__tutor__read_material", "deny")]
    [InlineData("mcp__widgets__widget_show", "allow")]
    [InlineData("mcp__dify__search_knowledge", "deny")]
    [InlineData("mcp__wsp__files_read", "deny")]
    [InlineData("AskUserQuestion", "allow")]
    [InlineData("Bash", "deny")]
    [InlineData("Task", "deny")]
    [InlineData("", "deny")]
    public void ИменаВнеПути_ПоПравиламИмени(string tool, string expected)
    {
        var session = NewChildSession();

        Decide(session, tool).Behavior.Should().Be(expected, tool);
    }

    [Theory]
    [InlineData("Read")]
    [InlineData("Write")]
    [InlineData("Edit")]
    public void ФайловыйВызовБезПути_НераспознанныйInput_Отказ(string tool)
    {
        var session = NewChildSession();

        var (behavior, message) = Decide(session, tool, new { content = "текст без пути" });

        using var scope = new AssertionScope();
        behavior.Should().Be("deny", $"{tool} без file_path");
        message.Should().NotBeNullOrWhiteSpace();
    }

    // --- конкурентность: два can_use_tool одного хода (параллельные инструменты) ---

    private static object NewRun(Process process)
    {
        var run = Activator.CreateInstance(CliRunType, nonPublic: true)!;
        CliRunType.GetProperty("Process")!.SetValue(run, process);
        CliRunType.GetProperty("Signature")!.SetValue(run, "test");
        return run;
    }

    private static async Task DriveLine(ClaudeSession session, object run, string line) =>
        await (Task)ProcessLineAsyncMethod.Invoke(session, [run, line])!;

    private static string ControlRequestLine(string requestId, string tool, string path) =>
        JsonSerializer.Serialize(new
        {
            type = "control_request",
            request_id = requestId,
            request = new { subtype = "can_use_tool", tool_name = tool, tool_use_id = requestId, input = new { file_path = path } },
        });

    // «CLI», который пишет обратно всё, что сервер положил в его stdin, в файл: виден сам
    // control_response — его request_id и поведение.
    private Process StartStdinRecorder(string outFile)
    {
        var dir = Path.Combine(Path.GetTempPath(), "ccs-child-conc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        ProcessStartInfo psi;
        if (OperatingSystem.IsWindows())
        {
            var script = Path.Combine(dir, "cli.cmd");
            File.WriteAllText(script, "@echo off\r\nfind /v \"\" > \"" + outFile + "\"\r\n", Encoding.ASCII);
            psi = new ProcessStartInfo("cmd.exe", $"/c \"{script}\"");
        }
        else
        {
            var script = Path.Combine(dir, "cli.sh");
            File.WriteAllText(script, "#!/bin/sh\ncat > \"" + outFile + "\"\n", Encoding.UTF8);
            psi = new ProcessStartInfo("/bin/sh", script);
        }
        psi.RedirectStandardInput = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        var process = Process.Start(psi)!;
        _processes.Add(process);
        return process;
    }

    [Fact]
    public async Task ДваОдновременныхCanUseTool_КаждыйОтветСвоимRequestId()
    {
        // 20 повторов по паре запросов: один путь внутри папки предмета, другой вне. Оба
        // получают ответ, и каждый называет СВОЙ request_id со своим поведением: решение —
        // чистая функция запроса, состояния на сессию нет (мутация «запомнить последний
        // запрос в поле сессии» красит этот тест).
        var session = NewChildSession();
        var outFile = Path.Combine(Path.GetTempPath(), "ccs-child-conc-" + Guid.NewGuid().ToString("N") + ".txt");
        var process = StartStdinRecorder(outFile);
        var run = NewRun(process);

        var inside = Path.Combine(_root, "Заметки", "черновик.md");
        var outside = Path.Combine(_otherSubject, "секрет.md");

        for (var i = 0; i < 20; i++)
        {
            await Task.WhenAll(
                DriveLine(session, run, ControlRequestLine($"in-{i}", "Read", inside)),
                DriveLine(session, run, ControlRequestLine($"out-{i}", "Read", outside)));
        }

        // EOF по stdin — «CLI» дописывает файл и выходит: читать его можно без гонки.
        process.StandardInput.Close();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));

        var lines = (await File.ReadAllTextAsync(outFile)).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines.Length.Should().Be(40, "на каждый can_use_tool — свой control_response");

        var seen = new Dictionary<string, string>();
        foreach (var line in lines)
        {
            using var doc = JsonDocument.Parse(line);
            var id = doc.RootElement.GetProperty("response").GetProperty("request_id").GetString()!;
            seen[id] = doc.RootElement.GetProperty("response").GetProperty("response").GetProperty("behavior").GetString()!;
        }

        using var scope = new AssertionScope();
        for (var i = 0; i < 20; i++)
        {
            seen.TryGetValue($"in-{i}", out var insideAnswer).Should().BeTrue($"ответ для in-{i}");
            insideAnswer.Should().Be("allow", $"in-{i}: путь внутри папки");
            seen.TryGetValue($"out-{i}", out var outsideAnswer).Should().BeTrue($"ответ для out-{i}");
            outsideAnswer.Should().Be("deny", $"out-{i}: путь вне папки");
        }
    }

    // --- Б10c: снимок прежней версии перед Write/Edit существующего файла (решение 54) ---
    // Полный поток: control_request → граница → control_response. Снимок — боковой эффект
    // разрешения (его делает ChildPermissionDecision), поэтому проверяется здесь, на потоке,
    // а не белым ящиком по одной функции.

    private async Task<string> DriveControlRequestAndReadAnswer(ClaudeSession session, string tool, string path)
    {
        var outFile = Path.Combine(Path.GetTempPath(), "ccs-child-trash-" + Guid.NewGuid().ToString("N") + ".txt");
        var process = StartStdinRecorder(outFile);
        var run = NewRun(process);
        await DriveLine(session, run, ControlRequestLine("req-1", tool, path));
        // EOF по stdin — «CLI» дописывает файл и выходит: читать его можно без гонки.
        process.StandardInput.Close();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
        var lines = (await File.ReadAllTextAsync(outFile)).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines.Should().HaveCount(1, "на один can_use_tool — один control_response");
        using var doc = JsonDocument.Parse(lines[0]);
        return doc.RootElement.GetProperty("response").GetProperty("response").ToString();
    }

    [Theory]
    [InlineData("Write")]
    [InlineData("Edit")]
    public async Task WriteEditСуществующегоФайла_ОставляютОднуЗаписьВКорзине(string tool)
    {
        // Критерий c3: «Edit существующего файла → в .ccs-trash появилась 1 запись» —
        // прежняя версия в ней байт-в-байт, разрешение при этом allow.
        var session = NewChildSession();
        var original = "прежняя версия конспекта";
        var path = Path.Combine(_root, "Заметки", "конспект.md");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, original);

        var answer = await DriveControlRequestAndReadAnswer(session, tool, path);

        using var scope = new AssertionScope();
        answer.Should().Contain("\"allow\"", $"{tool}: разрешение существующего файла остаётся allow");
        var entries = ChildTrash.List(_root);
        entries.Should().ContainSingle($"{tool} по существующему файлу оставил ровно одну запись");
        File.ReadAllText(Path.Combine(_root, ChildTrash.TrashDir, entries[0].Id, "Заметки", "конспект.md"))
            .Should().Be(original, "в записи — прежняя версия");
        entries[0].OriginalPath.Should().Be("Заметки/конспект.md", "мета называет исходный путь");
    }

    [Fact]
    public async Task СбойСнимка_WriteСуществующегоФайла_ОтклонёнБезИсключения()
    {
        // Нет снимка — нет перезаписи: сбой записи (диск занят: «.ccs-trash» — файл, а не
        // каталог) даёт deny с причиной, а не необработанное IOException из потока.
        // Мутация (г) «разрешать Write при сбое снимка» краснит этот тест.
        var session = NewChildSession();
        var path = Path.Combine(_root, "конспект.md");
        File.WriteAllText(path, "прежняя версия");
        File.WriteAllText(Path.Combine(_root, ChildTrash.TrashDir), "не каталог");

        var answer = await DriveControlRequestAndReadAnswer(session, "Write", path);

        using var scope = new AssertionScope();
        // JSON ответа экранирует кириллицу (Н…) — сравнивать надо декодированное поле,
        // а не подстроку сырого текста.
        var payload = JsonDocument.Parse(answer).RootElement;
        payload.GetProperty("behavior").GetString().Should().Be("deny", "без снимка перезапись отклонена");
        payload.GetProperty("message").GetString().Should().Contain("прежнюю версию", "отказ называет причину");
        File.ReadAllText(path).Should().Be("прежняя версия", "отказ не тронул файл");
    }

    [Theory]
    [InlineData("Write")]
    [InlineData("Edit")]
    public async Task НовыйФайл_БезСнимка_НоРазрешён(string tool)
    {
        // Нового файла защищать нечем: снимок не делается, корзина пуста, разрешение allow.
        var session = NewChildSession();
        var path = Path.Combine(_root, "Заметки", "нового-нет.md");

        var answer = await DriveControlRequestAndReadAnswer(session, tool, path);

        using var scope = new AssertionScope();
        answer.Should().Contain("\"allow\"", $"{tool} нового файла разрешён");
        ChildTrash.List(_root).Should().BeEmpty("несуществующий файл не снимается");
    }
}
