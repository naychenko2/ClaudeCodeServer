using ClaudeHomeServer.Services.Mcp;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

// Классификатор экрана «MCP-серверы»: ось — кто подключил сервер и кто им управляет.
// От группы зависит, что увидит человек (заголовок, подсказки, можно ли управлять),
// поэтому распознавание каждой группы зафиксировано отдельно.
public class McpRegistryBuiltinGroupTests
{
    [Theory]
    [InlineData("tasks", McpBuiltinGroups.Product)]
    [InlineData("notes", McpBuiltinGroups.Product)]
    [InlineData("memory", McpBuiltinGroups.Product)]
    [InlineData("personas", McpBuiltinGroups.Product)]
    [InlineData("wsp", McpBuiltinGroups.Product)]
    [InlineData("notifications", McpBuiltinGroups.Product)]
    [InlineData("widgets", McpBuiltinGroups.Product)]
    [InlineData("codegraph", McpBuiltinGroups.Product)]
    public void BuiltinGroupOf_СервисыПродукта_ГруппаProduct(string key, string expected) =>
        McpRegistry.BuiltinGroupOf(key).Should().Be(expected);

    [Theory]
    [InlineData("dify", McpBuiltinGroups.Integration)]
    [InlineData("fal-ai", McpBuiltinGroups.Integration)]
    [InlineData("glif", McpBuiltinGroups.Integration)]
    public void BuiltinGroupOf_Интеграции_ГруппаIntegration(string key, string expected) =>
        McpRegistry.BuiltinGroupOf(key).Should().Be(expected);

    [Theory]
    [InlineData("pmem_alex", McpBuiltinGroups.PersonaMemory)]
    [InlineData("pmem_so_fya", McpBuiltinGroups.PersonaMemory)]
    public void BuiltinGroupOf_ПамятьПерсон_ГруппаPersonaMemory(string key, string expected) =>
        McpRegistry.BuiltinGroupOf(key).Should().Be(expected);

    [Theory]
    [InlineData("markitdown", McpBuiltinGroups.External)]
    [InlineData("outlook", McpBuiltinGroups.External)]
    [InlineData("vdi", McpBuiltinGroups.External)]
    public void BuiltinGroupOf_НезнакомыйКлюч_ГруппаExternal(string key, string expected) =>
        McpRegistry.BuiltinGroupOf(key).Should().Be(expected);

    [Theory]
    [InlineData("GLIF", McpBuiltinGroups.Integration)]
    [InlineData("Tasks", McpBuiltinGroups.Product)]
    [InlineData("PMEM_alex", McpBuiltinGroups.PersonaMemory)]
    public void BuiltinGroupOf_НеЗависитОтРегистра(string key, string expected) =>
        McpRegistry.BuiltinGroupOf(key).Should().Be(expected);

    [Fact]
    public void IntegrationKeys_ВсеВходятВReservedKeys() =>
        McpRegistry.IntegrationKeys.Should().BeSubsetOf(McpRegistry.ReservedKeys,
            "интеграции нельзя занять записью реестра — они тоже часть продукта");
}
