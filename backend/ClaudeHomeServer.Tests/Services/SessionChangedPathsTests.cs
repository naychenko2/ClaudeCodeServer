using System.Text.Json;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// SessionChangedPaths.Extract — экстрактор путей чата для обратного индекса
// «файл → какие ещё чаты его меняли» (панель «Изменения»). Таблица фикстур
// зеркалит (значимым поднадзором) фронтовый тест
// frontend/src/lib/__tests__/git.changedBy.test.ts — там же обратная ссылка сюда.
public class SessionChangedPathsTests
{
    private const string Root = "/repo";

    private static JsonElement ToolInput(object o) =>
        JsonDocument.Parse(JsonSerializer.Serialize(o)).RootElement;

    [Fact]
    public void Extract_ПустаяИстория_ReturnsEmpty()
    {
        SessionChangedPaths.Extract([], Root).Should().BeEmpty();
    }

    // [общий с фронтом] file_changed с External=false — путь уже относительный, только нормализация
    [Fact]
    public void Extract_FileChanged_НормализуетСлэшиИРегистр()
    {
        var messages = new List<StoredMessage> { new StoredFileChangedMessage("Src\\App.CS", 1, 0) };

        SessionChangedPaths.Extract(messages, Root).Keys.Should().BeEquivalentTo(["src/app.cs"]);
    }

    // Ведущий "./" срезается — как фронтовый экстрактор (path.replace(/^\.\//, ''))
    [Fact]
    public void Extract_FileChanged_СрезаетВедущуюТочкуСлэш()
    {
        var messages = new List<StoredMessage> { new StoredFileChangedMessage("./src/app.ts", 1, 0) };

        SessionChangedPaths.Extract(messages, Root).Keys.Should().BeEquivalentTo(["src/app.ts"]);
    }

    // External=true (правка вне заявленного хода — Bash/скрипты модели, человек в IDE)
    // ВКЛЮЧАЕТСЯ со значением true: фильтру «только файлы чата» она нужна, бейдж
    // «Также меняли» отсекает её на стороне потребителя (SessionRef.External)
    [Fact]
    public void Extract_FileChangedExternal_ВключаетсяСоЗначениемTrue()
    {
        var messages = new List<StoredMessage> { new StoredFileChangedMessage("src/app.cs", 1, 0, external: true) };

        SessionChangedPaths.Extract(messages, Root)
            .Should().BeEquivalentTo(new Dictionary<string, bool> { ["src/app.cs"] = true });
    }

    // Слияние по пути: false побеждает — чат правил файл и Edit-ом, и Bash-ом
    // в разных ходах → запись «сильная» (не external), порядок сообщений не важен
    [Fact]
    public void Extract_СлияниеExternal_FalseПобеждает()
    {
        var externalThenDirect = new List<StoredMessage>
        {
            new StoredFileChangedMessage("src/app.cs", 1, 0, external: true),
            new StoredFileChangedMessage("src/app.cs", 1, 0),
        };
        var directThenExternal = new List<StoredMessage>
        {
            new StoredFileChangedMessage("src/app.cs", 1, 0),
            new StoredFileChangedMessage("src/app.cs", 1, 0, external: true),
        };

        SessionChangedPaths.Extract(externalThenDirect, Root)
            .Should().BeEquivalentTo(new Dictionary<string, bool> { ["src/app.cs"] = false });
        SessionChangedPaths.Extract(directThenExternal, Root)
            .Should().BeEquivalentTo(new Dictionary<string, bool> { ["src/app.cs"] = false });
    }

    // tool_use из WriteTools — заявленная правка хода: external=false
    [Fact]
    public void Extract_ToolUse_ДаётExternalFalse()
    {
        var tool = new StoredToolUseMessage { Name = "Edit", Input = ToolInput(new { file_path = $"{Root}/src/a.ts" }) };

        SessionChangedPaths.Extract([tool], Root)
            .Should().BeEquivalentTo(new Dictionary<string, bool> { ["src/a.ts"] = false });
    }

    [Theory]
    [InlineData("Write")]
    [InlineData("Edit")]
    [InlineData("MultiEdit")]
    [InlineData("NotebookEdit")]
    [InlineData("write_file")]
    [InlineData("edit_file")]
    public void Extract_ToolUseWriteTools_ПутьИзFilePath(string toolName)
    {
        var tool = new StoredToolUseMessage { Name = toolName, Input = ToolInput(new { file_path = $"{Root}/src/a.ts" }) };

        SessionChangedPaths.Extract([tool], Root).Keys.Should().BeEquivalentTo(["src/a.ts"]);
    }

    // Инструмент вне белого списка (например Read/Bash) — не источник правки
    [Fact]
    public void Extract_ToolUseНеWriteTool_Игнорируется()
    {
        var tool = new StoredToolUseMessage { Name = "Read", Input = ToolInput(new { file_path = $"{Root}/src/a.ts" }) };

        SessionChangedPaths.Extract([tool], Root).Should().BeEmpty();
    }

    [Fact]
    public void Extract_ToolUse_NotebookPathВместоFilePath()
    {
        var tool = new StoredToolUseMessage { Name = "NotebookEdit", Input = ToolInput(new { notebook_path = $"{Root}/nb.ipynb" }) };

        SessionChangedPaths.Extract([tool], Root).Keys.Should().BeEquivalentTo(["nb.ipynb"]);
    }

    [Fact]
    public void Extract_ToolUse_PlainPathСвойство()
    {
        var tool = new StoredToolUseMessage { Name = "write_file", Input = ToolInput(new { path = $"{Root}/legacy.txt" }) };

        SessionChangedPaths.Extract([tool], Root).Keys.Should().BeEquivalentTo(["legacy.txt"]);
    }

    // Абсолютный путь ВНЕ rootPath — не файл проекта, отбрасывается
    [Fact]
    public void Extract_ToolUse_ПутьВнеRoot_Отбрасывается()
    {
        var tool = new StoredToolUseMessage { Name = "Write", Input = ToolInput(new { file_path = "/other/file.ts" }) };

        SessionChangedPaths.Extract([tool], Root).Should().BeEmpty();
    }

    [Fact]
    public void Extract_ToolUse_InputNull_ТихоПропускается()
    {
        var tool = new StoredToolUseMessage { Name = "Write", Input = null };

        var act = () => SessionChangedPaths.Extract([tool], Root);

        act.Should().NotThrow();
        SessionChangedPaths.Extract([tool], Root).Should().BeEmpty();
    }

    // Input не JsonElement (десериализация из истории всегда даёт JsonElement, но
    // конструктор сообщения этого не гарантирует — экстрактор обязан не падать)
    [Fact]
    public void Extract_ToolUse_InputНеJsonElement_ТихоПропускается()
    {
        var tool = new StoredToolUseMessage { Name = "Write", Input = new { file_path = "/repo/a.ts" } };

        SessionChangedPaths.Extract([tool], Root).Should().BeEmpty();
    }

    [Fact]
    public void Extract_ToolUse_InputБезНужныхСвойств_ТихоПропускается()
    {
        var tool = new StoredToolUseMessage { Name = "Write", Input = ToolInput(new { command = "ls" }) };

        SessionChangedPaths.Extract([tool], Root).Should().BeEmpty();
    }

    // --- Пропуск хода в чужом worktree ---

    [Fact]
    public void Extract_ХодВЧужомWorktree_ПутиИсключены()
    {
        var messages = new List<StoredMessage>
        {
            new StoredSessionStartedMessage("claude-3", "auto", new TurnWorktreeInfo("/repo/.claude/worktrees/x", "x")),
            new StoredFileChangedMessage("src/in-worktree.ts", 1, 0),
        };

        SessionChangedPaths.Extract(messages, Root).Should().BeEmpty();
    }

    // Сброс пропуска следующим session_started БЕЗ TurnWorktree (ход вернулся в основное дерево)
    [Fact]
    public void Extract_WorktreeХод_СбрасываетсяSessionStartedБезTurnWorktree()
    {
        var messages = new List<StoredMessage>
        {
            new StoredSessionStartedMessage("claude-3", "auto", new TurnWorktreeInfo("/repo/.claude/worktrees/x", "x")),
            new StoredFileChangedMessage("src/in-worktree.ts", 1, 0),
            new StoredSessionStartedMessage("claude-3", "auto"),
            new StoredFileChangedMessage("src/back-in-root.ts", 1, 0),
        };

        SessionChangedPaths.Extract(messages, Root).Keys.Should().BeEquivalentTo(["src/back-in-root.ts"]);
    }

    // Сброс пропуска сообщением пользователя (начало нового хода в основном дереве)
    [Fact]
    public void Extract_WorktreeХод_СбрасываетсяUserMessage()
    {
        var messages = new List<StoredMessage>
        {
            new StoredSessionStartedMessage("claude-3", "auto", new TurnWorktreeInfo("/repo/.claude/worktrees/x", "x")),
            new StoredFileChangedMessage("src/in-worktree.ts", 1, 0),
            new StoredUserMessage("продолжай"),
            new StoredFileChangedMessage("src/after-user.ts", 1, 0),
        };

        SessionChangedPaths.Extract(messages, Root).Keys.Should().BeEquivalentTo(["src/after-user.ts"]);
    }

    // Дедуп: один и тот же файл разным регистром из разных источников — одна запись (lowercase)
    [Fact]
    public void Extract_ОдинФайлРазнымиИсточниками_Дедуп()
    {
        var messages = new List<StoredMessage>
        {
            new StoredFileChangedMessage("src/App.ts", 1, 0),
            new StoredToolUseMessage { Name = "Edit", Input = ToolInput(new { file_path = $"{Root}/src/app.ts" }) },
        };

        SessionChangedPaths.Extract(messages, Root).Keys.Should().BeEquivalentTo(["src/app.ts"]);
    }
}
