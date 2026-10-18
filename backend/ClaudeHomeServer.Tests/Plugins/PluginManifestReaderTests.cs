using ClaudeHomeServer.Services.Plugins;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Plugins;

// Разбор манифеста пакета (rework C3): plugin.json / ccs-plugin.json / .claude-plugin/plugin.json.
// Топ-уровневые поля проверяются по ManifestFieldPolicy: каждый ключ вне белого списка →
// deny-manifest-field с именем поля. Критерий: каждый запрещённый ключ (hooks, mcpServers,
// agents, lspServers, ...) даёт свой тест.
public class PluginManifestReaderTests
{
    private static string CreateTempDir() =>
        Path.Combine(Path.GetTempPath(), "ccs-manifest-" + Guid.NewGuid().ToString("N"));

    private static string NewPackage(params (string Rel, string Content)[] files)
    {
        var root = CreateTempDir();
        Directory.CreateDirectory(root);
        foreach (var (rel, content) in files)
        {
            var full = Path.Combine(root, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }
        return root;
    }

    private static PluginManifestReader Reader() => new();

    // --- Каждый запрещённый ключ → deny-manifest-field с именем поля (критерий) ---

    [Theory]
    [InlineData("hooks")]
    [InlineData("mcpServers")]
    [InlineData("agents")]
    [InlineData("lspServers")]
    [InlineData("outputStyles")]
    [InlineData("settings")]
    [InlineData("enableAllProjectMcpServers")]
    [InlineData("commands")]
    public void ЗапрещённоеПоле_ДаетDenyManifestField(string field)
    {
        var json = "{\"name\":\"P\",\"version\":\"0.1.0\",\"" + field + "\":{}}";
        var problems = Reader().ValidateFields(json);

        problems.Should().Contain(p => p.Code == "deny-manifest-field" && p.Message.Contains(field));
    }

    // --- Разрешённые поля → без проблем ---

    [Fact]
    public void РазрешённыеПоля_БезПроблем()
    {
        const string json = """
        {
          "id": "local-x/pkg",
          "name": "P",
          "version": "0.1.0",
          "description": "d",
          "author": "a",
          "license": "MIT",
          "homepage": "https://x",
          "repository": "https://y",
          "keywords": ["k"],
          "contributes": { "skills": ["s"] },
          "requires": { "mcp": ["m"] },
          "permissions": { "requested": ["notes:read"] },
          "bridge": { "v": 1 },
          "engines": { "claude": ">=1.0" }
        }
        """;
        Reader().ValidateFields(json).Should().BeEmpty();
    }

    // --- Read: находит ccs-plugin.json и проверяет его поля ---

    [Fact]
    public void Read_CcsPluginJson_ПроверяетПоля()
    {
        var root = NewPackage(
            ("ccs-plugin.json", """{"name":"P","version":"0.1.0","hooks":{}}"""));

        var (raws, problems) = Reader().Read(root);

        raws.Should().NotBeEmpty("найден ccs-plugin.json");
        problems.Should().Contain(p => p.Code == "deny-manifest-field" && p.Message.Contains("hooks"));
    }

    // --- Read: находит нативный plugin.json и проверяет его поля ---

    [Fact]
    public void Read_PluginJson_ПроверяетПоля()
    {
        var root = NewPackage(
            ("plugin.json", """{"name":"P","version":"0.1.0","mcpServers":{}}"""));

        var (_, problems) = Reader().Read(root);

        problems.Should().Contain(p => p.Code == "deny-manifest-field" && p.Message.Contains("mcpServers"));
    }

    // --- Read: C3 — КАЖДЫЙ найденный манифест проверяется (цикл без раннего return) ---

    [Fact]
    public void Read_ОбаМанифеста_Проверяются()
    {
        var root = NewPackage(
            ("ccs-plugin.json", """{"name":"P","version":"0.1.0","hooks":{}}"""),
            ("plugin.json", """{"name":"P","version":"0.1.0","mcpServers":{}}"""));

        var (_, problems) = Reader().Read(root);

        // C3: оба манифеста проверяются: hooks (ccs) и mcpServers (plugin.json) оба видны.
        problems.Should().Contain(p => p.Message.Contains("hooks"));
        problems.Should().Contain(p => p.Message.Contains("mcpServers"));
    }

    // --- C3: чистый ccs-plugin.json + hooks в .claude-plugin/plugin.json → deny-manifest-field ---
    // CLI читает именно .claude-plugin/plugin.json: «чистый» ccs-plugin.json не спасает.

    [Fact]
    public void C3_ЧистыйCcsИ_ХукиВClaudePlugin_ДаетDenyManifestField()
    {
        var root = NewPackage(
            ("ccs-plugin.json", """{"name":"P","version":"0.1.0"}"""),
            (".claude-plugin/plugin.json", """{"name":"P","version":"0.1.0","hooks":{}}"""));

        var (_, problems) = Reader().Read(root);

        problems.Should().Contain(p => p.Code == "deny-manifest-field" && p.Message.Contains("hooks"));
    }

    // --- Read: нет манифеста → ([], []) ---

    [Fact]
    public void Read_НетМанифеста_ПустойСписок()
    {
        var root = NewPackage(("panel/index.html", "<html></html>"));

        var (raws, problems) = Reader().Read(root);

        raws.Should().BeEmpty("манифестов нет");
        problems.Should().BeEmpty();
    }

    // --- Read: битый JSON → без проблем полей (модель PluginManifest отчитается отдельно) ---

    [Fact]
    public void Read_БитыйJson_БезПроблемПолей()
    {
        var root = NewPackage(("ccs-plugin.json", "{ not json"));

        var (_, problems) = Reader().Read(root);

        problems.Should().BeEmpty();
    }
}
