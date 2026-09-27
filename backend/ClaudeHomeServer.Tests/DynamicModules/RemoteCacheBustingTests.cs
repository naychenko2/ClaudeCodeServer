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

    private TestWebApplicationFactory Factory()
    {
        var factory = new TestWebApplicationFactory();
        factory.ExtraConfig[RemoteStaticFiles.RootKey] = _root;
        // Индекс с запасом за пределами боевого массива DynamicModules из appsettings.json
        factory.ExtraConfig["DynamicModules:20:Key"] = "cachetest";
        factory.ExtraConfig["DynamicModules:20:Enabled"] = "true";
        factory.ExtraConfig["DynamicModules:20:Frontend:RemoteUrl"] = "/cachetest-remote/remoteEntry.js";
        return factory;
    }

    private static async Task<string> RemoteUrl(HttpClient client)
    {
        var body = await client.GetFromJsonAsync<JsonElement>("/api/subsystem-modules");
        return body.GetProperty("items").EnumerateArray()
            .Single(i => i.GetProperty("id").GetString() == "cachetest")
            .GetProperty("remoteUrl").GetString()!;
    }

    [Fact]
    public async Task SubsystemModules_UrlRemoteEntryМеняетсяПриНовойСборке()
    {
        WriteEntry("export const build = 'a';");
        using var factory = Factory();
        using var client = factory.CreateAuthenticatedClient();

        var before = await RemoteUrl(client);
        WriteEntry("export const build = 'bb';");
        var after = await RemoteUrl(client);

        before.Should().StartWith("/cachetest-remote/remoteEntry.js?v=");
        after.Should().StartWith("/cachetest-remote/remoteEntry.js?v=");
        after.Should().NotBe(before);
    }

    [Fact]
    public async Task СтатикаRemote_RemoteEntryNoCache_ЧанкиImmutable()
    {
        WriteEntry("export const build = 'a';");
        File.WriteAllText(Path.Combine(_root, "cachetest-remote", "assets", "chunk-abc123.js"), "export default 1;");
        using var factory = Factory();
        using var client = factory.CreateClient();

        var entry = await client.GetAsync("/cachetest-remote/remoteEntry.js");
        entry.IsSuccessStatusCode.Should().BeTrue();
        entry.Headers.CacheControl!.NoCache.Should().BeTrue();
        entry.Headers.CacheControl.MustRevalidate.Should().BeTrue();

        var chunk = await client.GetAsync("/cachetest-remote/assets/chunk-abc123.js");
        chunk.IsSuccessStatusCode.Should().BeTrue();
        chunk.Headers.CacheControl!.ToString().Should().Contain("immutable").And.Contain("max-age=31536000");
    }
}
