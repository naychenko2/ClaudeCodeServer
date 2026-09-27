using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Mcp;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Регресс-тесты на <see cref="McpRegistry.Update"/>: путь обновления встроенных записей
/// (ключ в <see cref="McpRegistry.ReservedKeys"/> при сохранении токенов и при выходе) и
/// защита от того, чтобы лечение стало лазейкой для занятия чужого ключа через Update.
/// </summary>
public class McpRegistryUpdateTests : IDisposable
{
    private readonly string _tempDir;
    private readonly McpRegistry _registry;
    private const string OwnerId = "owner-mcp-update";

    public McpRegistryUpdateTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "mcp_registry_update_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_tempDir, "projects.json"),
            }).Build();
        var secrets = new McpSecretStore(config);
        _registry = new McpRegistry(config, secrets);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    // ── регрессия: вход Higgsfield через OAuth ──────────────────────────────────────

    [Fact]
    public void Update_ЗаписьСЗарезервированнымКлючом_НеПадает()
    {
        // Регресс: до правки StoreTokens в McpOAuthService для записи «higgsfield»
        // падал с «Ключ «higgsfield» занят встроенным сервером продукта» — токены не
        // сохранялись, вход не завершался. Ключ существующей встроенной записи при
        // обновлении тот же, что и был, — проверку резерва для него пропускаем.
        var draft = new McpServerRecord
        {
            Key = HiggsfieldOAuthService.Key,
            Label = HiggsfieldOAuthService.Label,
            Transport = McpTransport.Http,
            Url = HiggsfieldOAuthService.Url,
            Auth = new McpAuthConfig { Kind = McpAuthKind.OAuth2 },
        };
        var created = _registry.CreateBuiltIn(OwnerId, draft);
        var versionBefore = created.AuthVersion;

        // Имитация StoreTokens — обновление тем же ключом, новый access_token в OAuth
        created.Auth!.OAuth = new McpOAuthConfig
        {
            AuthorizationServer = "https://auth.example.com",
            TokenEndpoint = "https://auth.example.com/token",
            ClientId = "client-x",
            AccessTokenRef = "secret:fake",
            ExpiresAt = DateTime.UtcNow.AddHours(1),
        };

        var act = () => _registry.Update(OwnerId, created.Id, created);

        act.Should().NotThrow("обновление существующей встроенной записи тем же ключом обязано работать — это путь StoreTokens");
        var saved = _registry.Get(OwnerId, created.Id)!;
        saved.Key.Should().Be(HiggsfieldOAuthService.Key);
        saved.AuthVersion.Should().BeGreaterThan(versionBefore, "Update поднимает AuthVersion — без него живой CLI остался бы со старым секретом");
    }

    [Theory]
    [InlineData("higgsfield")]
    [InlineData("glif")]
    [InlineData("tasks")]
    [InlineData("notes")]
    [InlineData("memory")]
    public void Update_ЗаписьИзReservedKeys_ПравкаТемЖеКлючомПроходит(string reservedKey)
    {
        // Все записи из ReservedKeys (и интеграции, и продуктовые) должны обновляться
        // штатно — иначе любой встроенный сервер при первом же StoreTokens/Logout падает.
        var draft = new McpServerRecord
        {
            Key = reservedKey,
            Label = reservedKey,
            Transport = McpTransport.Http,
            Url = "https://example.com/mcp",
            Auth = new McpAuthConfig { Kind = McpAuthKind.OAuth2 },
        };
        var created = _registry.CreateBuiltIn(OwnerId, draft);

        var act = () => _registry.Update(OwnerId, created.Id, created);

        act.Should().NotThrow();
        _registry.Get(OwnerId, created.Id)!.Key.Should().Be(reservedKey);
    }

    // ── защита от дыры: лечение не должно открывать занятие чужого ключа ───────────

    [Theory]
    [InlineData("glif")]
    [InlineData("tasks")]
    [InlineData("higgsfield")]
    [InlineData("memory")]
    public void Update_СменаКлючаОбычнойЗаписиНаЗарезервированный_Отвергается(string reservedKey)
    {
        // Человек не должен иметь возможность переименовать свой сервер в ключ
        // встроенного — иначе на экране «MCP-серверы» его карточка займёт место
        // продукта и собьёт доставку конфига хода. Смена ключа → проверка резерва
        // остаётся включённой.
        var created = _registry.Create(OwnerId, new McpServerRecord
        {
            Key = "my-weather",
            Label = "Погода",
            Transport = McpTransport.Http,
            Url = "https://weather.example.com/mcp",
        });

        // Create возвращает ссылку на объект в списке реестра — мутировать её
        // нельзя (иначе existing.Key внутри Update подстроится под черновик и
        // сравнение ключей соврёт). Клонируем черновик ровно так, как это делает
        // McpOAuthService.SaveAuth (Json round-trip), и трогаем ключ уже в копии.
        var draft = CloneWith(created, key: reservedKey);

        var act = () => _registry.Update(OwnerId, created.Id, draft);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*занят встроенным сервером*");
        // Ключ не применился — запись осталась с прежним.
        _registry.Get(OwnerId, created.Id)!.Key.Should().Be("my-weather");
    }

    [Fact]
    public void Update_СменаКлючаНаСвободный_Принимается()
    {
        var created = _registry.Create(OwnerId, new McpServerRecord
        {
            Key = "old-key",
            Label = "Старое имя",
            Transport = McpTransport.Http,
            Url = "https://example.com/mcp",
        });

        var draft = CloneWith(created, key: "renamed-server", label: "Новое имя");

        var act = () => _registry.Update(OwnerId, created.Id, draft);

        act.Should().NotThrow();
        var saved = _registry.Get(OwnerId, created.Id)!;
        saved.Key.Should().Be("renamed-server");
        saved.Label.Should().Be("Новое имя");
    }

    [Fact]
    public void Update_КонфликтКлюча_Отвергается()
    {
        // Стандартная защита уникальности — без неё две записи могли бы жить под одним ключом.
        var first = _registry.Create(OwnerId, new McpServerRecord
        {
            Key = "weather",
            Label = "Погода",
            Transport = McpTransport.Http,
            Url = "https://weather.example.com/mcp",
        });
        var second = _registry.Create(OwnerId, new McpServerRecord
        {
            Key = "calendar",
            Label = "Календарь",
            Transport = McpTransport.Http,
            Url = "https://calendar.example.com/mcp",
        });

        var draft = CloneWith(second, key: "weather");

        var act = () => _registry.Update(OwnerId, second.Id, draft);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*уже есть*");
        // И вторая запись осталась со старым ключом
        _registry.Get(OwnerId, second.Id)!.Key.Should().Be("calendar");
        // И первая не пострадала
        _registry.Get(OwnerId, first.Id)!.Key.Should().Be("weather");
    }

    [Fact]
    public void Update_ЗаписьОтсутствует_ВозвращаетNull()
    {
        var draft = new McpServerRecord
        {
            Key = "weather",
            Label = "Погода",
            Transport = McpTransport.Http,
            Url = "https://weather.example.com/mcp",
        };

        var result = _registry.Update(OwnerId, "missing-id", draft);

        result.Should().BeNull();
    }

    // Глубокая копия через сериализацию — как McpOAuthService.SaveAuth на проде.
    private static McpServerRecord Clone(McpServerRecord source) =>
        JsonSerializer.Deserialize<McpServerRecord>(JsonSerializer.Serialize(source))!;

    // Копия + одно-два переопределённых поля. McpServerRecord — обычный class,
    // поэтому «with»-выражение недоступно и приходится патчить копию явно.
    private static McpServerRecord CloneWith(McpServerRecord source,
        string? key = null, string? label = null)
    {
        var copy = Clone(source);
        if (key is not null) copy.Key = key;
        if (label is not null) copy.Label = label;
        return copy;
    }
}
