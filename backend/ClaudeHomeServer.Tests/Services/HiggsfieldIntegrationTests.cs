using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Mcp;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services;

// Регресс: вход Higgsfield падал с InvalidOperationException «Ключ занят встроенным
// сервером продукта» — EnsureRecord шёл через Create, а ключ «higgsfield» сидит в
// ReservedKeys. Защиту «человек через форму не мог занять ключ руками» сохраняем —
// McpRegistry.Create по-прежнему режет; встроенный путь CreateBuiltIn пускает только
// ключи из ReservedKeys.
public class HiggsfieldIntegrationTests : IDisposable
{
    private readonly string _tempDir;
    private readonly McpRegistry _registry;
    private readonly UserStore _users;
    private readonly FeatureFlagService _flags;
    private readonly HiggsfieldIntegration _sut;
    private const string OwnerId = "owner-hf";

    public HiggsfieldIntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "higgsfield_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_tempDir, "projects.json"),
            }).Build();
        var secrets = new McpSecretStore(config);
        var statuses = new McpStatusStore(config);
        _registry = new McpRegistry(config, secrets);
        _users = new UserStore(config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        // Создаём владельца под тест — UserStore с пустым хранилищем отдаст дефолтного admin,
        // но интеграции нужна конкретная запись, чтобы не зависеть от каталога флагов.
        var owner = _users.GetFirst() ?? throw new InvalidOperationException("UserStore пуст");
        // Гарантируем, что запись идёт ровно под OwnerId — используем id владельца.
        // (тест-локальная переменная OwnerId ниже переиспользуется)
        _flags = new FeatureFlagService(_users);
        var oauth = new McpOAuthService(_registry, secrets, statuses,
            new StubHttpClientFactory(new EmptyHandler()), config, NullLogger<McpOAuthService>.Instance);
        _sut = new HiggsfieldIntegration(_registry, secrets, statuses, oauth, _flags,
            NullLogger<HiggsfieldIntegration>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public void EnsureRecord_ПервыйВход_СоздаётЗаписьВРеестреБезИсключения()
    {
        var record = _sut.EnsureRecord(OwnerId);

        record.Should().NotBeNull();
        record.Key.Should().Be(HiggsfieldIntegration.Key);
        record.Url.Should().Be(HiggsfieldIntegration.Url);
        record.Auth.Kind.Should().Be(McpAuthKind.OAuth2);
        // Та же запись возвращается при повторном вызове — без дублей.
        _sut.EnsureRecord(OwnerId).Id.Should().Be(record.Id);
    }

    [Fact]
    public void McpRegistryCreate_КлючHiggsfield_ОтвергаетсяКакИРаньше()
    {
        // Защита от руки через форму: человек не должен мочь занять ключ встроенного
        // сервера. После введения CreateBuiltIn это поведение не должно меняться.
        var draft = new McpServerRecord
        {
            Key = HiggsfieldIntegration.Key,
            Label = "Подделка",
            Transport = McpTransport.Http,
            Url = "https://example.com/mcp",
        };

        var act = () => _registry.Create(OwnerId, draft);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*higgsfield*");
    }

    [Fact]
    public void McpRegistryCreateBuiltIn_КлючHiggsfield_Принимается()
    {
        var draft = new McpServerRecord
        {
            Key = HiggsfieldIntegration.Key,
            Label = HiggsfieldIntegration.Label,
            Description = HiggsfieldIntegration.Description,
            Transport = McpTransport.Http,
            Url = HiggsfieldIntegration.Url,
            Auth = new McpAuthConfig { Kind = McpAuthKind.OAuth2 },
            Enabled = true,
        };

        var created = _registry.CreateBuiltIn(OwnerId, draft);

        created.Key.Should().Be(HiggsfieldIntegration.Key);
        _registry.Get(OwnerId, created.Id).Should().NotBeNull();
    }

    [Fact]
    public void McpRegistryCreateBuiltIn_ЧужойКлюч_ОтвергаетсяКакЛазейка()
    {
        // Sanity check: если бы CreateBuiltIn не проверял, что ключ — из ReservedKeys,
        // им можно было бы заводить произвольные серверы в обход ValidateKey.
        var draft = new McpServerRecord
        {
            Key = "my-custom-server",
            Transport = McpTransport.Http,
            Url = "https://example.com/mcp",
        };

        var act = () => _registry.CreateBuiltIn(OwnerId, draft);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*ReservedKeys*");
    }

    // Сквозной цикл: EnsureRecord (создание) + сохранение токенов (Update через
    // McpOAuthService.StoreTokens). До правки второй шаг падал по резерву ключа
    // «higgsfield» и вход не завершался.
    [Fact]
    public void Вход_EnsureRecordИОбновлениеСТокеном_НеПадает()
    {
        var record = _sut.EnsureRecord(OwnerId);

        // Имитация McpOAuthService.StoreTokens — обновление заполненным OAuth.
        record.Auth!.OAuth = new McpOAuthConfig
        {
            AuthorizationServer = "https://auth.higgsfield.ai",
            TokenEndpoint = "https://auth.higgsfield.ai/token",
            ClientId = "client-test",
            AccessTokenRef = "secret:stub",
            ExpiresAt = DateTime.UtcNow.AddHours(1),
        };

        var act = () => _registry.Update(OwnerId, record.Id, record);

        act.Should().NotThrow("StoreTokens-подобное обновление обязано работать — иначе вход не завершится");
        var saved = _registry.Get(OwnerId, record.Id)!;
        saved.Auth!.OAuth!.AccessTokenRef.Should().Be("secret:stub");
        saved.Key.Should().Be(HiggsfieldIntegration.Key);
    }

    // Минимальная обвязка для конструктора HiggsfieldIntegration — сеть не нужна,
    // потому что EnsureRecord не ходит по проводу.
    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class EmptyHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct) =>
            throw new InvalidOperationException("сеть в тесте не нужна");
    }
}
