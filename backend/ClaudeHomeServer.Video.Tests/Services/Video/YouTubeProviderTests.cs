using ClaudeHomeServer.Services.Video;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services.Video;

/// <summary>
/// Провайдер ленты YouTube. Живых вызовов к Google здесь нет и быть не должно —
/// проверяется то, что можно проверить без сети: экономия квоты и выключенность
/// провайдера без ключей.
/// </summary>
public class YouTubeProviderTests
{
    // Плейлист загрузок выводится из id канала (UC… → UU…). Отдельный вызов
    // channels.list ради этого стоил бы по запросу на канал: при сотне подписок
    // это сотня единиц суточной квоты на ровном месте.
    [Theory]
    [InlineData("UCabc123", "UUabc123")]
    [InlineData("UC_x5XG1OV2P6uZZ5FSM9Ttw", "UU_x5XG1OV2P6uZZ5FSM9Ttw")]
    public void Плейлист_загрузок_выводится_из_id_канала(string channelId, string expected)
    {
        YouTubeProvider.UploadsPlaylistOf(channelId).Should().Be(expected);
    }

    [Theory]
    [InlineData("PLsomething")]   // плейлист, а не канал
    [InlineData("UC")]            // слишком короткий
    [InlineData("")]
    public void Чужой_идентификатор_плейлиста_не_даёт(string channelId)
    {
        YouTubeProvider.UploadsPlaylistOf(channelId).Should().BeNull();
    }

    [Fact]
    public void Без_ключей_провайдер_выключен()
    {
        var options = new YouTubeOptions { ClientId = "", ClientSecret = "" };
        options.IsConfigured.Should().BeFalse("пустые ключи = провайдер не показывается вовсе");
    }

    [Fact]
    public void Половины_ключей_недостаточно()
    {
        new YouTubeOptions { ClientId = "id", ClientSecret = "" }.IsConfigured.Should().BeFalse();
        new YouTubeOptions { ClientId = "", ClientSecret = "secret" }.IsConfigured.Should().BeFalse();
        new YouTubeOptions { ClientId = "id", ClientSecret = "secret" }.IsConfigured.Should().BeTrue();
    }
}
