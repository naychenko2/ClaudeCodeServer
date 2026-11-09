using System.Text.Json;
using ClaudeHomeServer.Models;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Plugins;

/// <summary>
/// Разбор и валидация манифеста плагина (ADR-031 §2.2): валидный манифест, отсутствие
/// вклада, неизвестное право, обязательный секрет. Опции JSON — как в ModuleRegistry.
/// </summary>
public class PluginManifestTests
{
    private static readonly JsonSerializerOptions Json = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static PluginManifest Parse(string json) =>
        JsonSerializer.Deserialize<PluginManifest>(json, Json)!;

    [Fact]
    public void ВалидныйМанифест_РазбираетсяВСеПолями()
    {
        const string json = """
        {
          "id": "local-3f9a/linear-notes",
          "name": "Linear из заметок",
          "version": "0.1.0",
          "bridge": { "v": 1 },
          "contributes": {
            "skills": ["skills/linear-sync"],
            "commands": ["commands/linear.md"],
            "panels": [{ "id": "main", "slot": "sidebar", "entry": "panel/index.html", "title": "Linear" }],
            "requires": {
              "mcp": ["linear"],
              "secrets": [{ "name": "LINEAR_TOKEN", "required": true }]
            }
          },
          "permissions": { "requested": ["notes:read", "net:api.linear.app", "secret:LINEAR_TOKEN"] }
        }
        """;

        var m = Parse(json);

        m.Id.Should().Be("local-3f9a/linear-notes");
        m.Name.Should().Be("Linear из заметок");
        m.Version.Should().Be("0.1.0");
        m.Bridge!.V.Should().Be(1);
        m.Contributes!.Skills!.Should().Equal("skills/linear-sync");
        m.Contributes.Commands!.Should().Equal("commands/linear.md");
        m.Contributes.Panels!.Should().HaveCount(1);
        m.Contributes.Panels![0].Id.Should().Be("main");
        m.Contributes.Panels[0].Entry.Should().Be("panel/index.html");
        m.Contributes.Requires!.Mcp!.Should().Equal("linear");
        m.Contributes.Requires.Secrets!.Should().HaveCount(1);
        m.Contributes.Requires.Secrets![0].Name.Should().Be("LINEAR_TOKEN");
        m.Contributes.Requires.Secrets[0].Required.Should().BeTrue();
        m.Permissions!.Requested!.Should().Equal("notes:read", "net:api.linear.app", "secret:LINEAR_TOKEN");

        var v = m.Validate();
        v.IsValid.Should().BeTrue();
        v.RequiredSecrets.Should().Equal("LINEAR_TOKEN");
    }

    [Fact]
    public void МанифестБезContributes_ПроблемаНетВклада()
    {
        const string json = """
        {
          "id": "local-x/empty",
          "name": "Пустой",
          "version": "0.1.0",
          "permissions": { "requested": ["notes:read"] }
        }
        """;

        var v = Parse(json).Validate();

        v.IsValid.Should().BeFalse();
        v.Problems.Should().Contain(p => p.Code == "no-contribution");
    }

    [Fact]
    public void МанифестСContributesНоПустыми_ПроблемаНетВклада()
    {
        const string json = """
        {
          "id": "local-x/empty",
          "name": "Пустой",
          "version": "0.1.0",
          "contributes": { "skills": [], "commands": [], "panels": [] }
        }
        """;

        var v = Parse(json).Validate();

        v.IsValid.Should().BeFalse();
        v.Problems.Should().Contain(p => p.Code == "no-contribution");
    }

    [Fact]
    public void НеизвестноеПраво_ПроблемаUnknownPermission()
    {
        const string json = """
        {
          "id": "local-x/perm",
          "name": "Права",
          "version": "0.1.0",
          "contributes": { "skills": ["skills/a"] },
          "permissions": { "requested": ["notes:read", "foo:bar"] }
        }
        """;

        var v = Parse(json).Validate();

        v.IsValid.Should().BeFalse();
        v.Problems.Should().Contain(p => p.Code == "unknown-permission" && p.Message.Contains("foo:bar"));
    }

    [Fact]
    public void ИзвестныеПрава_Проходят()
    {
        const string json = """
        {
          "id": "local-x/perm",
          "name": "Права",
          "version": "0.1.0",
          "contributes": { "skills": ["skills/a"] },
          "permissions": { "requested": ["notes:read", "tasks:read", "net:api.linear.app", "secret:LINEAR_TOKEN"] }
        }
        """;

        var v = Parse(json).Validate();

        v.IsValid.Should().BeTrue();
    }

    [Fact]
    public void ПравоStorage_Проходит()
    {
        const string json = """
        {
          "id": "local-x/storage",
          "name": "Хранилище",
          "version": "0.1.0",
          "contributes": { "skills": ["skills/a"] },
          "permissions": { "requested": ["storage"] }
        }
        """;

        var v = Parse(json).Validate();

        v.IsValid.Should().BeTrue();
    }

    [Fact]
    public void ОбязательныйСекрет_БлокируетУстановку()
    {
        const string json = """
        {
          "id": "local-x/secret",
          "name": "Секрет",
          "version": "0.1.0",
          "contributes": { "skills": ["skills/a"], "requires": { "secrets": [
            { "name": "LINEAR_TOKEN", "required": true },
            { "name": "OPTIONAL_TOKEN", "required": false }
          ] } }
        }
        """;

        var v = Parse(json).Validate();

        // Только обязательный секрет блокирует установку; необязательный — нет
        v.RequiredSecrets.Should().Equal("LINEAR_TOKEN");
    }
}
