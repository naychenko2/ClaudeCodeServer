using System.Text.Json;
using ClaudeHomeServer.Models;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Models;

public class SphereTests
{
    [Fact]
    public void СтарыйJsonГруппыБезIconИCharter_ДесериализуетсяВSphere()
    {
        const string json = """[{"id":"g1","name":"Работа","color":"#3E7CA6","order":2,"ownerId":"u1"}]""";

        var list = JsonSerializer.Deserialize<List<Sphere>>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        list.Should().ContainSingle();
        list[0].Id.Should().Be("g1");
        list[0].Name.Should().Be("Работа");
        list[0].Icon.Should().BeNull();
        list[0].Charter.Should().BeNull();
    }

    [Fact]
    public void SphereId_ЭтоGroupIdИНеПишетсяВJson()
    {
        var p = new Project { GroupId = "g1" };

        p.SphereId.Should().Be("g1");
        JsonSerializer.Serialize(p).Should().NotContain("SphereId");
    }
}
