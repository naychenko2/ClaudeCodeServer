using ClaudeHomeServer.Services.Mcp;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Стор секретов после волны 7 хранит два вида значений: плоские строки (ключи API,
/// Bearer) и записи токенов OAuth. Формат файла обязан читаться в обе стороны — тот же
/// файл открывают архивы бэкапа, снятые до этой волны.
/// </summary>
public class McpSecretStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ccs-mcp-secrets-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* уборка best-effort */ }
    }

    private string FilePath => Path.Combine(_dir, "mcp-secrets.json");

    private McpSecretStore NewStore()
    {
        Directory.CreateDirectory(_dir);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(_dir, "projects.json"),
        }).Build();
        return new McpSecretStore(config);
    }

    [Fact]
    public void СтарыйФайлСоСтрокамиЧитаетсяКакПрежде()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, """{"owner1":{"abc":"ключ-api"}}""");

        var store = NewStore();

        store.Get("owner1", "abc").Should().Be("ключ-api");
        store.Resolve("owner1", McpSecretStore.Placeholder("abc")).Should().Be("ключ-api");
        store.ResolveEntry("owner1", McpSecretStore.Placeholder("abc"))!.IsPlain.Should().BeTrue();
    }

    [Fact]
    public void ПлоскийСекретОстаётсяСтрокойВФайле()
    {
        var store = NewStore();

        store.Set("owner1", "api-key-1");

        File.ReadAllText(FilePath).Should().Contain("\"api-key-1\"").And.NotContain("\"Value\"");
    }

    [Fact]
    public void ЗаписьТокеновПереживаетПерезагрузкуСтора()
    {
        var store = NewStore();
        var expires = DateTime.UtcNow.AddHours(1);

        var placeholder = store.SetEntry("owner1", new McpSecretEntry
        {
            Value = "access", RefreshToken = "refresh", ExpiresAt = expires,
            Scope = "read write", TokenType = "Bearer",
        });

        var reloaded = NewStore().ResolveEntry("owner1", placeholder)!;
        reloaded.Value.Should().Be("access");
        reloaded.RefreshToken.Should().Be("refresh");
        reloaded.ExpiresAt.Should().BeCloseTo(expires, TimeSpan.FromSeconds(1));
        reloaded.Scope.Should().Be("read write");
        // Заголовок собирается общей точкой McpAuthHeaders через Resolve — она видит access
        NewStore().Resolve("owner1", placeholder).Should().Be("access");
    }

    [Fact]
    public void ОбновлениеТокеновСохраняетСсылку()
    {
        var store = NewStore();
        var placeholder = store.SetEntry("owner1", new McpSecretEntry { Value = "access-1", RefreshToken = "r1" });

        var again = store.SetEntry("owner1",
            new McpSecretEntry { Value = "access-2", RefreshToken = "r2" }, placeholder);

        // Ссылка в реестре не меняется — иначе рефреш правил бы запись сервера на каждый ход
        again.Should().Be(placeholder);
        store.Resolve("owner1", placeholder).Should().Be("access-2");
    }

    [Fact]
    public void УдалениеРаботаетИДляЗаписиТокенов()
    {
        var store = NewStore();
        var placeholder = store.SetEntry("owner1", new McpSecretEntry { Value = "access", RefreshToken = "r" });

        store.Remove("owner1", [placeholder]);

        store.Resolve("owner1", placeholder).Should().BeNull();
    }
}
