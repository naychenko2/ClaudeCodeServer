using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.ImageEditor.Chats;
using ClaudeHomeServer.Services.Images.LocalMedia;
using ClaudeHomeServer.Services.Mcp.Http;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Шаг 4 «локальная модель по умолчанию»: три старых текста «локально — только по явной просьбе»
// ссылаются на правило хвоста одной опорной фразой, и запрет у них всегда стоит ПОСЛЕ оговорки.
// Модуль ImageEditor не видит Images, поэтому связь — текстом, а не общей константой
public class LocalMediaDefaultConsistencyTests
{
    private const string Anchor = "хвостовой секции «Картинки и видео: локальная модель по умолчанию»";

    public static TheoryData<string, string> Texts() => new()
    {
        // Абзац о медиа содержит и другие «только если» (glif/fal) — берём фрагмент про local-media
        { "BuiltInSystemPrompt", ProjectManager.BuiltInSystemPrompt[
            ProjectManager.BuiltInSystemPrompt.IndexOf("Если подключён MCP-сервер local-media", StringComparison.Ordinal)..] },
        { "LocalMediaToolset.ExplicitOnly", LocalMediaToolset.ExplicitOnly },
        { "PriorityRule", ImageEditorStateContributor.PriorityRule },
        { "PersonalPriorityRule", ImageEditorStateContributor.PersonalPriorityRule },
    };

    [Theory]
    [MemberData(nameof(Texts))]
    public void Запрет_только_после_оговорки_о_правиле_хвоста(string where, string text)
    {
        var anchor = text.IndexOf(Anchor, StringComparison.Ordinal);
        anchor.Should().BeGreaterThanOrEqualTo(0, $"{where} ссылается на правило хвоста");

        var restriction = text.IndexOf("только если", StringComparison.OrdinalIgnoreCase);
        restriction.Should().BeGreaterThan(anchor, $"{where}: «только» без оговорки о правиле хвоста противоречит секции");
    }

    // Опорная фраза — заголовок самой секции: переименовали секцию — старые тексты ссылаются в пустоту
    [Fact]
    public void Опорная_фраза_совпадает_с_заголовком_секции() =>
        LocalMediaDefaultContributor.ProjectRule.Should().StartWith("## Картинки и видео: локальная модель по умолчанию\n");
}
