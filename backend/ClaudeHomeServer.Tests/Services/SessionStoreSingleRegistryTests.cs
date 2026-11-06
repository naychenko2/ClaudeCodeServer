using System.Reflection;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Sessions;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Services;

// «Реестр один» (S3-1, инвариант разреза): SessionManager, собранный контейнером, держит тот же
// экземпляр SessionStore, что и sp.GetRequiredService<SessionStore>(). Второй реестр = вторая
// точка правды владения и видимости (per-owner изоляция) и тихая потеря чатов.
[Trait("Category", "Host")]
public class SessionStoreSingleRegistryTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public SessionStoreSingleRegistryTests(TestWebApplicationFactory factory) => _factory = factory;

    private static object? Field(object target, string name) =>
        typeof(SessionManager).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(target);

    [Fact]
    public void SessionManager_ИзКонтейнера_ДержитТотЖеСтор()
    {
        var sp = _factory.Services;
        var store = sp.GetRequiredService<SessionStore>();
        var manager = sp.GetRequiredService<SessionManager>();

        Field(manager, "_store").Should().BeSameAs(store);
        Field(manager, "_sessions").Should().BeSameAs(store.Sessions,
            "поле реестра на фасаде — ссылка на словарь стора, а не его копия");
    }

    [Fact]
    public async Task ЧатСозданныйЧерезФасад_ВиденВСторе_ТемЖеОбъектом()
    {
        var sp = _factory.Services;
        var store = sp.GetRequiredService<SessionStore>();
        var manager = sp.GetRequiredService<SessionManager>();
        var ownerId = sp.GetRequiredService<UserStore>().FindByUsername(TestWebApplicationFactory.TestUsername)!.Id;

        var chat = await manager.CreateChatAsync(ownerId, ClaudeMode.Auto);

        store.GetById(chat.Id).Should().BeSameAs(chat);
        store.GetOwned(chat.Id, ownerId).Should().BeSameAs(chat);
    }
}
