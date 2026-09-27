using System.Net.Http;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Mcp;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Тест миграции personal-записей Higgsfield (ф.2.1):
/// RunMigration() усыновляет самый свежий токен под сервисного владельца
/// и удаляет per-owner записи. Идемпотентна: повторный вызов вернёт false.
/// </summary>
public class HiggsfieldOAuthMigrationTests : IDisposable
{
    private const string PersonalOwner = "owner-alex";
    private const string SecretId = "tok1";

    private readonly string _dir = Path.Combine(Path.GetTempPath(),
        "ccs-hf-migr-" + Guid.NewGuid().ToString("N")[..8]);

    public HiggsfieldOAuthMigrationTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* уборка best-effort */ }
    }

    [Fact]
    public void Migration_УсыновляетТокен_ИУдаляетПерOwnerЗапись()
    {
        var (service, registry, secrets) = NewService();

        // Per-owner higgsfield-запись с OAuth2 и валидным access-токеном
        var record = CreatePersonalHiggsfieldRecord(registry, secrets);

        var result = service.RunMigration();

        // Миграция нашла и усыновила токен
        result.Should().BeTrue("ожидалось усыновление токена");

        // Per-owner запись удалена из реестра
        var remaining = registry.GetByOwner(PersonalOwner)
            .Where(r => string.Equals(r.Key, HiggsfieldOAuthService.Key, StringComparison.OrdinalIgnoreCase));
        remaining.Should().BeEmpty("per-owner higgsfield-запись должна быть удалена");

        // Токен перенесён под сервисного владельца
        var svcRecords = registry.GetByOwner(HiggsfieldOAuthService.ServiceOwnerId);
        svcRecords.Should().Contain(r => string.Equals(r.Key, HiggsfieldOAuthService.Key, StringComparison.OrdinalIgnoreCase),
            "сервисная запись higgsfield-instance должна существовать");

        var entry = secrets.GetEntry(HiggsfieldOAuthService.ServiceOwnerId, SecretId);
        entry.Should().NotBeNull("access-токен должен быть перенесён под сервисного владельца");
        entry!.Value.Should().Be("test-access-token");
    }

    [Fact]
    public void Migration_Идемпотентна_ПовторныйВызов_ВернётFalse()
    {
        var (service, registry, secrets) = NewService();
        CreatePersonalHiggsfieldRecord(registry, secrets);

        // Первый вызов: усыновляет токен
        service.RunMigration().Should().BeTrue();

        // Второй вызов: per-owner записей уже нет
        service.RunMigration().Should().BeFalse("повторный вызов без per-owner записей вернёт false");
    }

    [Fact]
    public void Migration_БезPerOwnerЗаписей_НеТронетСервисную()
    {
        var (service, registry, _) = NewService();

        // Создаём только сервисную запись (как после предыдущей миграции)
        var svcRecord = registry.CreateBuiltIn(HiggsfieldOAuthService.ServiceOwnerId,
            new McpServerRecord
            {
                Key = HiggsfieldOAuthService.Key,
                Label = HiggsfieldOAuthService.Label,
                Transport = McpTransport.Http,
                Url = HiggsfieldOAuthService.Url,
                Auth = new McpAuthConfig { Kind = McpAuthKind.OAuth2 },
                Enabled = true,
            });
        svcRecord.Should().NotBeNull();

        var result = service.RunMigration();

        result.Should().BeFalse("без per-owner записей миграция не делает ничего");
        // Сервисная запись осталась
        registry.GetByOwner(HiggsfieldOAuthService.ServiceOwnerId)
            .Should().Contain(r => r.Id == svcRecord!.Id);
    }

    [Fact]
    public void Migration_PerOwnerСекрет_УдаляетсяИзСтора()
    {
        var (service, registry, secrets) = NewService();

        // Создаём per-owner запись + секреты
        var record = CreatePersonalHiggsfieldRecord(registry, secrets);

        service.RunMigration();

        // Секрет per-owner удалён из стора
        secrets.GetEntry(PersonalOwner, SecretId).Should().BeNull(
            "per-owner секрет higgsfield должен быть удалён");
    }

    // ── Вспомогательные ─────────────────────────────────────────────────────────────────

    private (HiggsfieldOAuthService Service, McpRegistry Registry, McpSecretStore Secrets) NewService()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_dir, "projects.json"),
            }).Build();

        var secrets = new McpSecretStore(config);
        var registry = new McpRegistry(config, secrets);
        var statuses = new McpStatusStore(config);
        var oauth = new McpOAuthService(registry, secrets, statuses,
            new StubHttpFactory(), config, NullLogger<McpOAuthService>.Instance);
        var service = new HiggsfieldOAuthService(registry, secrets, statuses, oauth,
            config, NullLogger<HiggsfieldOAuthService>.Instance);
        return (service, registry, secrets);
    }

    private static McpServerRecord CreatePersonalHiggsfieldRecord(McpRegistry registry, McpSecretStore secrets)
    {
        // Access-токен в секрете (id фиксированный для проверяемости)
        secrets.SetEntry(PersonalOwner, new McpSecretEntry
        {
            Value = "test-access-token",
            RefreshToken = "test-refresh-token",
            ExpiresAt = DateTime.UtcNow.AddDays(1),
        }, refOrId: SecretId);

        // Per-owner higgsfield-запись: OAuth2, ссылается на токен
        var record = registry.CreateBuiltIn(PersonalOwner, new McpServerRecord
        {
            Key = HiggsfieldOAuthService.Key,
            Label = HiggsfieldOAuthService.Label,
            Transport = McpTransport.Http,
            Url = HiggsfieldOAuthService.Url,
            Auth = new McpAuthConfig
            {
                Kind = McpAuthKind.OAuth2,
                OAuth = new McpOAuthConfig
                {
                    AccessTokenRef = McpSecretStore.Placeholder(SecretId),
                    ClientId = "client-dcr-1",
                },
            },
            Enabled = true,
        });
        return record;
    }

    private sealed class StubHttpFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new StubHandler());
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
    }
}
