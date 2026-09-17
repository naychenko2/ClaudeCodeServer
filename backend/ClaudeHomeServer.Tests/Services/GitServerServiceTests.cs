using ClaudeHomeServer.Services.Git;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

// OwnsUrl — чистая функция-гейт: по ней решается, уедет ли пара логин-токен Forgejo
// в Basic-заголовке к remote-операции. Ошибка границы = токен с правом записи на чужом хосте.
public class GitServerServiceTests
{
    // Кроме конфига OwnsUrl ничего не трогает — остальные зависимости в этих тестах не нужны
    private static GitServerService Service(string baseUrl, string? publicUrl = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Forgejo:BaseUrl"] = baseUrl,
            ["Forgejo:AdminToken"] = "test-token",
        };
        if (publicUrl is not null) settings["Forgejo:PublicUrl"] = publicUrl;
        return new GitServerService(TestConfig.Build(settings), null!, null!, null!);
    }

    [Fact]
    public void OwnsUrl_Свой_Clone_Url_Признаётся_Своим()
    {
        Service("http://localhost:3005").OwnsUrl("http://localhost:3005/grisha/project.git").Should().BeTrue();
    }

    [Fact]
    public void OwnsUrl_Публичный_Адрес_Признаётся_Своим()
    {
        // Внутренний localhost:3005 человек не видит вовсе — руками он вводит PublicUrl
        var svc = Service("http://localhost:3005", "https://git.example.com");

        svc.OwnsUrl("https://git.example.com/grisha/project.git").Should().BeTrue();
        svc.OwnsUrl("http://localhost:3005/grisha/project.git").Should().BeTrue();
    }

    [Fact]
    public void OwnsUrl_Хост_Двойник_С_Суффиксом_Чужой()
    {
        Service("https://git.example.com")
            .OwnsUrl("https://git.example.com.attacker.net/repo.git").Should().BeFalse();
    }

    [Fact]
    public void OwnsUrl_Порт_Двойник_Чужой()
    {
        Service("http://localhost:3000").OwnsUrl("http://localhost:30001/repo.git").Should().BeFalse();
    }

    [Fact]
    public void OwnsUrl_Userinfo_Подмена_Хоста_Чужая()
    {
        // Всё до «@» — это userinfo, настоящий хост здесь evil.net: строковый префикс
        // обманулся бы, а Uri.Host — нет
        Service("https://git.example.com")
            .OwnsUrl("https://git.example.com@evil.net/repo.git").Should().BeFalse();
    }

    [Fact]
    public void OwnsUrl_Префикс_Пути_За_Реверс_Прокси_Учитывается()
    {
        // Forgejo за прокси на /git: свои — только адреса внутри этого префикса
        var svc = Service("https://git.example.com/git");

        svc.OwnsUrl("https://git.example.com/git/grisha/project.git").Should().BeTrue();
        svc.OwnsUrl("https://git.example.com/other/project.git").Should().BeFalse();
        svc.OwnsUrl("https://git.example.com/gitlab/project.git").Should().BeFalse();
    }

    [Fact]
    public void ToPublicHtmlUrl_Подменяет_Хост_Только_Своему_Адресу()
    {
        var svc = Service("http://localhost:3005", "https://git.example.com");

        svc.ToPublicHtmlUrl("http://localhost:3005/grisha/project.git")
            .Should().Be("https://git.example.com/grisha/project");
        // Порт-двойник: чужой адрес обязан остаться собой (без .git)
        svc.ToPublicHtmlUrl("http://localhost:30051/grisha/project.git")
            .Should().Be("http://localhost:30051/grisha/project");
    }

    [Fact]
    public void OwnsUrl_Без_Настроенного_Forgejo_Всегда_False()
    {
        Service("").OwnsUrl("https://github.com/user/repo.git").Should().BeFalse();
    }

    [Fact]
    public void OwnsUrl_Другая_Схема_И_Мусор_Чужие()
    {
        var svc = Service("https://git.example.com");

        svc.OwnsUrl("http://git.example.com/repo.git").Should().BeFalse();
        svc.OwnsUrl("git@git.example.com:grisha/project.git").Should().BeFalse();
        svc.OwnsUrl(null).Should().BeFalse();
    }
}
