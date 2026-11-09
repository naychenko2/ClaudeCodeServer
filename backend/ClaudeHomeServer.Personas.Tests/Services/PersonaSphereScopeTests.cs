using System.Text.Json;
using System.Text.Json.Serialization;
using ClaudeHomeServer.Models;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

public class PersonaSphereScopeTests
{
    // Те же опции, что у PersonaManager: enum строкой в camelCase
    private static readonly JsonSerializerOptions Opts = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    [Fact]
    public void ПерсонаСоScopeSphere_ПереживаетКругСериализацииJson()
    {
        var persona = new Persona { Scope = PersonaScope.Sphere, SphereId = "sphere-1" };

        var json = JsonSerializer.Serialize(persona, Opts);
        var back = JsonSerializer.Deserialize<Persona>(json, Opts)!;

        json.Should().Contain("\"Scope\":\"sphere\"");
        back.Scope.Should().Be(PersonaScope.Sphere);
        back.SphereId.Should().Be("sphere-1");
    }
}
