using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Services.DynamicModules;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Tests.DynamicModules;

// Сброс кэша MF-remote после выкатки: ?v= в URL remoteEntry.js — хеш содержимого файла
// (версия манифеста у модулей годами «1.0.0»), а сам remoteEntry.js раздаётся с no-cache,
// хешированные чанки remote — immutable.
public class RemoteCacheBustingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ccs_remote_cache_" + Guid.NewGuid().ToString("N"));

    public RemoteCacheBustingTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "cachetest-remote", "assets"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private void WriteEntry(string content) =>
        File.WriteAllText(Path.Combine(_root, "cachetest-remote", "remoteEntry.js"), content);

    private RemoteStaticFiles Files() =>
        new(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { [RemoteStaticFiles.RootKey] = _root }).Build());

    [Fact]
    public void ContentVersion_МеняетсяВместеССодержимымRemoteEntry()
    {
        var files = Files();
        WriteEntry("export const build = 1;");
        var first = files.ContentVersion("/cachetest-remote/remoteEntry.js");

        // Та же длина — пересчёт обязан сработать по времени записи, а не только по размеру
        WriteEntry("export const build = 2;");
        File.SetLastWriteTimeUtc(Path.Combine(_root, "cachetest-remote", "remoteEntry.js"), DateTime.UtcNow.AddMinutes(1));
        var second = files.ContentVersion("/cachetest-remote/remoteEntry.js");

        first.Should().NotBeNullOrEmpty();
        second.Should().NotBeNullOrEmpty();
        second.Should().NotBe(first, "новая сборка модуля обязана дать новый URL remoteEntry.js");
        files.ContentVersion("/cachetest-remote/remoteEntry.js?x=1").Should().Be(second, "query не часть пути файла");
    }

    [Fact]
    public void ContentVersion_НетФайлаИлиВыходЗаКорень_Null()
    {
        var files = Files();
        files.ContentVersion("/cachetest-remote/remoteEntry.js").Should().BeNull();
        files.ContentVersion("/../outside/remoteEntry.js").Should().BeNull();
        files.ContentVersion("https://cdn.example/remoteEntry.js").Should().BeNull();
    }

    // Модуль берём реально загруженный в тестовом окружении: с гейтом «раздаём только загруженные»
    // (ServedRemotes / SubsystemStateStore) фиктивный модуль без сборки в раздачу не попадает.
    private TestWebApplicationFactory Factory()
    {
        var factory = new TestWebApplicationFactory();
        factory.ExtraConfig[RemoteStaticFiles.RootKey] = _root;
        return factory;
    }

    private static async Task<(string Id, string Url)> FirstRemote(HttpClient client)
    {
        var body = await client.GetFromJsonAsync<JsonElement>("/api/subsystem-modules");
        var items = body.GetProperty("items").EnumerateArray().ToList();
        items.Should().NotBeEmpty("в тестовом окружении должен быть загружен хотя бы один модуль с MF-remote");
        var first = items[0];
        return (first.GetProperty("id").GetString()!, first.GetProperty("remoteUrl").GetString()!);
    }

    private static string Folder(string url) => url.TrimStart('/').Split('/', '?')[0];

    private void WriteRemote(string folder, string entry, string? chunk = null)
    {
        Directory.CreateDirectory(Path.Combine(_root, folder, "assets"));
        File.WriteAllText(Path.Combine(_root, folder, "remoteEntry.js"), entry);
        if (chunk is not null) File.WriteAllText(Path.Combine(_root, folder, "assets", chunk), "export default 1;");
    }

    [Fact]
    public async Task SubsystemModules_UrlRemoteEntryМеняетсяПриНовойСборке()
    {
        using var factory = Factory();
        using var client = factory.CreateAuthenticatedClient();
        var (id, bare) = await FirstRemote(client);
        var folder = Folder(bare);

        WriteRemote(folder, "export const build = 'a';");
        var before = (await FirstRemote(client)).Url;
        WriteRemote(folder, "export const build = 'bb';");
        var after = (await FirstRemote(client)).Url;

        before.Should().StartWith($"/{folder}/remoteEntry.js?v=", $"модуль {id}: URL несёт хеш сборки");
        after.Should().StartWith($"/{folder}/remoteEntry.js?v=");
        after.Should().NotBe(before, "новая сборка модуля обязана дать новый URL remoteEntry.js");
    }

    [Fact]
    public async Task СтатикаRemote_RemoteEntryNoCache_ЧанкиImmutable()
    {
        string folder;
        using (var probe = Factory())
        using (var probeClient = probe.CreateAuthenticatedClient())
            folder = Folder((await FirstRemote(probeClient)).Url);
        // Папка должна существовать ДО старта: раздача статики remote собирается при старте
        WriteRemote(folder, "export const build = 'a';", "chunk-abc123.js");

        using var factory = Factory();
        using var client = factory.CreateClient();

        var entry = await client.GetAsync($"/{folder}/remoteEntry.js");
        entry.IsSuccessStatusCode.Should().BeTrue();
        entry.Headers.CacheControl!.NoCache.Should().BeTrue();
        entry.Headers.CacheControl.MustRevalidate.Should().BeTrue();

        var chunk = await client.GetAsync($"/{folder}/assets/chunk-abc123.js");
        chunk.IsSuccessStatusCode.Should().BeTrue();
        chunk.Headers.CacheControl!.ToString().Should().Contain("immutable").And.Contain("max-age=31536000");
    }
}
