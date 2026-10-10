using ClaudeHomeServer.Services.ImageEditor;
using ClaudeHomeServer.Services.ImageEditor.Chats;
using ClaudeHomeServer.Services.ImageEditor.Threads;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.ImageEditor.Chats;

// Строка о взятых фото персонажа в блоке «Картинки в этом чате» (ADR-027 §8): агент видит выбор текстом,
// схемы MCP-инструментов не меняются
public class CharacterPhotosStateTextTests
{
    private static string Render(ImageThreadCharacter? character, string status = ImageThreadLaunchStatus.Done)
    {
        var now = DateTime.UtcNow;
        var thread = new ImageThread("t1", "images/hero.png", [], null, [], null, null, null, now)
        {
            Versions = [ImageThreadVersion.Origin(now), new("v1", 1, "job-1", 1, ImageThreadVersion.OriginId, null, [], null, now)],
            CurrentVersionId = "v1",
            Launches = [new ImageThreadLaunch("job-1", ImageThreadVersion.OriginId, null, now, status, "human", "в профиль у окна", character)],
        };
        return ImageEditorStateContributor.RenderThreads(new ImageThreadsState("t1", 1, [thread]), [], _ => null);
    }

    [Fact]
    public void Подобрано_под_сюжет_с_признаками_по_русски()
    {
        var block = Render(new ImageThreadCharacter("anya", ["a.jpg", "b.jpg"], CharacterPhotoPick.Auto,
            new CharacterScene("profile", "level", "smile")));

        block.Should().Contain("Фото персонажа «anya»: a.jpg, b.jpg — подобраны под сюжет (в профиль, улыбка)");
    }

    [Theory]
    [InlineData("three-quarter", "laugh", "вполоборота, смех")]
    [InlineData("front", "unknown", "анфас")]
    public void Признаки_сцены_переводятся_а_unknown_пропускается(string angle, string emotion, string expected)
    {
        var block = Render(new ImageThreadCharacter("anya", ["a.jpg"], CharacterPhotoPick.Auto, new CharacterScene(angle, null, emotion)));

        block.Should().Contain($"подобраны под сюжет ({expected})");
    }

    [Fact]
    public void Выбраны_человеком()
    {
        Render(new ImageThreadCharacter("anya", ["c.jpg"], CharacterPhotoPick.Manual, null))
            .Should().Contain("Фото персонажа «anya»: c.jpg — выбраны человеком");
    }

    [Fact]
    public void Обычный_порядок()
    {
        Render(new ImageThreadCharacter("anya", ["a.jpg", "b.jpg"], CharacterPhotoPick.Fallback, null))
            .Should().Contain("Фото персонажа «anya»: a.jpg, b.jpg — обычный порядок");
    }

    [Fact]
    public void Без_персонажа_в_запуске_строки_нет()
    {
        Render(null).Should().NotContain("Фото персонажа");
    }
}
