using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Video;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services.Video;

/// <summary>
/// Избранные каналы: форма ключа и — главное — различие «не настраивал» и «снял всё».
/// Свести их в одно значение соблазнительно, но тогда снятие последней звёздочки
/// возвращало бы дефолтный набор, и убрать канал из полосы стало бы невозможно.
/// </summary>
public class VideoFavoritesTests : IDisposable
{
    private readonly string _tempDir;

    public VideoFavoritesTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "cc_video_favs_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    private UserStore BuildStore()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(_tempDir, "projects.json"),
        }).Build();
        return new UserStore(config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
    }

    [Theory]
    [InlineData("smotrim:1")]
    [InlineData("youtube:UC_x5XG1OV2P6uZZ5FSM9Ttw")]
    [InlineData("smotrim:678")]
    public void Валидный_ключ_принимается(string key)
        => VideoFavorites.IsValidKey(key).Should().BeTrue();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("smotrim")]           // без id
    [InlineData(":1")]                // без провайдера
    [InlineData("smotrim:../../etc")] // путь вместо id
    [InlineData("SMOTRIM:1")]         // провайдер только в нижнем регистре
    public void Мусорный_ключ_отбрасывается(string? key)
        => VideoFavorites.IsValidKey(key).Should().BeFalse();

    [Fact]
    public void Normalize_убирает_дубли_и_мусор_сохраняя_порядок()
    {
        var result = VideoFavorites.Normalize(["smotrim:3", "плохой", "smotrim:1", "smotrim:3", ""]);
        result.Should().Equal("smotrim:3", "smotrim:1");
    }

    [Fact]
    public void Normalize_режет_по_потолку()
    {
        var many = Enumerable.Range(1, VideoFavorites.MaxFavorites + 10).Select(i => $"smotrim:{i}");
        VideoFavorites.Normalize(many).Should().HaveCount(VideoFavorites.MaxFavorites);
    }

    [Fact]
    public void Ненастроенный_пользователь_отдаёт_null_а_не_пустой_список()
    {
        var store = BuildStore();
        var user = store.Add("u1", "password123", "user");

        // null здесь и означает «показывай дефолт» — пустой список сказал бы «пусто осознанно»
        store.GetFavoriteVideoChannels(user.Id).Should().BeNull();
    }

    [Fact]
    public void Снятие_всех_звёздочек_сохраняется_как_пустой_список()
    {
        var store = BuildStore();
        var user = store.Add("u1", "password123", "user");

        store.SetFavoriteVideoChannels(user.Id, []).Should().BeTrue();

        store.GetFavoriteVideoChannels(user.Id).Should().NotBeNull().And.BeEmpty();
        // И переживает перезагрузку: иначе после рестарта полоса вернула бы дефолт
        BuildStore().GetFavoriteVideoChannels(user.Id).Should().NotBeNull().And.BeEmpty();
    }

    [Fact]
    public void Набор_сохраняется_в_порядке_показа_и_переживает_перезагрузку()
    {
        var store = BuildStore();
        var user = store.Add("u1", "password123", "user");

        store.SetFavoriteVideoChannels(user.Id, ["smotrim:3", "smotrim:1"]).Should().BeTrue();

        BuildStore().GetFavoriteVideoChannels(user.Id).Should().Equal("smotrim:3", "smotrim:1");
    }

    [Fact]
    public void Неизвестный_пользователь_не_сохраняется()
    {
        BuildStore().SetFavoriteVideoChannels("no-such-user", ["smotrim:1"]).Should().BeFalse();
    }

    [Fact]
    public void Дефолт_состоит_из_валидных_ключей_без_дублей()
    {
        VideoFavorites.Defaults.Should().OnlyContain(k => VideoFavorites.IsValidKey(k));
        VideoFavorites.Defaults.Should().OnlyHaveUniqueItems();
    }
}
