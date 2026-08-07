using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Mcp;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

// Регресс: импорт вставленного JSON через форму «Добавить» — ручное действие пользователя,
// а не автообнаружение в глобальном .mcp.json/~/.claude.json. Запись обязана получать
// Source = Manual (иначе UI навешивал бы бейдж «наследство» и прятал кнопку удаления
// у только что добавленного пользователем сервера — баг, найденный при вёрстке волны 4).
public class McpRegistryImportSourceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly McpRegistry _registry;
    private const string OwnerId = "owner-1";

    public McpRegistryImportSourceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "mcp_import_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_tempDir, "projects.json"),
            })
            .Build();
        _registry = new McpRegistry(config, new McpSecretStore(config));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public void ParseImport_ВставленныйФрагмент_ДаётSourceManual()
    {
        using var doc = JsonDocument.Parse("""
            {"mcpServers":{"context7":{"command":"npx","args":["-y","context7-mcp"]}}}
            """);

        var drafts = McpRegistry.ParseImport(doc.RootElement);

        drafts.Should().ContainSingle();
        drafts[0].Source.Should().Be(McpServerSource.Manual);
    }

    [Fact]
    public void ЗаписьИзИмпорта_СоздаётсяИУдаляетсяШтатно()
    {
        using var doc = JsonDocument.Parse("""
            {"mcpServers":{"context7":{"command":"npx","args":["-y","context7-mcp"]}}}
            """);
        var draft = McpRegistry.ParseImport(doc.RootElement).Single();

        var created = _registry.Create(OwnerId, draft);
        created.Source.Should().Be(McpServerSource.Manual);

        var removed = _registry.Delete(OwnerId, created.Id);
        removed.Should().NotBeNull();
        _registry.Get(OwnerId, created.Id).Should().BeNull();
    }
}
