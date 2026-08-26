using ClaudeHomeServer.Services.Http;
using FluentAssertions;
using Microsoft.AspNetCore.Http;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Гейт кук прокси по Sec-Fetch-Site.
///
/// Смысл: SameSite=Strict считает границей САЙТ, поэтому поддомен внешнего доступа для него
/// свой, и кука ушла бы вместе с запросом со страницы проксируемого дев-сайта. Заголовок
/// ставит браузер, из скрипта он не подделывается.
/// </summary>
public class SecFetchSiteGuardTests
{
    private static HttpRequest RequestWith(string? site)
    {
        var ctx = new DefaultHttpContext();
        if (site is not null) ctx.Request.Headers[SecFetchSiteGuard.HeaderName] = site;
        return ctx.Request;
    }

    /// <summary>Наш iframe и его сабресурсы — единственный случай, где кука нужна.</summary>
    [Fact]
    public void Свой_адрес_куку_пропускает()
    {
        SecFetchSiteGuard.CookieAuthAllowed(RequestWith("same-origin")).Should().BeTrue();
    }

    /// <summary>
    /// Главный защищаемый случай: страница на соседнем поддомене. Для SameSite это «свой»
    /// запрос, для нас — чужой код.
    /// </summary>
    [Fact]
    public void Соседний_поддомен_куку_не_получает()
    {
        SecFetchSiteGuard.CookieAuthAllowed(RequestWith("same-site")).Should().BeFalse();
    }

    [Theory]
    [InlineData("cross-site")]
    [InlineData("none")]
    public void Чужой_сайт_и_ручной_ввод_куку_не_получают(string site)
    {
        SecFetchSiteGuard.CookieAuthAllowed(RequestWith(site)).Should().BeFalse();
    }

    /// <summary>
    /// Отсутствие заголовка — не отказ: его не шлют не-браузерные клиенты и старые браузеры,
    /// а без браузера описанной угрозы не существует вовсе.
    /// </summary>
    [Fact]
    public void Отсутствие_заголовка_не_считается_отказом()
    {
        SecFetchSiteGuard.CookieAuthAllowed(RequestWith(null)).Should().BeTrue();
        SecFetchSiteGuard.CookieAuthAllowed(RequestWith("")).Should().BeTrue();
    }

    [Fact]
    public void Регистр_значения_не_важен()
    {
        SecFetchSiteGuard.CookieAuthAllowed(RequestWith("Same-Origin")).Should().BeTrue();
    }
}
