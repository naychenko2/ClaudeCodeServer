using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Fixtures;

/// <summary>
/// Пул общих хостов (Э5): один хост на ключ, ссылка на wwwroot берётся и отдаётся ровно по разу,
/// общих учёток нет, правка конфига общего хоста — отказ, владельцы не видят данных друг друга.
/// Пул — свой экземпляр теста, а не процессный: его Dispose проверяется здесь же.
/// </summary>
[Trait("Category", "Host")]
public class SharedHostPoolTests
{
    [Fact]
    public void ОдинХостНаКлюч_ФронтБерётсяИОтдаётсяОдинРаз()
    {
        var pool = new SharedHostPool();
        var first = new PooledHost(pool);
        var second = new PooledHost(pool);
        _ = first.Services;
        _ = second.Services;

        second.Factory.Should().BeSameAs(first.Factory, "классы пула делят один хост");
        first.TempDir.Should().NotBe(second.TempDir, "каталог — свой на класс");
        first.Factory.FrontendAcquired.Should().Be(1, "ссылка на wwwroot — одна на хост, а не на класс");

        first.Dispose();
        second.Dispose();
        first.Factory.FrontendReleased.Should().Be(0, "класс уходит — общий хост и его ссылка остаются");

        pool.Dispose();
        pool.Dispose();
        first.Factory.FrontendReleased.Should().Be(1, "пул гасит хост, ссылка снимается ровно один раз");
        FluentActions.Invoking(() => pool.Get()).Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public void ОбщихУчётокНет_ВходTestuserОтклонён()
    {
        using var pool = new SharedHostPool();
        var host = new PooledHost(pool);

        FluentActions.Invoking(() => host.Factory.CreateAuthenticatedClient())
            .Should().Throw<HttpRequestException>("в хосте пула нет testuser — класс обязан завести своего владельца");
    }

    [Fact]
    public void ПравкаКонфигаОбщегоХоста_Отказ()
    {
        using var pool = new SharedHostPool();
        var host = new PooledHost(pool);

        host.Factory.ExtraConfig["Mutation"] = "1";

        FluentActions.Invoking(() => new PooledHost(pool)).Should().Throw<InvalidOperationException>()
            .WithMessage("*ExtraServices/ExtraConfig*");
    }

    [Fact]
    public async Task ВладельцыПула_НеВидятПроектовДругДруга()
    {
        using var pool = new SharedHostPool();
        var host = new PooledHost(pool);
        var a = await host.NewOwnerAsync();
        var b = await host.NewOwnerAsync();
        var dir = Path.Combine(host.TempDir, "proj");
        Directory.CreateDirectory(dir);

        (await a.Client.PostAsJsonAsync("/api/projects", new { name = "A", rootPath = dir }))
            .EnsureSuccessStatusCode();

        a.Id.Should().NotBe(b.Id);
        var mine = await a.Client.GetFromJsonAsync<JsonElement>("/api/projects");
        var theirs = await b.Client.GetFromJsonAsync<JsonElement>("/api/projects");
        mine.EnumerateArray().Should().ContainSingle(p => p.GetProperty("name").GetString() == "A");
        theirs.EnumerateArray().Should().NotContain(p => p.GetProperty("name").GetString() == "A");
    }
}
