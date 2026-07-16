using ClaudeHomeServer.Services;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

public class WorkflowAgentParserTests : IDisposable
{
    private readonly string _dir;

    public WorkflowAgentParserTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "wf_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private string WriteAgentFile(string agentId, params string[] lines)
    {
        var path = Path.Combine(_dir, $"agent-{agentId}.jsonl");
        File.WriteAllLines(path, lines);
        return path;
    }

    // Первая строка agent-файла: user-сообщение с промптом
    private static string UserLine(string prompt) =>
        """{"message":{"role":"user","content":"<P>"}}""".Replace("<P>", prompt);

    // То же с меткой времени старта (для проверки хронологии ParseDirectory)
    private static string UserLineAt(string prompt, string timestamp) =>
        """{"timestamp":"<TS>","message":{"role":"user","content":"<P>"}}"""
            .Replace("<TS>", timestamp)
            .Replace("<P>", prompt);

    // Ответ ассистента текстом (становится summary)
    private static string AssistantTextLine(string text, bool endTurn = false) =>
        """{"message":{"role":"assistant","stop_reason":<S>,"content":[{"type":"text","text":"<T>"}]}}"""
            .Replace("<S>", endTurn ? "\"end_turn\"" : "null")
            .Replace("<T>", text);

    private static string ToolUseLine(string tool, string inputJson = "{}") =>
        """{"message":{"role":"assistant","content":[{"type":"tool_use","name":"<N>","input":<I>}]}}"""
            .Replace("<N>", tool)
            .Replace("<I>", inputJson);

    // ─── ParseDirectory ──────────────────────────────────────────────────────

    [Fact]
    public void ParseDirectory_НесуществующаяДиректория_ПустойСписок()
    {
        WorkflowAgentParser.ParseDirectory(Path.Combine(_dir, "ghost")).Should().BeEmpty();
    }

    [Fact]
    public void ParseDirectory_ПустаяДиректория_ПустойСписок()
    {
        WorkflowAgentParser.ParseDirectory(_dir).Should().BeEmpty();
    }

    [Fact]
    public void ParseDirectory_КорректныеФайлы_ВозвращаетАгентов()
    {
        WriteAgentFile("a1",
            UserLine("Сделай раз"),
            ToolUseLine("Read", """{"file_path":"C:/x/one.cs"}"""),
            AssistantTextLine("Готово раз", endTurn: true));
        WriteAgentFile("a2",
            UserLine("Сделай два"),
            AssistantTextLine("Работаю"));

        var agents = WorkflowAgentParser.ParseDirectory(_dir);

        agents.Should().HaveCount(2);
        var a1 = agents.Single(a => a.Id == "a1");
        a1.Prompt.Should().Be("Сделай раз");
        a1.Summary.Should().Be("Готово раз");
        a1.IsDone.Should().BeTrue();
        a1.Tools.Should().ContainSingle(t => t.Name == "Read" && t.Count == 1);
        a1.Files.Should().ContainSingle().Which.Should().Be("one.cs");

        var a2 = agents.Single(a => a.Id == "a2");
        a2.IsDone.Should().BeFalse();
        a2.Summary.Should().Be("Работаю");
    }

    [Fact]
    public void ParseDirectory_ПорядокАгентов_ПоTimestampПервойСтроки()
    {
        // Алфавитный порядок имён файлов (a2 < b1) противоречит хронологии запуска —
        // выигрывает timestamp первой строки транскрипта
        WriteAgentFile("a2",
            UserLineAt("Второй", "2026-07-16T10:05:00.000Z"),
            AssistantTextLine("работаю"));
        WriteAgentFile("b1",
            UserLineAt("Первый", "2026-07-16T10:00:00.000Z"),
            AssistantTextLine("работаю"));

        var agents = WorkflowAgentParser.ParseDirectory(_dir);

        agents.Select(a => a.Id).Should().ContainInOrder("b1", "a2");
    }

    [Fact]
    public void ParseDirectory_JournalСResult_ПомечаетАгентаDone()
    {
        // Агент сам не сообщил end_turn, но journal.jsonl — источник истины
        WriteAgentFile("abc", UserLine("Задание"), AssistantTextLine("В процессе"));
        File.WriteAllLines(Path.Combine(_dir, "journal.jsonl"),
        [
            """{"type":"start","agentId":"abc"}""",
            "мусор не-JSON",
            """{"type":"result","agentId":"abc"}""",
        ]);

        var agents = WorkflowAgentParser.ParseDirectory(_dir);

        agents.Should().ContainSingle().Which.IsDone.Should().BeTrue();
    }

    // ─── ParseAgentFile: битые строки ────────────────────────────────────────

    [Fact]
    public void ParseAgentFile_БитыеСтрокиВСередине_Пропускаются()
    {
        var path = WriteAgentFile("x",
            UserLine("Промпт"),
            """{"message":{"role":"assistant","content":[{"type":"te""", // оборванный JSON
            "просто мусор",
            AssistantTextLine("Итог", endTurn: true));

        var agent = WorkflowAgentParser.ParseAgentFile(path);

        agent.Should().NotBeNull();
        agent!.Prompt.Should().Be("Промпт");
        agent.Summary.Should().Be("Итог");
        agent.IsDone.Should().BeTrue();
    }

    [Fact]
    public void ParseAgentFile_БитаяПерваяСтрока_ВозвращаетNull()
    {
        var path = WriteAgentFile("bad", "{не json", AssistantTextLine("Текст"));

        WorkflowAgentParser.ParseAgentFile(path).Should().BeNull();
    }

    [Fact]
    public void ParseAgentFile_ПустойФайл_ВозвращаетNull()
    {
        var path = WriteAgentFile("empty");

        WorkflowAgentParser.ParseAgentFile(path).Should().BeNull();
    }

    [Fact]
    public void ParseAgentFile_НесуществующийФайл_ВозвращаетNull()
    {
        WorkflowAgentParser.ParseAgentFile(Path.Combine(_dir, "agent-ghost.jsonl")).Should().BeNull();
    }

    [Fact]
    public void ParseAgentFile_ИмяАгента_ИзИмениФайла()
    {
        var path = WriteAgentFile("my-agent-42", UserLine("p"));

        WorkflowAgentParser.ParseAgentFile(path)!.Id.Should().Be("my-agent-42");
    }

    [Fact]
    public void ParseAgentFile_СчитаетИнструментыИФайлы()
    {
        var path = WriteAgentFile("t",
            UserLine("p"),
            ToolUseLine("Read", """{"file_path":"/a/b/first.cs"}"""),
            ToolUseLine("Read", """{"file_path":"/a/b/first.cs"}"""), // дубль файла
            ToolUseLine("Glob", """{"pattern":"src/**/*.ts"}"""),
            ToolUseLine("Bash"));

        var agent = WorkflowAgentParser.ParseAgentFile(path)!;

        agent.Tools.Should().Contain(t => t.Name == "Read" && t.Count == 2);
        agent.Tools.Should().Contain(t => t.Name == "Glob" && t.Count == 1);
        agent.Tools.Should().Contain(t => t.Name == "Bash" && t.Count == 1);
        agent.Files.Should().BeEquivalentTo("first.cs", "*.ts");
    }

    // ─── ParseAgentTimeline ──────────────────────────────────────────────────

    [Fact]
    public void ParseAgentTimeline_СобираетБлокиПоПорядку_БезПромпта()
    {
        var path = WriteAgentFile("tl",
            UserLine("Промпт не должен попасть в таймлайн"),
            """{"message":{"role":"assistant","content":[{"type":"thinking","thinking":"размышляю"}]}}""",
            AssistantTextLine("Начинаю"),
            """{"message":{"role":"assistant","content":[{"type":"tool_use","id":"t1","name":"Read","input":{"file_path":"/a/b/target.cs"}}]}}""",
            """{"message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"t1","content":"строки файла"}]}}""",
            AssistantTextLine("Готово", endTurn: true));

        var blocks = WorkflowAgentParser.ParseAgentTimeline(path);

        blocks.Should().HaveCount(4);
        blocks[0].Kind.Should().Be("thinking");
        blocks[0].Text.Should().Be("размышляю");
        blocks[1].Kind.Should().Be("text");
        blocks[1].Text.Should().Be("Начинаю");
        blocks[2].Kind.Should().Be("tool_use");
        blocks[2].ToolName.Should().Be("Read");
        blocks[2].ToolId.Should().Be("t1");
        blocks[2].ToolResult.Should().Be("строки файла");
        blocks[2].IsError.Should().BeFalse();
        blocks[3].Kind.Should().Be("text");
        blocks[3].Text.Should().Be("Готово");
    }

    [Fact]
    public void ParseAgentTimeline_ОшибкаИнструмента_IsErrorTrue()
    {
        var path = WriteAgentFile("tlerr",
            UserLine("p"),
            """{"message":{"role":"assistant","content":[{"type":"tool_use","id":"t1","name":"Bash","input":{"command":"boom"}}]}}""",
            """{"message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"t1","is_error":true,"content":[{"type":"text","text":"команда упала"}]}]}}""");

        var block = WorkflowAgentParser.ParseAgentTimeline(path).Should().ContainSingle().Which;
        block.ToolResult.Should().Be("команда упала");
        block.IsError.Should().BeTrue();
    }

    [Fact]
    public void ParseAgentTimeline_БитыеСтроки_Пропускаются()
    {
        var path = WriteAgentFile("tlbad",
            UserLine("p"),
            "{оборванный json",
            AssistantTextLine("Живой блок"));

        var blocks = WorkflowAgentParser.ParseAgentTimeline(path);

        blocks.Should().ContainSingle().Which.Text.Should().Be("Живой блок");
    }

    [Fact]
    public void ParseAgentTimeline_НесуществующийФайл_ПустойСписок()
    {
        WorkflowAgentParser.ParseAgentTimeline(Path.Combine(_dir, "agent-ghost.jsonl"))
            .Should().BeEmpty();
    }

    [Fact]
    public void ParseAgentTimeline_StructuredOutput_ОтдельныйСворачиваемыйБлок()
    {
        var path = WriteAgentFile("so",
            UserLine("p"),
            ToolUseLine("StructuredOutput", """{"ok":true,"note":"агент на связи"}"""));

        var blocks = WorkflowAgentParser.ParseAgentTimeline(path);

        var block = blocks.Should().ContainSingle().Which;
        block.Kind.Should().Be("structured");
        block.Text.Should().Contain("агент на связи");
        block.ToolName.Should().BeNull();
    }

    // ─── IsPathAllowed ───────────────────────────────────────────────────────

    [Fact]
    public void IsPathAllowed_ПутьВнутриAllowedRoot_True()
    {
        var inside = Path.Combine(WorkflowAgentParser.DefaultRoot, "proj", "wf_1");
        WorkflowAgentParser.IsPathAllowed(inside).Should().BeTrue();
    }

    [Fact]
    public void IsPathAllowed_ПутьВнеAllowedRoot_False()
    {
        WorkflowAgentParser.IsPathAllowed(_dir).Should().BeFalse();
        WorkflowAgentParser.IsPathAllowed(Path.GetTempPath()).Should().BeFalse();
    }

    [Fact]
    public void IsPathAllowed_ПрофильПодProfilesRoot_РазрешёнТолькоProjects()
    {
        var prev = WorkflowAgentParser.ProfilesRoot;
        try
        {
            WorkflowAgentParser.ProfilesRoot = _dir;
            // Транскрипты любого профиля (в т.ч. подписки sub-*, созданной после старта)
            var wf = Path.Combine(_dir, "sub-my-second", "projects", "-p-x-", "sid", "subagents", "workflows", "wf_1");
            WorkflowAgentParser.IsPathAllowed(wf).Should().BeTrue();
            // Но не остальное содержимое профиля (креденшалы и т.п.)
            WorkflowAgentParser.IsPathAllowed(Path.Combine(_dir, "sub-my-second", ".credentials.json"))
                .Should().BeFalse();
            // И не сам корень с одним сегментом
            WorkflowAgentParser.IsPathAllowed(Path.Combine(_dir, "projects")).Should().BeFalse();
        }
        finally { WorkflowAgentParser.ProfilesRoot = prev; }
    }

    [Fact]
    public void IsPathAllowed_ProfilesRoot_TraversalНеПроходит()
    {
        var prev = WorkflowAgentParser.ProfilesRoot;
        try
        {
            WorkflowAgentParser.ProfilesRoot = Path.Combine(_dir, "profiles");
            var sneaky = Path.Combine(_dir, "profiles", "key", "..", "..", "secret", "projects", "x");
            WorkflowAgentParser.IsPathAllowed(sneaky).Should().BeFalse();
        }
        finally { WorkflowAgentParser.ProfilesRoot = prev; }
    }
}
