using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Prompts;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Подсказка про материалы контекста чата (A5, фича chat-context): текст и условия его
/// появления. Секция обещает модели инструмент context_list — разойтись с реальностью
/// (пустой контекст, снимок вместо живого состава) она не должна.
/// </summary>
public class ChatContextPromptsTests
{
    [Fact]
    public void ПустойКонтекст_СекцииНет()
    {
        ChatContextPrompts.SectionFor([]).Should().BeNull("звать инструмент незачем");
        ChatContextPrompts.SectionFor(null).Should().BeNull();
    }

    [Fact]
    public void НепустойКонтекст_ПеречисляетСоставИНазываетИнструмент()
    {
        var section = ChatContextPrompts.SectionFor([
            new SessionContextEntry { Type = SessionContextTypes.File, Id = "docs/a.md" },
            new SessionContextEntry { Type = SessionContextTypes.File, Id = "docs/b.md" },
            new SessionContextEntry { Type = SessionContextTypes.Task, Id = "t1" },
        ]);

        section.Should().NotBeNull();
        section.Should().Contain("mcp__wsp__context_list", "иначе модель не знает, чем раскрыть материалы");
        section.Should().Contain("файлов — 2").And.Contain("задач — 1");
        section.Should().Contain("(3:", "общее число материалов названо");
        section.Should().NotContain("ссылок", "типа без записей в перечислении быть не должно");
    }

    /// <summary>
    /// Сторож A5: секция собирается из ЖИВОГО провайдера и только при включённом флаге
    /// владельца, внутри блока смонтированного wsp-сервера. Снимок состава (поле контекста
    /// вместо Func) не пережил бы добавление материала в идущем чате — вкладки у человека
    /// есть, а модель о них не знает.
    /// </summary>
    [SkippableFact]
    public void СекцияХода_СобираетсяЖивымПровайдеромИПодФлагом()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        string? path = null;
        while (dir is not null && path is null)
        {
            var candidate = Path.Combine(dir.FullName, "backend", "ClaudeHomeServer",
                "Services", "Llm", "Claude", "ClaudeSession.cs");
            if (File.Exists(candidate)) path = candidate;
            dir = dir.Parent;
        }
        Skip.If(path is null, "ClaudeSession.cs не найден (сборка вне дерева репозитория)");

        var source = File.ReadAllText(path!);
        var start = source.IndexOf("ChatContextPrompts.SectionFor", StringComparison.Ordinal);
        start.Should().BeGreaterThan(0, "секция контекста чата обязана собираться в ClaudeSession");

        // Условие секции целиком — от начала if до Add(...)
        var ifStart = source.LastIndexOf("if (", start, StringComparison.Ordinal);
        var add = source.IndexOf("Add(\"mcp-context\"", start, StringComparison.Ordinal);
        add.Should().BeGreaterThan(start, "секция обязана называться mcp-context");
        var block = source[ifStart..add];

        block.Should().Contain("_chatContextProvider?.Invoke()",
            "состав берётся живым провайдером на каждый ход, а не снимком при создании адаптера");
        block.Should().Contain("ChatContextEnabled",
            "подсказка появляется только при включённом флаге владельца — иначе обещала бы "
            + "инструмент, которого нет в составе");
    }
}
